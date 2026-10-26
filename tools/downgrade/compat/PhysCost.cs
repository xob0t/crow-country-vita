using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// What a physics step works on in a fight (measurements only; results in docs/status-log.md, Enemy physics, 19 Oct).
//   -crow-phys-census <s>   s seconds after each area load (and with the cycle key pcensus=<frames>), over 10
//                           frames: every enabled collider by owner (an enemy type, the player, a level root),
//                           static (no rigidbody), on a kinematic or a dynamic body, a body's own or a compound
//                           child, trigger or solid, layer and shape; how many moved a frame (world pose) and
//                           how many moved against their body (a compound child's local pose, which PhysX gets
//                           as a shape change); the pairs PhysX keeps for them (bounds that overlap, layers that
//                           collide, at least one side on a body, not kinematic against static or kinematic
//                           unless one is a trigger; trigger against trigger counted as a trigger pair), by kind
//                           (contact, trigger) and what the other side is (CROW_PHYS_OWNER, CROW_PHYS_PAIRS, CROW_PHYS_BODIES, CROW_PHYS_STATIC_MOVED: static
//                           colliders that moved, each a remove and insert in PhysX 3.3; CROW_PHYS_LAYERS)
//   -crow-phys-events       every enemy's and the player's colliders and bodies get a listener (2 s after each
//                           load, and every 5 s for new ones) that counts the trigger and collision enters and
//                           exits each receives, by receiver (owner/object) and other side (owner/object, tag);
//                           every 10 s the counts (CROW_PHYS_EVENTS; Windows test players only: the listener is a
//                           MonoBehaviour the PS Vita build's data doesn't know; it turns on contact reports)
// The cycle key pcensus=<frames> (-crow-cycle / -crow-fight-cycle / -crow-ab) takes the census now. The experiments
// that took parts away (psolid=, pnone=, pstatic=, ptrigstatic=) are done and gone (status-log.md, Enemy physics).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    static class PhysCost
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static float censusAfter = -1f, censusAt = -1f;
        static bool hooked;

        // From Diag.Install (every player, flags or not).
        internal static void Install(string[] args)
        {
            censusAfter = CrowFlags.Number(args, "-crow-phys-census", -1f);
#if !CROW_VITA
            if (CrowFlags.Has(args, "-crow-phys-events")) PhysEvents.Install();
#endif
            if (censusAfter < 0f) return;
            Hook();
            CrowEvents.Loaded("PhysCost.sceneLoaded", (scene, mode) =>
            {
                if (scene.name != MaraScene.Scene) censusAt = Time.realtimeSinceStartup + censusAfter;
            });
        }

        static void Hook()
        {
            if (hooked) return;
            hooked = true;
            CrowEvents.Canvases("PhysCost.Frame", Frame);
        }

        // A -crow-cycle / -crow-fight-cycle / -crow-ab change (Quality.Change); false when it isn't one.
        internal static bool Change(string key, string value)
        {
            switch (key)
            {
                case "pcensus":
                    int frames;
                    if (!int.TryParse(value, NumberStyles.Integer, Inv, out frames) || frames < 1) frames = 10;
                    Hook();
                    StartCensus(frames);
                    return true;
            }
            return false;
        }

        // The static colliders the last census saw move (CROW_PHYS_STATIC_MOVED).
        static readonly List<Collider> staticMoved = new List<Collider>();

        // ---------------------------------------------------------------- census

        class Entry
        {
            public Collider Collider;
            public string Owner;
            public int Body;  // 0 static, 1 kinematic, 2 dynamic
            public bool Own, Trigger;
            public Matrix4x4 World, Local;
            public int Moved, LocalMoved;
        }

        static List<Entry> entries;
        static int censusFrames, censusLeft;

        static void StartCensus(int frames)
        {
            censusFrames = censusLeft = frames;
            entries = null;
        }

        static void Frame()
        {
            if (censusAt >= 0f && Time.realtimeSinceStartup >= censusAt)
            {
                censusAt = -1f;
                StartCensus(10);
            }
            if (censusLeft <= 0) return;
            try
            {
                if (entries == null)
                {
                    Begin();
                    return;
                }
                foreach (Entry entry in entries)
                {
                    if (entry.Collider == null) continue;
                    Matrix4x4 world = entry.Collider.transform.localToWorldMatrix;
                    if (world != entry.World) entry.Moved++;
                    entry.World = world;
                    Rigidbody body = entry.Collider.attachedRigidbody;
                    if (body == null || entry.Own) continue;
                    Matrix4x4 local = body.transform.worldToLocalMatrix * world;
                    if (local != entry.Local) entry.LocalMoved++;
                    entry.Local = local;
                }
                if (--censusLeft == 0) Report();
            }
            catch (Exception exception)
            {
                censusLeft = 0;
                Debug.Log("CROW_PHYS census failed: " + exception);
            }
        }

        static GameObject player;

        internal static string OwnerOf(Transform t)
        {
            if (player == null) player = MaraScene.FindPlayer();
            return OwnerOfKnown(t);
        }

        static string OwnerOfKnown(Transform t)
        {
            if (player != null && t.gameObject.scene == player.scene) return "player";
            for (Transform p = t; p != null; p = p.parent)
                if (EnemyNames.IsRoot(p)) return "enemy:" + EnemyNames.TypeOf(p).Replace(' ', '_');
            Transform root = t.root;
            return "level:" + (root.name.Length > 24 ? root.name.Substring(0, 24) : root.name).Replace(' ', '_');
        }

        static void Begin()
        {
            player = MaraScene.FindPlayer();
            entries = new List<Entry>();
            foreach (Collider collider in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                if (!collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                Rigidbody body = collider.attachedRigidbody;
                var entry = new Entry
                {
                    Collider = collider,
                    Owner = OwnerOfKnown(collider.transform),
                    Body = body == null ? 0 : body.isKinematic ? 1 : 2,
                    Own = body != null && body.transform == collider.transform,
                    Trigger = collider.isTrigger,
                    World = collider.transform.localToWorldMatrix
                };
                if (body != null && !entry.Own) entry.Local = body.transform.worldToLocalMatrix * entry.World;
                entries.Add(entry);
            }
        }

        static readonly string[] BodyNames = { "static", "kin", "dyn" };

        static string Shape(Collider collider)
        {
            var mesh = collider as MeshCollider;
            if (mesh != null) return mesh.convex ? "convex" : "mesh";
            return collider.GetType().Name.Replace("Collider", "").ToLowerInvariant();
        }

        static void Report()
        {
            entries.RemoveAll(e => e.Collider == null);
            float frames = censusFrames - 1;
            string scene = SceneManager.GetActiveScene().name.Replace(' ', '_');
            // Per owner: colliders by body kind, triggers, moved and locally moved a frame, shapes.
            foreach (IGrouping<string, Entry> group in entries.GroupBy(e => e.Owner).OrderByDescending(g => g.Sum(e => e.Moved)).ThenByDescending(g => g.Count()))
            {
                if (group.Key.StartsWith("level:", StringComparison.Ordinal) && group.All(e => e.Moved == 0)) continue;
                var line = new StringBuilder("CROW_PHYS_OWNER scene=" + scene + " " + group.Key + " n=" + group.Count());
                for (int kind = 0; kind < 3; kind++)
                {
                    List<Entry> part = group.Where(e => e.Body == kind).ToList();
                    if (part.Count == 0) continue;
                    line.Append(' ').Append(BodyNames[kind]).Append('=').Append(part.Count)
                        .Append("(own ").Append(part.Count(e => e.Own)).Append(" trig ").Append(part.Count(e => e.Trigger))
                        .Append(" moved ").Append((part.Sum(e => e.Moved) / frames).ToString("0.#", Inv))
                        .Append(" local ").Append((part.Sum(e => e.LocalMoved) / frames).ToString("0.#", Inv)).Append(')');
                }
                line.Append(" shapes=").Append(string.Join(",", group.GroupBy(e => Shape(e.Collider) + (e.Trigger ? "T" : ""))
                    .OrderByDescending(g => g.Count()).Select(g => g.Key + ":" + g.Count()).ToArray()));
                line.Append(" layers=").Append(string.Join(",", group.GroupBy(e => e.Collider.gameObject.layer)
                    .OrderByDescending(g => g.Count()).Select(g => LayerMask.LayerToName(g.Key).Replace(' ', '_') + ":" + g.Count()).ToArray()));
                Debug.Log(line.ToString());
            }
            staticMoved.Clear();
            foreach (Entry entry in entries.Where(e => e.Body == 0 && e.Moved > 0).OrderByDescending(e => e.Moved))
            {
                staticMoved.Add(entry.Collider);
                if (staticMoved.Count <= 20)
                    Debug.Log("CROW_PHYS_STATIC_MOVED " + entry.Owner + " frames=" + entry.Moved + "/" + frames + " " + Shape(entry.Collider) +
                              (entry.Trigger ? "T" : "") + " layer=" + LayerMask.LayerToName(entry.Collider.gameObject.layer).Replace(' ', '_') + " path=" + Hierarchy.Path(entry.Collider.transform, 5));
            }
            Debug.Log("CROW_PHYS_CENSUS scene=" + scene + " frames=" + frames + " colliders=" + entries.Count +
                      " static=" + entries.Count(e => e.Body == 0) + " kin=" + entries.Count(e => e.Body == 1) + " dyn=" + entries.Count(e => e.Body == 2) +
                      " triggers=" + entries.Count(e => e.Trigger) + " moved/frame=" + (entries.Sum(e => e.Moved) / frames).ToString("0.#", Inv) +
                      " local/frame=" + (entries.Sum(e => e.LocalMoved) / frames).ToString("0.#", Inv) + " staticMoved=" + staticMoved.Count +
                      " (static " + (entries.Where(e => e.Body == 0).Sum(e => e.Moved) / frames).ToString("0.#", Inv) + "/frame)");
            Pairs();
            Bodies();
            Layers();
        }

        // The pairs PhysX keeps: bounds overlapping (contact offsets aside), layers colliding, one side on a body.
        // Kinematic against static or kinematic makes a pair only when one is a trigger.
        static void Pairs()
        {
            var bounds = entries.Select(e => e.Collider.bounds).ToArray();
            var counts = new Dictionary<string, int[]>();  // owner -> contact, trigger, contact vs mesh, trigger vs mesh
            var others = new Dictionary<string, Dictionary<string, int>>();
            for (int i = 0; i < entries.Count; i++)
            {
                Entry a = entries[i];
                if (a.Body == 0) continue;
                for (int j = 0; j < entries.Count; j++)
                {
                    if (i == j) continue;
                    Entry b = entries[j];
                    // Each pair once: a body side against a static, or the lower index of two body sides.
                    if (b.Body != 0 && j < i) continue;
                    if (a.Collider.attachedRigidbody == b.Collider.attachedRigidbody) continue;
                    if (!bounds[i].Intersects(bounds[j])) continue;
                    if (Physics.GetIgnoreLayerCollision(a.Collider.gameObject.layer, b.Collider.gameObject.layer)) continue;
                    bool trigger = a.Trigger || b.Trigger;
                    if (!trigger && a.Body == 1 && b.Body != 2) continue;
                    bool mesh = b.Collider is MeshCollider && !((MeshCollider)b.Collider).convex;
                    foreach (string owner in new[] { a.Owner, b.Body != 0 ? b.Owner : null })
                    {
                        if (owner == null) continue;
                        int[] c;
                        if (!counts.TryGetValue(owner, out c)) counts[owner] = c = new int[4];
                        c[(trigger ? 1 : 0) + (mesh ? 2 : 0)]++;
                    }
                    string side = (trigger ? "T:" : "C:") + (b.Body == 0 ? "static" : b.Owner == a.Owner ? "same" : b.Owner) +
                                  (mesh ? "/mesh" : "") + (b.Body == 0 ? "/" + Hierarchy.Path(b.Collider.transform, 2).Replace(' ', '_') : "");
                    Dictionary<string, int> list;
                    if (!others.TryGetValue(a.Owner, out list)) others[a.Owner] = list = new Dictionary<string, int>();
                    int n;
                    list.TryGetValue(side, out n);
                    list[side] = n + 1;
                }
            }
            foreach (KeyValuePair<string, int[]> pair in counts.OrderByDescending(p => p.Value.Sum()))
            {
                int[] c = pair.Value;
                Dictionary<string, int> list;
                others.TryGetValue(pair.Key, out list);
                Debug.Log("CROW_PHYS_PAIRS " + pair.Key + " contact=" + (c[0] + c[2]) + "(mesh " + c[2] + ") trigger=" + (c[1] + c[3]) + "(mesh " + c[3] + ")" +
                          (list == null ? "" : " with " + string.Join(" ", list.OrderByDescending(p => p.Value).Take(12).Select(p => p.Key + "=" + p.Value).ToArray())));
            }
        }

        static void Bodies()
        {
            foreach (IGrouping<string, Rigidbody> group in UnityEngine.Object.FindObjectsOfType<Rigidbody>().Where(b => b.gameObject.activeInHierarchy)
                         .GroupBy(b => OwnerOfKnown(b.transform)).OrderByDescending(g => g.Count()))
            {
                Debug.Log("CROW_PHYS_BODIES " + group.Key + " n=" + group.Count() + " kin=" + group.Count(b => b.isKinematic) +
                          " dynAwake=" + group.Count(b => !b.isKinematic && !b.IsSleeping()) + " dynSleeping=" + group.Count(b => !b.isKinematic && b.IsSleeping()) +
                          " ccd=" + group.Count(b => b.collisionDetectionMode != CollisionDetectionMode.Discrete) +
                          " interp=" + group.Count(b => b.interpolation != RigidbodyInterpolation.None) +
                          " names=" + string.Join(",", group.GroupBy(b => b.name.Replace(' ', '_')).Select(g => g.Key + (g.Count() > 1 ? "x" + g.Count() : "")).Take(8).ToArray()));
            }
        }

        static void Layers()
        {
            var used = new SortedSet<int>(entries.Select(e => e.Collider.gameObject.layer));
            var line = new StringBuilder("CROW_PHYS_LAYERS");
            foreach (int a in used)
            {
                line.Append(' ').Append(LayerMask.LayerToName(a).Replace(' ', '_')).Append('(').Append(entries.Count(e => e.Collider.gameObject.layer == a)).Append("):");
                line.Append(string.Join(",", used.Where(b => !Physics.GetIgnoreLayerCollision(a, b)).Select(b => LayerMask.LayerToName(b).Replace(' ', '_')).ToArray()));
            }
            Debug.Log(line.ToString());
        }
    }

