using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// MeshCombineStudio's run-time combining done as the build writes each scene. A MeshCombiner set to
// combine at run time (combineInRuntime, combineOnStart) reads its search parents' meshes in its
// Start: it copies every source mesh into managed arrays (MeshCombineJobManager's mesh caches), builds
// the combined meshes and only then lets go. On the PS Vita that grew the GC heap of Station Square's
// load from 88.5 to 104 MB, which the heap never gives back, and Station Square -> Pool then ran out of
// main memory in Pool's own combining (1-12 MB left; 26-30 MB with the combining off). Here each such
// combiner that is active and enabled when its scene starts runs CombineAll in the editor, which the
// tool is made for (its inspector's Combine button): the combined objects and meshes are written with
// the scene, the source renderers are written off, as the combining leaves them, and the combiner no
// longer combines at run time. Combiners switched on later in play keep combining then. The meshes
// are written unreadable, as the combiner makes them, except those a MeshCollider uses. On one thread
// (the combining's own threads finish after its CombineAtOnce loop, which only Update, in play mode,
// would collect). From CROW_MCS_BAKE: on (the PS Vita's default) or off (other platforms' default).
class CrowMeshCombineBake : UnityEditor.Build.IProcessSceneWithReport
{
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static Type combinerType, managerType;

    public int callbackOrder
    {
        get { return 10; }  // after CrowMergeScenes and the split, which leave a combiner's meshes to it
    }

    internal static string Mode()
    {
        string mode = Environment.GetEnvironmentVariable("CROW_MCS_BAKE");
        if (!string.IsNullOrEmpty(mode)) return mode;
        return DowngradeBuild.VitaData ? "on" : "off";  // the PS Vita, or CROW_VITA_DATA=1
    }

    internal static object Get(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, Any);
        return field == null ? null : field.GetValue(target);
    }

    static void Set(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, Any);
        if (field != null) field.SetValue(target, value);
    }

    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (report == null || !BuildPipeline.isBuildingPlayer) return;  // entering play mode
        if (Mode() == "off") return;
        if (combinerType == null)
        {
            combinerType = Type.GetType("MeshCombineStudio.MeshCombiner, MeshCombineStudio.Runtime");
            managerType = Type.GetType("MeshCombineStudio.MeshCombineJobManager, MeshCombineStudio.Runtime");
            if (combinerType == null || managerType == null) return;  // a project without MeshCombineStudio
        }
        var combiners = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren(combinerType, true)).Cast<Behaviour>().ToList();
        if (combiners.Count == 0) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        MethodInfo combineAll = combinerType.GetMethod("CombineAll", new[] { typeof(bool) });
        int baked = 0, later = 0, empty = 0, failed = 0, meshes = 0, unreadable = 0;
        long before = 0, after = 0;
        foreach (Behaviour combiner in combiners)
        {
            if (!(bool)Get(combiner, "combineInRuntime")) continue;
            if (!combiner.enabled || !combiner.gameObject.activeInHierarchy || !(bool)Get(combiner, "combineOnStart"))
            {
                later++;
                continue;
            }
            var existing = new HashSet<MeshFilter>(combiner.GetComponentsInChildren<MeshFilter>(true));
            object settings = Get(combiner, "jobSettings");
            object mode = Get(settings, "combineJobMode");
            object threads = Get(settings, "useMultiThreading");
            object main = Get(settings, "useMainThread");
            object makeUnreadable = Get(combiner, "makeMeshesUnreadable");
            Set(settings, "combineJobMode", Enum.ToObject(Get(settings, "combineJobMode").GetType(), 0));  // CombineAtOnce
            Set(settings, "useMultiThreading", false);
            Set(settings, "useMainThread", true);
            Set(combiner, "makeMeshesUnreadable", false);  // marked below: the editor would drop the data before it's written
            try
            {
                combinerType.GetMethod("InitMeshCombineJobManager").Invoke(combiner, null);
                object manager = managerType.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                managerType.GetMethod("SetJobMode").Invoke(manager, new[] { settings });
                combineAll.Invoke(combiner, new object[] { true });
            }
            catch (Exception exception)
            {
                if (failed++ == 0) Debug.LogError("DOWNGRADE_MCS " + scene.name + " " + combiner.name + ": " + (exception.InnerException ?? exception));
            }
            Set(settings, "combineJobMode", mode);
            Set(settings, "useMultiThreading", threads);
            Set(settings, "useMainThread", main);
            Set(combiner, "makeMeshesUnreadable", makeUnreadable);
            if (!(bool)Get(combiner, "combined"))
            {
                // Nothing found (its search parents empty at build time), or it failed: it combines at run
                // time, as before.
                empty++;
                continue;
            }
            Set(combiner, "combineInRuntime", false);
            baked++;
            before += (int)Get(combiner, "originalTotalVertices");
            after += (int)Get(combiner, "newTotalVertices");
            var collided = new HashSet<Mesh>(combiner.GetComponentsInChildren<MeshCollider>(true).Select(c => c.sharedMesh).Where(m => m != null));
            foreach (MeshFilter filter in combiner.GetComponentsInChildren<MeshFilter>(true))
            {
                if (existing.Contains(filter) || filter.sharedMesh == null) continue;
                meshes++;
                if (!(bool)makeUnreadable || collided.Contains(filter.sharedMesh)) continue;
                var serialized = new SerializedObject(filter.sharedMesh);
                SerializedProperty readable = serialized.FindProperty("m_IsReadable");
                if (readable == null) continue;
                readable.boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                unreadable++;
            }
        }
        // The job manager is the combining's helper object (DontSave), with its caches of the sources.
        var instance = managerType.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).GetValue(null) as Component;
        if (instance != null) UnityEngine.Object.DestroyImmediate(instance.gameObject);
        if (baked + later + empty + failed == 0) return;
        Debug.Log("DOWNGRADE_MCS " + scene.name + " baked=" + baked + " later=" + later + " empty=" + empty + " failed=" + failed +
                  " meshes=" + meshes + " unreadable=" + unreadable + " vertices=" + before + "->" + after + " ms=" + clock.ElapsedMilliseconds);
    }
}

