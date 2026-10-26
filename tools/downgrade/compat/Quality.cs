using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.LowLevel;
using UnityEngine.SceneManagement;

// Lighting and shadow settings, for trying the PS Vita's cheaper ones on any player. The game uses
// one quality level (4 pixel lights, soft and hard shadows at High resolution, 50 m shadow
// distance); in Station Square that is about 300 base draws, 250-270 additive light draws and one
// shadow-casting directional light a frame (VitaLit's one-pass lighting has no additive draws). Each
// flag overrides one setting, at start and after every scene load:
//   -crow-pixel-lights <n>          lights per object drawn per pixel (the others per vertex)
//   -crow-shadows off|hard|all      no shadows, hard shadows only, or the game's soft and hard
//   -crow-shadow-distance <m>       how far from the camera shadows are drawn
//   -crow-shadow-resolution <0-3>   Low, Medium, High, VeryHigh
// The PS Vita's defaults: hard shadows, Low, 12 m (the game: soft and hard, Medium there, 50 m).
// And measurements:
//   -crow-loop-profile <s>          every s seconds, the main thread's time per player loop step
//                                   (CROW_LOOP lines; LoopProfile.cs)
//   -crow-physics-sync-probe        with it, each physics step's transform sync done and timed on its
//                                   own just before the step (FixedUpdate.SyncTransforms(probe))
//   -crow-loop-cpu                  with it, on the PS Vita, the main thread's run time per frame too
//                                   (cpu=; the rest of the frame it waits: for jobs, the render thread,
//                                   the GPU)
//   -crow-frame-probe               with it, on the PS Vita, where the frame waits (CROW_FRAME,
//                                   LoopProfile.cs' FrameProbe): a sampler thread reads the main
//                                   thread's and the render thread's state every 2 ms, by player loop
//                                   step and camera stage
//   -crow-sample-dump <stage> wait|run <ms> <s>
//                                   with it, a crash dump once the main thread has waited (or run) ms
//                                   in a camera stage (cull, draw, after, image, late) or a loop step
//                                   (its name), s seconds after the probe started: every thread's stack
//                                   at that moment (vita.py crashes --pull --thread <name>)
//   -crow-gfx-workers <p>:<cpus>|game  Unity's job workers' priority and CPUs (PS Vita default 64:1;
//                                   Threads.cs' GfxThreads); the cycles take workers=
//   -crow-ab <s> <path> <changes>   s seconds after a scene load, stop time, save <path>-a.png, apply
//                                   the changes (comma-separated: pixel=<n>,
//                                   shadows=off|hard|all, distance=<m>, resolution=<0-3>, and
//                                   every module's: Quality.modules) and save <path>-b.png of the same
//                                   moment; several sets split by '/' are applied in turn, -c.png,
//                                   -d.png... after them
//   -crow-ab-hold <s>               with it, each picture stays s seconds (CROW_AB_SHOW names it as it
//                                   shows), for a system screenshot (vita.py shot) of each
//   -crow-census <s>                every s seconds, the enabled behaviours in loaded scenes by type
//                                   (CROW_CENSUS), the most numerous first
//   -crow-disable <Type,...>        every second, disable every behaviour of these types (by class
//                                   name), to measure what their updates cost
//   -crow-disable-in <scene>        only from 5 s after that scene loads (when the types drive the
//                                   way there, as PlayMaker's FSMs do from the logos to the title)
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Quality
    {
        static int pixelLights = -1, shadowResolution = -1;
        static float shadowDistance = -1f;
        static string shadows;

        // Called by Diag.Install, the probe's one start hook: Unity lists a build's start hooks in the
        // build itself, so a probe DLL swapped into a built player can't add another. Each module under a
        // Guard of its own (Diag.Guard), so one that throws doesn't skip the PS Vita defaults after it
        // (VitaLit's key lights, the shadows, the job workers' placement).
        public static void Install()
        {
            string[] args = ProbeFiles.Args;
            float profile = CrowFlags.Number(args, "-crow-loop-profile", -1);
            if (profile > 0) Diag.Guard("LoopProfile", () => LoopProfile.Install(profile));
            Diag.Guard("Draws", () => Draws.Install(args));  // -crow-casters
            Diag.Guard("Merge", () => Merge.Install(args));  // Merge.cs, PlayerMerge.cs
            Diag.Guard("Cameras", () => Cameras.Install(args));  // Cameras.cs: -crow-rt-cameras, -crow-menu-cull, -crow-near-plane, UiPark
            Diag.Guard("DrawReport", () => Draws.InstallReport(args));  // -crow-draw-report
            Diag.Guard("Perf", () => Perf.Install(args));
            Diag.Guard("VitaLit", () => VitaLit.Install(args));
            string abPath = CrowFlags.Word(args, "-crow-ab", 2), abChanges = CrowFlags.Word(args, "-crow-ab", 3);
            if (abChanges != null)
                Diag.Guard("AB", () => AB.Install(CrowFlags.Number(args, "-crow-ab", 10f), abPath, abChanges, CrowFlags.Number(args, "-crow-ab-hold", 0f)));
            float census = CrowFlags.Number(args, "-crow-census", -1);
            if (census > 0 || CrowFlags.Has(args, "-crow-disable"))
                Diag.Guard("Census", () => Census.Install(census, CrowFlags.Value(args, "-crow-disable"), CrowFlags.Value(args, "-crow-disable-in")));
            pixelLights = (int)CrowFlags.Number(args, "-crow-pixel-lights", -1);
            shadowResolution = (int)CrowFlags.Number(args, "-crow-shadow-resolution", -1);
            shadowDistance = CrowFlags.Number(args, "-crow-shadow-distance", -1);
            shadows = CrowFlags.Value(args, "-crow-shadows");
            if (Application.platform == RuntimePlatform.PSP2)
            {
                // The PS Vita's shadows: hard, Low resolution, 12 m, and (Draws.cs) only the player's
                // parts cast them (none in the scenes without the player prefab: Ladder, the Driving
                // Intro, the Ending; see Draws.CasterOff). The shadow pass's GPU cost is mostly fixed: in Roadside with one-pass
                // lighting (VitaLit.cs) the game's soft Medium 50 m shadows take a frame from 33.4 to
                // 38 ms, these 33.3 (30 fps), with Mara's shadow kept.
                if (shadows == null) shadows = "hard";
                if (shadowResolution < 0) shadowResolution = 0;
                if (shadowDistance < 0) shadowDistance = 12f;
            }
            Diag.Guard("GfxThreads", () => GfxThreads.Install(args));
            if (pixelLights < 0 && shadowResolution < 0 && shadowDistance < 0 && shadows == null) return;
            Apply();
            CrowEvents.Loaded("Quality.sceneLoaded", (scene, mode) => Apply());
        }

        // The modules' own change keys, asked in turn after Quality's: each takes the keys it knows and
        // returns false for the others. Every module with keys is here, none reached through another's Change.
        static readonly Func<string, string, bool>[] modules =
        {
            VitaLit.Change, GfxThreads.Change, Perf.Change, Cameras.Change, UiPark.Change, RigPark.Change,
            EnemyRebuild.Change, PhysCost.Change, Merge.ChangePlayer, FixedStep.Change, FastPaths.Change, Decor.Change,
            SharedClosures.Change, FilterGate.Change, Cuts.Change, CamEffect.Change, FsmTrim.Change, FsmCompile.Change,
            Pad.Change, Clock.Change, TweenProbe.Change, GcHandles.Change, AudioCensus.Change
        };

        // Applies a change list (-crow-ab, -crow-cycle, -crow-fight-cycle).
        internal static void Change(string changes)
        {
            foreach (string change in changes.Split(','))
            {
                string[] pair = change.Split('=');
                if (pair.Length != 2) continue;
                float number;
                float.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out number);
                if (pair[0] == "pixel") pixelLights = (int)number;
                else if (pair[0] == "shadows") shadows = pair[1];
                else if (pair[0] == "distance") shadowDistance = number;
                else if (pair[0] == "resolution") shadowResolution = (int)number;
                else if (pair[0] == "casters") Draws.Casters(pair[1]);
                else if (!modules.Any(module => module(pair[0], pair[1]))) Debug.Log("CROW_QUALITY unknown change " + change);
            }
            Apply();
        }

        static void Apply()
        {
            if (pixelLights >= 0) QualitySettings.pixelLightCount = pixelLights;
            if (shadowResolution >= 0) QualitySettings.shadowResolution = (ShadowResolution)shadowResolution;
            if (shadowDistance >= 0) QualitySettings.shadowDistance = shadowDistance;
            if (shadows == "off") QualitySettings.shadows = ShadowQuality.Disable;
            else if (shadows == "hard") QualitySettings.shadows = ShadowQuality.HardOnly;
            else if (shadows == "all") QualitySettings.shadows = ShadowQuality.All;
        }
    }

    // -crow-census and -crow-disable: which scripts run every frame, and what turning a kind of
    // them off saves (with -crow-loop-profile's Update.ScriptRunBehaviourUpdate).
    static class Census
    {
        static float every, lastCensus, lastDisable, loaded;
        static string[] disabled;
        static string disableIn;

        public static void Install(float seconds, string types, string scene)
        {
            every = seconds;
            disabled = types == null ? null : types.Split(',');
            disableIn = scene;
            CrowEvents.Loaded("Census.sceneLoaded", (s, mode) => loaded = Time.realtimeSinceStartup);
            CrowEvents.BeforeRender("Census.Tick", Tick);
            if (disabled != null) Debug.Log("CROW_CENSUS disabling " + types + (scene == null ? "" : " in " + scene));
        }

        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            bool here = disableIn == null || (SceneManager.GetActiveScene().name == disableIn && now - loaded >= 5f);
            if (disabled != null && here && now - lastDisable >= 1f)
            {
                lastDisable = now;
                foreach (Behaviour behaviour in UnityEngine.Object.FindObjectsOfType<Behaviour>())
                    if (behaviour.enabled && Array.IndexOf(disabled, behaviour.GetType().Name) >= 0) behaviour.enabled = false;
            }
            if (every <= 0f || now - lastCensus < every) return;
            lastCensus = now;
            var counts = new Dictionary<Type, int>();
            foreach (Behaviour behaviour in UnityEngine.Object.FindObjectsOfType<Behaviour>())
            {
                if (!behaviour.isActiveAndEnabled) continue;
                int count;
                counts.TryGetValue(behaviour.GetType(), out count);
                counts[behaviour.GetType()] = count + 1;
            }
            var line = new StringBuilder("CROW_CENSUS scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            line.Append(" total=").Append(counts.Values.Sum());
            foreach (KeyValuePair<Type, int> pair in counts.OrderByDescending(p => p.Value).Take(40))
            {
                Type type = pair.Key;
                const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                bool update = type.GetMethods(all).Any(m => m.Name == "Update" || m.Name == "LateUpdate" || m.Name == "FixedUpdate");
                line.Append(' ').Append(type.Name).Append(update ? "*" : "").Append('=').Append(pair.Value);
            }
            Debug.Log(line.ToString());
            Actions();
        }

        // The actions of every enabled PlayMaker FSM's active state that are still running (not
        // Finished): what PlayMaker updates every frame (CROW_FSM). By reflection: CrowCompat
        // doesn't reference PlayMaker.
        static void Actions()
        {
            Type fsmType = GameTypes.PlayMakerFsm;
            if (fsmType == null) return;
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            int fsms = 0, running = 0;
            var counts = new Dictionary<string, int>();
            foreach (UnityEngine.Object component in UnityEngine.Object.FindObjectsOfType(fsmType))
            {
                var behaviour = component as Behaviour;
                if (behaviour == null || !behaviour.isActiveAndEnabled) continue;
                fsms++;
                try
                {
                    object fsm = fsmType.GetProperty("Fsm", all).GetValue(component, null);
                    object state = fsm == null ? null : fsm.GetType().GetProperty("ActiveState", all).GetValue(fsm, null);
                    var actions = state == null ? null : state.GetType().GetProperty("Actions", all).GetValue(state, null) as Array;
                    if (actions == null) continue;
                    foreach (object action in actions)
                    {
                        if (action == null) continue;
                        System.Reflection.PropertyInfo finished = action.GetType().GetProperty("Finished", all);
                        if (finished != null && (bool)finished.GetValue(action, null)) continue;
                        running++;
                        string name = action.GetType().Name;
                        int count;
                        counts.TryGetValue(name, out count);
                        counts[name] = count + 1;
                    }
                }
                catch (Exception)
                {
                }
            }
            var line = new StringBuilder("CROW_FSM fsms=").Append(fsms).Append(" running=").Append(running);
            foreach (KeyValuePair<string, int> pair in counts.OrderByDescending(p => p.Value).Take(40)) line.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            Debug.Log(line.ToString());
        }
    }

    // -crow-ab: two screenshots of one moment, with the game's settings and with changed ones. Time
    // stops (timeScale 0) s seconds after a scene load; frames still render, so the lights and
    // shadows of the second shot are the changed ones. The post-processing grain still moves.
    static class AB
    {
        static float after, loaded, hold, shown;
        static string path;
        static string[] sets;
        static int step = -1, frame;

        public static void Install(float seconds, string prefix, string list, float holdSeconds)
        {
            after = seconds;
            path = prefix;
            sets = list.Split('/');
            hold = holdSeconds;
            CrowEvents.Loaded("AB.sceneLoaded", (scene, mode) =>
            {
                if (step > 0 && step <= 2 * sets.Length + 1) return;  // waiting: s seconds after the last load
                loaded = Time.realtimeSinceStartup;
                step = 0;
            });
            CrowEvents.BeforeRender("AB.Tick", Tick);
        }

        // Steps: 0 waits for the moment; then for each picture an odd step that saves it (after 10
        // frames and, with -crow-ab-hold, the hold) and an even one that applies the next set.
        static void Tick()
        {
            if (step < 0 || step > 2 * sets.Length + 1) return;
            if (step == 0)
            {
                if (Time.realtimeSinceStartup - loaded < after) return;
                Time.timeScale = 0f;
                step = 1;
                frame = Time.frameCount;
                shown = -1f;
                return;
            }
            if (Time.frameCount - frame < 10) return;
            int picture = (step - 1) / 2;
            string name = path + "-" + (char)('a' + picture) + ".png";
            string set = picture == 0 ? "before" : sets[picture - 1];
            if (step % 2 == 1)
            {
                if (hold > 0f)
                {
                    if (shown < 0f)
                    {
                        shown = Time.realtimeSinceStartup;
                        Debug.Log("CROW_AB_SHOW " + (char)('a' + picture) + " " + set);
                        return;
                    }
                    if (Time.realtimeSinceStartup - shown < hold) return;
                }
                ScreenCapture.CaptureScreenshot(name);
                Debug.Log("CROW_AB saved " + name + " (" + set + ")");
                if (picture == sets.Length) Debug.Log("CROW_AB done");
            }
            else Quality.Change(sets[picture]);
            shown = -1f;
            frame = Time.frameCount;
            step++;
        }
    }

}
