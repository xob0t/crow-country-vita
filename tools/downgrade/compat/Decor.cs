using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// Decoration that moves every frame. Every transform change costs the engine's transform change
// dispatch (TransformChangeDispatch: renderers' bounds, physics, particles, audio, canvases follow
// changed transforms; about 20% of a fight frame's main thread) whether anyone sees the object or
// not. In the idle areas almost all of it is DOTween tweens that PlayMaker FSMs start in loops (the
// bunting, crows and flies of Station Square, Pool's breathing creature, Mansion's clock and ghosts),
// a few of the game's animation scripts (RotationAnimationScript, ScaleAnimationScript), and Mara's
// own breathing (a tween on breath_vert_1 that moves about 600 of her transforms every frame). The
// scene data has no Animators. The rules (-crow-decor on|off; on is the PS Vita's default):
//   - A tween on decoration, and an AbstractAnimationScript's target (rewriter/Decor.cs hooks both),
//     runs on every frame while a renderer below it was drawn in the frame before, and on one frame in
//     -crow-decor-hidden <n> (2 or 4, default 4) while none was, with the time of the frames between:
//     a tween ends when it would have (to within those frames), so the FSMs waiting for it go on as
//     before, and a hidden thing that moves back into view does so on time. -crow-decor-far <m>: a
//     visible one farther than m metres from the camera runs on one frame in -crow-decor-far-rate <n>
//     (default 2; 0 m, the default, leaves visible ones alone).
//   - Decoration: what the tween or script moves has a renderer below it and nothing that matters to
//     play: no collider (triggers too), light, camera or rigidbody below it, no rigidbody or camera
//     above it, not the UI, not the player (DontDestroyOnLoad) and not an enemy. Only Normal and Late
//     tweens on a Transform (the fixed and manual updates keep theirs).
//   -crow-decor-log           every 10 s (CROW_DECOR): targets looked at, the share slowed, runs and
//                             skips, what was refused and why
// -crow-cycle / -crow-ab take decor=on|off, decorhidden=, decorfar=, decorfarrate=, and
// the experiment breathpause=off|on (Mara's breathing tweens paused or played: what her ~600 transforms cost;
// Cuts.cs' breath= is the rule). -crow-decor-visible <n> (Cuts.cs parses it; decorvisible=, the PS Vita's 2):
// decoration that was drawn runs on one frame in n too, with the frames' time.
// The probe:
//   -crow-motion-report <s>   every s seconds, for 6 frames in a row, every transform in the loaded
//                             scenes that changed in the frame (Transform.hasChanged, cleared each
//                             frame at onBeforeRender), grouped by where the change starts (a changed
//                             transform whose parent didn't change) and by what drives it there: a
//                             DOTween tween on it (loop: infinite loops), the game's animation scripts
//                             and TransformLock, a PlayMaker action of the FSM's active state that names
//                             it, an Animator or Animation above it (with its culling mode), a
//                             non-kinematic rigidbody above it, enemies, or else its components; player/
//                             before those of the DontDestroyOnLoad roots. CROW_MOTION: per frame, the
//                             changed transforms, the starts, the hierarchies they are in and those
//                             hierarchies' transforms (the dispatch walks a changed hierarchy whole);
//                             CROW_MOTION_SRC per source: starts and changed transforms a frame, the
//                             share of starts with a renderer drawn (any camera) below them, the share
//                             written with the value they had (a write that changes nothing still
//                             counts as a change), the hierarchies; CROW_MOTION_EX: examples.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Decor
    {
        // The rules (see the top): on by default on the PS Vita, off elsewhere (-crow-decor on|off).
        internal static bool On = Application.platform == RuntimePlatform.PSP2;
        internal static int HiddenDivisor = 4, FarDivisor = 2;
        // -crow-decor-visible <n> (Cuts.cs sets the PS Vita's 2): decoration that was drawn runs on one frame in n.
        internal static int VisibleDivisor = 1;
        internal static float Far;  // metres from the camera; 0: no far rule
        static bool log;

        internal static void Install(string[] args)
        {
            float report = Quality.Number(args, "-crow-motion-report", -1f);
            if (report > 0f) MotionReport.Install(report);
            string mode = Perf.Value(args, "-crow-decor");
            if (mode != null) On = mode == "on";
            DecorTweens.Active = On;
            HiddenDivisor = Divisor(Quality.Number(args, "-crow-decor-hidden", HiddenDivisor));
            FarDivisor = Divisor(Quality.Number(args, "-crow-decor-far-rate", FarDivisor));
            Far = Quality.Number(args, "-crow-decor-far", Far);
            log = Array.IndexOf(args, "-crow-decor-log") >= 0;
            SceneManager.sceneUnloaded += scene => Views.Clear();
            if (log) Canvas.willRenderCanvases += Views.Log;
            string moverName = Perf.Value(args, "-crow-find-movers");
            if (moverName != null)
            {
                float after = 5f;
                int at = Array.IndexOf(args, "-crow-find-movers");
                if (at + 2 < args.Length) float.TryParse(args[at + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out after);
                float due = -1f;
                SceneManager.sceneLoaded += (scene, loadMode) =>
                {
                    if (scene.name != "CrowGap" && scene.name != "CrowMara" && scene.name != "Title" && scene.name != "Logos") due = Time.realtimeSinceStartup + after;
                };
                Canvas.willRenderCanvases += () =>
                {
                    if (due < 0f || Time.realtimeSinceStartup < due) return;
                    due = -1f;
                    MotionReport.FindMovers(moverName);
                };
            }
            if (Array.IndexOf(args, "-crow-player-tree") >= 0)
                SceneManager.sceneLoaded += (scene, loadMode) =>
                {
                    if (scene.name == "CrowGap" || scene.name == "CrowMara" || scene.name == "Title" || scene.name == "Logos") return;
                    GameObject player = GameObject.Find("CharacterCameraPrefab(Clone)");
                    if (player == null) return;
                    var line = new StringBuilder("CROW_PLAYER_TREE ").Append(player.transform.root.hierarchyCount);
                    Tree(player.transform.root, 0, 10, line);
                    Debug.Log(line.ToString());
                    // What Merge.Split's analysis (with movers) would take out of the player's hierarchy.
                    bool movers = Merge.SplitMovers;
                    Merge.SplitMovers = true;
                    Debug.Log("CROW_PLAYER_TREE " + Merge.Split(player.scene, false));
                    Merge.SplitMovers = movers;
                };
        }

        // An experiment (-crow-cycle breathpause=off|on): the player's breathing tweens (on "breath_vert_1",
        // which moves about 600 of Mara's transforms every frame) paused or played again through DOTween,
        // to measure what those transform changes cost.
        static void Breath(bool play)
        {
            GameObject player = GameObject.Find("CharacterCameraPrefab(Clone)");
            Type dotween = Type.GetType("DG.Tweening.DOTween, DOTween");
            if (player == null || dotween == null) return;
            MethodInfo method = dotween.GetMethod(play ? "Play" : "Pause", new[] { typeof(object) });
            int count = 0;
            foreach (Transform t in player.GetComponentsInChildren<Transform>(true))
                if (t.name == "breath_vert_1") count += (int)method.Invoke(null, new object[] { t });
            Debug.Log("CROW_DECOR breath " + (play ? "played" : "paused") + ": " + count + " tweens");
        }

        // -crow-player-tree: the player's hierarchy below its root, subtrees of 40 transforms or more to
        // depth levels, with their size and whether they are active.
        static void Tree(Transform node, int level, int depth, StringBuilder line)
        {
            foreach (Transform child in node)
            {
                int size = child.GetComponentsInChildren<Transform>(true).Length;
                if (size < 40) continue;
                line.Append(" | ").Append(new string('>', level)).Append(child.name.Replace(' ', '_')).Append('=').Append(size).Append(child.gameObject.activeInHierarchy ? "" : "(off)");
                if (level + 1 < depth) Tree(child, level + 1, depth, line);
            }
        }

        static int Divisor(float value)
        {
            int n = (int)value;
            return n >= 4 ? 4 : n >= 2 ? 2 : 1;
        }

        // A -crow-cycle / -crow-ab change; false when it isn't one of these.
        internal static bool Change(string key, string value)
        {
            float number;
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            switch (key)
            {
                case "decor":
                    On = value == "on";
                    DecorTweens.Active = On || DecorTweens.Active;
                    break;
                case "decorhidden":
                    HiddenDivisor = Divisor(number);
                    break;
                case "decorfar":
                    Far = number;
                    break;
                case "decorfarrate":
                    FarDivisor = Divisor(number);
                    break;
                case "breathpause":
                    Breath(value != "off");
                    break;
                case "lift":
                    Lift.Set(value == "on");
                    break;
                default:
                    return false;
            }
            Debug.Log("CROW_DECOR " + key + "=" + value);
            return true;
        }
    }

    // An experiment (-crow-cycle lift=on|off), to measure what a moving part costs inside a big hierarchy:
    // on watches the loaded areas' roots of 1,000 transforms or more for 6 frames, and every transform
    // whose change starts there (it changed, its parent didn't) leaves its parents for the scene's root,
    // keeping its world pose; off puts them back. Not safe for play (FSMs that look parts up by path,
    // parents switched off or destroyed): a measurement only.
    static class Lift
    {
        static readonly List<KeyValuePair<Transform, Transform>> lifted = new List<KeyValuePair<Transform, Transform>>();
        static readonly List<int> siblings = new List<int>();
        static Transform[] watched;
        static int frames = -1;

        internal static void Set(bool on)
        {
            if (!on)
            {
                for (int i = lifted.Count - 1; i >= 0; i--)
                    if (lifted[i].Key != null && lifted[i].Value != null)
                    {
                        lifted[i].Key.SetParent(lifted[i].Value, true);
                        lifted[i].Key.SetSiblingIndex(siblings[i]);
                    }
                Debug.Log("CROW_DECOR lift off: " + lifted.Count + " back");
                lifted.Clear();
                siblings.Clear();
                return;
            }
            var all = new List<Transform>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || scene.name == "CrowMara" || scene.name == "DontDestroyOnLoad") continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                    if (root.transform.hierarchyCount >= 1000) all.AddRange(root.GetComponentsInChildren<Transform>(true));
            }
            watched = all.ToArray();
            foreach (Transform t in watched) t.hasChanged = false;
            if (frames < 0) Application.onBeforeRender += Tick;
            frames = 0;
        }

        static readonly HashSet<Transform> starts = new HashSet<Transform>();

        static void Tick()
        {
            if (frames < 0) return;
            foreach (Transform t in watched)
            {
                if (t == null || !t.hasChanged) continue;
                if (t.parent != null && !t.parent.hasChanged) starts.Add(t);
            }
            foreach (Transform t in watched)
                if (t != null) t.hasChanged = false;
            if (++frames < 6) return;
            frames = -1;
            Application.onBeforeRender -= Tick;
            int moved = 0;
            foreach (Transform t in starts)
            {
                if (t == null || t.parent == null) continue;
                lifted.Add(new KeyValuePair<Transform, Transform>(t, t.parent));
                siblings.Add(t.GetSiblingIndex());
                moved += t.GetComponentsInChildren<Transform>(true).Length;
                t.SetParent(null, true);
            }
            Debug.Log("CROW_DECOR lift on: " + starts.Count + " moving parts (" + moved + " transforms) out of their big roots");
            starts.Clear();
        }
    }

    // What runs slower: a target (a transform a tween or an animation script moves) that is decoration.
    // Per frame, a target's rate divisor: 1 (every frame) while any renderer below it was drawn in the
    // last frame and it is nearer than Decor.Far, FarDivisor beyond, HiddenDivisor while none was drawn.
    // Targets run on frames (frameCount + phase) % divisor == 0, phases spread over the targets so the
    // work doesn't bunch into one frame.
    sealed class View
    {
        public Transform Target;
        public Renderer[] Renderers;
        public int Phase, Frame = -1, Divisor = 1;
        // What the target is (the kinds but Decoration follow Cuts.cs' rules): decoration, a prop (below an
        // object Cuts.Props names: colliders allowed), an enemy's part (free: no collider or body below it
        // and not the enemy's own transform; fixed: one of those), Mara's breathing.
        public int Kind;
        public EnemyState Enemy;
        public const int Decoration = 0, Prop = 1, EnemyFree = 2, EnemyFixed = 3, Breath = 4;
    }

    static class Views
    {
        static readonly List<View> list = new List<View>();
        static readonly Dictionary<int, int> byTarget = new Dictionary<int, int>();  // instance ID -> entry
        static readonly Dictionary<int, string> refused = new Dictionary<int, string>();
        static int generation = 1;
        static Camera camera;
        static float cameraAt = -10f;
        static int counted, slowed, ran, skipped;

        internal static void Clear()
        {
            list.Clear();
            byTarget.Clear();
            refused.Clear();
            camera = null;
            generation = (generation + 1) & 0x7FFF;
            if (generation == 0) generation = 1;
        }

        // A cached entry (0: not looked at; > 0: a view; < 0: not decoration), for this generation of the
        // list; the view, or null when the target isn't decoration.
        internal static View Resolve(ref int entry, Transform target)
        {
            if (entry != 0 && ((entry < 0 ? -entry : entry) >> 16) == generation)
                return entry > 0 ? list[(entry & 0xFFFF) - 1] : null;
            entry = Classify(target);
            return entry > 0 ? list[(entry & 0xFFFF) - 1] : null;
        }

        internal static bool Current(int entry)
        {
            return entry != 0 && ((entry < 0 ? -entry : entry) >> 16) == generation;
        }

        static int Classify(Transform target)
        {
            if (target == null) return -(generation << 16);
            int id = target.GetInstanceID(), entry;
            if (byTarget.TryGetValue(id, out entry)) return entry;
            int kind;
            EnemyState enemy;
            string why = Refusal(target, out kind, out enemy);
            if (why != null || list.Count >= 0xFFFE)
            {
                refused[id] = why ?? "full";
                return byTarget[id] = -(generation << 16);
            }
            var view = new View { Target = target, Renderers = target.GetComponentsInChildren<Renderer>(true), Phase = enemy != null ? enemy.Index : list.Count,
                                  Kind = kind, Enemy = enemy };
            list.Add(view);
            return byTarget[id] = (generation << 16) | list.Count;
        }

        // Why a target isn't decoration, or null: what it moves has no renderer to judge by, or moving it
        // matters beyond its looks (physics, lights, cameras, the UI, the player, the enemies).
        static string Refusal(Transform target, out int kind, out EnemyState enemy)
        {
            kind = View.Decoration;
            enemy = null;
            if (target.gameObject.scene.name == "DontDestroyOnLoad")
            {
                if (!Cuts.IsBreath(target)) return "player";
                kind = View.Breath;
                return null;
            }
            Transform root = Cuts.EnemyRoot(target);
            if (root != null)
            {
                enemy = Enemies.Of(root);
                kind = target == root || target.GetComponentsInChildren<Rigidbody>(true).Length > 0 ||
                       target.GetComponentsInChildren<Collider>(true).Length > 0 ? View.EnemyFixed : View.EnemyFree;
                return null;
            }
            bool prop = Cuts.IsProp(target);
            if (prop) kind = View.Prop;
            if (target.GetComponentsInChildren<Renderer>(true).Length == 0) return "no renderer";
            if (!prop && target.GetComponentsInChildren<Collider>(true).Length > 0) return "collider";
            if (target.GetComponentsInChildren<Light>(true).Length > 0) return "light";
            if (target.GetComponentsInChildren<Camera>(true).Length > 0) return "camera";
            if (target.GetComponentsInChildren<Rigidbody>(true).Length > 0) return "body";
            if (target.GetComponentInParent<Canvas>() != null || target.GetComponentsInChildren<Canvas>(true).Length > 0) return "ui";
            for (Transform t = target; t != null; t = t.parent)
            {
                if (t.GetComponent<Rigidbody>() != null) return t.name.StartsWith("Enemy ", StringComparison.Ordinal) ? "enemy" : "body";
                if (t.GetComponent<Camera>() != null) return "camera";
            }
            return null;
        }

        static Camera Camera(float now)
        {
            if (camera == null || now - cameraAt > 2f || !camera.isActiveAndEnabled)
            {
                camera = UnityEngine.Camera.main;
                cameraAt = now;
            }
            return camera;
        }

        // This frame's divisor of a view. A slowed view is looked at again only on the frames it runs
        // (it shows within the frames of one run of coming into view); a full-rate one every frame.
        internal static int Divisor(View view, int frame)
        {
            if (view.Frame == frame) return view.Divisor;
            if (view.Divisor > 1 && (frame + view.Phase) % view.Divisor != 0) return view.Divisor;
            view.Frame = frame;
            bool seen = false;
            Renderer[] renderers = view.Renderers;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer != null && renderer.isVisible)
                {
                    seen = true;
                    break;
                }
            }
            int divisor = seen ? Decor.VisibleDivisor : Decor.HiddenDivisor;
            if (seen && Decor.Far > 0f && view.Target != null)
            {
                Camera main = Camera(Time.realtimeSinceStartup);
                if (main != null && (main.transform.position - view.Target.position).sqrMagnitude > Decor.Far * Decor.Far) divisor = Decor.FarDivisor;
            }
            view.Divisor = divisor;
            counted++;
            if (divisor > 1) slowed++;
            return divisor;
        }

        // This frame's divisor of a view by its kind: 0 holds it (no time passes), 1 runs it every frame.
        // script: an animation script's (Cuts' -crow-enemy-anim) rather than a tween's.
        internal static int Rate(View view, int frame, bool script)
        {
            switch (view.Kind)
            {
                case View.EnemyFree:
                case View.EnemyFixed:
                    if (view.Enemy.Frozen && Cuts.FreezeMode > 0) return 0;
                    if (view.Kind == View.EnemyFixed) return 1;
                    return script ? Cuts.EnemyAnim : Cuts.EnemyTweens;
                case View.Breath:
                    return Cuts.Breath;
                case View.Prop:
                    if (Cuts.PropsDivisor > 1) return Cuts.PropsDivisor;
                    return Decor.On ? Divisor(view, frame) : 1;
                default:
                    return Decor.On ? Divisor(view, frame) : 1;
            }
        }

        // Whether a view with this frame's divisor (above 0) runs this frame.
        internal static bool RunsAt(View view, int frame, int divisor)
        {
            bool runs = divisor <= 1 || (frame + view.Phase) % divisor == 0;
            if (runs) ran++;
            else skipped++;
            return runs;
        }

        // Whether a view runs this frame.
        internal static bool Runs(View view, int frame)
        {
            int divisor = Divisor(view, frame);
            bool runs = divisor <= 1 || (frame + view.Phase) % divisor == 0;
            if (runs) ran++;
            else skipped++;
            return runs;
        }

        static int failures;

        internal static void Failed(Exception exception)
        {
            if (failures++ < 5) Debug.Log("CROW_DECOR failed: " + exception);
        }

        static float logAt;

        // -crow-decor-log: every 10 s the views, the share of view-frames slowed, runs and skips, and the
        // refused targets by reason.
        internal static void Log()
        {
            float now = Time.realtimeSinceStartup;
            if (now - logAt < 10f) return;
            logAt = now;
            var reasons = refused.Values.GroupBy(r => r).OrderByDescending(g => g.Count()).Select(g => g.Key.Replace(' ', '_') + "=" + g.Count());
            Debug.Log("CROW_DECOR on=" + Decor.On + " views=" + list.Count + " slowed=" + (counted == 0 ? 0 : 100 * slowed / counted) + "% runs=" + ran + " skips=" + skipped +
                      " tweenCalls=" + DecorTweens.Count + " scriptCalls=" + DecorScripts.Count +
                      " refused: " + string.Join(" ", reasons.ToArray()));
            counted = slowed = ran = skipped = 0;
            DecorTweens.Count = DecorScripts.Count = 0;
        }
    }

    // DOTween's tweens on decoration (rewriter DecorTweenHooks): TweenManager.Update takes each tween's
    // time scale from Scale. A tween of a slowed view gets no time on the frames it skips and the time of
    // the frames since its last run on the frame it runs, so it ends when it would have (within the
    // frames of one run) and FSMs waiting for it go on as before. Only Normal and Late tweens on a
    // Transform (fixed and manual updates keep theirs).
    public static class DecorTweens
    {
        // Read by Tween.CrowScale before it calls Scale: off, or a tween found not to be decoration
        // (crowDecor -1), costs a field read.
        public static bool Active;
        internal static int Count;
        static int frame = -1;
        static readonly float[] times = new float[4];
        static float factor2, factor4;

        public static float Scale(float timeScale, ref int state, object target, int updateType)
        {
            if (state == -1) return timeScale;
            if (updateType > 1)
            {
                state = -1;
                return timeScale;
            }
            try
            {
                View view = Views.Resolve(ref state, target as Transform);
                if (view == null)
                {
                    state = -1;  // not decoration: for good (a recycled tween starts again at 0)
                    return timeScale;
                }
                int now = Time.frameCount;
                if (now != frame) Advance(now);
                Count++;
                int divisor = Views.Rate(view, now, false);
                if (divisor == 1) return timeScale;
                if (divisor == 0 || !Views.RunsAt(view, now, divisor)) return 0f;
                return timeScale * (divisor == 2 ? factor2 : factor4);
            }
            catch (Exception exception)
            {
                // Never in DOTween's update loop: the tween goes on at full rate.
                state = -1;
                Views.Failed(exception);
                return timeScale;
            }
        }

        // The time of the last frames over this one's, for a run that stands for 2 or 4 frames.
        static void Advance(int now)
        {
            if (now != frame + 1) Array.Clear(times, 0, 4);
            frame = now;
            float dt = Time.unscaledDeltaTime;
            times[now & 3] = dt;
            if (dt <= 1e-6f)
            {
                factor2 = factor4 = 0f;
                return;
            }
            float two = dt + times[(now - 1) & 3];
            factor2 = two / dt;
            factor4 = (two + times[(now - 2) & 3] + times[(now - 3) & 3]) / dt;
        }
    }

    // The game's AbstractAnimationScript (rewriter DecorAnimationScripts).
    public static class DecorScripts
    {
        internal static int Count;
        static float delta;
        static FieldInfo targetField;

        // The frame time AbstractAnimationScript.Update adds: this frame's and the skipped frames'.
        public static float Delta()
        {
            return delta;
        }

        // At the start of AbstractAnimationScript.Update: false skips this frame's update (its time is
        // kept in pending for the next run).
        public static bool Step(Component script, ref float pending, ref int entry)
        {
            float dt = Time.deltaTime;
            if (entry >= 0 && (Decor.On || Cuts.ScriptRules))
            {
                try
                {
                    View view = Views.Resolve(ref entry, Views.Current(entry) ? null : Target(script));
                    if (view != null)
                    {
                        Count++;
                        int frame = Time.frameCount, divisor = Views.Rate(view, frame, true);
                        if (divisor == 0) return false;  // held (a frozen enemy, or the rule off): its time passes by
                        if (!Views.RunsAt(view, frame, divisor))
                        {
                            pending += dt;
                            return false;
                        }
                    }
                }
                catch (Exception exception)
                {
                    entry = -1;  // at full rate from now on
                    Views.Failed(exception);
                }
            }
            delta = pending + dt;
            pending = 0f;
            return true;
        }

        // The script's target (its own transform when it has none).
        static Transform Target(Component script)
        {
            if (targetField == null)
                for (Type type = script.GetType(); type != null && targetField == null; type = type.BaseType)
                    targetField = type.GetField("target", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Transform target = targetField == null ? null : targetField.GetValue(script) as Transform;
            return target != null ? target : script.transform;
        }
    }

    static class MotionReport
    {
        const int Samples = 6;
        const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static float every, next;
        static int sample = -1;
        static Transform[] all;
        static Vector3[] position, scale;
        static Quaternion[] rotation;
        static Dictionary<Transform, int> index;
        static Dictionary<Transform, string> labels, details;
        static Dictionary<Transform, bool> visible;
        static Dictionary<Transform, string> drivers;  // exact targets: DOTween, scripts, FSM actions
        static Dictionary<Transform, string> driverDetails, weak;
        static int changedSum, startSum, hierarchySum, hierarchyTransformSum;

        sealed class Source
        {
            public int Starts, Changed, Visible, Same;
            public readonly HashSet<Transform> Roots = new HashSet<Transform>();
            public readonly Dictionary<Transform, int> Examples = new Dictionary<Transform, int>();
        }

        static Dictionary<string, Source> sources;

        public static void Install(float seconds)
        {
            every = seconds;
            next = Time.realtimeSinceStartup + seconds;
            Application.onBeforeRender += Tick;
            Debug.Log("CROW_MOTION on: every " + seconds + " s, " + Samples + " frames");
        }

        static void Tick()
        {
            try
            {
                Step();
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_MOTION failed: " + exception);
                sample = -1;
                next = Time.realtimeSinceStartup + every;
            }
        }

        static void Step()
        {
            if (sample < 0)
            {
                if (Time.realtimeSinceStartup < next) return;
                Begin();
                return;
            }
            Measure();
            if (++sample < Samples) return;
            Report();
            sample = -1;
            all = null;
            next = Time.realtimeSinceStartup + every;
        }

        static void Begin()
        {
            all = Resources.FindObjectsOfTypeAll<Transform>().Where(t => t != null && t.gameObject.scene.IsValid()).ToArray();
            position = new Vector3[all.Length];
            scale = new Vector3[all.Length];
            rotation = new Quaternion[all.Length];
            index = new Dictionary<Transform, int>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                index[t] = i;
                position[i] = t.localPosition;
                rotation[i] = t.localRotation;
                scale[i] = t.localScale;
                t.hasChanged = false;
            }
            labels = new Dictionary<Transform, string>();
            details = new Dictionary<Transform, string>();
            visible = new Dictionary<Transform, bool>();
            sources = new Dictionary<string, Source>();
            changedSum = startSum = hierarchySum = hierarchyTransformSum = 0;
            drivers = new Dictionary<Transform, string>();
            driverDetails = new Dictionary<Transform, string>();
            weak = new Dictionary<Transform, string>();
            try { Tweens(); } catch (Exception e) { Debug.Log("CROW_MOTION tweens: " + e.GetType().Name + " " + e.Message); }
            try { Scripts(); } catch (Exception e) { Debug.Log("CROW_MOTION scripts: " + e.GetType().Name + " " + e.Message); }
            try { Machines(); } catch (Exception e) { Debug.Log("CROW_MOTION fsms: " + e.GetType().Name + " " + e.Message); }
            sample = 0;
        }

        static void Measure()
        {
            var changed = new List<Transform>();
            foreach (Transform t in all)
                if (t != null && t.hasChanged) changed.Add(t);
            // Each changed transform counted at its start: the topmost changed one above it.
            var counts = new Dictionary<Transform, int>();
            foreach (Transform t in changed)
            {
                Transform start = t;
                while (start.parent != null && start.parent.hasChanged) start = start.parent;
                int n;
                counts.TryGetValue(start, out n);
                counts[start] = n + 1;
            }
            var roots = new HashSet<Transform>();
            foreach (KeyValuePair<Transform, int> pair in counts)
            {
                Transform start = pair.Key;
                string label = Label(start);
                Source source;
                if (!sources.TryGetValue(label, out source)) sources[label] = source = new Source();
                source.Starts++;
                source.Changed += pair.Value;
                if (Visible(start)) source.Visible++;
                int i;
                if (index.TryGetValue(start, out i) && start.localPosition == position[i] && start.localRotation == rotation[i] && start.localScale == scale[i] &&
                    Exact(start.localPosition, position[i]) && Exact(start.localRotation, rotation[i]) && Exact(start.localScale, scale[i]))
                    source.Same++;
                Transform root = start.root;
                source.Roots.Add(root);
                roots.Add(root);
                int seen;
                source.Examples.TryGetValue(start, out seen);
                source.Examples[start] = seen + pair.Value;
            }
            foreach (Transform t in changed)
            {
                int i;
                if (index.TryGetValue(t, out i))
                {
                    position[i] = t.localPosition;
                    rotation[i] = t.localRotation;
                    scale[i] = t.localScale;
                }
                t.hasChanged = false;
            }
            changedSum += changed.Count;
            startSum += counts.Count;
            hierarchySum += roots.Count;
            foreach (Transform root in roots) hierarchyTransformSum += root.hierarchyCount;
        }

        static bool Exact(Vector3 a, Vector3 b)
        {
            return a.x == b.x && a.y == b.y && a.z == b.z;
        }

        static bool Exact(Quaternion a, Quaternion b)
        {
            return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
        }

        static bool Visible(Transform start)
        {
            bool seen;
            if (visible.TryGetValue(start, out seen)) return seen;
            foreach (Renderer renderer in start.GetComponentsInChildren<Renderer>())
                if (renderer.enabled && renderer.isVisible)
                {
                    seen = true;
                    break;
                }
            return visible[start] = seen;
        }

        static string F(float value)
        {
            return value.ToString("0.#", Inv);
        }

        static void Report()
        {
            string scene = SceneManager.GetActiveScene().name.Replace(' ', '_');
            Camera camera = Camera.main;
            Debug.Log("CROW_MOTION scene=" + scene + " frames=" + Samples + " transforms=" + all.Length + " changed=" + F(changedSum / (float)Samples) +
                      " starts=" + F(startSum / (float)Samples) + " hierarchies=" + F(hierarchySum / (float)Samples) +
                      " hierarchyTransforms=" + F(hierarchyTransformSum / (float)Samples));
            foreach (KeyValuePair<string, Source> pair in sources.OrderByDescending(p => p.Value.Changed))
            {
                Source s = pair.Value;
                int rootTransforms = s.Roots.Where(r => r != null).Sum(r => r.hierarchyCount);
                Debug.Log("CROW_MOTION_SRC scene=" + scene + " " + pair.Key + " starts=" + F(s.Starts / (float)Samples) + " changed=" + F(s.Changed / (float)Samples) +
                          " visible=" + (100 * s.Visible / Math.Max(1, s.Starts)) + "% same=" + (100 * s.Same / Math.Max(1, s.Starts)) + "% hierarchies=" + s.Roots.Count +
                          " (" + rootTransforms + " transforms)");
            }
            int lines = 0;
            foreach (KeyValuePair<string, Source> pair in sources.OrderByDescending(p => p.Value.Changed))
            {
                if (lines++ >= 25) break;
                var text = new StringBuilder("CROW_MOTION_EX ").Append(pair.Key);
                foreach (KeyValuePair<Transform, int> example in pair.Value.Examples.OrderByDescending(e => e.Value).Take(6))
                {
                    Transform t = example.Key;
                    if (t == null) continue;
                    text.Append(" | ").Append(Perf.Path(t, 4)).Append(" x").Append(F(example.Value / (float)Samples));
                    bool seen;
                    visible.TryGetValue(t, out seen);
                    text.Append(seen ? " seen" : " unseen");
                    if (camera != null) text.Append(' ').Append(Vector3.Distance(camera.transform.position, t.position).ToString("0", Inv)).Append('m');
                    text.Append(" root=").Append(t.root.hierarchyCount);
                    string detail;
                    if (details.TryGetValue(t, out detail) && detail != null) text.Append(' ').Append(detail);
                }
                Debug.Log(text.ToString());
            }
        }

        // ---------------------------------------------------------------- attribution

        static string Label(Transform start)
        {
            string label;
            if (labels.TryGetValue(start, out label)) return label;
            string detail = null;
            label = Attribute(start, ref detail);
            details[start] = detail;
            return labels[start] = label;
        }

        // The player and the other DontDestroyOnLoad roots get "player/" before what drives them.
        static string Attribute(Transform start, ref string detail)
        {
            string label = Driven(start, ref detail);
            return start.root.gameObject.scene.name == "DontDestroyOnLoad" ? "player/" + label : label;
        }

        // Audit.cs: what drives a change inside an enemy (its tweens, scripts, FSM actions...) instead of
        // "enemy"; a body moves only its own transform there.
        internal static bool InsideEnemies;

        // Audit.cs: the driver tables as Begin makes them, for its own frames.
        internal static void PrepareDrivers()
        {
            labels = new Dictionary<Transform, string>();
            details = new Dictionary<Transform, string>();
            drivers = new Dictionary<Transform, string>();
            driverDetails = new Dictionary<Transform, string>();
            weak = new Dictionary<Transform, string>();
            try { Tweens(); } catch (Exception e) { Debug.Log("CROW_MOTION tweens: " + e.GetType().Name + " " + e.Message); }
            try { Scripts(); } catch (Exception e) { Debug.Log("CROW_MOTION scripts: " + e.GetType().Name + " " + e.Message); }
            try { Machines(); } catch (Exception e) { Debug.Log("CROW_MOTION fsms: " + e.GetType().Name + " " + e.Message); }
        }

        // Audit.cs' flatten experiment: something found moving it (a tween, an animation script, an FSM
        // action of an active state, a script's update on it).
        internal static bool IsDriven(Transform t)
        {
            return drivers != null && (drivers.ContainsKey(t) || weak.ContainsKey(t));
        }

        internal static string LabelOf(Transform start, out string detail)
        {
            string label = Label(start);
            details.TryGetValue(start, out detail);
            return label;
        }

        static string Driven(Transform start, ref string detail)
        {
            for (Transform t = start; t != null && !InsideEnemies; t = t.parent)
            {
                Rigidbody body = t.GetComponent<Rigidbody>();
                if (body != null && t.name.StartsWith("Enemy ", StringComparison.Ordinal)) return "enemy";
            }
            string driver;
            if (drivers.TryGetValue(start, out driver))
            {
                driverDetails.TryGetValue(start, out detail);
                return driver;
            }
            Animator animator = start.GetComponentInParent<Animator>();
            if (animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
            {
                detail = animator.name.Replace(' ', '_') + ":" + animator.runtimeAnimatorController.name.Replace(' ', '_') + " params=" + animator.parameterCount +
                         " update=" + animator.updateMode;
                return "animator:" + animator.cullingMode;
            }
            Animation animation = start.GetComponentInParent<Animation>();
            if (animation != null && animation.isActiveAndEnabled && animation.isPlaying)
            {
                detail = animation.name.Replace(' ', '_');
                return "animation:" + animation.cullingType;
            }
            Rigidbody rigidbody = InsideEnemies ? start.GetComponent<Rigidbody>() : start.GetComponentInParent<Rigidbody>();
            if (rigidbody != null && !rigidbody.isKinematic) return "physics";
            if (weak.TryGetValue(start, out driver))
            {
                driverDetails.TryGetValue(start, out detail);
                return driver;
            }
            // A driver of an ancestor that moves it by moving a part (a parent's script writing a child).
            for (Transform t = start.parent; t != null; t = t.parent)
                if (drivers.TryGetValue(t, out driver))
                {
                    detail = "above:" + t.name.Replace(' ', '_');
                    return driver + "(above)";
                }
            var names = start.GetComponents<Component>().Where(c => c != null && !(c is Transform) && !(c is MeshFilter) && !(c is MeshRenderer))
                .Select(c => c.GetType().Name).Distinct().Take(4).ToArray();
            detail = start.parent == null ? null : "parent:" + string.Join("+", start.parent.GetComponents<Component>()
                .Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name).Distinct().Take(4).ToArray());
            return "other:" + (names.Length == 0 ? "-" : string.Join("+", names));
        }

        static void Driver(Transform target, string label, string detail)
        {
            if (target == null || drivers.ContainsKey(target)) return;
            drivers[target] = label;
            driverDetails[target] = detail;
        }

        // Drivers that may not move what they name (other FSM actions, scripts with an update): after
        // the Animators and bodies.
        static void Weak(Transform target, string label, string detail)
        {
            if (target == null || weak.ContainsKey(target)) return;
            weak[target] = label;
            if (!driverDetails.ContainsKey(target)) driverDetails[target] = detail;
        }

        static Transform TransformOf(object target)
        {
            var component = target as Component;
            if (component != null) return component.transform;
            var gameObject = target as GameObject;
            return gameObject != null ? gameObject.transform : null;
        }

        // DOTween's playing tweens, and the tweens inside playing sequences, by target.
        static void Tweens()
        {
            Type dotween = Type.GetType("DG.Tweening.DOTween, DOTween");
            Type tweenType = Type.GetType("DG.Tweening.Tween, DOTween");
            if (dotween == null || tweenType == null) return;
            FieldInfo targetField = tweenType.GetField("target", Fields), loopsField = tweenType.GetField("loops", Fields);
            var list = dotween.GetMethod("PlayingTweens").Invoke(null, new object[] { null }) as IList;
            if (list == null) return;
            var pending = new Stack<object>();
            foreach (object tween in list) pending.Push(tween);
            while (pending.Count > 0)
            {
                object tween = pending.Pop();
                FieldInfo sequenced = tween.GetType().GetField("sequencedTweens", Fields);
                if (sequenced != null)
                {
                    var children = sequenced.GetValue(tween) as IList;
                    if (children != null) foreach (object child in children) pending.Push(child);
                }
                object target = targetField.GetValue(tween);
                Transform transform = TransformOf(target);
                if (transform == null) continue;
                int loops = (int)loopsField.GetValue(tween);
                Driver(transform, "dotween:" + (loops == -1 ? "loop" : "once") + (target is Rigidbody ? ":body" : ""), tween.GetType().Name);
            }
        }

        // The game's scripts that move a target of theirs every frame.
        static void Scripts()
        {
            foreach (MonoBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (behaviour == null || !behaviour.isActiveAndEnabled) continue;
                Type type = behaviour.GetType();
                string name = type.Name;
                bool animation = false;
                for (Type t = type; t != null; t = t.BaseType)
                    if (t.Name == "AbstractAnimationScript") animation = true;
                if (animation)
                {
                    FieldInfo field = type.GetField("target", Fields);
                    Transform target = field == null ? null : field.GetValue(behaviour) as Transform;
                    Driver(target ?? behaviour.transform, "script:" + name, null);
                }
                else if (name == "PlayMakerFSM" || name.StartsWith("PlayMaker", StringComparison.Ordinal)) continue;
                else if (type.Namespace == null || !type.Namespace.StartsWith("CrowCompat", StringComparison.Ordinal))
                {
                    MethodInfo update = type.GetMethod("Update", Fields) ?? type.GetMethod("LateUpdate", Fields) ?? type.GetMethod("FixedUpdate", Fields);
                    if (update != null) Weak(behaviour.transform, "script:" + name, null);
                }
            }
        }

        static Type fsmComponent, ownerType, fsmGameObjectType;
        static PropertyInfo fsmProperty, activeState, actionsProperty, enabledProperty, valueProperty, fsmName;
        static MethodInfo ownerTarget;
        static readonly Dictionary<Type, FieldInfo[]> actionFields = new Dictionary<Type, FieldInfo[]>();
        static readonly System.Text.RegularExpressions.Regex Moving = new System.Text.RegularExpressions.Regex(
            "Rotat|Position|Look|Translate|Scale|Move|Follow|Transform|Tween|Smooth|Spin|Euler|Orbit|Swing|Bob|Shake|Punch|Path|Align|Parent");

        // The GameObjects the actions of each running FSM's active state name.
        static void Machines()
        {
            if (fsmComponent == null)
            {
                fsmComponent = Type.GetType("PlayMakerFSM, PlayMaker");
                ownerType = Type.GetType("HutongGames.PlayMaker.FsmOwnerDefault, PlayMaker");
                fsmGameObjectType = Type.GetType("HutongGames.PlayMaker.FsmGameObject, PlayMaker");
                if (fsmComponent == null || ownerType == null || fsmGameObjectType == null) return;
                fsmProperty = fsmComponent.GetProperty("Fsm");
                activeState = fsmProperty.PropertyType.GetProperty("ActiveState");
                actionsProperty = activeState.PropertyType.GetProperty("Actions");
                enabledProperty = Type.GetType("HutongGames.PlayMaker.FsmStateAction, PlayMaker").GetProperty("Enabled");
                valueProperty = fsmGameObjectType.GetProperty("Value");
                ownerTarget = fsmProperty.PropertyType.GetMethod("GetOwnerDefaultTarget");
                fsmName = fsmProperty.PropertyType.GetProperty("Name");
            }
            var list = fsmComponent.GetProperty("FsmList", BindingFlags.Public | BindingFlags.Static).GetValue(null, null) as IList;
            if (list == null) return;
            var other = new Dictionary<Transform, string>();
            foreach (object component in list)
            {
                var behaviour = component as MonoBehaviour;
                if (behaviour == null || !behaviour.isActiveAndEnabled) continue;
                object fsm = fsmProperty.GetValue(component, null);
                object state = fsm == null ? null : activeState.GetValue(fsm, null);
                var actions = state == null ? null : actionsProperty.GetValue(state, null) as Array;
                if (actions == null) continue;
                string where = behaviour.name.Replace(' ', '_') + "/" + ((string)fsmName.GetValue(fsm, null) ?? "").Replace(' ', '_') + "@" +
                               ((string)activeState.PropertyType.GetProperty("Name").GetValue(state, null) ?? "").Replace(' ', '_');
                foreach (object action in actions)
                {
                    if (action == null || !(bool)enabledProperty.GetValue(action, null)) continue;
                    Type type = action.GetType();
                    bool moving = Moving.IsMatch(type.Name);
                    foreach (FieldInfo field in FieldsOf(type))
                    {
                        object value = field.GetValue(action);
                        GameObject target = null;
                        if (value != null && field.FieldType == ownerType) target = ownerTarget.Invoke(fsm, new[] { value }) as GameObject;
                        else if (value != null) target = valueProperty.GetValue(value, null) as GameObject;
                        if (target == null) continue;
                        if (moving) Driver(target.transform, "fsm:" + type.Name, where);
                        else if (!other.ContainsKey(target.transform)) other[target.transform] = type.Name + " " + where;
                    }
                }
            }
            foreach (KeyValuePair<Transform, string> pair in other) Weak(pair.Key, "fsm-other", pair.Value);
        }

        // -crow-find-movers <name> [s]: s seconds (default 5) after each area load, every action of every
        // state of every FSM whose GameObject parameters name an object called name (as they resolve
        // then: variables by their current value), with the variable's name (CROW_FIND_MOVER).
        internal static void FindMovers(string name)
        {
            drivers = new Dictionary<Transform, string>();
            driverDetails = new Dictionary<Transform, string>();
            weak = new Dictionary<Transform, string>();
            Machines();  // the reflection set up
            if (fsmComponent == null) return;
            PropertyInfo states = fsmProperty.PropertyType.GetProperty("States");
            PropertyInfo useVariable = fsmGameObjectType.GetProperty("UseVariable");
            PropertyInfo variableName = fsmGameObjectType.GetProperty("Name");
            PropertyInfo stateName = activeState.PropertyType.GetProperty("Name");
            int found = 0;
            foreach (UnityEngine.Object component in Resources.FindObjectsOfTypeAll(fsmComponent))
            {
                var behaviour = component as MonoBehaviour;
                if (behaviour == null || !behaviour.gameObject.scene.IsValid()) continue;
                object fsm = fsmProperty.GetValue(component, null);
                var all = fsm == null ? null : states.GetValue(fsm, null) as Array;
                if (all == null) continue;
                foreach (object state in all)
                {
                    var actions = actionsProperty.GetValue(state, null) as Array;
                    if (actions == null) continue;
                    foreach (object action in actions)
                    {
                        if (action == null) continue;
                        foreach (FieldInfo field in FieldsOf(action.GetType()))
                        {
                            object value = field.GetValue(action);
                            GameObject target = null;
                            object variable = value;
                            try
                            {
                                if (value != null && field.FieldType == ownerType)
                                {
                                    target = ownerTarget.Invoke(fsm, new[] { value }) as GameObject;
                                    variable = ownerType.GetProperty("GameObject").GetValue(value, null);
                                }
                                else if (value != null) target = valueProperty.GetValue(value, null) as GameObject;
                            }
                            catch (Exception)
                            {
                                continue;
                            }
                            if (target == null || target.name != name) continue;
                            string via = variable != null && (bool)useVariable.GetValue(variable, null) ? " variable=" + variableName.GetValue(variable, null) : "";
                            Debug.Log("CROW_FIND_MOVER " + name + " <- " + Perf.Path(behaviour.transform, 5) + " fsm=" + fsmName.GetValue(fsm, null) +
                                      " state=" + stateName.GetValue(state, null) + " action=" + action.GetType().Name + " field=" + field.Name + via);
                            found++;
                        }
                    }
                }
            }
            Debug.Log("CROW_FIND_MOVER " + name + ": " + found + " actions");
        }

        static FieldInfo[] FieldsOf(Type type)
        {
            FieldInfo[] fields;
            if (actionFields.TryGetValue(type, out fields)) return fields;
            fields = type.GetFields(Fields).Where(f => f.FieldType == ownerType || f.FieldType == fsmGameObjectType).ToArray();
            return actionFields[type] = fields;
        }
    }
}