// What MeshCombineStudio's combining leaves in the scenes the PS Vita build writes (M2, 22 Oct). A combiner
// that no longer combines at run time (baked above, or combined in the editor by the game's makers) has
// switched its sources' renderers off for good, and lists them in its MeshCombinerData (per source its
// GameObject, Transform, renderer, filter, mesh and three matrices) only to undo the combining, which
// nothing does in the player: MeshCombiner has no Update, and its OnDestroy restores from its own lists,
// which InitData emptied into the data. Haunted Hilltop had 4,971 sources that are nothing but a Transform,
// a MeshFilter and that switched-off renderer, 2.1 MB of MeshCombinerData, and about 20,000 of its 64,000
// pinned GC handles held the data's managed wrappers; its load after Ocean Kingdom bottomed at 4-7 MB of
// free main memory. Here such a source loses its renderer and filter, unless
//   - anything else is on its object (a collider, a script; CrowStripScenes has taken the inert ones),
//   - it is under the search parents of a combiner that still combines at run time (one switched on later
//     in play, or the enemies' that the merge switched off): that combiner and MeshRelease's KeepPending
//     read those filters,
//   - an LODGroup or a particle system's shape names its renderer,
//   - a behaviour of the scene (PlayMaker FSMs and their templates included) references its object,
//     transform, renderer or filter, or names it in a string (a find by name);
// and the data's lists (found objects, LOD objects, LOD groups, colliders) and the combiner's own are
// emptied: emptying the colliders' list loses only their re-enabling at the scene's unload. The objects
// stay, so names, child indices and hierarchy walks are as they were. From CROW_MCS_STRIP: on or off (by
// default as CROW_MCS_BAKE: on for the PS Vita's scene data).
class CrowMeshCombineStrip : UnityEditor.Build.IProcessSceneWithReport
{
    static readonly string[] Lists = { "foundObjects", "foundLodObjects", "foundLodGroups", "foundColliders" };
    // Arrays of these element types hold no object reference and no name (strings are read).
    static readonly HashSet<string> Plain = new HashSet<string>
    {
        "int", "unsigned int", "SInt8", "UInt8", "SInt16", "UInt16", "SInt64", "UInt64", "float", "double", "bool", "char",
        "Vector2f", "Vector3f", "Vector4f", "Quaternionf", "ColorRGBA", "Rectf",
    };

    public int callbackOrder
    {
        get { return 30; }  // after CrowStripScenes (20), which removes the inert scripts some sources carry
    }

    static string Mode()
    {
        string mode = Environment.GetEnvironmentVariable("CROW_MCS_STRIP");
        return string.IsNullOrEmpty(mode) ? CrowMeshCombineBake.Mode() : mode;
    }

    // Every object a behaviour's serialized data references, and every string in it (with a path's last
    // part); an FsmTemplate it references is read too.
    static void Walk(UnityEngine.Object behaviour, HashSet<UnityEngine.Object> objects, HashSet<string> names, HashSet<UnityEngine.Object> walked)
    {
        if (!walked.Add(behaviour)) return;
        var serialized = new SerializedObject(behaviour);
        SerializedProperty property = serialized.GetIterator();
        bool enter = true;
        while (property.Next(enter))
        {
            enter = true;
            if (property.propertyType == SerializedPropertyType.ObjectReference)
            {
                UnityEngine.Object target = property.objectReferenceValue;
                if (target != null)
                {
                    objects.Add(target);
                    if (target is ScriptableObject && target.GetType().Name == "FsmTemplate") Walk(target, objects, names, walked);
                }
                enter = false;
            }
            else if (property.propertyType == SerializedPropertyType.String)
            {
                string text = property.stringValue;
                if (!string.IsNullOrEmpty(text))
                {
                    names.Add(text);
                    int slash = text.LastIndexOf('/');
                    if (slash >= 0) names.Add(text.Substring(slash + 1));
                }
                enter = false;
            }
            else if (property.isArray && Plain.Contains(property.arrayElementType)) enter = false;
        }
    }

