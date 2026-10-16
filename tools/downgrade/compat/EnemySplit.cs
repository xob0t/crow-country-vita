using System;
using System.Collections.Generic;
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
//       a moved subtree (not its root) puts its root back too and is logged (CROW_SPLIT_MISS): a test
//       of what the root-only watch would miss.
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
        static readonly Dictionary<Transform, Place> moved = new Dictionary<Transform, Place>();
        static int restored, misses;

        struct Place
        {
            public Transform Parent;
            public int Sibling;
        }

        // From Diag.Install (every player).
        internal static void Install(string[] args)
        {
            int flag = Array.IndexOf(args, "-crow-enemy-split");
            if (flag >= 0 && flag + 1 < args.Length) mode = args[flag + 1];
            if (mode != "on" && mode != "check") return;
            check = mode == "check";
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
            moved.Clear();  // the last area's were unloaded with it, holders too
            holders.Clear();
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

        // An enemy's topmost inactive subtrees out (Fight's copies too).
        internal static void Split(Transform enemy)
        {
            int subtrees = 0, transforms = 0;
            if (mode == "on" || mode == "check") Split(enemy, ref subtrees, ref transforms);
        }

        static void Split(Transform enemy, ref int subtrees, ref int transforms)
        {
            Scene scene = enemy.gameObject.scene;
            Transform holder;
            if (!holders.TryGetValue(scene.GetHashCode(), out holder) || holder == null)
            {
                var root = new GameObject(HolderName);
                SceneManager.MoveGameObjectToScene(root, scene);
                holders[scene.GetHashCode()] = holder = root.transform;
            }
            var tops = new List<Transform>();
            foreach (Transform t in enemy.GetComponentsInChildren<Transform>(true))
                if (!t.gameObject.activeSelf && t.parent != null && t.parent.gameObject.activeInHierarchy && !Trail(t)) tops.Add(t);
            foreach (Transform t in tops)
            {
                moved[t] = new Place { Parent = t.parent, Sibling = t.GetSiblingIndex() };
                transforms += t.GetComponentsInChildren<Transform>(true).Length;
                t.SetParent(holder, false);
                subtrees++;
            }
        }

        // A Blob trail's pool piece (its spawner switches pieces on many times a second): left in place.
        static bool Trail(Transform t)
        {
            foreach (MonoBehaviour behaviour in t.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour != null && behaviour.GetType().Name == "BlobTrailPiece") return true;
            return false;
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
            if (t == null || place.Parent == null) return;
            t.SetParent(place.Parent, false);
            t.SetSiblingIndex(place.Sibling);
            restored++;
        }

        static void Touch(Transform t)
        {
            if (moved.Count == 0 || ReferenceEquals(t, null)) return;
            if (moved.ContainsKey(t))
            {
                Restore(t);
                return;
            }
            if (!check || t.root.name != HolderName) return;
            // check: a part inside a moved subtree: its root back, and the miss logged.
            for (Transform up = t.parent; up != null; up = up.parent)
                if (moved.ContainsKey(up))
                {
                    misses++;
                    Debug.Log("CROW_SPLIT_MISS " + t.name + " inside " + up.name + " (" + misses + ")\n" + Environment.StackTrace);
                    Restore(up);
                    return;
                }
        }

        // The rewriter's targets (RouteEnemySplit): the game's calls, with the object first.
        public static void SetActive(GameObject gameObject, bool value)
        {
            if (value && moved.Count != 0 && !ReferenceEquals(gameObject, null)) Touch(gameObject.transform);
            gameObject.SetActive(value);
        }

        public static void SetParent(Transform transform, Transform parent)
        {
            Touch(transform);
            transform.SetParent(parent);
        }

        public static void SetParentStays(Transform transform, Transform parent, bool worldPositionStays)
        {
            Touch(transform);
            transform.SetParent(parent, worldPositionStays);
        }

        public static Transform GetParent(Transform transform)
        {
            Touch(transform);
            return transform.parent;
        }

        public static void SetParentProperty(Transform transform, Transform parent)
        {
            Touch(transform);
            transform.parent = parent;
        }

        public static Vector3 GetPosition(Transform transform)
        {
            Touch(transform);
            return transform.position;
        }

        public static void SetPosition(Transform transform, Vector3 position)
        {
            Touch(transform);
            transform.position = position;
        }

        public static Quaternion GetRotation(Transform transform)
        {
            Touch(transform);
            return transform.rotation;
        }

        public static void SetRotation(Transform transform, Quaternion rotation)
        {
            Touch(transform);
            transform.rotation = rotation;
        }

        public static Vector3 GetEulerAngles(Transform transform)
        {
            Touch(transform);
            return transform.eulerAngles;
        }

        public static void SetEulerAngles(Transform transform, Vector3 angles)
        {
            Touch(transform);
            transform.eulerAngles = angles;
        }

        public static void SetPositionAndRotation(Transform transform, Vector3 position, Quaternion rotation)
        {
            Touch(transform);
            transform.SetPositionAndRotation(position, rotation);
        }

        // A summary for Fight's windows: subtrees still out, put back, misses (check).
        internal static string Report()
        {
            return mode == "off" ? null : "split=" + moved.Count + "/" + restored + "/" + misses;
        }
    }
}
