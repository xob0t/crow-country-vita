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
// canvases' steps 2.2 -> 1.5 ms a frame and the main thread 1.1 ms less (status-log.md, Render step).
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
            string value = CrowFlags.Value(args, "-crow-ui-park", "on");
            // Each under a Guard of its own (Diag.Guard, 21 Oct): one failing leaves the other.
            if (value != null) Diag.Guard("UiPark", () => Set(value));
            Diag.Guard("RigPark", () => RigPark.Install(args));
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
                CrowEvents.Loaded("UiPark.sceneLoaded", (scene, mode) =>
                {
                    if (on) nextPass = Time.realtimeSinceStartup + 2f;
                });
                CrowEvents.BeforeRender("UiPark.Tick", Tick);
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
                Debug.Log("CROW_UI_PARK restore " + (part == null ? "?" : Hierarchy.Path(part, 4)) + " by " + why + "\n" + Environment.StackTrace);
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
                if (canvas.gameObject.scene.name != Scenes.Kept) continue;
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
                Collect(canvas.transform, found, null);
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
                    if (check) Debug.Log("CROW_UI_PARK park " + Hierarchy.Path(pair.Key, 4) + ": " + pair.Value.Count + " parts, " + moved + " transforms");
                }
            }
            if (waiting > 0) nextPass = now + 30f;
            Debug.Log("CROW_UI_PARK pass: " + parts + " parts parked, " + transforms + " transforms; " + EnemySplit.UiParked + " parked in all" +
                      (waiting > 0 ? ", " + waiting + " parents restored lately" : ""));
        }

        const string HolderName = "CrowUiPark";

        // The topmost switched-off subtrees big enough (MinTransforms), by parent, but children named skip (both
        // parkers').
        internal static void Collect(Transform parent, Dictionary<Transform, List<Transform>> found, string skip)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (skip != null && child.name == skip) continue;
                if (child.gameObject.activeSelf)
                {
                    Collect(child, found, skip);
                    continue;
                }
                if (Count(child, MinTransforms) < MinTransforms) continue;
                List<Transform> list;
                if (!found.TryGetValue(parent, out list)) found[parent] = list = new List<Transform>();
                list.Add(child);
            }
        }

        // Transforms in a subtree, counting up to limit.
        internal static int Count(Transform t, int limit)
        {
            int n = 1;
            for (int i = 0; i < t.childCount && n < limit; i++) n += Count(t.GetChild(i), limit - n);
            return n;
        }
    }

    // The camera rig ("camera follow player" in the player's prefab) carries the 3D screens that the main camera
    // draws in front of the world: the inventory's 3D view (Inventory Screenspace OFF: Mara, the item grid, the
    // selected item, 1,500 transforms), the examine views' 3D items (ItemAction 607, Use Item 355, Give Heal 52, Tips
    // 57) and the inventory's code (48): 2,619 of the rig's 2,890 transforms, all switched off in play. PMFollow
    // writes the rig's position every frame, so every one of them changes every frame, and each engine system that
    // follows transform changes walks the player's whole hierarchy (3,948 transforms, the rig 2,890 of them).
    // RigPark parks them as UiPark parks the canvases' screens: 2 s after each area load the rig's topmost
    // switched-off subtrees of 8 transforms or more (UI Canvases left to UiPark) go below CrowRigPark, a root of its
    // own in DontDestroyOnLoad that nothing moves (a placeholder below the player's root, SplitMirror, switches it and
    // destroys it with the player), local poses kept; they come back to their old parent, in their order and with
    // their local poses, before the game's code switches one on, reparents it, reads or sets its world pose or
    // parent, or looks at its old parent's or an ancestor's children (EnemySplit, as for UiPark); a parent whose
    // children came back is parked again 30 s after if they are all off again.
    //   -crow-rig-park on|off|check|report  on parks (the PS Vita's default); check also logs each park and restore
    //                         with its stack, each parked transform whose local pose or activity changed while
    //                         parked (every second and at its restore) and every playing DOTween tween whose target
    //                         is parked (CROW_RIG_PARK); report logs what the rig carries, by subtree, and EnemySplit's
    //                         routed calls, filter passes and finds a frame since the last report.
    // -crow-cycle / -crow-fight-cycle / -crow-ab take rigpark=on|off|check|report (on and check park what is off at
    // once, the screens open included, which the stress tests use).
    static class RigPark
    {
        const string HolderName = "CrowRigPark";
        static bool on, check, hooked;
        static float nextPass = -1f, nextLog, nextCheck;
        static int lastParked = -1, lastRestores = -1, changes;
        static Transform holder;
        static readonly Dictionary<Transform, float> restoredAt = new Dictionary<Transform, float>();
        // check: each parked part's transforms with their local poses and activity at the park
        static readonly Dictionary<Transform, Snapshot> snapshots = new Dictionary<Transform, Snapshot>();
        static readonly HashSet<Transform> reported = new HashSet<Transform>();

        sealed class Snapshot
        {
            public Transform[] All;
            public Vector3[] Position, Scale;
            public Quaternion[] Rotation;
            public bool[] Active;
        }

        internal static void Install(string[] args)
        {
            string value = CrowFlags.Value(args, "-crow-rig-park", "on");
            if (value != null) Set(value);
        }

        internal static bool Change(string key, string value)
        {
            if (key != "rigpark") return false;
            Set(value);
            return true;
        }

        static void Set(string value)
        {
            if (value == "report")
            {
                Report();
                return;
            }
            if (value == "probe")
            {
                Probe();
                return;
            }
            check = value == "check";
            on = value == "on" || check;
            if (!hooked)
            {
                hooked = true;
                CrowEvents.Loaded("RigPark.sceneLoaded", (scene, mode) =>
                {
                    if (on) nextPass = Time.realtimeSinceStartup + 2f;
                });
                CrowEvents.BeforeRender("RigPark.Tick", Tick);
                EnemySplit.RigRestored = Restored;
                EnemySplit.RigGroupRestored = (parent, why, transforms, ms, moving) =>
                    Debug.Log("CROW_RIG_PARK back " + (parent == null ? "?" : Hierarchy.Path(parent, 3)) + " by " + why + ": " + transforms + " transforms in " +
                              ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms (" +
                              moving.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " moving)");
            }
            if (on)
            {
                restoredAt.Clear();
                nextPass = Time.realtimeSinceStartup;
            }
            else
            {
                nextPass = -1f;
                EnemySplit.RestoreAllRig("off");
            }
            Debug.Log("CROW_RIG_PARK " + (on ? check ? "check" : "on" : "off") + " parked=" + EnemySplit.RigParked);
        }

        static Transform rigFound;

        // The rig of the player kept across areas (PlayerSplit's camera form makes it a root of its own).
        static Transform Rig()
        {
            if (rigFound != null) return rigFound;
            GameObject player = MaraScene.FindPlayer();
            Transform rig = player == null ? null : player.transform.Find(MaraScene.Rig);
            if (rig == null)
            {
                GameObject found = GameObject.Find(MaraScene.Rig);
                rig = found == null ? null : found.transform;
            }
            return rigFound = rig != null && rig.gameObject.scene.name == Scenes.Kept ? rig : null;
        }

        static Transform Holder(Transform rig)
        {
            if (holder != null) return holder;
            var made = new GameObject(HolderName);
            UnityEngine.Object.DontDestroyOnLoad(made);
            holder = made.transform;
            var place = new GameObject(HolderName + " (place)");
            place.SetActive(false);
            place.transform.SetParent(rig.root, false);
            var mirror = place.AddComponent<SplitMirror>();
            mirror.part = made;
            mirror.active = true;
            place.SetActive(true);
            return holder;
        }

        static void Restored(Transform part, string why)
        {
            Transform parent = part == null ? null : part.parent;
            if (check && part != null) Compare(part, "restore");
            snapshots.Remove(part);
            if (why == "off") return;
            if (parent != null) restoredAt[parent] = Time.realtimeSinceStartup;
            if (on && nextPass < 0f) nextPass = Time.realtimeSinceStartup + 30f;
            if (check)
                Debug.Log("CROW_RIG_PARK restore " + (part == null ? "?" : Hierarchy.Path(part, 4)) + " by " + why + "\n" + Environment.StackTrace);
        }

        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (on && nextPass >= 0f && now >= nextPass)
            {
                nextPass = -1f;
                Pass(now);
            }
            if (check && now >= nextCheck)
            {
                nextCheck = now + 1f;
                foreach (Transform part in snapshots.Keys.ToList()) Compare(part, "parked");
                Tweens();
            }
            if (now < nextLog) return;
            nextLog = now + 30f;
            if (EnemySplit.RigParked == lastParked && EnemySplit.RigRestores == lastRestores) return;
            lastParked = EnemySplit.RigParked;
            lastRestores = EnemySplit.RigRestores;
            Debug.Log("CROW_RIG_PARK parked=" + lastParked + " restores=" + lastRestores + " inside=" + EnemySplit.RigInsideRestores + (check ? " changes=" + changes : ""));
        }

        static void Pass(float now)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp(), moving = 0;
            Transform rig = Rig();
            if (rig == null)
            {
                Debug.Log("CROW_RIG_PARK pass: no rig");
                return;
            }
            int rootBefore = rig.root.hierarchyCount;
            int parts = 0, transforms = 0, waiting = 0;
            var found = new Dictionary<Transform, List<Transform>>();
            UiPark.Collect(rig, found, MaraScene.Canvases);  // UI Canvases is UiPark's
            var names = new List<string>();
            foreach (KeyValuePair<Transform, List<Transform>> pair in found)
            {
                float at;
                if (restoredAt.TryGetValue(pair.Key, out at) && now - at < 30f)
                {
                    waiting++;
                    continue;
                }
                Transform into = Holder(rig);
                if (check)
                    foreach (Transform part in pair.Value) snapshots[part] = Take(part);
                long before = System.Diagnostics.Stopwatch.GetTimestamp();
                int moved = EnemySplit.ParkUiGroup(pair.Key, pair.Value, into, true);
                moving += System.Diagnostics.Stopwatch.GetTimestamp() - before;
                if (moved == 0)
                {
                    foreach (Transform part in pair.Value) if (part.parent != into) snapshots.Remove(part);
                    continue;
                }
                restoredAt.Remove(pair.Key);
                parts += pair.Value.Count;
                transforms += moved;
                if (check)
                {
                    foreach (Transform part in pair.Value) names.Add(part.name.Replace(' ', '_') + "=" + part.GetComponentsInChildren<Transform>(true).Length);
                    Debug.Log("CROW_RIG_PARK park " + Hierarchy.Path(pair.Key, 4) + ": " + pair.Value.Count + " parts, " + moved + " transforms");
                }
            }
            if (waiting > 0) nextPass = now + 30f;
            if (parts == 0 && waiting == 0) return;
            double tick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Debug.Log("CROW_RIG_PARK pass: " + parts + " parts parked, " + transforms + " transforms in " +
                      ((System.Diagnostics.Stopwatch.GetTimestamp() - started) * tick).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms (" +
                      (moving * tick).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " parking); " + EnemySplit.RigParked + " parked in all" +
                      (waiting > 0 ? ", " + waiting + " parents restored lately" : "") + "; the player's hierarchy " + rootBefore + " -> " + rig.root.hierarchyCount +
                      (names.Count > 0 ? ": " + string.Join(" ", names.ToArray()) : ""));
        }

        // ---------------------------------------------------------------- check

        static Snapshot Take(Transform part)
        {
            Transform[] all = part.GetComponentsInChildren<Transform>(true);
            var s = new Snapshot
            {
                All = all, Position = new Vector3[all.Length], Scale = new Vector3[all.Length], Rotation = new Quaternion[all.Length], Active = new bool[all.Length]
            };
            for (int i = 0; i < all.Length; i++)
            {
                s.Position[i] = all[i].localPosition;
                s.Rotation[i] = all[i].localRotation;
                s.Scale[i] = all[i].localScale;
                s.Active[i] = all[i].gameObject.activeSelf;
            }
            return s;
        }

        // A parked part's transforms against their state at the park: anything that changed them while parked (the
        // game's local writes are fine; a world write that EnemySplit didn't see would have put them elsewhere).
        static void Compare(Transform part, string when)
        {
            Snapshot s;
            if (!snapshots.TryGetValue(part, out s)) return;
            for (int i = 0; i < s.All.Length; i++)
            {
                Transform t = s.All[i];
                if (t == null || reported.Contains(t)) continue;
                string what = t.localPosition != s.Position[i] ? "localPosition " + s.Position[i].ToString("F4") + " -> " + t.localPosition.ToString("F4") :
                              t.localRotation != s.Rotation[i] ? "localRotation " + s.Rotation[i].eulerAngles.ToString("F2") + " -> " + t.localRotation.eulerAngles.ToString("F2") :
                              t.localScale != s.Scale[i] ? "localScale " + s.Scale[i].ToString("F4") + " -> " + t.localScale.ToString("F4") :
                              t.gameObject.activeSelf != s.Active[i] ? "active " + s.Active[i] + " -> " + t.gameObject.activeSelf : null;
                if (what == null) continue;
                reported.Add(t);
                changes++;
                Debug.Log("CROW_RIG_PARK changed while parked (" + when + "): " + Hierarchy.Path(t, 5) + " " + what);
            }
        }

        static System.Reflection.FieldInfo targetField;
        static readonly HashSet<object> tweensReported = new HashSet<object>();

        // Every playing tween whose target is parked or inside a parked part (DOTween writes its targets itself).
        static void Tweens()
        {
            if (snapshots.Count == 0) return;
            if (targetField == null)
            {
                if (GameTypes.Tween == null) return;
                targetField = GameTypes.Tween.GetField("target", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (targetField == null) return;
            }
            var list = GameTypes.PlayingTweens();
            if (list == null) return;
            foreach (object tween in list)
            {
                object target = targetField.GetValue(tween);
                var component = target as Component;
                Transform t = component != null ? component.transform : target is GameObject ? ((GameObject)target).transform : null;
                if (t == null) continue;
                for (Transform up = t; up != null; up = up.parent)
                {
                    if (up != holder) continue;
                    if (tweensReported.Add(tween))
                    {
                        // The shortcut that made it (DOLocalMove, DOMove...), from its setter's method name.
                        System.Reflection.FieldInfo setterField = tween.GetType().GetField("setter");
                        var setter = setterField == null ? null : setterField.GetValue(tween) as Delegate;
                        Debug.Log("CROW_RIG_PARK tween on a parked part: " + tween.GetType().Name + " " + (setter == null ? "?" : setter.Method.Name) + " " + Hierarchy.Path(t, 5));
                    }
                    break;
                }
            }
        }

        // What the rig carries: its transforms, active ones, renderers, the player's hierarchy, the parked ones, and
        // its subtrees of 50 transforms or more down to where they branch (name=transforms/active).
        // rigpark=probe (a test of the way back from inside a part): a world position read, as the game's code makes
        // it (EnemySplit.GetPosition), on the deepest transform of the first parked part; the part must be back below
        // its old parent with the local pose it was parked with.
        static void Probe()
        {
            foreach (KeyValuePair<Transform, List<Transform>> group in EnemySplit.RigGroups())
            {
                Transform parent = group.Key, part = group.Value.FirstOrDefault(p => p != null && p.childCount > 0);
                if (parent == null || part == null) continue;
                Transform[] all = part.GetComponentsInChildren<Transform>(true);
                Transform deep = all[all.Length - 1];
                Vector3 position = part.localPosition;
                Quaternion rotation = part.localRotation;
                int before = EnemySplit.RigInsideRestores;
                EnemySplit.GetPosition(deep);
                Debug.Log("CROW_RIG_PARK probe " + Hierarchy.Path(deep, 4) + " inside " + part.name + ": back=" + (part.parent == parent) +
                          " pose=" + (part.localPosition == position && part.localRotation == rotation) + " insideRestores=" + (EnemySplit.RigInsideRestores - before));
                return;
            }
            Debug.Log("CROW_RIG_PARK probe: nothing parked");
        }

        static int reportFrame;

        static void Report()
        {
            Transform rig = Rig();
            if (rig == null)
            {
                Debug.Log("CROW_RIG_PARK report: no rig");
                return;
            }
            var line = new System.Text.StringBuilder("CROW_RIG_PARK report ").Append(Hierarchy.Path(rig, 3))
                .Append(" transforms=").Append(rig.GetComponentsInChildren<Transform>(true).Length)
                .Append(" active=").Append(rig.GetComponentsInChildren<Transform>(false).Length)
                .Append(" renderers=").Append(rig.GetComponentsInChildren<Renderer>(false).Length)
                .Append(" root=").Append(rig.root.hierarchyCount)
                .Append(" parked=").Append(EnemySplit.RigParked).Append(" holder=").Append(holder == null ? 0 : holder.hierarchyCount);
            int frames = Math.Max(1, Time.frameCount - reportFrame);
            line.Append(" split a frame: calls=").Append(EnemySplit.Touches / frames).Append(" passed=").Append(EnemySplit.Passes / frames)
                .Append(" found=").Append(EnemySplit.Hits / frames);
            EnemySplit.Touches = EnemySplit.Passes = EnemySplit.Hits = 0;
            reportFrame = Time.frameCount;
            Subtrees(rig, 0, line);
            Debug.Log(line.ToString());
        }

        static void Subtrees(Transform node, int depth, System.Text.StringBuilder line)
        {
            for (int i = 0; i < node.childCount; i++)
            {
                Transform child = node.GetChild(i);
                int size = child.GetComponentsInChildren<Transform>(true).Length;
                if (size < 50) continue;
                line.Append(" | ").Append(new string('>', depth)).Append(child.name.Replace(' ', '_')).Append('=').Append(size).Append('/')
                    .Append(child.gameObject.activeInHierarchy ? child.GetComponentsInChildren<Transform>(false).Length : 0);
                if (depth < 16) Subtrees(child, depth + 1, line);
            }
        }
    }
}
