using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;

// The enemies' inactive parts out of their moving hierarchies. Each physics step writes an enemy body's
// pose, which marks its whole transform hierarchy changed, inactive parts too: a Grinner is 1,725
// transforms, about 470 of them death debris (spikes, shards, blood pools, effects) that is off until
// it dies. In Station Square's fight (6 enemies awake, PS Vita) taking every enemy's topmost inactive
// subtrees out (3,000 transforms) cut physics by 0.3 ms a step, the frame by about 3 ms; in the stress
// fight 15-25 ms.
// The PS Vita build moves the enemies' death debris out when it writes each scene (compat/EnemyRebuild.cs,
// CROW_ENEMY_DEBRIS): its EnemyDebrisHolder hands the parts it holds to Adopt as the scene loads. The
// first time the game's code switches one on, reparents it, or reads or sets its world position or
// rotation or its parent, it goes back exactly where it was first (the rewriter's RouteEnemySplit sends
// GameObject.SetActive and those Transform members here), so the death code finds everything in place;
// a call on a part inside a moved subtree (not only its root) puts the subtree back too, logged with
// -crow-enemy-debris check (CROW_SPLIT_MISS). (The run-time split of 18 Oct, -crow-enemy-split, which
// this replaced, is gone.)
// Every routed call asks whether its transform is a moved part or inside one. Two dictionary lookups
// cost about 1.2 us each on the PS Vita (interface calls and a software modulo), 4.9 us of a
// GetWorldDistance's 14.7 in Station Square's fight (19 Oct, profiling build, debris in and out per
// window): a filter of a bit per identity hash, set for every transform that goes into either set and
// cleared only when rebuilt from them, answers no for nearly all of them first (Filter; FsmCompile's
// splitfilter=on|off; status.md, FSM compile).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class EnemySplit
    {
        static bool check;
        // Per scene: the root the moved parts go under, in the enemy's own scene (unloaded with it).
        static readonly Dictionary<int, Transform> holders = new Dictionary<int, Transform>();
        const string HolderName = "CrowEnemySplit";
        static readonly Dictionary<Transform, Place> moved = new Dictionary<Transform, Place>(Same.Comparer);
        // Every transform below a moved part (not the parts themselves): a call on one puts its part back.
        static readonly HashSet<Transform> inside = new HashSet<Transform>(Same.Comparer);
        // The parts handed over by the build's holders and taken out after their pick (Adopt), by enemy
        // root, for -crow-enemy-debris in|out and Fight's copies.
        static readonly List<Adopted> adopted = new List<Adopted>();
        static int restored, misses, restoredBySwitch, restoredByTouch, restoredByInside;
        // The filter: 65,536 bits (8 KB), a transform's bit the top bits of its identity hash; removals since
        // the last rebuild (a removed transform's bit stays set until then: only a lookup more).
        internal static bool Filter = true;
        const int FilterBits = 16;
        static readonly uint[] filter = new uint[1 << (FilterBits - 5)];
        // Calls that reached the filter, passed it, and found a moved part or one inside one (RigPark's report).
        internal static int Touches, Passes, Hits;
        static int filterStale;

        struct Place
        {
            public Transform Parent;
            public int Sibling;
        }

        sealed class Adopted
        {
            public Transform Part, Parent, Holder;
            public int Sibling;
        }

        // Reference identity for transforms: a lookup that never calls into the engine.
        sealed class Same : IEqualityComparer<Transform>
        {
            internal static readonly Same Comparer = new Same();

            public bool Equals(Transform a, Transform b)
            {
                return ReferenceEquals(a, b);
            }

            public int GetHashCode(Transform t)
            {
                return RuntimeHelpers.GetHashCode(t);
            }
        }

        // From Diag.Install (every player).
        internal static void Install(string[] args)
        {
            EnemyRebuild.Install(args);
            SceneManager.sceneUnloaded += scene => Purge();
            check = EnemyRebuild.CheckDebris;
        }

        static int Bit(Transform t)
        {
            return (int)((uint)RuntimeHelpers.GetHashCode(t) >> (32 - FilterBits));
        }

        static void Mark(Transform t)
        {
            int bit = Bit(t);
            filter[bit >> 5] |= 1u << (bit & 31);
        }

        static void Rebuild()
        {
            Array.Clear(filter, 0, filter.Length);
            foreach (Transform t in moved.Keys) Mark(t);
            foreach (Transform t in inside) Mark(t);
            // The parked groups' ancestors (TouchAncestor asks the filter first; before 20 Oct a rebuild dropped them,
            // so a child lookup on an ancestor after one no longer brought its parked UI back).
            foreach (Transform t in uiAncestors.Keys) Mark(t);
            foreach (Transform[] below in rigBelow.Values)
                for (int i = 1; i < below.Length; i++) Mark(below[i]);
            filterStale = 0;
        }

        // What an unloaded scene took with it.
        static void Purge()
        {
            var dead = new List<Transform>();
            foreach (KeyValuePair<Transform, Place> pair in moved) if (pair.Key == null) dead.Add(pair.Key);
            foreach (Transform t in dead) moved.Remove(t);
            inside.RemoveWhere(t => t == null);
            adopted.RemoveAll(a => a.Part == null || a.Holder == null);
            foreach (Transform parent in uiByParent.Keys.Where(k => k == null).ToList()) uiByParent.Remove(parent);
            foreach (Transform up in uiAncestors.Keys.Where(k => k == null).ToList()) uiAncestors.Remove(up);
            rigParents.RemoveWhere(k => k == null);
            foreach (Transform part in rigBelow.Keys.Where(k => k == null).ToList()) rigBelow.Remove(part);
            UiParked = RigParked = 0;
            foreach (KeyValuePair<Transform, List<Transform>> group in uiByParent)
                if (rigParents.Contains(group.Key)) RigParked += group.Value.Count;
                else UiParked += group.Value.Count;
            var gone = new List<int>();
            foreach (KeyValuePair<int, Transform> pair in holders) if (pair.Value == null) gone.Add(pair.Key);
            foreach (int key in gone) holders.Remove(key);
            Rebuild();
        }

        static Transform HolderOf(Scene scene)
        {
            Transform holder;
            if (!holders.TryGetValue(scene.GetHashCode(), out holder) || holder == null)
            {
                var root = new GameObject(HolderName);
                SceneManager.MoveGameObjectToScene(root, scene);
                holders[scene.GetHashCode()] = holder = root.transform;
            }
            return holder;
        }

        // A part out to the holder, local pose kept; returns its transforms.
        static int Out(Transform part, Transform parent, int sibling, Transform holder)
        {
            moved[part] = new Place { Parent = parent, Sibling = sibling };
            Mark(part);
            Transform[] below = part.GetComponentsInChildren<Transform>(true);
            for (int i = 1; i < below.Length; i++)
            {
                inside.Add(below[i]);
                Mark(below[i]);
            }
            if (part.parent != holder) part.SetParent(holder, false);
            return below.Length;
        }

        // The build's moved parts (EnemyDebrisHolder.Awake) and the parts taken out after their pick
        // (EnemyRebuild.TakeVariants): out under their holder already, or now; out = false keeps them
        // in place, ready for -crow-enemy-debris out.
        internal static int Adopt(Transform part, Transform parent, int sibling, Transform holder, bool out_)
        {
            if (part == null || parent == null || holder == null) return 0;
            adopted.Add(new Adopted { Part = part, Parent = parent, Holder = holder, Sibling = sibling });
            holders[holder.gameObject.scene.GetHashCode()] = holder;
            if (!out_)
            {
                if (part.parent == holder) Put(part, parent, sibling);
                return 0;
            }
            return Out(part, parent, sibling, holder);
        }

        // -crow-enemy-debris in|out (edebrisout=off|on): every adopted part back in its place, or out again
        // where it is still off and in its place.
        internal static string SetAdopted(bool out_)
        {
            int parts = 0, transforms = 0, skipped = 0;
            adopted.RemoveAll(a => a.Part == null || a.Parent == null || a.Holder == null);
            foreach (Adopted a in adopted)
            {
                if (out_)
                {
                    // Out from where it is now (the flatten switch may have given it another parent).
                    if (moved.ContainsKey(a.Part)) continue;
                    if (a.Part.gameObject.activeSelf || a.Part.parent == null || a.Part.parent == a.Holder)
                    {
                        skipped++;
                        continue;
                    }
                    a.Parent = a.Part.parent;
                    a.Sibling = a.Part.GetSiblingIndex();
                    transforms += Out(a.Part, a.Parent, a.Sibling, a.Holder);
                    parts++;
                }
                else if (moved.ContainsKey(a.Part))
                {
                    Restore(a.Part);
                    parts++;
                }
            }
            return (out_ ? "out " : "in ") + parts + " parts" + (out_ ? " (" + transforms + " transforms, " + skipped + " on or moved)" : "");
        }

        // Fight's copies: the model's adopted parts that RestoreAll put back, out again, and the copy's same
        // parts (found by their sibling indices below the root) out with them.
        internal static void Mirror(Transform model, Transform copy)
        {
            if (model == null || copy == null) return;
            int count = adopted.Count;
            for (int i = 0; i < count; i++)
            {
                Adopted a = adopted[i];
                if (a.Part == null || a.Parent == null || a.Holder == null || !a.Parent.IsChildOf(model)) continue;
                if (copy == model)
                {
                    if (!moved.ContainsKey(a.Part) && !a.Part.gameObject.activeSelf && a.Part.parent == a.Parent) Out(a.Part, a.Parent, a.Part.GetSiblingIndex(), a.Holder);
                    continue;
                }
                // The path from the model's root to the part's place.
                var path = new List<int>();
                Transform t = a.Parent;
                for (; t != null && t != model; t = t.parent) path.Add(t.GetSiblingIndex());
                if (t != model) continue;
                Transform found = copy;
                for (int p = path.Count - 1; p >= 0 && found != null; p--) found = path[p] < found.childCount ? found.GetChild(path[p]) : null;
                if (found == null || a.Sibling >= found.childCount) continue;
                Transform part = found.GetChild(Math.Min(a.Sibling, found.childCount - 1));
                if (part.name != a.Part.name || part.gameObject.activeSelf) continue;
                adopted.Add(new Adopted { Part = part, Parent = found, Holder = HolderOf(copy.gameObject.scene), Sibling = part.GetSiblingIndex() });
                Out(part, found, part.GetSiblingIndex(), adopted[adopted.Count - 1].Holder);
            }
        }

        // An enemy's parts back, all of them (before Fight copies it).
        internal static void RestoreAll(Transform enemy)
        {
            var mine = new List<Transform>();
            foreach (KeyValuePair<Transform, Place> pair in moved)
                if (pair.Value.Parent != null && pair.Value.Parent.IsChildOf(enemy)) mine.Add(pair.Key);
            foreach (Transform t in mine) Restore(t);
        }

        // ------------------------------------------------------------------ parked UI (UiPark.cs)
        // Switched-off subtrees of the overlay canvases parked below a holder outside any canvas. They go
        // back as the enemies' parts do (switched on, reparented, a world pose or a parent read on them or
        // inside them) and also when the game's code looks at their old parent's children or at any
        // ancestor's (GetChild, childCount, foreach, Find, sibling indices, GetComponent(s)InChildren with
        // inactive ones): all the parked children of an old parent go back together, in their order.
        // The camera rig's switched-off subtrees (RigPark, UiPark.cs) are parked and come back the same way.
        static readonly Dictionary<Transform, List<Transform>> uiByParent = new Dictionary<Transform, List<Transform>>(Same.Comparer);
        static readonly Dictionary<Transform, List<Transform>> uiAncestors = new Dictionary<Transform, List<Transform>>(Same.Comparer);
        internal static int UiParked, UiRestores;
        internal static Action<Transform, string> UiRestored;  // UiPark's log (check mode)
        // The camera rig's parked parts (RigPark, UiPark.cs): the same groups, by old parent, counted and
        // logged apart.
        static readonly HashSet<Transform> rigParents = new HashSet<Transform>(Same.Comparer);
        // Each parked rig part's transforms (its own first), for the filter's rebuilds; and the rig's holder, whose
        // descendants are inside a parked part.
        static readonly Dictionary<Transform, Transform[]> rigBelow = new Dictionary<Transform, Transform[]>(Same.Comparer);
        internal static Transform RigHolder;
        internal static int RigInsideRestores;  // restores for a call on a transform inside a parked rig part
        internal static int RigParked, RigRestores;
        internal static Action<Transform, string> RigRestored;
        internal static Action<Transform, string, int, double, double> RigGroupRestored;  // transforms, ms, ms moving

        // Parks the parts of one old parent at once, highest sibling first, so each keeps its old index.
        internal static int ParkUiGroup(Transform parent, List<Transform> parts, Transform holder, bool rig = false)
        {
            if (parent == null || parts.Count == 0 || uiByParent.ContainsKey(parent)) return 0;
            int transforms = 0;
            var group = new List<Transform>();
            foreach (Transform part in parts.OrderBy(p => p.GetSiblingIndex()))
            {
                if (part.parent != parent || moved.ContainsKey(part)) continue;
                moved[part] = new Place { Parent = parent, Sibling = part.GetSiblingIndex() };
                group.Add(part);
            }
            if (group.Count == 0) return 0;
            uiByParent[parent] = group;
            if (rig) rigParents.Add(parent);
            for (Transform up = parent; up != null; up = up.parent)
            {
                List<Transform> below;
                if (!uiAncestors.TryGetValue(up, out below)) uiAncestors[up] = below = new List<Transform>();
                below.Add(parent);
                Mark(up);
            }
            for (int i = group.Count - 1; i >= 0; i--)
            {
                Transform part = group[i];
                Mark(part);
                Transform[] below = part.GetComponentsInChildren<Transform>(true);
                if (rig)
                {
                    // Not in inside: a call inside one is found by its root (RigHolder), and the restore has
                    // nothing to take out per transform.
                    rigBelow[part] = below;
                    for (int k = 1; k < below.Length; k++) Mark(below[k]);
                }
                else
                    for (int k = 1; k < below.Length; k++)
                    {
                        inside.Add(below[k]);
                        Mark(below[k]);
                    }
                transforms += below.Length;
                part.SetParent(holder, false);
                if (rig)
                {
                    RigHolder = holder;
                    RigParked++;
                }
                else UiParked++;
            }
            return transforms;
        }

        // An old parent's parked children back, in their order (lowest sibling first).
        static void RestoreUiGroup(Transform parent, string why)
        {
            List<Transform> group;
            if (!uiByParent.TryGetValue(parent, out group)) return;
            uiByParent.Remove(parent);
            bool rig = rigParents.Remove(parent);
            long started = System.Diagnostics.Stopwatch.GetTimestamp(), moving = 0;
            int transforms = 0;
            for (Transform up = parent; up != null; up = up.parent)
            {
                List<Transform> below;
                if (uiAncestors.TryGetValue(up, out below))
                {
                    below.Remove(parent);
                    if (below.Count == 0) uiAncestors.Remove(up);
                }
            }
            foreach (Transform part in group)
            {
                Place place;
                if (!moved.TryGetValue(part, out place)) continue;
                moved.Remove(part);
                if (part == null) continue;
                Transform[] below;
                if (rig && rigBelow.TryGetValue(part, out below))
                    // Its transforms' filter bits stay until the next rebuild (a lookup more, no harm), and a park
                    // sets them again anyway. (Taking 1,500 transforms out of inside one by one cost 120 ms on the
                    // PS Vita, the whole restore now 7-8 ms.)
                    rigBelow.Remove(part);
                else
                {
                    below = part.GetComponentsInChildren<Transform>(true);
                    for (int i = 1; i < below.Length; i++) inside.Remove(below[i]);
                    filterStale += below.Length;
                }
                transforms += below.Length;
                if (parent != null)
                {
                    long before = System.Diagnostics.Stopwatch.GetTimestamp();
                    part.SetParent(parent, false);
                    part.SetSiblingIndex(Math.Min(place.Sibling, parent.childCount - 1));
                    moving += System.Diagnostics.Stopwatch.GetTimestamp() - before;
                }
                if (rig)
                {
                    RigParked--;
                    RigRestores++;
                    if (RigRestored != null) RigRestored(part, why);
                    continue;
                }
                UiParked--;
                UiRestores++;
                if (UiRestored != null) UiRestored(part, why);
            }
            if (filterStale > 1024) Rebuild();
            if (rig && RigGroupRestored != null)
            {
                double tick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                RigGroupRestored(parent, why, transforms, (System.Diagnostics.Stopwatch.GetTimestamp() - started) * tick, moving * tick);
            }
        }

        // Every UI part back (UiPark off, and before an unload destroys what they belonged to).
        internal static void RestoreAllUi(string why)
        {
            foreach (Transform parent in uiByParent.Keys.Where(p => !rigParents.Contains(p)).ToList()) RestoreUiGroup(parent, why);
        }

        // Every part of the camera rig back (RigPark off).
        internal static void RestoreAllRig(string why)
        {
            foreach (Transform parent in uiByParent.Keys.Where(p => rigParents.Contains(p)).ToList()) RestoreUiGroup(parent, why);
        }

        // RigPark's check: the parked parts by old parent.
        internal static IEnumerable<KeyValuePair<Transform, List<Transform>>> RigGroups()
        {
            return uiByParent.Where(pair => rigParents.Contains(pair.Key));
        }

        // A call that sees t's children or descendants: the parked parts below t back first.
        static void TouchAncestor(Transform t, string why)
        {
            if (uiAncestors.Count == 0 || ReferenceEquals(t, null)) return;
            int bit = Bit(t);
            if ((filter[bit >> 5] & (1u << (bit & 31))) == 0) return;
            List<Transform> below;
            if (!uiAncestors.TryGetValue(t, out below)) return;
            foreach (Transform parent in below.ToList()) RestoreUiGroup(parent, why);
        }

        static void Restore(Transform t)
        {
            Place place;
            if (!moved.TryGetValue(t, out place)) return;
            if (!ReferenceEquals(place.Parent, null) && uiByParent.ContainsKey(place.Parent))
            {
                RestoreUiGroup(place.Parent, "touch");
                return;
            }
            moved.Remove(t);
            if (ReferenceEquals(t, null) || t == null) return;
            Transform[] below = t.GetComponentsInChildren<Transform>(true);
            for (int i = 1; i < below.Length; i++) inside.Remove(below[i]);
            filterStale += below.Length;
            if (filterStale > 1024) Rebuild();
            if (place.Parent == null) return;
            Put(t, place.Parent, place.Sibling);
            restored++;
        }

        static void Put(Transform t, Transform parent, int sibling)
        {
            t.SetParent(parent, false);
            t.SetSiblingIndex(Math.Min(sibling, parent.childCount - 1));
        }

        static void Touch(Transform t, bool switching)
        {
            if (moved.Count == 0 || ReferenceEquals(t, null)) return;
            Touches++;
            if (Filter)
            {
                int bit = Bit(t);
                if ((filter[bit >> 5] & (1u << (bit & 31))) == 0) return;
            }
            Passes++;
            if (moved.ContainsKey(t))
            {
                Hits++;
                if (switching) restoredBySwitch++;
                else restoredByTouch++;
                Restore(t);
                return;
            }
            if (!inside.Contains(t))
            {
                // Inside one of the camera rig's parked parts (RigPark): below its holder.
                if (rigBelow.Count == 0 || ReferenceEquals(RigHolder, null) || RigHolder == null || t.root != RigHolder) return;
                for (Transform up = t; up != null; up = up.parent)
                    if (up.parent == RigHolder)
                    {
                        if (!moved.ContainsKey(up)) return;
                        Hits++;
                        restoredByInside++;
                        RigInsideRestores++;
                        if (check)
                        {
                            misses++;
                            Debug.Log("CROW_SPLIT_MISS " + t.name + " inside " + up.name + " (" + misses + ")\n" + Environment.StackTrace);
                        }
                        Restore(up);
                        return;
                    }
                return;
            }
            Hits++;
            // A part inside a moved subtree: its root back.
            for (Transform up = t.parent; up != null; up = up.parent)
                if (moved.ContainsKey(up))
                {
                    restoredByInside++;
                    if (check)
                    {
                        misses++;
                        Debug.Log("CROW_SPLIT_MISS " + t.name + " inside " + up.name + " (" + misses + ")\n" + Environment.StackTrace);
                    }
                    Restore(up);
                    return;
                }
        }

        // The rewriter's targets (RouteEnemySplit): the game's calls, with the object first.
        public static void SetActive(GameObject gameObject, bool value)
        {
            if (value && moved.Count != 0 && !ReferenceEquals(gameObject, null)) Touch(gameObject.transform, true);
            gameObject.SetActive(value);
        }

        public static void SetParent(Transform transform, Transform parent)
        {
            Touch(transform, false);
            transform.SetParent(parent);
        }

        public static void SetParentStays(Transform transform, Transform parent, bool worldPositionStays)
        {
            Touch(transform, false);
            transform.SetParent(parent, worldPositionStays);
        }

        public static Transform GetParent(Transform transform)
        {
            Touch(transform, false);
            return transform.parent;
        }

        public static void SetParentProperty(Transform transform, Transform parent)
        {
            Touch(transform, false);
            transform.parent = parent;
        }

        public static Vector3 GetPosition(Transform transform)
        {
            Touch(transform, false);
            return transform.position;
        }

        public static void SetPosition(Transform transform, Vector3 position)
        {
            Touch(transform, false);
            transform.position = position;
        }

        public static Quaternion GetRotation(Transform transform)
        {
            Touch(transform, false);
            return transform.rotation;
        }

        public static void SetRotation(Transform transform, Quaternion rotation)
        {
            Touch(transform, false);
            transform.rotation = rotation;
        }

        public static Vector3 GetEulerAngles(Transform transform)
        {
            Touch(transform, false);
            return transform.eulerAngles;
        }

        public static void SetEulerAngles(Transform transform, Vector3 angles)
        {
            Touch(transform, false);
            transform.eulerAngles = angles;
        }

        public static void SetPositionAndRotation(Transform transform, Vector3 position, Quaternion rotation)
        {
            Touch(transform, false);
            transform.SetPositionAndRotation(position, rotation);
        }

        // The rewriter's targets that see children or parents (RouteEnemySplit's second list): parked UI
        // below the object back first; a parent-side call on a moved part (or inside one) puts it back.
        public static Transform GetChild(Transform t, int index)
        {
            TouchAncestor(t, "GetChild");
            return t.GetChild(index);
        }

        public static int GetChildCount(Transform t)
        {
            TouchAncestor(t, "childCount");
            return t.childCount;
        }

        public static System.Collections.IEnumerator GetEnumerator(Transform t)
        {
            TouchAncestor(t, "foreach");
            return t.GetEnumerator();
        }

        public static Transform Find(Transform t, string name)
        {
            TouchAncestor(t, "Find");
            return t.Find(name);
        }

        public static int GetSiblingIndex(Transform t)
        {
            Touch(t, false);
            if (!ReferenceEquals(t, null) && uiAncestors.Count != 0) TouchAncestor(t.parent, "GetSiblingIndex");
            return t.GetSiblingIndex();
        }

        public static void SetSiblingIndex(Transform t, int index)
        {
            Touch(t, false);
            if (!ReferenceEquals(t, null) && uiAncestors.Count != 0) TouchAncestor(t.parent, "SetSiblingIndex");
            t.SetSiblingIndex(index);
        }

        public static void SetAsFirstSibling(Transform t)
        {
            Touch(t, false);
            if (!ReferenceEquals(t, null) && uiAncestors.Count != 0) TouchAncestor(t.parent, "SetAsFirstSibling");
            t.SetAsFirstSibling();
        }

        public static void SetAsLastSibling(Transform t)
        {
            Touch(t, false);
            if (!ReferenceEquals(t, null) && uiAncestors.Count != 0) TouchAncestor(t.parent, "SetAsLastSibling");
            t.SetAsLastSibling();
        }

        public static bool IsChildOf(Transform t, Transform parent)
        {
            Touch(t, false);
            return t.IsChildOf(parent);
        }

        static Transform Of(Component c)
        {
            return ReferenceEquals(c, null) ? null : c.transform;
        }

        static Transform Of(GameObject g)
        {
            return ReferenceEquals(g, null) ? null : g.transform;
        }

        public static Component[] ComponentsInChildren(Component c, Type type, bool includeInactive)
        {
            if (includeInactive && uiAncestors.Count != 0) TouchAncestor(Of(c), "GetComponentsInChildren");
            return c.GetComponentsInChildren(type, includeInactive);
        }

        public static Component[] ComponentsInChildren(GameObject g, Type type, bool includeInactive)
        {
            if (includeInactive && uiAncestors.Count != 0) TouchAncestor(Of(g), "GetComponentsInChildren");
            return g.GetComponentsInChildren(type, includeInactive);
        }

        public static Component ComponentInChildren(Component c, Type type, bool includeInactive)
        {
            if (includeInactive && uiAncestors.Count != 0) TouchAncestor(Of(c), "GetComponentInChildren");
            return c.GetComponentInChildren(type, includeInactive);
        }

        public static Component ComponentInChildren(GameObject g, Type type, bool includeInactive)
        {
            if (includeInactive && uiAncestors.Count != 0) TouchAncestor(Of(g), "GetComponentInChildren");
            return g.GetComponentInChildren(type, includeInactive);
        }

        public static T[] ComponentsInChildrenT<T>(Component c, bool includeInactive)
        {
            if (includeInactive && uiAncestors.Count != 0) TouchAncestor(Of(c), "GetComponentsInChildren");
            return c.GetComponentsInChildren<T>(includeInactive);
        }

        public static T[] ComponentsInChildrenT<T>(GameObject g, bool includeInactive)
        {
            if (includeInactive && uiAncestors.Count != 0) TouchAncestor(Of(g), "GetComponentsInChildren");
            return g.GetComponentsInChildren<T>(includeInactive);
        }

        public static void ComponentsInChildrenList<T>(Component c, bool includeInactive, List<T> result)
        {
            if (includeInactive && uiAncestors.Count != 0) TouchAncestor(Of(c), "GetComponentsInChildren");
            c.GetComponentsInChildren(includeInactive, result);
        }

        public static void ComponentsInChildrenList<T>(GameObject g, bool includeInactive, List<T> result)
        {
            if (includeInactive && uiAncestors.Count != 0) TouchAncestor(Of(g), "GetComponentsInChildren");
            g.GetComponentsInChildren(includeInactive, result);
        }

        public static T ComponentInChildrenT<T>(Component c, bool includeInactive)
        {
            if (includeInactive && uiAncestors.Count != 0) TouchAncestor(Of(c), "GetComponentInChildren");
            return c.GetComponentInChildren<T>(includeInactive);
        }

        public static T ComponentInChildrenT<T>(GameObject g, bool includeInactive)
        {
            if (includeInactive && uiAncestors.Count != 0) TouchAncestor(Of(g), "GetComponentInChildren");
            return g.GetComponentInChildren<T>(includeInactive);
        }

        // The parent side: a moved part (or anything inside it) looking up its parents.
        public static Component ComponentInParent(Component c, Type type)
        {
            Touch(Of(c), false);
            return c.GetComponentInParent(type);
        }

        public static Component ComponentInParent(GameObject g, Type type)
        {
            Touch(Of(g), false);
            return g.GetComponentInParent(type);
        }

        public static T ComponentInParentT<T>(Component c)
        {
            Touch(Of(c), false);
            return c.GetComponentInParent<T>();
        }

        public static T ComponentInParentT<T>(GameObject g)
        {
            Touch(Of(g), false);
            return g.GetComponentInParent<T>();
        }

        public static Component[] ComponentsInParent(Component c, Type type, bool includeInactive)
        {
            Touch(Of(c), false);
            return c.GetComponentsInParent(type, includeInactive);
        }

        public static Component[] ComponentsInParent(GameObject g, Type type, bool includeInactive)
        {
            Touch(Of(g), false);
            return g.GetComponentsInParent(type, includeInactive);
        }

        public static T[] ComponentsInParentT<T>(Component c, bool includeInactive)
        {
            Touch(Of(c), false);
            return c.GetComponentsInParent<T>(includeInactive);
        }

        public static T[] ComponentsInParentT<T>(GameObject g, bool includeInactive)
        {
            Touch(Of(g), false);
            return g.GetComponentsInParent<T>(includeInactive);
        }

        public static T[] ComponentsInParentT0<T>(Component c)
        {
            Touch(Of(c), false);
            return c.GetComponentsInParent<T>();
        }

        public static T[] ComponentsInParentT0<T>(GameObject g)
        {
            Touch(Of(g), false);
            return g.GetComponentsInParent<T>();
        }

        // A summary for Fight's windows: subtrees out, put back (switched on / touched / touched inside),
        // misses (check).
        internal static string Report()
        {
            if (adopted.Count == 0) return null;
            return "split=" + moved.Count + "/" + restored + "(" + restoredBySwitch + "s," + restoredByTouch + "t," + restoredByInside + "i)/" + misses;
        }
    }
}
