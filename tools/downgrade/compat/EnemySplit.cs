using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;

// The enemies' inactive parts out of their moving hierarchies. Each physics step writes an enemy body's
// pose, which marks its whole transform hierarchy changed, inactive parts too: a Grinner is 1,725
// transforms, about 470 of them death debris (spikes, shards, blood pools, effects) that is off until
// it dies. In Station Square's fight (6 enemies awake, PS Vita) taking every enemy's topmost inactive
// subtrees out (3,000 transforms) cut physics by 0.3 ms a step, the frame by about 3 ms; in the stress
// fight 15-25 ms.
//   -crow-enemy-split on|off|check
//       on: 4 s after each area load, each enemy's topmost inactive subtrees (but the Blob trails'
//       pieces, which their spawner switches on all the time) go to a root of their own, keeping their
//       local place; the first time the game's code switches one on, reparents it, or reads or sets its
//       world position or rotation or its parent, it goes back exactly where it was first (the
//       rewriter's RouteEnemySplit sends GameObject.SetActive and those Transform members here), so the
//       death code finds everything in place. check: the same, and any of those calls on a part inside
//       a moved subtree (not its root) is logged (CROW_SPLIT_MISS).
// The PS Vita build moves the enemies' death debris out when it writes each scene (compat/EnemyRebuild.cs,
// CROW_ENEMY_DEBRIS): its EnemyDebrisHolder hands the parts it holds to Adopt as the scene loads, and
// they come back the same way. A call on a part inside a moved subtree (not only its root) puts the
// subtree back too, logged in check mode.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class EnemySplit
    {
        static string mode = "off";
        static bool check;
        // Per scene: the root the moved parts go under, in the enemy's own scene (unloaded with it).
        static readonly Dictionary<int, Transform> holders = new Dictionary<int, Transform>();
        const string HolderName = "CrowEnemySplit";
        static float splitAt = -1f;
        static readonly Dictionary<Transform, Place> moved = new Dictionary<Transform, Place>(Same.Comparer);
        // Every transform below a moved part (not the parts themselves): a call on one puts its part back.
        static readonly HashSet<Transform> inside = new HashSet<Transform>(Same.Comparer);
        // The parts handed over by the build's holders and taken out after their pick (Adopt), by enemy
        // root, for -crow-enemy-debris in|out and Fight's copies.
        static readonly List<Adopted> adopted = new List<Adopted>();
        static int restored, misses, restoredBySwitch, restoredByTouch, restoredByInside;

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
            int flag = Array.IndexOf(args, "-crow-enemy-split");
            if (flag >= 0 && flag + 1 < args.Length) mode = args[flag + 1];
            check = mode == "check" || EnemyRebuild.CheckDebris;
            if (mode != "on" && mode != "check") return;
            SceneManager.sceneLoaded += (scene, loadMode) =>
            {
                if (scene.name != "CrowGap" && scene.name != "CrowMara" && scene.name != "Title" && scene.name != "Logos")
                    splitAt = Time.realtimeSinceStartup + 4f;
            };
            Canvas.willRenderCanvases += Frame;
            Debug.Log("CROW_SPLIT_ENEMY " + mode);
        }

        static void Frame()
        {
            if (splitAt < 0f || Time.realtimeSinceStartup < splitAt) return;
            splitAt = -1f;
            int subtrees = 0, transforms = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (Rigidbody body in root.GetComponentsInChildren<Rigidbody>(true))
                        if (body.name.StartsWith("Enemy ", StringComparison.Ordinal)) Split(body.transform, ref subtrees, ref transforms);
            }
            Debug.Log("CROW_SPLIT_ENEMY " + subtrees + " inactive subtrees (" + transforms + " transforms) out of the enemies");
        }

        // What an unloaded scene took with it.
        static void Purge()
        {
            var dead = new List<Transform>();
            foreach (KeyValuePair<Transform, Place> pair in moved) if (pair.Key == null) dead.Add(pair.Key);
            foreach (Transform t in dead) moved.Remove(t);
            inside.RemoveWhere(t => t == null);
            adopted.RemoveAll(a => a.Part == null || a.Holder == null);
        }

        // An enemy's topmost inactive subtrees out (Fight's copies too).
        internal static void Split(Transform enemy)
        {
            int subtrees = 0, transforms = 0;
            if (mode == "on" || mode == "check") Split(enemy, ref subtrees, ref transforms);
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

        static void Split(Transform enemy, ref int subtrees, ref int transforms)
        {
            Transform holder = HolderOf(enemy.gameObject.scene);
            var tops = new List<Transform>();
            foreach (Transform t in enemy.GetComponentsInChildren<Transform>(true))
                if (!t.gameObject.activeSelf && t.parent != null && t.parent.gameObject.activeInHierarchy && !Trail(t) && !moved.ContainsKey(t)) tops.Add(t);
            foreach (Transform t in tops)
            {
                transforms += Out(t, t.parent, t.GetSiblingIndex(), holder);
                subtrees++;
            }
        }

        // A part out to the holder, local pose kept; returns its transforms.
        static int Out(Transform part, Transform parent, int sibling, Transform holder)
        {
            moved[part] = new Place { Parent = parent, Sibling = sibling };
            Transform[] below = part.GetComponentsInChildren<Transform>(true);
            for (int i = 1; i < below.Length; i++) inside.Add(below[i]);
            if (part.parent != holder) part.SetParent(holder, false);
            return below.Length;
        }

        // A Blob trail's pool piece (its spawner switches pieces on many times a second): left in place.
        static bool Trail(Transform t)
        {
            foreach (MonoBehaviour behaviour in t.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour != null && behaviour.GetType().Name == "BlobTrailPiece") return true;
            return false;
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

        static void Restore(Transform t)
        {
            Place place;
            if (!moved.TryGetValue(t, out place)) return;
            moved.Remove(t);
            if (ReferenceEquals(t, null) || t == null) return;
            Transform[] below = t.GetComponentsInChildren<Transform>(true);
            for (int i = 1; i < below.Length; i++) inside.Remove(below[i]);
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
            if (moved.ContainsKey(t))
            {
                if (switching) restoredBySwitch++;
                else restoredByTouch++;
                Restore(t);
                return;
            }
            if (!inside.Contains(t)) return;
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

        // A summary for Fight's windows: subtrees out, put back (switched on / touched / touched inside),
        // misses (check).
        internal static string Report()
        {
            if (mode == "off" && adopted.Count == 0) return null;
            return "split=" + moved.Count + "/" + restored + "(" + restoredBySwitch + "s," + restoredByTouch + "t," + restoredByInside + "i)/" + misses;
        }
    }
}
