using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

// Mesh colliders whose scale keeps changing, a box instead (-crow-scaled-colliders; Perf.cs installs it and takes
// its -crow-cycle key, scaled=).
//   -crow-scaled-colliders game|box|report
//       box (the PS Vita's default) gives each mesh collider whose scale keeps changing a box instead; report only
//       logs them (CROW_SCALED); game leaves them
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    // A mesh collider whose transform's scale changes (its own or a parent's) is cooked again from its
    // mesh by PhysX at the next physics step (PhysicsManager::SyncTransforms -> MeshCollider::ScaleChanged
    // -> ExtractMeshGeometry -> PhysX cooking), every frame for one that animates. Station Square's
    // bunting: 18 ProBuilder flags (PrismPentagon, with ProBuilder's default mesh collider) under
    // swinging and stretching parents, about 12 ms of the PS Vita's main thread a frame in
    // FixedUpdate.PhysicsFixedUpdate (with Physics.autoSyncTransforms on, the same time went to the
    // scripts' first physics query instead). Two seconds after each scene load the loaded scenes'
    // non-trigger mesh colliders are listed (on or off), and then and every 15 s after, those that are
    // on are watched for 40 frames (nothing allocated); each whose shape (the world matrix's axis
    // lengths and angles) changed in 10 of them or more (the flags' sway pauses now and then) gets a
    // BoxCollider of its mesh's bounds on the same GameObject (same layer and rigidbody, same physics
    // material) and is switched off. A box takes a scale change as a shape update,
    // without cooking. Left alone: triggers, and colliders of non-kinematic rigidbodies (their shape
    // drives the simulation).
    // Triggers a ScaleAnimationScript scales (1 Nov, DEATHS): an enemy's death pool, `blood sfx surface`
    // (a convex trigger for the blood footstep sound), grows from 0.6 to 1 over 7-10 s, and PhysX cooked its
    // convex hull again at every physics step meanwhile. Station Square's fight after a Grinner's death:
    // physics 20-34 ms a frame (12-17 ms a step) for about 8 s, 1.8 once settled; boxed, 7-12.5 with 7 enemies
    // still fighting (5.5-8.7 without a death). Those triggers, at or
    // below a ScaleAnimationScript's target, get a trigger box of their mesh's bounds when the scene is
    // listed, on or off, before any of them grows (64 in the game's scenes, all enemies' death pools).
    static class ScaledColliders
    {
        const int Frames = 40, Often = 10;  // watched frames; changes that count as animated
        static string mode = "game";
        static bool hooked;
        static float next = -1f;
        static int frame = -1;
        static bool rescan;
        static readonly List<MeshCollider> candidates = new List<MeshCollider>();
        static float[] shapes = new float[0];
        static int[] changes = new int[0];
        static bool[] watching = new bool[0];
        static readonly float[] shape = new float[6];
        static readonly Dictionary<MeshCollider, BoxCollider> swapped = new Dictionary<MeshCollider, BoxCollider>();

        public static void Install(string[] args)
        {
            string value = CrowFlags.Value(args, "-crow-scaled-colliders", "box");
            if (value != null) Set(value);
        }

        internal static void Set(string value)
        {
            mode = value;
            if (mode == "game")
            {
                int restored = 0;
                foreach (KeyValuePair<MeshCollider, BoxCollider> pair in swapped)
                {
                    if (pair.Value != null) UnityEngine.Object.Destroy(pair.Value);
                    if (pair.Key == null) continue;
                    pair.Key.enabled = true;
                    restored++;
                }
                swapped.Clear();
                frame = -1;
                Debug.Log("CROW_SCALED game: " + restored + " mesh colliders back");
                return;
            }
            next = Time.realtimeSinceStartup;
            rescan = true;
            if (hooked) return;
            hooked = true;
            CrowEvents.Loaded("ScaledColliders.sceneLoaded", (scene, loadMode) =>
            {
                next = Time.realtimeSinceStartup + 2f;
                rescan = true;
                frame = -1;
            });
            CrowEvents.BeforeRender("ScaledColliders.Tick", Tick);
        }

        static void Tick()
        {
            if (mode == "game") return;
            if (frame < 0)
            {
                if (next < 0f || Time.realtimeSinceStartup < next) return;
                next = Time.realtimeSinceStartup + 15f;
                if (rescan)
                {
                    List();
                    if (mode == "box") SwapScaledTriggers();
                }
                rescan = false;
                // The candidates that are on now; nothing allocated.
                int on = 0;
                for (int i = 0; i < candidates.Count; i++)
                {
                    MeshCollider collider = candidates[i];
                    Rigidbody body = collider == null ? null : collider.attachedRigidbody;
                    watching[i] = collider != null && collider.enabled && collider.gameObject.activeInHierarchy && !collider.isTrigger &&
                                  !swapped.ContainsKey(collider) && (body == null || body.isKinematic);
                    changes[i] = 0;
                    if (!watching[i]) continue;
                    Shape(collider.transform, i);
                    on++;
                }
                if (on > 0) frame = 0;
                return;
            }
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!watching[i] || candidates[i] == null) continue;
                if (Shape(candidates[i].transform, i)) changes[i]++;
            }
            if (++frame < Frames) return;
            frame = -1;
            List<string> found = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                MeshCollider collider = candidates[i];
                if (!watching[i] || collider == null || changes[i] < Often) continue;
                if (found == null) found = new List<string>();
                // Not one the game switched off meanwhile: its box would collide where nothing should.
                if (mode == "box" && collider.enabled) Swap(collider);
                BoxCollider box;
                Bounds bounds = swapped.TryGetValue(collider, out box) ? box.bounds : collider.bounds;
                if (found.Count < 12)
                    found.Add(Hierarchy.Path(collider.transform, 5) + "(" + collider.sharedMesh.vertexCount + "v" + (collider.convex ? ",convex" : "") + ",layer" +
                              collider.gameObject.layer + ",y" + bounds.min.y.ToString("F2", CultureInfo.InvariantCulture) + ".." +
                              bounds.max.y.ToString("F2", CultureInfo.InvariantCulture) + ")");
            }
            if (found == null) return;
            // The player's body for comparison: the rigidbody in DontDestroyOnLoad that isn't kinematic.
            string player = "";
            foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
                if (!body.isKinematic && body.gameObject.scene.name == Scenes.Kept)
                {
                    var bounds = new Bounds(body.position, Vector3.zero);
                    foreach (Collider collider in body.GetComponentsInChildren<Collider>()) if (collider.attachedRigidbody == body) bounds.Encapsulate(collider.bounds);
                    player = " player=" + Hierarchy.Path(body.transform, 2) + "(y" + bounds.min.y.ToString("F2", CultureInfo.InvariantCulture) + ".." +
                             bounds.max.y.ToString("F2", CultureInfo.InvariantCulture) + ")";
                    break;
                }
            Debug.Log("CROW_SCALED scene=" + SceneManager.GetActiveScene().name.Replace(' ', '_') + " candidates=" + candidates.Count +
                      " animated=" + changes.Count(c => c >= Often) + (mode == "box" ? " boxed" : " (report)") + " total=" + swapped.Count + player +
                      " " + string.Join(" ", found.ToArray()));
        }

        // Once per area load: the loaded scenes' non-trigger mesh colliders, on or off (allocates once).
        static void List()
        {
            foreach (MeshCollider gone in swapped.Keys.Where(c => c == null).ToList()) swapped.Remove(gone);
            candidates.Clear();
            foreach (MeshCollider collider in Resources.FindObjectsOfTypeAll<MeshCollider>())
                if (collider.gameObject.scene.IsValid() && !collider.isTrigger && collider.sharedMesh != null) candidates.Add(collider);
            shapes = new float[candidates.Count * 6];
            changes = new int[candidates.Count];
            watching = new bool[candidates.Count];
        }

        // What a scale change alters and a rotation or move doesn't: the axes' squared lengths and dot
        // products. Stores them for candidate i; whether they changed.
        static bool Shape(Transform transform, int i)
        {
            Matrix4x4 m = transform.localToWorldMatrix;
            Vector3 x = m.GetColumn(0), y = m.GetColumn(1), z = m.GetColumn(2);
            shape[0] = x.sqrMagnitude;
            shape[1] = y.sqrMagnitude;
            shape[2] = z.sqrMagnitude;
            shape[3] = Vector3.Dot(x, y);
            shape[4] = Vector3.Dot(x, z);
            shape[5] = Vector3.Dot(y, z);
            // Relative to the largest axis: small props have world scales of a few hundredths.
            float tolerance = 1e-6f * Mathf.Max(shape[0], Mathf.Max(shape[1], shape[2]));
            bool changed = false;
            for (int k = 0; k < 6; k++)
            {
                if (Mathf.Abs(shape[k] - shapes[i * 6 + k]) > tolerance) changed = true;
                shapes[i * 6 + k] = shape[k];
            }
            return changed;
        }

        static Type scaleAnimation;
        static System.Reflection.FieldInfo targetSelf, target;
        static bool looked;

        // The enabled trigger mesh colliders at or below a ScaleAnimationScript's target, boxed (see above).
        static void SwapScaledTriggers()
        {
            if (!looked)
            {
                looked = true;
                // By its full name in whichever assembly holds it (the rewritten game's isn't named Assembly-CSharp).
                foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (scaleAnimation == null) scaleAnimation = assembly.GetType("Code.com.sfbgames.general.animation.ScaleAnimationScript");
                if (scaleAnimation != null)
                {
                    targetSelf = scaleAnimation.GetField("targetSelf");
                    target = scaleAnimation.GetField("target");
                }
            }
            if (scaleAnimation == null || targetSelf == null || target == null)
            {
                Debug.Log("CROW_SCALED scaled triggers: no ScaleAnimationScript type");
                return;
            }
            int boxed = 0;
            foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(scaleAnimation))
            {
                var script = found as Component;
                if (script == null || !script.gameObject.scene.IsValid()) continue;
                object value = (bool)targetSelf.GetValue(script) ? script.transform : target.GetValue(script);
                Transform root = value is Transform ? (Transform)value : value is GameObject ? ((GameObject)value).transform : null;
                if (root == null) continue;
                foreach (MeshCollider mesh in root.GetComponentsInChildren<MeshCollider>(true))
                {
                    if (!mesh.isTrigger || !mesh.enabled || mesh.sharedMesh == null || swapped.ContainsKey(mesh)) continue;
                    Swap(mesh);
                    boxed++;
                }
            }
            if (boxed > 0) Debug.Log("CROW_SCALED scaled triggers boxed=" + boxed + " total=" + swapped.Count);
        }

        static void Swap(MeshCollider mesh)
        {
            Bounds bounds = mesh.sharedMesh.bounds;
            BoxCollider box = mesh.gameObject.AddComponent<BoxCollider>();
            box.center = bounds.center;
            box.size = Vector3.Max(bounds.size, new Vector3(0.001f, 0.001f, 0.001f));
            box.sharedMaterial = mesh.sharedMaterial;
            box.isTrigger = mesh.isTrigger;
            box.contactOffset = mesh.contactOffset;
            mesh.enabled = false;
            swapped[mesh] = box;
        }
    }
}
