using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

// The player's ProBuilder meshes, built before they are first shown. The player prefab has 839
// ProBuilderMesh components, and the export kept none of their meshes (the game's build has them),
// so each builds its mesh from its shape in its Awake (ProBuilderMesh.Rebuild: vertices, UVs, hard
// normals, tangents), which runs the first time its GameObject is active. Most are in hierarchies
// that start inactive: the inventory's 3D items (421 meshes, 50,000 positions, under Inventory
// Screenspace OFF), the examine modes' items (ItemAction 212, Use Item 133), the guns, Mara's outfits.
// On the PS Vita the first inventory open (L) took 791-816 ms, the next ones 167-171 ms; a stall dump
// in it was in ProBuilderMesh.Awake's normals. Here, during an area load's empty scene (SceneGap, a
// black screen), each ProBuilderMesh under the inventory's 3D items (Groups) that is not active and
// has no mesh runs its Awake. Unity calls Awake again when the object first activates, which then
// finds the mesh and only assigns the filter and collider again, as for a mesh the game's build kept.
// Only the inventory's: every player opens it, and its meshes stay built once it has opened. All 839
// took 4.3 s and 10 MB of main memory (129,000 vertices); the other groups are small, or rarely seen,
// and build a few meshes at a time. The PS Vita build now builds the meshes itself and leaves the
// ProBuilderMesh components out (unity/ProBuilderMeshes.cs), so there is nothing left to build and
// this is off: -crow-prewarm on, for builds made with CROW_PROBUILDER=off; all builds every player mesh.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Prewarm
    {
        // The hierarchies whose meshes are built ahead (a GameObject of this name above the mesh).
        static readonly string[] Groups = { "Inventory Screenspace OFF" };
        static bool installed, on, all;
        static Type proBuilderMesh;
        static MethodInfo awake;

        static bool On
        {
            get
            {
                if (installed) return on;
                installed = true;
                string value = Perf.Value(ProbeFiles.Args, "-crow-prewarm");
                on = value == "on" || value == "all";
                all = value == "all";
                proBuilderMesh = Type.GetType("UnityEngine.ProBuilder.ProBuilderMesh, Unity.ProBuilder");
                if (proBuilderMesh != null)
                    awake = proBuilderMesh.GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (on && awake == null) Debug.Log("CROW_PREWARM no ProBuilderMesh.Awake");
                on = on && awake != null;
                return on;
            }
        }

        // From SceneGap, with the old area gone and before its collection (ProBuilder's working arrays
        // go with it). Cheap when there is nothing left to build (the player's meshes stay built).
        internal static void ProBuilder()
        {
            if (!On) return;
            var clock = Stopwatch.StartNew();
            int built = 0, vertices = 0, failed = 0;
            foreach (Component component in Resources.FindObjectsOfTypeAll(proBuilderMesh).OfType<Component>())
            {
                GameObject owner = component.gameObject;
                if (!owner.scene.IsValid() || owner.activeInHierarchy) continue;  // assets, or awake already
                MeshFilter filter = owner.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null) continue;
                if (!all && !InGroup(owner.transform)) continue;
                try
                {
                    awake.Invoke(component, null);
                }
                catch (Exception e)
                {
                    if (failed++ == 0) Debug.Log("CROW_PREWARM " + owner.name + ": " + (e.InnerException ?? e).GetType().Name + " " + (e.InnerException ?? e).Message);
                    continue;
                }
                built++;
                if (filter == null) filter = owner.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null) vertices += filter.sharedMesh.vertexCount;
            }
            if (built > 0 || failed > 0)
                Debug.Log("CROW_PREWARM built " + built + " ProBuilder meshes (" + vertices + " vertices" + (failed > 0 ? ", " + failed + " failed" : "") +
                          ") in " + clock.ElapsedMilliseconds + " ms");
        }

        static bool InGroup(Transform transform)
        {
            for (Transform t = transform; t != null; t = t.parent)
                if (Array.IndexOf(Groups, t.name) >= 0) return true;
            return false;
        }
    }
}
