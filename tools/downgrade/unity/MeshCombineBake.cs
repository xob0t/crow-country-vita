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

    static string Mode()
    {
        string mode = Environment.GetEnvironmentVariable("CROW_MCS_BAKE");
        if (!string.IsNullOrEmpty(mode)) return mode;
        return EditorUserBuildSettings.activeBuildTarget == BuildTarget.PSP2 ? "on" : "off";
    }

    static object Get(object target, string name)
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
