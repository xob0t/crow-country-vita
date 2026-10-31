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
// would collect). From CROW_MCS_BAKE (common.BUILD_SWITCHES): on (the default with the PS Vita's scene
// data) or off.
class CrowMeshCombineBake : UnityEditor.Build.IProcessSceneWithReport
{
    static Type combinerType, managerType;

    public int callbackOrder
    {
        get { return 10; }  // after CrowMergeScenes and the split, which leave a combiner's meshes to it
    }

    static object Get(object target, string name)
    {
        return BuildUtil.Field(target, name);
    }

    static void Set(object target, string name, object value)
    {
        BuildUtil.SetField(target, name, value);
    }

    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (report == null || !BuildPipeline.isBuildingPlayer) return;  // entering play mode
        if (BuildUtil.Switch("CROW_MCS_BAKE") == "off") return;
        if (combinerType == null)
        {
            combinerType = Type.GetType("MeshCombineStudio.MeshCombiner, MeshCombineStudio.Runtime");
            managerType = Type.GetType("MeshCombineStudio.MeshCombineJobManager, MeshCombineStudio.Runtime");
            if (combinerType == null || managerType == null) return;  // a project without MeshCombineStudio
        }
        var combiners = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren(combinerType, true)).Cast<Behaviour>().ToList();
        if (combiners.Count == 0) return;
        CullHidden(scene);
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
            Set(combiner, "combineTime", 0f);  // the combining's stopwatch time, written with the scene: not the same in two builds
            baked++;
            before += (int)Get(combiner, "originalTotalVertices");
            after += (int)Get(combiner, "newTotalVertices");
            var collided = new HashSet<Mesh>(combiner.GetComponentsInChildren<MeshCollider>(true).Select(c => c.sharedMesh).Where(m => m != null));
            foreach (MeshFilter filter in combiner.GetComponentsInChildren<MeshFilter>(true))
            {
                if (existing.Contains(filter) || filter.sharedMesh == null) continue;
                meshes++;
                if (!(bool)makeUnreadable || collided.Contains(filter.sharedMesh)) continue;
                if (BuildUtil.MarkUnreadable(filter.sharedMesh)) unreadable++;
            }
        }
        // The job manager is the combining's helper object (DontSave), with its caches of the sources.
        var instance = managerType.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).GetValue(null) as Component;
        if (instance != null) UnityEngine.Object.DestroyImmediate(instance.gameObject);
        if (baked + later + empty + failed == 0) return;
        Debug.Log("DOWNGRADE_MCS " + scene.name + " baked=" + baked + " later=" + later + " empty=" + empty + " failed=" + failed +
                  " meshes=" + meshes + " unreadable=" + unreadable + " vertices=" + before + "->" + after + " ms=" + clock.ElapsedMilliseconds);
    }

    // Pool's rock floor (31 Oct, POOLROCKS): asset_rules.mesh_hull_overlay makes the rock, placed 1,056 times, a
    // convex hull, and the rocks overlap. Here, before the scene's combine, each placing loses the triangles with
    // every corner more than 1 cm inside one other placing of a hull: closed, convex and opaque, so its inside is
    // never seen. Each placing that loses any gets a copy of the mesh, which the combine then reads. In HiddenAreas,
    // for meshes placed HiddenMinUses times or more (asset_rules.HULL_AREAS, HULL_MIN_USES).
    static readonly string[] HiddenAreas = { "Pool" };
    const int HiddenMinUses = 1000;
    const float Cell = 2f, Inside = 0.01f;

    static void CullHidden(UnityEngine.SceneManagement.Scene scene)
    {
        if (Array.IndexOf(HiddenAreas, scene.name) < 0) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        List<MeshFilter> filters = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MeshFilter>(true)).ToList();
        var uses = new Dictionary<Mesh, int>();
        foreach (MeshFilter filter in filters)
        {
            int n;
            if (filter.sharedMesh != null) uses[filter.sharedMesh] = (uses.TryGetValue(filter.sharedMesh, out n) ? n : 0) + 1;
        }
        List<MeshFilter> placings = filters.Where(f => f.sharedMesh != null && uses[f.sharedMesh] >= HiddenMinUses &&
                                                       f.GetComponent<MeshRenderer>() != null && f.GetComponent<MeshRenderer>().enabled &&
                                                       f.gameObject.activeInHierarchy).ToList();
        if (placings.Count == 0) return;
        // Each repeated mesh's faces as planes (a point, an outward normal), when it is convex.
        var planes = new Dictionary<Mesh, KeyValuePair<Vector3, Vector3>[]>();
        foreach (Mesh mesh in placings.Select(f => f.sharedMesh).Distinct())
        {
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            var faces = new List<KeyValuePair<Vector3, Vector3>>();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                Vector3 a = vertices[triangles[t]];
                Vector3 normal = Vector3.Cross(vertices[triangles[t + 1]] - a, vertices[triangles[t + 2]] - a);
                if (normal.sqrMagnitude > 1e-12f) faces.Add(new KeyValuePair<Vector3, Vector3>(a, normal.normalized));
            }
            bool convex = vertices.All(v => faces.All(f => Vector3.Dot(f.Value, v - f.Key) <= 1e-4f));
            if (convex) planes[mesh] = faces.ToArray();
        }
        // The placings by 2 m cells of x, z.
        var near = new Dictionary<long, List<MeshFilter>>();
        foreach (MeshFilter filter in placings.Where(f => planes.ContainsKey(f.sharedMesh)))
        {
            Bounds bounds = filter.GetComponent<MeshRenderer>().bounds;
            foreach (long key in Cells(bounds.min.x, bounds.max.x, bounds.min.z, bounds.max.z))
            {
                List<MeshFilter> list;
                if (!near.TryGetValue(key, out list)) near[key] = list = new List<MeshFilter>();
                list.Add(filter);
            }
        }
        int cut = 0, before = 0, after = 0, verticesBefore = 0, verticesAfter = 0;
        foreach (MeshFilter filter in placings)
        {
            Mesh mesh = filter.sharedMesh;
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            Matrix4x4 world = filter.transform.localToWorldMatrix;
            Bounds bounds = filter.GetComponent<MeshRenderer>().bounds;
            // The other placings whose bounds meet this one's.
            var others = new HashSet<MeshFilter>();
            foreach (long key in Cells(bounds.min.x, bounds.max.x, bounds.min.z, bounds.max.z))
            {
                List<MeshFilter> list;
                if (near.TryGetValue(key, out list))
                    foreach (MeshFilter other in list)
                        if (other != filter && other.GetComponent<MeshRenderer>().bounds.Intersects(bounds)) others.Add(other);
            }
            var points = vertices.Select(v => world.MultiplyPoint3x4(v)).ToArray();
            // Per vertex, the others it is inside of.
            var within = new List<MeshFilter>[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                within[i] = new List<MeshFilter>();
                foreach (MeshFilter other in others)
                {
                    Vector3 local = other.transform.worldToLocalMatrix.MultiplyPoint3x4(points[i]);
                    // In the other's own space its planes are unscaled; the margin is taken along each normal.
                    if (planes[other.sharedMesh].All(f => Vector3.Dot(f.Value, local - f.Key) < -Inside)) within[i].Add(other);
                }
            }
            var keep = new List<int>(triangles.Length);
            var used = new bool[vertices.Length];
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                if (within[a].Any(o => within[b].Contains(o) && within[c].Contains(o))) continue;
                keep.Add(a);
                keep.Add(b);
                keep.Add(c);
                used[a] = used[b] = used[c] = true;
            }
            before += triangles.Length / 3;
            after += keep.Count / 3;
            int count = used.Count(u => u);
            verticesBefore += vertices.Length;
            verticesAfter += count;
            if (keep.Count == triangles.Length) continue;
            cut++;
            var remap = new int[vertices.Length];
            for (int i = 0, next = 0; i < vertices.Length; i++) remap[i] = used[i] ? next++ : -1;
            var copy = new Mesh { name = mesh.name };
            copy.vertices = vertices.Where((v, i) => used[i]).ToArray();
            Vector3[] normals = mesh.normals;
            if (normals.Length == vertices.Length) copy.normals = normals.Where((v, i) => used[i]).ToArray();
            Vector4[] tangents = mesh.tangents;
            if (tangents.Length == vertices.Length) copy.tangents = tangents.Where((v, i) => used[i]).ToArray();
            Color32[] colors = mesh.colors32;
            if (colors.Length == vertices.Length) copy.colors32 = colors.Where((v, i) => used[i]).ToArray();
            for (int channel = 0; channel < 4; channel++)
            {
                var uv = new List<Vector4>();
                mesh.GetUVs(channel, uv);
                if (uv.Count == vertices.Length) copy.SetUVs(channel, uv.Where((v, i) => used[i]).ToList());
            }
            copy.triangles = keep.Select(i => remap[i]).ToArray();
            copy.RecalculateBounds();
            filter.sharedMesh = copy;
        }
        Debug.Log("DOWNGRADE_MCS " + scene.name + " hidden: placings=" + placings.Count + " convex=" + planes.Count + " cut=" + cut +
                  " triangles=" + before + "->" + after + " vertices=" + verticesBefore + "->" + verticesAfter + " ms=" + clock.ElapsedMilliseconds);
    }

    static IEnumerable<long> Cells(float x0, float x1, float z0, float z1)
    {
        for (int x = Mathf.FloorToInt(x0 / Cell); x <= Mathf.FloorToInt(x1 / Cell); x++)
            for (int z = Mathf.FloorToInt(z0 / Cell); z <= Mathf.FloorToInt(z1 / Cell); z++)
                yield return ((long)x << 32) ^ (uint)z;
    }
}

