using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using GCHandle = System.Runtime.InteropServices.GCHandle;

// MeshCombineStudio's caches freed once its combiners are done (MeshRelease; on by default on the PS Vita):
//   -crow-keep-mesh-caches  keep them on the PS Vita
//   -crow-release-meshes    free them on other players too
// Memory.Install installs it.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    // MeshCombineStudio combines the meshes under its MeshCombiners' search parents when a scene
    // starts, into new meshes it makes unreadable. It reads each source mesh (which must be readable,
    // so it keeps a CPU copy of its vertices and indices) into a managed MeshCache, and keeps those
    // caches, and the sources' CPU copies, for as long as the scene is loaded: nothing clears
    // MeshCombineJobManager.meshCacheDictionary. On the PS Vita, once every MeshCombiner has combined
    // and no job is left, MeshRelease clears the caches and makes the cached source meshes unreadable
    // (UploadMeshData(true) frees the CPU copy; the GPU's stays, so the original renderers draw as
    // before), except meshes a MeshCollider or a particle system also uses, and meshes under the search
    // parents of a run-time MeshCombiner that hasn't combined yet, active or not: most areas have
    // several, switched on later (Station Square 14, Gift Shop 7), and a combiner that meets a source
    // mesh made unreadable skips it with an error and leaves its renderer as it was, a draw call of
    // its own (Gift Shop's "Cube" and "gums"). Those are released at the check after they combine.
    // It also drops MeshCombineStudio's pooled work buffers (about 7 MB, ReleaseBuffers).
    // Off with -crow-keep-mesh-caches; on other players with -crow-release-meshes.
    public class MeshRelease : MonoBehaviour
    {
        static Type combinerType, managerType;
        static FieldInfo instancesField, inRuntimeField, combinedField, combinerJobsField, managerField, cacheField, managerJobsField;
        static FieldInfo searchOptionsField, parentField, parentsField;
        static FieldInfo poolField, tempCacheField, weldField;
        // Unity's built-in primitive meshes, which every scene shares and no scene unload frees: made
        // unreadable in one area, they stayed so in the next (Roadside then Station Square: a
        // MeshCollider on a scaled Quad stopped working, and MeshCombineStudio skipped a Plane).
        static readonly HashSet<string> Builtin = new HashSet<string> { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" };
        static int released;
        static long releasedBytes;
        float nextCheck;

        internal static void Install(string[] args)
        {
            bool on = Application.platform == RuntimePlatform.PSP2 ? !CrowFlags.Has(args, "-crow-keep-mesh-caches") : CrowFlags.Has(args, "-crow-release-meshes");
            combinerType = GameTypes.MeshCombiner;
            managerType = Type.GetType("MeshCombineStudio.MeshCombineJobManager, MeshCombineStudio.Runtime");
            if (combinerType == null || managerType == null) return;
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic;
            instancesField = combinerType.GetField("instances", any | BindingFlags.Static);
            inRuntimeField = combinerType.GetField("combineInRuntime", any | BindingFlags.Instance);
            combinedField = combinerType.GetField("combined", any | BindingFlags.Instance);
            combinerJobsField = combinerType.GetField("meshCombineJobs", any | BindingFlags.Instance);
            managerField = managerType.GetField("instance", any | BindingFlags.Static);
            cacheField = managerType.GetField("meshCacheDictionary", any | BindingFlags.Instance);
            managerJobsField = managerType.GetField("meshCombineJobs", any | BindingFlags.Instance);
            poolField = managerType.GetField("newMeshObjectsPool", any | BindingFlags.Instance);
            tempCacheField = managerType.GetField("tempMeshCache", any | BindingFlags.Instance);
            Type newMeshObject = managerType.GetNestedType("NewMeshObject", any);
            weldField = newMeshObject == null ? null : newMeshObject.GetField("weldVertices", any | BindingFlags.Static);
            searchOptionsField = combinerType.GetField("searchOptions", any | BindingFlags.Instance);
            if (searchOptionsField != null)
            {
                parentField = searchOptionsField.FieldType.GetField("parent", any | BindingFlags.Instance);
                parentsField = searchOptionsField.FieldType.GetField("parentGOs", any | BindingFlags.Instance);
            }
            if (instancesField == null || inRuntimeField == null || combinedField == null || managerField == null || cacheField == null)
            {
                Debug.Log("MeshRelease: MeshCombineStudio's fields not found");
                combinerType = null;
                return;
            }
            if (!on) return;
            var holder = new GameObject("CrowMeshRelease");
            DontDestroyOnLoad(holder);
            holder.AddComponent<MeshRelease>();
        }

        static int Count(object collection)
        {
            var items = collection as System.Collections.ICollection;
            return items == null ? 0 : items.Count;
        }

        static System.Collections.IDictionary Cache()
        {
            object manager = managerField.GetValue(null);
            return manager == null ? null : cacheField.GetValue(manager) as System.Collections.IDictionary;
        }

        // Whether every active run-time MeshCombiner has combined and no job is left.
        static bool Idle(out int combiners, out int combined)
        {
            combiners = combined = 0;
            object manager = managerField.GetValue(null);
            bool idle = manager != null && (managerJobsField == null || Count(managerJobsField.GetValue(manager)) == 0);
            var instances = instancesField.GetValue(null) as System.Collections.IList;
            if (instances == null) return idle;
            foreach (object instance in instances)
            {
                var behaviour = instance as Behaviour;
                if (behaviour == null || !behaviour.isActiveAndEnabled || !(bool)inRuntimeField.GetValue(instance)) continue;
                combiners++;
                if ((bool)combinedField.GetValue(instance)) combined++;
                else idle = false;
                if (combinerJobsField != null && Count(combinerJobsField.GetValue(instance)) > 0) idle = false;
            }
            return idle;
        }

        // For the probe's MEMTYPES line: combiners done of active ones, cached meshes, meshes released.
        internal static string Report()
        {
            if (combinerType == null) return "";
            int combiners, combined;
            Idle(out combiners, out combined);
            System.Collections.IDictionary cache = Cache();
            return " mcs=" + combined + "/" + combiners + ",cache" + (cache == null ? 0 : cache.Count) +
                   ",released" + released + "," + (releasedBytes / 1048576f).ToString("0.0", CultureInfo.InvariantCulture) + "MB";
        }

        void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            int combiners, combined;
            System.Collections.IDictionary cache = Cache();
            if (cache == null || cache.Count == 0 || !Idle(out combiners, out combined)) return;
            var keep = new HashSet<Mesh>();
            // Called directly (CrowCompat references the physics module), so the linker keeps the getter.
            foreach (MeshCollider collider in Resources.FindObjectsOfTypeAll<MeshCollider>())
                if (collider.sharedMesh != null) keep.Add(collider.sharedMesh);
            var meshes = new Mesh[8];
            foreach (ParticleSystemRenderer renderer in Resources.FindObjectsOfTypeAll<ParticleSystemRenderer>())
            {
                int count = Math.Min(renderer.GetMeshes(meshes), meshes.Length);
                for (int i = 0; i < count; i++) if (meshes[i] != null) keep.Add(meshes[i]);
            }
            foreach (ParticleSystem system in Resources.FindObjectsOfTypeAll<ParticleSystem>())
            {
                Mesh mesh = system.shape.mesh;
                if (mesh != null) keep.Add(mesh);
            }
            int pending = KeepPending(keep);
            int now = 0;
            long bytes = 0;
            foreach (object key in cache.Keys)
            {
                var mesh = key as Mesh;
                if (mesh == null || !mesh.isReadable || keep.Contains(mesh) || Builtin.Contains(mesh.name)) continue;
                bytes += Memory.MeshBytes(mesh);
                mesh.UploadMeshData(true);
                now++;
            }
            int entries = cache.Count;
            cache.Clear();
            released += now;
            releasedBytes += bytes;
            int buffers = ReleaseBuffers();
            Debug.Log("MeshRelease: " + combined + " of " + combiners + " MeshCombiners done; cleared " + entries +
                      " mesh caches, made " + now + " source meshes unreadable (about " +
                      (bytes / 1048576f).ToString("0.0", CultureInfo.InvariantCulture) + " MB), dropped " + buffers +
                      " combine buffers; " + pending + " MeshCombiners still to combine keep theirs");
        }

        // MeshCombineStudio's work buffers, which it keeps for its next combine: each pooled NewMeshObject
        // has arrays for a whole 65,534-vertex mesh (vertices, 786,408 triangle indices, normals, tangents,
        // uvs, colours: about 7 MB of managed heap), and the job manager a vertex copy and the welder a
        // vertex list. Dropped while no job runs; MeshCombineStudio makes new ones when a combiner that
        // switches on later combines. Returns how many pooled objects went.
        static int ReleaseBuffers()
        {
            object manager = managerField.GetValue(null);
            if (manager == null) return 0;
            int dropped = 0;
            object pool = poolField == null ? null : poolField.GetValue(manager);
            if (pool != null)
            {
                lock (pool)
                {
                    Type list = pool.GetType();
                    FieldInfo items = FieldOf(list, "items"), count = FieldOf(list, "Count"), innerCount = FieldOf(list, "_count");
                    var array = items == null ? null : items.GetValue(pool) as Array;
                    if (array != null && count != null && innerCount != null)
                    {
                        dropped = (int)count.GetValue(pool);
                        Array.Clear(array, 0, array.Length);
                        count.SetValue(pool, 0);
                        innerCount.SetValue(pool, 0);
                    }
                }
            }
            if (tempCacheField != null) tempCacheField.SetValue(manager, null);
            if (weldField != null) weldField.SetValue(null, null);
            return dropped;
        }

        // An instance field declared by the type or a base type (FastList's are in two generations of bases).
        static FieldInfo FieldOf(Type type, string name)
        {
            const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo found = t.GetField(name, declared);
                if (found != null) return found;
            }
            return null;
        }

        // Adds the meshes under the search parents of every run-time MeshCombiner in a loaded scene that
        // hasn't combined, active or not, inactive children included (MeshCombiner searches with
        // GetComponentsInChildren(true)); its tag, layer and other conditions are left out, so a few
        // more stay readable than it takes. Returns how many such combiners there are.
        static int KeepPending(HashSet<Mesh> keep)
        {
            if (searchOptionsField == null || (parentField == null && parentsField == null)) return 0;
            int pending = 0;
            foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(combinerType))
            {
                var combiner = found as Component;
                if (combiner == null || !combiner.gameObject.scene.IsValid()) continue;  // a prefab asset
                if (!(bool)inRuntimeField.GetValue(combiner) || (bool)combinedField.GetValue(combiner)) continue;
                pending++;
                object options = searchOptionsField.GetValue(combiner);
                if (options == null) continue;
                var parents = new List<GameObject>();
                var many = parentsField == null ? null : parentsField.GetValue(options) as GameObject[];
                if (many != null) parents.AddRange(many);
                var one = parentField == null ? null : parentField.GetValue(options) as GameObject;
                if (one != null) parents.Add(one);
                foreach (GameObject parent in parents)
                {
                    if (parent == null) continue;
                    foreach (MeshFilter filter in parent.GetComponentsInChildren<MeshFilter>(true))
                        if (filter.sharedMesh != null) keep.Add(filter.sharedMesh);
                }
            }
            return pending;
        }
    }
}
