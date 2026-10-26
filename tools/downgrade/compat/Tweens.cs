using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

// DOTween's update and what its tweens cost (19 Oct; status-log.md, "DOTween and moving transforms"). The rewriter
// (rewriter/Tweens.cs, TweenProbeHooks) times TweenManager.Update (every update type) and Tween.OnTweenCallback,
// and sends TweenManager.Update's one Tween.DoGoto through Tween.CrowGoto, which counts it; nothing else changes.
// A lean evaluator for the Transform shortcuts' steady frames (the same values bit for bit, checked over 15 million
// tween frames on Windows) saved nothing measurable on the console and was taken out again (its code:
// work/scratch/tweens/fastpath/).
//   -crow-tween-log           every 10 s CROW_TWEENS: DOTween's update in ms a frame, the tweens it moved a frame;
//                             fight windows log dotween=<ms>/<moved>
//   -crow-tween-detail        (tweendetail=on|off) also DOTween's callbacks (OnStart, OnComplete... and the FSM
//                             work they start: transitions, the next state's tween made and set up) as
//                             cb=<ms>/<calls> a frame
//   -crow-tween-census <s>    every s seconds (from 10 s after a load) the playing tweens by owner (Decor's kinds:
//                             breath, enemy-free, enemy-fixed, prop, decor; else player, enemy, ui, scene), plugin
//                             types, loops and loop type, ease, update type and callbacks (CROW_TWEEN_CENSUS)
// The experiments tweenhold= and rigstill= are done and gone (status-log.md, DOTween and moving transforms, 19 Oct;
// RigPark, UiPark.cs, now parks the rig for good).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class TweenProbe
    {
        // Counted by Tween.CrowGoto: tweens moved.
        public static int Gotos;
        static bool log;
        static float census = -1f, censusAt = -1f;

        internal static void Install(string[] args)
        {
            log = CrowFlags.Has(args, "-crow-tween-log");
            detail = CrowFlags.Has(args, "-crow-tween-detail");
            timing = log || Fight.On;  // only -crow-tween-log and the fight windows read the time
            census = CrowFlags.Number(args, "-crow-tween-census", -1f);
            CrowEvents.Loaded("TweenProbe.sceneLoaded", (scene, loadMode) =>
            {
                if (census > 0f) censusAt = Time.realtimeSinceStartup + 10f;
            });
            if (log || census > 0f) CrowEvents.Canvases("TweenProbe.Tick", Tick);
        }

        // A -crow-cycle / -crow-fight-cycle / -crow-ab change; false when it isn't one of these.
        internal static bool Change(string key, string value)
        {
            switch (key)
            {
                case "tweendetail":
                    detail = value == "on";
                    callDepth = 0;
                    break;
                default:
                    return false;
            }
            Debug.Log("CROW_TWEENS " + key + "=" + value + " detail=" + detail);
            return true;
        }

        // ---------------------------------------------------------------- timing

        static long began, ticks;
        static int frameGotos;
        // -crow-tween-detail: the callbacks' time.
        static bool detail;
        static int callDepth, calls;
        static long callStart, callTicks;

        // TweenManager.Update's start and end.
        static bool timing;

        public static void Begin()
        {
            if (timing) began = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public static void End()
        {
            if (timing) ticks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
        }

        // Tween.OnTweenCallback's start and end (a callback inside a callback counts in the outer one).
        public static void CallBegin()
        {
            if (!detail) return;
            if (callDepth++ == 0) callStart = System.Diagnostics.Stopwatch.GetTimestamp();
            calls++;
        }

        public static void CallEnd()
        {
            if (!detail || callDepth == 0) return;
            if (--callDepth == 0) callTicks += System.Diagnostics.Stopwatch.GetTimestamp() - callStart;
        }

        static string Ms(long span, int frames)
        {
            return (span * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames).ToString("0.00", CultureInfo.InvariantCulture);
        }

        // Per frame since the last call: ms of DOTween's update and tweens moved (and the callbacks).
        static string PerFrame(string label, int frames)
        {
            if (frames <= 0) frames = 1;
            string line = label + Ms(ticks, frames) + "/" + ((float)(Gotos - frameGotos) / frames).ToString("0.0", CultureInfo.InvariantCulture);
            if (detail) line += " cb=" + Ms(callTicks, frames) + "/" + ((float)calls / frames).ToString("0.0", CultureInfo.InvariantCulture);
            ticks = callTicks = 0;
            calls = 0;
            frameGotos = Gotos;
            return line;
        }

        static int reportFrame;

        // Fight's window lines (a window starts with Mark).
        internal static string Report()
        {
            return PerFrame("dotween=", Time.frameCount - reportFrame);
        }

        internal static void Mark()
        {
            PerFrame("", 1);
            reportFrame = Time.frameCount;
        }

        static float logAt;
        static int logFrame;

        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (log && now - logAt >= 10f)
            {
                logAt = now;
                Debug.Log("CROW_TWEENS " + PerFrame("ms/moved=", Time.frameCount - logFrame));
                reportFrame = logFrame = Time.frameCount;
            }
            if (censusAt > 0f && now >= censusAt)
            {
                censusAt = now + census;
                try { Census(); } catch (Exception exception) { Debug.Log("CROW_TWEEN_CENSUS failed: " + exception); }
            }
        }

        // ---------------------------------------------------------------- census

        const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        static Type tweenType;
        static FieldInfo targetField, loopsField, loopTypeField, easeField, updateField, decorField, completeField, stepField, updateCallback, delayField, sequencedField;
        static void Census()
        {
            if (tweenType == null)
            {
                tweenType = GameTypes.Tween;
                if (tweenType == null || GameTypes.DOTween == null) return;
                targetField = tweenType.GetField("target", Fields);
                loopsField = tweenType.GetField("loops", Fields);
                loopTypeField = tweenType.GetField("loopType", Fields);
                easeField = tweenType.GetField("easeType", Fields);
                updateField = tweenType.GetField("updateType", Fields);
                decorField = tweenType.GetField("crowDecor", Fields);
                completeField = tweenType.GetField("onComplete", Fields);
                stepField = tweenType.GetField("onStepComplete", Fields);
                updateCallback = tweenType.GetField("onUpdate", Fields);
                delayField = tweenType.GetField("delay", Fields);
                sequencedField = tweenType.GetField("isSequenced", Fields);
            }
            IList list = GameTypes.PlayingTweens();
            if (list == null) return;
            var groups = new Dictionary<string, int>();
            var owners = new Dictionary<string, int>();
            int sequences = 0;
            foreach (object tween in list)
            {
                Type type = tween.GetType();
                if (type.Name.StartsWith("Sequence", StringComparison.Ordinal))
                {
                    sequences++;
                    continue;
                }
                object target = targetField.GetValue(tween);
                string owner = Owner(target, decorField == null ? 0 : (int)decorField.GetValue(tween));
                Type[] args = type.IsGenericType ? type.GetGenericArguments() : new Type[0];
                string plugin = args.Length == 3 ? args[0].Name + "," + args[1].Name : type.Name;
                int loops = (int)loopsField.GetValue(tween);
                string key = owner + " " + plugin + " loops" + (loops == -1 ? "inf" : loops.ToString()) + ":" + loopTypeField.GetValue(tween) +
                             " " + easeField.GetValue(tween) + " " + updateField.GetValue(tween) +
                             (completeField.GetValue(tween) != null ? " onComplete" : "") + (stepField.GetValue(tween) != null ? " onStep" : "") +
                             (updateCallback.GetValue(tween) != null ? " onUpdate" : "") + ((float)delayField.GetValue(tween) > 0f ? " delay" : "") +
                             ((bool)sequencedField.GetValue(tween) ? " sequenced" : "");
                int n;
                groups.TryGetValue(key, out n);
                groups[key] = n + 1;
                owners.TryGetValue(owner, out n);
                owners[owner] = n + 1;
            }
            var line = new StringBuilder("CROW_TWEEN_CENSUS scene=").Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name.Replace(' ', '_'))
                .Append(" playing=").Append(list.Count).Append(" sequences=").Append(sequences).Append(" owners:");
            foreach (KeyValuePair<string, int> pair in owners.OrderByDescending(p => p.Value)) line.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            Debug.Log(line.ToString());
            foreach (KeyValuePair<string, int> pair in groups.OrderByDescending(p => p.Value).Take(40))
                Debug.Log("CROW_TWEEN_CENSUS " + pair.Value + " x " + pair.Key);
        }

        // Who a tween belongs to: Decor's kind where it has one, else the player, an enemy, the UI, the scene.
        static string Owner(object target, int state)
        {
            View view = Views.Of(state);
            if (view != null)
                return view.Kind == View.Breath ? "breath" : view.Kind == View.EnemyFree ? "enemy-free" : view.Kind == View.EnemyFixed ? "enemy-fixed" :
                       view.Kind == View.Prop ? "prop" : "decor";
            var component = target as Component;
            Transform t = component != null ? component.transform : target is GameObject ? ((GameObject)target).transform : null;
            if (t == null) return target == null ? "none" : target.GetType().Name;
            if (t.gameObject.scene.name == Scenes.Kept) return "player";
            if (EnemyNames.RootOf(t) != null) return "enemy";
            if (t.GetComponentInParent<Canvas>() != null) return "ui";
            return "scene";
        }
    }
}
