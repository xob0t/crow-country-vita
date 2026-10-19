using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

// The player's gameplay canvas carries every examine screen's content switched off (the save, fast
// travel, computer, controls, code and piano screens, the KO menu: about 1,870 of its 1,930 transforms
// in Station Square, 60 active). Unity's canvas update walks a root canvas's RectTransforms every frame,
// twice (EarlyUpdate and PostLateUpdate UpdateCanvasRectTransform, about 0.55 ms each on the PS Vita),
// switched-off ones included: on the PS Vita's fight, with those subtrees moved out of the canvas, the
// canvases' steps 2.2 -> 1.5 ms a frame and the main thread 1.1 ms less (status.md, Render step).
// UiPark parks them: 2 s after each area load, the topmost switched-off subtrees of 8 transforms or more
// in the player's enabled overlay canvases (DontDestroyOnLoad) go below CrowUiPark, a plain transform
// beside the canvases under their root (so they are destroyed, kept across loads and found under the
// same root as before), local poses kept. They come back, all the parked children of a parent together
// in their order, before the game's code (the game's assemblies, PlayMaker's actions among them; the
// rewriter's RouteEnemySplit) switches one on, reparents it, reads or sets its world pose or parent,
// looks up its parents, or looks at its old parent's or any ancestor's children (GetChild, childCount,
// foreach, Find, sibling indices, GetComponent(s)InChildren with inactive ones): compat/EnemySplit.cs.
// A parent whose children came back is parked again 30 s after its last restore if they are all off
// again. Unity's own UI code sees only active objects, as before.
//   -crow-ui-park on|off|check  on (the PS Vita's default; off elsewhere); check also logs every park and
//                         every restore with the call that caused it and its stack (CROW_UI_PARK).
// -crow-cycle / -crow-fight-cycle / -crow-ab take uipark=on|off. CROW_UI_PARK lines count parks and
// restores (every 30 s while they change).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    static class UiPark
    {
        const int MinTransforms = 8;
        static bool on, check, hooked;
        static float nextPass = -1f, nextLog;
        static int lastParked = -1, lastRestores = -1;
        static readonly Dictionary<Transform, float> restoredAt = new Dictionary<Transform, float>();

        internal static void Install(string[] args)
        {
            string value = CrowFlags.Value(args, "-crow-ui-park") ?? (Application.platform == RuntimePlatform.PSP2 ? "on" : null);
            if (value != null) Set(value);
        }

        internal static bool Change(string key, string value)
        {
            if (key != "uipark") return false;
            Set(value);
            return true;
        }

        static void Set(string value)
        {
            check = value == "check";
            on = value == "on" || check;
            if (!hooked)
            {
                hooked = true;
                SceneManager.sceneLoaded += (scene, mode) =>
                {
                    if (on) nextPass = Time.realtimeSinceStartup + 2f;
                };
                Application.onBeforeRender += Tick;
                EnemySplit.UiRestored = Restored;
            }
            if (on)
            {
                restoredAt.Clear();
                nextPass = Time.realtimeSinceStartup;
            }
            else
            {
                nextPass = -1f;
                EnemySplit.RestoreAllUi("off");
            }
            Debug.Log("CROW_UI_PARK " + (on ? check ? "check" : "on" : "off"));
        }

        static void Restored(Transform part, string why)
        {
            Transform parent = part == null ? null : part.parent;
            if (why == "off") return;
            if (parent != null) restoredAt[parent] = Time.realtimeSinceStartup;
            if (on && nextPass < 0f) nextPass = Time.realtimeSinceStartup + 30f;
            if (check)
                Debug.Log("CROW_UI_PARK restore " + (part == null ? "?" : Perf.Path(part, 4)) + " by " + why + "\n" + Environment.StackTrace);
        }

        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (on && nextPass >= 0f && now >= nextPass)
            {
                nextPass = -1f;
                Pass(now);
            }
            if (now < nextLog) return;
            nextLog = now + 30f;
            if (EnemySplit.UiParked == lastParked && EnemySplit.UiRestores == lastRestores) return;
            lastParked = EnemySplit.UiParked;
            lastRestores = EnemySplit.UiRestores;
            Debug.Log("CROW_UI_PARK parked=" + lastParked + " restores=" + lastRestores);
        }

        static void Pass(float now)
        {
            int parts = 0, transforms = 0, waiting = 0;
            foreach (Canvas canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (!canvas.enabled || !canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                if (canvas.gameObject.scene.name != "DontDestroyOnLoad") continue;
                Transform root = canvas.transform.root;
                if (root == canvas.transform) continue;
                Transform holder = root.Find(HolderName);
                if (holder == null)
                {
                    var made = new GameObject(HolderName);
                    made.transform.SetParent(root, false);
                    holder = made.transform;
                }
                var found = new Dictionary<Transform, List<Transform>>();
                Collect(canvas.transform, found);
                foreach (KeyValuePair<Transform, List<Transform>> pair in found)
                {
                    float at;
                    if (restoredAt.TryGetValue(pair.Key, out at) && now - at < 30f)
                    {
                        waiting++;
                        continue;
                    }
                    int moved = EnemySplit.ParkUiGroup(pair.Key, pair.Value, holder);
                    if (moved == 0) continue;
                    restoredAt.Remove(pair.Key);
                    parts += pair.Value.Count;
                    transforms += moved;
                    if (check) Debug.Log("CROW_UI_PARK park " + Perf.Path(pair.Key, 4) + ": " + pair.Value.Count + " parts, " + moved + " transforms");
                }
            }
            if (waiting > 0) nextPass = now + 30f;
            Debug.Log("CROW_UI_PARK pass: " + parts + " parts parked, " + transforms + " transforms; " + EnemySplit.UiParked + " parked in all" +
                      (waiting > 0 ? ", " + waiting + " parents restored lately" : ""));
        }

        const string HolderName = "CrowUiPark";

        // The topmost switched-off subtrees big enough, by parent.
        static void Collect(Transform parent, Dictionary<Transform, List<Transform>> found)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.gameObject.activeSelf)
                {
                    Collect(child, found);
                    continue;
                }
                if (Count(child, MinTransforms) < MinTransforms) continue;
                List<Transform> list;
                if (!found.TryGetValue(parent, out list)) found[parent] = list = new List<Transform>();
                list.Add(child);
            }
        }

        // Transforms in a subtree, counting up to limit.
        static int Count(Transform t, int limit)
        {
            int n = 1;
            for (int i = 0; i < t.childCount && n < limit; i++) n += Count(t.GetChild(i), limit - n);
            return n;
        }
    }
}
