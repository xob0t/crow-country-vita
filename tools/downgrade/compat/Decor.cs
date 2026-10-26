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
// -crow-cycle / -crow-ab take decor=on|off, decorhidden=, decorfar=, decorfarrate=, decorvisible=. -crow-decor-visible <n>
// (the PS Vita's 2; Cuts' cuts= sets it too): decoration that was drawn runs on one frame in n too, with the frames'
// time.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Decor
    {
        // The rules (see the top): on by default on the PS Vita, off elsewhere (-crow-decor on|off).
        internal static bool On = Application.platform == RuntimePlatform.PSP2;
        internal static int HiddenDivisor = 4, FarDivisor = 2;
        // -crow-decor-visible <n> (decorvisible=; the PS Vita's default 2, also set by Cuts' cuts=): decoration that was
        // drawn runs on one frame in n.
        internal static int VisibleDivisor = 1;
        internal static float Far;  // metres from the camera; 0: no far rule
        static bool log;

        internal static void Install(string[] args)
        {
            string mode = CrowFlags.Value(args, "-crow-decor");
            if (mode != null) On = mode == "on";
            DecorTweens.Active = On;
            HiddenDivisor = Divisor(CrowFlags.Number(args, "-crow-decor-hidden", HiddenDivisor));
            FarDivisor = Divisor(CrowFlags.Number(args, "-crow-decor-far-rate", FarDivisor));
            Far = CrowFlags.Number(args, "-crow-decor-far", Far);
            SetVisible(CrowFlags.Value(args, "-crow-decor-visible", "2", "1"));
            log = CrowFlags.Has(args, "-crow-decor-log");
            CrowEvents.Unloaded("Decor.sceneUnloaded", scene => Views.Clear());
            if (log) CrowEvents.Canvases("Views.Log", Views.Log);
        }

        internal static void SetVisible(string value)
        {
            float number;
            VisibleDivisor = Divisor(float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : 1f);
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
                    Cuts.Refresh();  // DecorTweens.Active, which the cuts' rules share
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
                case "decorvisible":
                    VisibleDivisor = Divisor(number);
                    break;
                default:
                    return false;
            }
            Debug.Log("CROW_DECOR " + key + "=" + value);
            return true;
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

        // The view of a cached entry of this generation, or null (Tweens.cs' census and holds).
        internal static View Of(int entry)
        {
            if (entry <= 0 || (entry >> 16) != generation) return null;
            int index = (entry & 0xFFFF) - 1;
            return index < list.Count ? list[index] : null;
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
            if (target.gameObject.scene.name == Scenes.Kept)
            {
                if (!Cuts.IsBreath(target)) return "player";
                kind = View.Breath;
                return null;
            }
            Transform root = EnemyNames.RootOf(target);
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
                if (t.GetComponent<Rigidbody>() != null) return EnemyNames.Named(t.name) ? "enemy" : "body";
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

        static int failures;

        // A hook's failure (the tween or script then runs every frame): the first 5 logged, all counted
        // (failures= in CROW_DECOR).
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
                      " tweenCalls=" + DecorTweens.Count + " scriptCalls=" + DecorScripts.Count + " failures=" + failures +
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
        // kept in pending for the next run). A script refused once (entry < 0) stays refused for its
        // life, whatever the generation (Resolve isn't called again for it).
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
}