#if !CROW_VITA
    // (Windows test players only: a MonoBehaviour the PS Vita build's data doesn't know can't be added there.)
    // -crow-phys-events: the trigger and collision messages the enemies' and the player's colliders and bodies get.
    class PhysEvents : MonoBehaviour
    {
        static readonly Dictionary<string, int> counts = new Dictionary<string, int>();
        static float nextScan = -1f, nextReport;

        internal static void Install()
        {
            CrowEvents.Loaded("PhysEvents.sceneLoaded", (scene, mode) => nextScan = Time.realtimeSinceStartup + 2f);
            CrowEvents.Canvases("PhysEvents.Tick", Tick);
            Debug.Log("CROW_PHYS events on");
        }

        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (nextScan >= 0f && now >= nextScan)
            {
                nextScan = now + 5f;
                int added = 0;
                foreach (Collider collider in UnityEngine.Object.FindObjectsOfType<Collider>())
                {
                    string owner = PhysCost.OwnerOf(collider.transform);
                    if (owner.StartsWith("level:", StringComparison.Ordinal)) continue;
                    foreach (GameObject target in new[] { collider.gameObject, collider.attachedRigidbody != null ? collider.attachedRigidbody.gameObject : null })
                        if (target != null && target.GetComponent<PhysEvents>() == null)
                        {
                            target.AddComponent<PhysEvents>();
                            added++;
                        }
                }
                if (added > 0) Debug.Log("CROW_PHYS events: " + added + " listeners added");
            }
            if (now < nextReport || counts.Count == 0) return;
            nextReport = now + 10f;
            foreach (KeyValuePair<string, int> pair in counts.OrderByDescending(p => p.Value).Take(60))
                Debug.Log("CROW_PHYS_EVENTS " + pair.Value + " " + pair.Key);
            counts.Clear();
        }

        string self;

        void Count(string kind, Collider other)
        {
            if (other == null) return;
            if (self == null) self = PhysCost.OwnerOf(transform) + "/" + name.Replace(' ', '_');
            string key = kind + " " + self + " <- " + PhysCost.OwnerOf(other.transform) + "/" + other.name.Replace(' ', '_') +
                         "(" + other.tag.Replace(' ', '_') + (other.isTrigger ? ",T" : "") + ")";
            int n;
            counts.TryGetValue(key, out n);
            counts[key] = n + 1;
        }

        void OnTriggerEnter(Collider other) { Count("trigger+", other); }
        void OnTriggerExit(Collider other) { Count("trigger-", other); }
        void OnCollisionEnter(Collision collision) { Count("collide+", collision.collider); }
        void OnCollisionExit(Collision collision) { Count("collide-", collision.collider); }
    }
#endif

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
                if (rescan) List();
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

        static void Swap(MeshCollider mesh)
        {
            Bounds bounds = mesh.sharedMesh.bounds;
            BoxCollider box = mesh.gameObject.AddComponent<BoxCollider>();
            box.center = bounds.center;
            box.size = Vector3.Max(bounds.size, new Vector3(0.001f, 0.001f, 0.001f));
            box.sharedMaterial = mesh.sharedMaterial;
            box.contactOffset = mesh.contactOffset;
            mesh.enabled = false;
            swapped[mesh] = box;
        }
    }
}
