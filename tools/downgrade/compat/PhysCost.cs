using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// What a physics step works on in a fight, and experiments that take parts of it away (measurements only; results in
// status.md, Enemy physics, 19 Oct).
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
// Cycle keys (-crow-cycle / -crow-fight-cycle / -crow-ab through EnemyCost.Change), reversible, on the
// enemies alive when applied:
//   pcensus=<frames>        the census now
//   psolid=off|on           the enemies' solid colliders on compound children (not the body's own) off
//   pnone=trig|solid|all|off  those enemy colliders (triggers, solid children, or all of them, the bodies' own
//                           too) on a layer that collides with nothing, so PhysX keeps their shapes (sync,
//                           broadphase) but no pairs; off puts them back (solid and all let the enemies fall
//                           through the floor: a fight is no longer steady after them)
//   pstatic=off|neg|pos|on  the static colliders the last census saw move switched off (Station Square's
//                           bunting; neg: those with a negative scale, which a box collider warns of at every scale
//                           change; pos: the others), and back
//   ptrigstatic=ignore|on   Physics.IgnoreCollision between the enemies' enabled triggers and every enabled static
//                           collider (the trigger pairs with the level), and back
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
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (scene.name != "CrowMara") censusAt = Time.realtimeSinceStartup + censusAfter;
            };
        }

        static void Hook()
        {
            if (hooked) return;
            hooked = true;
            Canvas.willRenderCanvases += Frame;
        }

        // EnemyCost.Change's first look: true when the key is one of these.
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
                case "psolid":
                    if (value == "off")
                    {
                        foreach (Collider collider in EnemyColliders())
                            if (collider.enabled && !collider.isTrigger && collider.attachedRigidbody != null &&
                                collider.attachedRigidbody.transform != collider.transform)
                            {
                                collider.enabled = false;
                                solidOff.Add(collider);
                            }
                    }
                    else
                    {
                        foreach (Collider collider in solidOff)
                            if (collider != null) collider.enabled = true;
                        solidOff.Clear();
                    }
                    Debug.Log("CROW_PHYS psolid=" + value + " (" + solidOff.Count + " off)");
                    return true;
                case "pnone":
                    None(value);
                    return true;
                case "ptrigstatic":
                    foreach (KeyValuePair<Collider, Collider> pair in ignored)
                        if (pair.Key != null && pair.Value != null) Physics.IgnoreCollision(pair.Key, pair.Value, false);
                    ignored.Clear();
                    if (value == "ignore")
                    {
                        var statics = UnityEngine.Object.FindObjectsOfType<Collider>().Where(c => c.enabled && c.attachedRigidbody == null && !c.isTrigger).ToList();
                        foreach (Collider trigger in EnemyColliders())
                            if (trigger.enabled && trigger.isTrigger)
                                foreach (Collider other in statics)
                                {
                                    Physics.IgnoreCollision(trigger, other, true);
                                    ignored.Add(new KeyValuePair<Collider, Collider>(trigger, other));
                                }
                    }
                    Debug.Log("CROW_PHYS ptrigstatic=" + value + " (" + ignored.Count + " pairs)");
                    return true;
                case "pstatic":
                    if (value == "off" || value == "neg" || value == "pos")
                    {
                        foreach (Collider collider in staticMoved)
                            if (collider != null && collider.enabled && (value == "off" || Negative(collider.transform.lossyScale) == (value == "neg")))
                            {
                                collider.enabled = false;
                                staticOff.Add(collider);
                            }
                    }
                    else
                    {
                        foreach (Collider collider in staticOff)
                            if (collider != null) collider.enabled = true;
                        staticOff.Clear();
                    }
                    Debug.Log("CROW_PHYS pstatic=" + value + " (" + staticOff.Count + " off of " + staticMoved.Count + ")");
                    return true;
            }
            return false;
        }

        static readonly List<KeyValuePair<Collider, Collider>> ignored = new List<KeyValuePair<Collider, Collider>>();

        static bool Negative(Vector3 scale)
        {
            return scale.x < 0f || scale.y < 0f || scale.z < 0f;
        }

        static readonly List<Collider> solidOff = new List<Collider>(), staticOff = new List<Collider>(), staticMoved = new List<Collider>();
        static readonly Dictionary<GameObject, int> movedLayer = new Dictionary<GameObject, int>();
        static int noneLayer = -1;
        static bool[] noneRow;

        // pnone: the chosen enemy colliders' GameObjects on a layer no other layer collides with.
        static void None(string value)
        {
            foreach (KeyValuePair<GameObject, int> pair in movedLayer)
                if (pair.Key != null) pair.Key.layer = pair.Value;
            movedLayer.Clear();
            if (noneRow != null)
            {
                for (int b = 0; b < 32; b++) Physics.IgnoreLayerCollision(noneLayer, b, noneRow[b]);
                noneRow = null;
            }
            if (value == "trig" || value == "solid" || value == "all")
            {
                if (noneLayer < 0) noneLayer = UnusedLayer();
                if (noneLayer < 0)
                {
                    Debug.Log("CROW_PHYS pnone: no unused layer");
                    return;
                }
                noneRow = new bool[32];
                for (int b = 0; b < 32; b++)
                {
                    noneRow[b] = Physics.GetIgnoreLayerCollision(noneLayer, b);
                    Physics.IgnoreLayerCollision(noneLayer, b, true);
                }
                foreach (Collider collider in EnemyColliders())
                {
                    if (!collider.enabled) continue;
                    bool own = collider.attachedRigidbody != null && collider.attachedRigidbody.transform == collider.transform;
                    bool pick = value == "all" || (value == "trig" ? collider.isTrigger : !collider.isTrigger && !own);
                    if (!pick || movedLayer.ContainsKey(collider.gameObject)) continue;
                    // A GameObject that holds a body and other colliders moves them all (value all only).
                    if (!own || value == "all")
                    {
                        movedLayer[collider.gameObject] = collider.gameObject.layer;
                        collider.gameObject.layer = noneLayer;
                    }
                }
            }
            Debug.Log("CROW_PHYS pnone=" + value + " layer=" + noneLayer + " (" + movedLayer.Count + " objects)");
        }

        // The highest layer that no loaded GameObject is on.
        static int UnusedLayer()
        {
            var used = new bool[32];
            foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
                if (t != null && t.gameObject.scene.IsValid()) used[t.gameObject.layer] = true;
            for (int layer = 31; layer >= 8; layer--)
                if (!used[layer]) return layer;
            return -1;
        }

        // The enabled and disabled colliders of the enemies alive in the loaded scenes.
        static List<Collider> EnemyColliders()
        {
            var found = new List<Collider>();
            foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
                if (body.name.StartsWith("Enemy ", StringComparison.Ordinal) && body.name != "Enemy Set")
                    found.AddRange(body.GetComponentsInChildren<Collider>(false).Where(c => c.attachedRigidbody == body));
            return found;
        }

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
            if (player == null) player = GameObject.Find("CharacterCameraPrefab(Clone)");
            return OwnerOfKnown(t);
        }

        static string OwnerOfKnown(Transform t)
        {
            if (player != null && t.gameObject.scene == player.scene) return "player";
            for (Transform p = t; p != null; p = p.parent)
                if (p.name.StartsWith("Enemy ", StringComparison.Ordinal) && p.name != "Enemy Set" && p.GetComponent<Rigidbody>() != null)
                    return "enemy:" + p.name.Substring(6).Replace(' ', '_');
            Transform root = t.root;
            return "level:" + (root.name.Length > 24 ? root.name.Substring(0, 24) : root.name).Replace(' ', '_');
        }

        static void Begin()
        {
            player = GameObject.Find("CharacterCameraPrefab(Clone)");
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
                              (entry.Trigger ? "T" : "") + " layer=" + LayerMask.LayerToName(entry.Collider.gameObject.layer).Replace(' ', '_') + " path=" + Perf.Path(entry.Collider.transform, 5));
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
                                  (mesh ? "/mesh" : "") + (b.Body == 0 ? "/" + Perf.Path(b.Collider.transform, 2).Replace(' ', '_') : "");
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
            SceneManager.sceneLoaded += (scene, mode) => nextScan = Time.realtimeSinceStartup + 2f;
            Canvas.willRenderCanvases += Tick;
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
}