    static IEnumerable<GameObject> SearchParents(object combiner)
    {
        object options = CrowMeshCombineBake.Get(combiner, "searchOptions");
        if (options == null) yield break;
        var parent = CrowMeshCombineBake.Get(options, "parent") as GameObject;
        if (parent != null) yield return parent;
        var parents = CrowMeshCombineBake.Get(options, "parentGOs") as GameObject[];
        if (parents == null) yield break;
        foreach (GameObject one in parents)
            if (one != null) yield return one;
    }

    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (report == null || !BuildPipeline.isBuildingPlayer) return;  // entering play mode
        if (Mode() != "on") return;
        Type combinerType = Type.GetType("MeshCombineStudio.MeshCombiner, MeshCombineStudio.Runtime");
        if (combinerType == null) return;  // a project without MeshCombineStudio
        var combiners = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren(combinerType, true)).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var done = new List<Component>();
        var pending = new HashSet<GameObject>();  // under a run-time combiner's search parents
        foreach (Component combiner in combiners)
        {
            if ((bool)CrowMeshCombineBake.Get(combiner, "combineInRuntime"))
            {
                foreach (GameObject parent in SearchParents(combiner))
                    foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
                        pending.Add(t.gameObject);
            }
            else if ((bool)CrowMeshCombineBake.Get(combiner, "combined")) done.Add(combiner);
        }
        if (done.Count == 0) return;
        int sources = 0, stripped = 0, later = 0, named = 0, busy = 0, on = 0, entries = 0, failed = 0;
        try
        {
            var referenced = new HashSet<UnityEngine.Object>();
            var names = new HashSet<string>();
            var walked = new HashSet<UnityEngine.Object>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null) continue;  // a missing script
                    string type = behaviour.GetType().Name;
                    if (type == "MeshCombiner" || type == "MeshCombinerData") continue;  // what is emptied below
                    Walk(behaviour, referenced, names, walked);
                }
                foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>(true))
                    foreach (LOD lod in group.GetLODs())
                        foreach (Renderer renderer in lod.renderers)
                            if (renderer != null) referenced.Add(renderer);
                foreach (ParticleSystem system in root.GetComponentsInChildren<ParticleSystem>(true))
                    if (system.shape.meshRenderer != null) referenced.Add(system.shape.meshRenderer);
            }
            var seen = new HashSet<GameObject>();
            foreach (Component combiner in done)
            {
                var data = CrowMeshCombineBake.Get(combiner, "data") as Component;
                if (data == null) data = combiner.GetComponent("MeshCombinerData");
                var found = data == null ? null : CrowMeshCombineBake.Get(data, "foundObjects") as System.Collections.IList;
                if (found != null)
                    foreach (object cached in found)
                    {
                        var renderer = CrowMeshCombineBake.Get(cached, "mr") as MeshRenderer;
                        var filter = CrowMeshCombineBake.Get(cached, "mf") as MeshFilter;
                        if (renderer == null || filter == null || renderer.gameObject != filter.gameObject) continue;  // gone already
                        GameObject source = renderer.gameObject;
                        if (!seen.Add(source)) continue;
                        sources++;
                        if (renderer.enabled) on++;
                        else if (source.GetComponents<Component>().Length != 3) busy++;  // Transform, MeshFilter, MeshRenderer
                        else if (pending.Contains(source)) later++;
                        else if (referenced.Contains(source) || referenced.Contains(source.transform) || referenced.Contains(renderer) ||
                                 referenced.Contains(filter) || names.Contains(source.name)) named++;
                        else
                        {
                            UnityEngine.Object.DestroyImmediate(renderer, true);
                            UnityEngine.Object.DestroyImmediate(filter, true);
                            stripped++;
                        }
                    }
                foreach (object holder in new object[] { data, combiner })
                    if (holder != null)
                        foreach (string name in Lists)
                        {
                            var list = CrowMeshCombineBake.Get(holder, name) as System.Collections.IList;
                            if (list == null) continue;
                            entries += list.Count;
                            list.Clear();
                        }
            }
        }
        catch (Exception exception)
        {
            failed++;
            Debug.LogError("DOWNGRADE_MCS " + scene.name + " strip: " + exception);
        }
        Debug.Log("DOWNGRADE_MCS " + scene.name + " strip combiners=" + done.Count + " sources=" + sources + " stripped=" + stripped +
                  " later=" + later + " named=" + named + " busy=" + busy + " on=" + on + " entries=" + entries + " failed=" + failed +
                  " ms=" + clock.ElapsedMilliseconds);
    }
}