// What MeshCombineStudio's combining leaves in the scenes the PS Vita build writes. A combiner
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
// default as CROW_MCS_BAKE; common.BUILD_SWITCHES).
class CrowMeshCombineStrip : UnityEditor.Build.IProcessSceneWithReport
{
    static readonly string[] Lists = { "foundObjects", "foundLodObjects", "foundLodGroups", "foundColliders" };

    public int callbackOrder
    {
        get { return 30; }  // after CrowStripScenes (20), which removes the inert scripts some sources carry
    }

    static IEnumerable<GameObject> SearchParents(object combiner)
    {
        object options = BuildUtil.Field(combiner, "searchOptions");
        if (options == null) yield break;
        var parent = BuildUtil.Field(options, "parent") as GameObject;
        if (parent != null) yield return parent;
        var parents = BuildUtil.Field(options, "parentGOs") as GameObject[];
        if (parents == null) yield break;
        foreach (GameObject one in parents)
            if (one != null) yield return one;
    }

    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (report == null || !BuildPipeline.isBuildingPlayer) return;  // entering play mode
        if (BuildUtil.Switch("CROW_MCS_STRIP") != "on") return;
        Type combinerType = Type.GetType("MeshCombineStudio.MeshCombiner, MeshCombineStudio.Runtime");
        if (combinerType == null) return;  // a project without MeshCombineStudio
        var combiners = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren(combinerType, true)).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var done = new List<Component>();
        var pending = new HashSet<GameObject>();  // under a run-time combiner's search parents
        foreach (Component combiner in combiners)
        {
            if ((bool)BuildUtil.Field(combiner, "combineInRuntime"))
            {
                foreach (GameObject parent in SearchParents(combiner))
                    foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
                        pending.Add(t.gameObject);
            }
            else if ((bool)BuildUtil.Field(combiner, "combined")) done.Add(combiner);
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
                    BuildUtil.Walk(behaviour, referenced, names, true, walked);  // with a path's last part
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
                var data = BuildUtil.Field(combiner, "data") as Component;
                if (data == null) data = combiner.GetComponent("MeshCombinerData");
                var found = data == null ? null : BuildUtil.Field(data, "foundObjects") as System.Collections.IList;
                if (found != null)
                    foreach (object cached in found)
                    {
                        var renderer = BuildUtil.Field(cached, "mr") as MeshRenderer;
                        var filter = BuildUtil.Field(cached, "mf") as MeshFilter;
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
                            var list = BuildUtil.Field(holder, name) as System.Collections.IList;
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

// MeshCombineStudio's GarbageCollectMesh destroys its mesh when its object is destroyed. In the game the
// cells a combiner made in the editor hold their meshes inside the scene, so every load of the scene
// has meshes of its own and the unload's destroy frees them. The export saved those meshes as assets
// of their own, which every load of the scene shares: the scene's first unload destroyed them for the
// rest of the session, and from the next load on those cells drew nothing (Underground NW's walkways,
// floors and walls pitch black from the second visit on, through the PS Vita's area loads; 53 scenes
// have such cells, 2,994 of them; 28 Oct). Such a GarbageCollectMesh is written with no mesh, so the
// meshes go with the scene's assets as any other asset does; those of meshes the scene holds itself
// (the cells CrowMeshCombineBake combines, and those combined in play) keep theirs.
class CrowGarbageCollectMeshes : UnityEditor.Build.IProcessSceneWithReport
{
    public int callbackOrder
    {
        get { return 11; }  // after CrowMeshCombineBake (10), whose cells hold meshes of the scene's own
    }

    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (report == null || !BuildPipeline.isBuildingPlayer) return;  // entering play mode
        Type collector = Type.GetType("MeshCombineStudio.GarbageCollectMesh, MeshCombineStudio.Runtime");
        if (collector == null) return;  // a project without MeshCombineStudio
        int cleared = 0, kept = 0, failed = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Component component in root.GetComponentsInChildren(collector, true))
            {
                try
                {
                    var mesh = BuildUtil.Field(component, "mesh") as Mesh;
                    if (mesh == null) continue;
                    if (!AssetDatabase.Contains(mesh))
                    {
                        kept++;
                        continue;
                    }
                    BuildUtil.SetField(component, "mesh", null);
                    cleared++;
                }
                catch (Exception exception)
                {
                    failed++;
                    Debug.LogError("DOWNGRADE_MCS " + scene.name + " garbage meshes: " + exception);
                }
            }
        if (cleared + kept + failed > 0)
            Debug.Log("DOWNGRADE_MCS " + scene.name + " garbage meshes cleared=" + cleared + " kept=" + kept + " failed=" + failed);
    }
}
