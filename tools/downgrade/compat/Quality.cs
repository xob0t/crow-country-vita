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
// shadow-casting directional light a frame (Diag's heartbeat). Each flag overrides one setting, at
// start and after every scene load:
//   -crow-pixel-lights <n>          lights per object drawn per pixel (the others per vertex)
//   -crow-shadows off|hard|all      no shadows, hard shadows only, or the game's soft and hard
//   -crow-shadow-distance <m>       how far from the camera shadows are drawn
//   -crow-shadow-resolution <0-3>   Low, Medium, High, VeryHigh
// The PS Vita's defaults: hard shadows, Low, 12 m (the game: soft and hard, Medium there, 50 m).
// And measurements:
//   -crow-loop-profile <s>          every s seconds, the main thread's time per player loop step
//                                   (CROW_LOOP lines; see LoopProfile)
//   -crow-physics-sync-probe        with it, each physics step's transform sync done and timed on its
//                                   own just before the step (FixedUpdate.SyncTransforms(probe))
//   -crow-loop-cpu                  with it, on the PS Vita, the main thread's run time per frame too
//                                   (cpu=; the rest of the frame it waits: for jobs, the render thread,
//                                   the GPU)
//   -crow-frame-probe               with it, on the PS Vita, where the frame waits (CROW_FRAME, see
//                                   FrameProbe): a sampler thread reads the main thread's and the render
//                                   thread's state every 2 ms, by player loop step and camera stage
//   -crow-sample-dump <stage> wait|run <ms> <s>
//                                   with it, a crash dump once the main thread has waited (or run) ms
//                                   in a camera stage (cull, draw, after, image, late) or a loop step
//                                   (its name), s seconds after the probe started: every thread's stack
//                                   at that moment (vita.py crashes --pull --thread <name>)
//   -crow-gfx-workers <p>:<cpus>|game  Unity's job workers' priority and CPUs (PS Vita default 64:1; see
//                                   GfxThreads); the cycles take workers= and gfxthread=
//   -crow-light-report <s>        every s seconds, the forward renderer's light passes as Unity
//                                   assigns them, per light (CROW_LIGHTS, CROW_LIGHT; see LightReport)
//   -crow-ab <s> <path> <changes>   s seconds after a scene load, stop time, save <path>-a.png, apply
//                                   the changes (comma-separated: pixel=<n>,
//                                   shadows=off|hard|all, distance=<m>, resolution=<0-3>, and
//                                   Perf.cs's) and save <path>-b.png of the same moment; several sets
//                                   split by '/' are applied in turn, -c.png, -d.png... after them
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
        // build itself, so a probe DLL swapped into a built player can't add another.
        public static void Install()
        {
            string[] args = ProbeFiles.Args;
            float profile = CrowFlags.Number(args, "-crow-loop-profile", -1);
            if (profile > 0) LoopProfile.Install(profile);
            float report = CrowFlags.Number(args, "-crow-light-report", -1);
            if (report > 0) LightReport.Install(report);
            Draws.Install(args);
            Perf.Install(args);
            VitaLit.Install(args);
            int ab = Array.IndexOf(args, "-crow-ab");
            if (ab >= 0 && ab + 3 < args.Length) AB.Install(CrowFlags.Number(args, "-crow-ab", 10f), args[ab + 2], args[ab + 3], CrowFlags.Number(args, "-crow-ab-hold", 0f));
            float census = CrowFlags.Number(args, "-crow-census", -1);
            if (census > 0 || CrowFlags.Has(args, "-crow-disable"))
                Census.Install(census, CrowFlags.Value(args, "-crow-disable"), CrowFlags.Value(args, "-crow-disable-in"));
            pixelLights = (int)CrowFlags.Number(args, "-crow-pixel-lights", -1);
            shadowResolution = (int)CrowFlags.Number(args, "-crow-shadow-resolution", -1);
            shadowDistance = CrowFlags.Number(args, "-crow-shadow-distance", -1);
            shadows = CrowFlags.Value(args, "-crow-shadows");
            if (Application.platform == RuntimePlatform.PSP2)
            {
                // The PS Vita's shadows: hard, Low resolution, 12 m, and (Draws.cs) only the player's
                // parts cast them. The shadow pass's GPU cost is mostly fixed: in Roadside with one-pass
                // lighting (VitaLit.cs) the game's soft Medium 50 m shadows take a frame from 33.4 to
                // 38 ms, these 33.3 (30 fps), with Mara's shadow kept.
                if (shadows == null) shadows = "hard";
                if (shadowResolution < 0) shadowResolution = 0;
                if (shadowDistance < 0) shadowDistance = 12f;
            }
            GfxThreads.Install(args);
            if (pixelLights < 0 && shadowResolution < 0 && shadowDistance < 0 && shadows == null) return;
            Apply();
            SceneManager.sceneLoaded += (scene, mode) => Apply();
        }

        // The modules' own change keys, asked in turn after Quality's: each takes the keys it knows and
        // returns false for the others.
        static readonly Func<string, string, bool>[] modules =
        {
            GfxThreads.Change, Perf.Change, EnemyCost.Change, FixedStep.Change, FastPaths.Change, Decor.Change, SharedClosures.Change,
            FilterGate.Change, Cuts.Change, CamEffect.Change, FsmTrim.Change, FsmCompile.Change, Pad.Change,
            Clock.Change, TweenProbe.Change
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
                if (pair[0] == "lit") VitaLit.Set(pair[1]);
                else if (pair[0] == "litkeys" || pair[0] == "litrange") VitaLit.SetLimits(pair[0], number);
                else if (pair[0] == "litsoa") VitaLit.SetLimits(pair[0], pair[1] == "off" ? 0f : 1f);
                else if (pair[0] == "pixel") pixelLights = (int)number;
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
            SceneManager.sceneLoaded += (s, mode) => loaded = Time.realtimeSinceStartup;
            Application.onBeforeRender += Tick;
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
            Type fsmType = Type.GetType("PlayMakerFSM, PlayMaker");
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
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (step > 0 && step <= 2 * sets.Length + 1) return;  // waiting: s seconds after the last load
                loaded = Time.realtimeSinceStartup;
                step = 0;
            };
            Application.onBeforeRender += Tick;
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

    // -crow-loop-profile: where the main thread's frame goes, without a profiler (the PS Vita build
    // has none). A marker goes between every two steps of Unity's player loop (PlayerLoop, no
    // MonoBehaviour, so a code-only rebuild can add it) and adds the time since the previous marker
    // to the step between them: Update.ScriptRunBehaviourUpdate is every script's Update (PlayMaker's
    // FSMs among them), PostLateUpdate.FinishFrameRendering the cameras' culling and draw submission,
    // the steps around the frame's start the wait for the display and the GPU. Each line gives the
    // milliseconds per frame of the costliest steps.
    static class LoopProfile
    {
        static readonly List<string> labels = new List<string>();
        static long[] ticks;
        static long last, frameStart, worst;
        static int previous = -1, frames;
        static float every, lastReport;
        // -crow-loop-cpu (PS Vita): the main thread's own run time from the kernel (CrowClock), read at
        // each frame's start, so the frame splits into work and waiting. Per step it would mislead: the
        // kernel adds a thread's run time when it leaves the CPU, not while it runs.
        static int mainUid;
        static long runTotal, lastRun;
        static string flushLabel;

        // -crow-cycle: report now (at the next frame start) under a label, and start a new window.
        internal static void Flush(string label)
        {
            flushLabel = label ?? "";
        }

        // Fight.cs: each step's ticks since Install, never cleared (the reports clear ticks), so a
        // caller can take differences over windows of its own.
        static long[] totals;
        internal static bool Installed { get { return ticks != null; } }
        // Fight's physics test: called in each physics step, before the physics (the time goes to the step before).
        internal static Action FixedHook;
        internal static List<string> Labels { get { return labels; } }
        internal static long[] Totals { get { return totals; } }

        public static void Install(float seconds)
        {
            every = seconds;
            if (Array.IndexOf(ProbeFiles.Args, "-crow-loop-cpu") >= 0) mainUid = Threads.SelfAnyway();
            PlayerLoopSystem root = PlayerLoop.GetDefaultPlayerLoop();
            PlayerLoopSystem[] phases = root.subSystemList;
            for (int p = 0; p < phases.Length; p++)
            {
                string phase = phases[p].type == null ? "?" : phases[p].type.Name;
                PlayerLoopSystem[] steps = phases[p].subSystemList ?? new PlayerLoopSystem[0];
                var list = new List<PlayerLoopSystem>();
                foreach (PlayerLoopSystem step in steps)
                {
                    string name = step.type == null ? "?" : step.type.Name;
                    if (name == "PhysicsFixedUpdate")
                        list.Add(new PlayerLoopSystem { type = typeof(LoopProfile), updateDelegate = () => { if (FixedHook != null) FixedHook(); } });
                    // -crow-physics-sync-probe: the transforms' sync with the physics scene, which each
                    // physics step does first, done (and timed) on its own just before the step.
                    if (name == "PhysicsFixedUpdate" && Array.IndexOf(ProbeFiles.Args, "-crow-physics-sync-probe") >= 0)
                        list.Add(SyncProbe(phase + ".SyncTransforms(probe)"));
                    list.Add(Marker(phase + "." + name));
                    list.Add(step);
                }
                list.Add(Marker(phase + ".(end)"));
                phases[p].subSystemList = list.ToArray();
            }
            root.subSystemList = phases;
            ticks = new long[labels.Count];
            totals = new long[labels.Count];
            PlayerLoop.SetPlayerLoop(root);
            if (Array.IndexOf(ProbeFiles.Args, "-crow-frame-probe") >= 0) FrameProbe.Start();
            Camera.onPreCull += camera => CameraMark(camera, 0);
            Camera.onPreRender += camera => CameraMark(camera, 1);
            Camera.onPostRender += camera => CameraMark(camera, 2);
            Debug.Log("CROW_LOOP installed " + labels.Count + " markers");
        }

        // Per camera: cull (onPreCull to onPreRender), draw (to onPostRender) and after (from
        // onPostRender to the next camera or the frame's end: its image effects, OnRenderImage, the
        // overlay canvases, the frame's end). after splits into image (CamEffect.RenderImage, the
        // camera effect's passes) and late (from the effect's end to the next camera or the frame's
        // end); the rest of after is before the effect (Unity's own image-effect setup).
        static readonly Dictionary<string, long[]> cameras = new Dictionary<string, long[]>();
        static long[] lastCamera;
        static long cameraMark, imageMark;

        static void CameraMark(Camera camera, int stage)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (stage == 0) CameraEnd(now);
            long[] slot;
            if (!cameras.TryGetValue(camera.name, out slot)) cameras[camera.name] = slot = new long[6];
            FrameProbe.Stage = stage + 1;
            if (stage == 1) slot[0] += now - cameraMark;
            if (stage == 2)
            {
                slot[1] += now - cameraMark;
                slot[3]++;
                lastCamera = slot;
                imageMark = 0;
            }
            cameraMark = now;
        }

        // CamEffect.RenderImage's start and end (the camera that rendered last is the effect's).
        internal static void ImageMark(bool start)
        {
            if (ticks == null || lastCamera == null) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (start)
            {
                imageMark = now;
                FrameProbe.Stage = 4;
                return;
            }
            if (imageMark == 0) return;
            lastCamera[4] += now - imageMark;
            imageMark = now;
            FrameProbe.Stage = 5;
        }

        static void CameraEnd(long now)
        {
            if (lastCamera != null)
            {
                lastCamera[2] += now - cameraMark;
                if (imageMark != 0 && FrameProbe.Stage == 5) lastCamera[5] += now - imageMark;
            }
            lastCamera = null;
            imageMark = 0;
            FrameProbe.Stage = 0;
        }

        static PlayerLoopSystem SyncProbe(string label)
        {
            int index = labels.Count;
            labels.Add(label);
            return new PlayerLoopSystem
            {
                type = typeof(LoopProfile),
                updateDelegate = () =>
                {
                    Mark(index);
                    Physics.SyncTransforms();
                }
            };
        }

        static PlayerLoopSystem Marker(string label)
        {
            int index = labels.Count;
            labels.Add(label);
            return new PlayerLoopSystem { type = typeof(LoopProfile), updateDelegate = () => Mark(index) };
        }

        static void Mark(int index)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (previous >= 0)
            {
                ticks[previous] += now - last;
                totals[previous] += now - last;
            }
            previous = index;
            last = now;
            FrameProbe.Step = index;
            if (index != 0) return;
            if (mainUid > 0)
            {
                long run = Threads.RunMicros(mainUid);
                if (run >= 0 && lastRun > 0 && frameStart != 0) runTotal += run - lastRun;
                lastRun = run;
            }
            CameraEnd(now);
            if (frameStart != 0)
            {
                frames++;
                worst = Math.Max(worst, now - frameStart);
                FrameProbe.Frame(now - frameStart);
            }
            frameStart = now;
            float time = Time.realtimeSinceStartup;
            string label = flushLabel;
            if (label == null && (time - lastReport < every || frames == 0)) return;
            flushLabel = null;
            if (frames == 0) return;
            double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            long total = ticks.Sum();
            var line = new StringBuilder("CROW_LOOP scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            if (label != null) line.Append(" cycle=").Append(label);
            line.Append(" frames=").Append(frames).Append(" frame=").Append((total * ms / frames).ToString("F1"));
            if (mainUid > 0) line.Append(" cpu=").Append((runTotal / 1000.0 / frames).ToString("F1"));
            line.Append(" worst=").Append((worst * ms).ToString("F1"));
            foreach (int i in Enumerable.Range(0, ticks.Length).OrderByDescending(i => ticks[i]).Take(14))
            {
                double each = ticks[i] * ms / frames;
                if (each < 0.01) break;
                line.Append(' ').Append(labels[i]).Append('=').Append(each.ToString(each < 1 ? "F2" : "F1"));
            }
            runTotal = 0;
            Debug.Log(line.ToString());
            line = new StringBuilder("CROW_CAMS");
            foreach (KeyValuePair<string, long[]> pair in cameras.OrderByDescending(p => p.Value[0] + p.Value[1] + p.Value[2]))
            {
                line.Append(' ').Append(pair.Key.Replace(' ', '_')).Append("[x").Append((pair.Value[3] / (double)frames).ToString("F1"));
                line.Append(" cull=").Append((pair.Value[0] * ms / frames).ToString("F1")).Append(" draw=").Append((pair.Value[1] * ms / frames).ToString("F1"));
                line.Append(" after=").Append((pair.Value[2] * ms / frames).ToString("F1"));
                if (pair.Value[4] > 0)
                    line.Append(" image=").Append((pair.Value[4] * ms / frames).ToString("F1")).Append(" late=").Append((pair.Value[5] * ms / frames).ToString("F1"));
                line.Append(']');
                Array.Clear(pair.Value, 0, pair.Value.Length);
            }
            Debug.Log(line.ToString());
            FrameProbe.Report(label, total * ms / frames);
            Array.Clear(ticks, 0, ticks.Length);
            frames = 0;
            worst = 0;
            lastReport = time;
        }
    }

    // -crow-frame-probe (PS Vita; with LoopProfile): where a frame waits. The main thread's run time
    // (-crow-loop-cpu) says how much of a frame it waits, not where or for what. A sampler thread reads,
    // every 2 ms, the main thread's kernel state (running or ready, or waiting, and on what) with the
    // player loop step and camera stage it is in (LoopProfile's marks: Step, Stage), the render
    // thread's state (the thread named *Gfx*) and which of the game's threads each CPU runs. A state
    // read is right whatever CPU the sampler takes: a thread it preempts reads as ready, not waiting.
    // CROW_FRAME per LoopProfile report, the samples' shares as ms of the wall frame: main run/wait,
    // render thread run/wait, both waiting (none of the two runs: the GPU or the display), the job
    // workers' CPUs in use; the steps and camera stages the main thread waits in (no suffix: before or
    // without a camera, [cull], [draw], [after]: from the camera's onPostRender to the frame's end), each
    // with (rX) the part of that wait the render thread ran; the objects each waits on (wait type in hex
    // : name = share of its waits); the frames by length in vertical blanks (16.7 ms: <1/1-2/2-3/3-4/
    // 4-5/5+); and FrameTimingManager's GPU time if the player gives any (ftmGpu=none if not).
    static class FrameProbe
    {
        internal static volatile int Step, Stage;
        static int mainUid, renderUid = -1;
        static readonly object gate = new object();
        static readonly Dictionary<int, int> kinds = new Dictionary<int, int>();  // uid -> 0 other, 1 render, 2 worker
        static readonly Dictionary<int, string> names = new Dictionary<int, string>();
        // per key (step * 4 + stage) * 3: samples, main waiting, main waiting while the render thread runs
        static long[] perKey = new long[1024];
        static long samples, mainWait, renderRun, renderWait, bothWait, workerCpu, renderMissing;
        static readonly Dictionary<long, long> mainObjects = new Dictionary<long, long>();
        static readonly Dictionary<long, long> renderObjects = new Dictionary<long, long>();
        static readonly int[] bins = new int[6];
        static double gpuSum, cpuSum;
        static int gpuFrames;
        static readonly FrameTiming[] timings = new FrameTiming[1];
        static bool timingFailed;

        internal static void Start()
        {
            if (Application.platform != RuntimePlatform.PSP2) return;
            mainUid = Threads.SelfAnyway();
            if (mainUid <= 0) return;
            var thread = new System.Threading.Thread(Sample) { IsBackground = true, Name = "CrowFrameProbe" };
            thread.Start();
            Debug.Log("CROW_FRAME probe started, main thread 0x" + mainUid.ToString("X"));
            DumpInstall(ProbeFiles.Args);
        }

        static int Kind(int uid)
        {
            int kind;
            if (kinds.TryGetValue(uid, out kind)) return kind;
            string name = Threads.ThreadName(uid) ?? "?";
            kind = name.Contains("Gfx") ? 1 : name.Contains("Worker") ? 2 : 0;
            lock (gate)
            {
                kinds[uid] = kind;
                names[uid] = name;
            }
            if (kind == 1 && renderUid < 0)
            {
                renderUid = uid;
                Debug.Log("CROW_FRAME render thread " + name + " 0x" + uid.ToString("X"));
            }
            return kind;
        }

        static void Sample()
        {
            var main = new int[4];
            var render = new int[4];
            var running = new int[3];
            while (true)
            {
                System.Threading.Thread.Sleep(2);
                int key = Step * 8 + Stage;
                if (Threads.Wait(mainUid, main) < 0) continue;
                if (dumpStage >= 0) DumpCheck(key, (main[0] & 8) != 0);
                int workers = 0;
                if (Threads.Running(running) >= 0)
                    for (int cpu = 0; cpu < 3; cpu++)
                        if (running[cpu] > 0 && running[cpu] != mainUid && Kind(running[cpu]) == 2) workers++;
                bool haveRender = renderUid > 0 && Threads.Wait(renderUid, render) >= 0;
                bool mainWaits = (main[0] & 8) != 0;
                bool renderWaits = haveRender && (render[0] & 8) != 0;
                lock (gate)
                {
                    if (key * 3 + 2 >= perKey.Length)
                    {
                        var grown = new long[(key + 16) * 3];
                        Array.Copy(perKey, grown, perKey.Length);
                        perKey = grown;
                    }
                    samples++;
                    perKey[key * 3]++;
                    workerCpu += workers;
                    if (!haveRender) renderMissing++;
                    else if (renderWaits) renderWait++;
                    else renderRun++;
                    if (renderWaits) Count(renderObjects, render[1], render[2]);
                    if (!mainWaits) continue;
                    mainWait++;
                    perKey[key * 3 + 1]++;
                    if (haveRender && !renderWaits) perKey[key * 3 + 2]++;
                    if (renderWaits) bothWait++;
                    Count(mainObjects, main[1], main[2]);
                }
            }
        }

        static void Count(Dictionary<long, long> objects, int type, int id)
        {
            long key = ((long)(uint)type << 32) | (uint)id;
            long count;
            objects.TryGetValue(key, out count);
            objects[key] = count + 1;
        }

        // At each frame's start (LoopProfile), with the last frame's length.
        internal static void Frame(long ticks)
        {
            if (mainUid <= 0) return;
            double ms = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            bins[Math.Min(5, (int)(ms / 16.6667))]++;
            if (timingFailed) return;
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timings) > 0 && timings[0].gpuFrameTime > 0)
                {
                    gpuSum += timings[0].gpuFrameTime;
                    cpuSum += timings[0].cpuFrameTime;
                    gpuFrames++;
                }
            }
            catch (Exception exception)
            {
                timingFailed = true;
                Debug.Log("CROW_FRAME FrameTimingManager failed: " + exception.Message);
            }
        }

        static string Objects(Dictionary<long, long> objects, long total, int take)
        {
            var text = new StringBuilder();
            foreach (KeyValuePair<long, long> pair in objects.OrderByDescending(p => p.Value).Take(take))
            {
                int type = (int)(pair.Key >> 32), id = (int)pair.Key;
                string name;
                if (!names.TryGetValue(id, out name))
                {
                    name = Threads.SyncName(id, type) ?? ("0x" + id.ToString("X"));
                    names[id] = name;
                }
                text.Append(text.Length == 0 ? "" : ",").Append(type.ToString("X")).Append(':').Append(name.Replace(' ', '_'))
                    .Append('=').Append((100.0 * pair.Value / Math.Max(1, total)).ToString("F0")).Append('%');
            }
            return text.ToString();
        }

        static readonly string[] stages = { "", "[cull]", "[draw]", "[after]", "[image]", "[late]", "", "" };

        // -crow-sample-dump <stage> wait|run <ms> <s>: a crash dump (every thread's registers and stack,
        // vita.py crashes --pull --thread <name>) taken when the main thread has been waiting (or
        // running) for ms in a camera stage (cull, draw, after, image, late; or the step's name, as
        // CROW_FRAME's waits give it), s seconds or more after startup: where it waits and what the
        // render thread and the job workers do meanwhile, one sample a launch.
        static int dumpStage = -1, dumpStep = -1;
        static bool dumpWait;
        static float dumpMs, dumpAfter;
        static readonly System.Diagnostics.Stopwatch dumpClock = System.Diagnostics.Stopwatch.StartNew();
        static long dumpSince;

        internal static void DumpInstall(string[] args)
        {
            int index = Array.IndexOf(args, "-crow-sample-dump");
            if (index < 0 || index + 4 >= args.Length) return;
            string where = args[index + 1];
            dumpWait = args[index + 2] != "run";
            float.TryParse(args[index + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out dumpMs);
            float.TryParse(args[index + 4], NumberStyles.Float, CultureInfo.InvariantCulture, out dumpAfter);
            int stage = Array.IndexOf(stages, "[" + where + "]");
            if (stage > 0) dumpStage = stage;
            else
            {
                dumpStep = LoopProfile.Labels.FindIndex(l => l.EndsWith("." + where, StringComparison.Ordinal));
                if (dumpStep >= 0) dumpStage = 0;
            }
            Debug.Log("CROW_SAMPLE_DUMP " + (dumpStage < 0 ? "unknown stage " + where : where + (dumpWait ? " waiting " : " running ") + dumpMs + " ms, after " + dumpAfter + " s"));
        }

        static void DumpCheck(int key, bool waits)
        {
            int stage = key % 8, step = key / 8;
            bool match = stage == dumpStage && (dumpStep < 0 || step == dumpStep) && waits == dumpWait && dumpClock.Elapsed.TotalSeconds >= dumpAfter;
            long now = dumpClock.ElapsedTicks;
            if (!match)
            {
                dumpSince = 0;
                return;
            }
            if (dumpSince == 0) dumpSince = now;
            if ((now - dumpSince) * 1000.0 / System.Diagnostics.Stopwatch.Frequency < dumpMs) return;
            // At once: a log line flushed first would give the main thread time to move on (the line is
            // lost with the crash; the dump's own time says when).
            System.Runtime.InteropServices.Marshal.WriteInt32(new IntPtr(16), 0);  // unmapped: a data abort
        }

        internal static void Report(string label, double frameMs)
        {
            if (mainUid <= 0) return;
            var line = new StringBuilder("CROW_FRAME");
            if (label != null) line.Append(" cycle=").Append(label);
            lock (gate)
            {
                double n = Math.Max(1, samples);
                Func<long, string> ms = count => (frameMs * count / n).ToString("F1");
                line.Append(" samples=").Append(samples).Append(" frame=").Append(frameMs.ToString("F1"));
                line.Append(" mainRun=").Append(ms(samples - mainWait)).Append(" mainWait=").Append(ms(mainWait));
                line.Append(" renderRun=").Append(ms(renderRun)).Append(" renderWait=").Append(ms(renderWait));
                if (renderMissing > 0) line.Append(" renderUnknown=").Append(ms(renderMissing));
                line.Append(" bothWait=").Append(ms(bothWait)).Append(" workerCpus=").Append((workerCpu / n).ToString("F2"));
                line.Append(" waits:");
                var keys = new List<int>();
                for (int k = 0; k * 3 + 2 < perKey.Length; k++) if (perKey[k * 3 + 1] > 0) keys.Add(k);
                foreach (int k in keys.OrderByDescending(k => perKey[k * 3 + 1]).Take(8))
                {
                    int step = k / 8, stage = k % 8;
                    string name = step < LoopProfile.Labels.Count ? LoopProfile.Labels[step] : "?";
                    name = name.Substring(name.IndexOf('.') + 1);
                    line.Append(' ').Append(name).Append(stages[stage]).Append('=').Append(ms(perKey[k * 3 + 1]))
                        .Append("(r").Append(ms(perKey[k * 3 + 2])).Append(')');
                }
                line.Append(" mainOn=").Append(Objects(mainObjects, mainWait, 4));
                line.Append(" renderOn=").Append(Objects(renderObjects, renderWait, 4));
                Array.Clear(perKey, 0, perKey.Length);
                samples = mainWait = renderRun = renderWait = bothWait = workerCpu = renderMissing = 0;
                mainObjects.Clear();
                renderObjects.Clear();
            }
            line.Append(" vblanks=").Append(string.Join("/", bins.Select(b => b.ToString()).ToArray()));
            if (gpuFrames > 0)
                line.Append(" ftmGpu=").Append((gpuSum / gpuFrames).ToString("F1")).Append(" ftmCpu=").Append((cpuSum / gpuFrames).ToString("F1"))
                    .Append(" ftmFrames=").Append(gpuFrames);
            else line.Append(" ftmGpu=none");
            Array.Clear(bins, 0, bins.Length);
            gpuSum = cpuSum = 0;
            gpuFrames = 0;
            Debug.Log(line.ToString());
        }
    }

    // Unity's job workers ("Worker Thread", two): with graphics jobs they record the camera's draws
    // (ForwardRenderLoopJob, one job) while the main thread goes on, and the main thread waits for that
    // job, and its cleanup, at the frame's end (GfxDeviceClient::EndAsyncJobFrame; sample dumps,
    // status.md, Render step). The player starts them at priority 0x9f on CPUs 0-2, below the render
    // thread (0x40, CPU 1) and FMOD's mixer (0x40, CPU 2), and on CPU 0 below the main thread, so the job
    // ran late and the main thread waited about 4 ms a frame for it. -crow-gfx-workers <priority>:<cpus>
    // sets them once they first run (a thread polls which threads run, as Audio.cs finds UnityPreload);
    // the PS Vita's default is 64:1, the render thread's priority on its CPU (not the mixer's, which
    // keeps CPU 2 to itself: audio as before); game leaves them. -crow-cycle / -crow-fight-cycle / -crow-ab
    // take workers=<priority>:<cpus>|game and gfxthread=... (the render thread, UnityGfxDeviceWorker; a
    // test: on CPU 2 it shares the mixer's). CROW_GFX_THREADS logs each change with what the thread had.
    static class GfxThreads
    {
        static readonly Dictionary<int, int[]> original = new Dictionary<int, int[]>();
        static readonly Dictionary<int, string> found = new Dictionary<int, string>();
        static readonly object gate = new object();
        static string workers, render;
        static bool finding;

        internal static void Install(string[] args)
        {
            string value = CrowFlags.Value(args, "-crow-gfx-workers") ?? (Application.platform == RuntimePlatform.PSP2 ? "64:1" : null);
            if (value != null && value != "game") Change("workers", value);
        }

        internal static bool Change(string key, string value)
        {
            if (key != "workers" && key != "gfxthread") return false;
            if (Application.platform != RuntimePlatform.PSP2) return true;
            lock (gate)
            {
                if (key == "workers") workers = value;
                else render = value;
            }
            Apply(key == "workers" ? "Worker Thread" : "GfxDevice", value);
            if (!finding)
            {
                finding = true;
                new System.Threading.Thread(Find) { IsBackground = true, Name = "CrowGfxThreads" }.Start();
            }
            return true;
        }

        // Until both workers and the render thread are known (at most 2 minutes).
        static void Find()
        {
            var uids = new int[3];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (clock.ElapsedMilliseconds < 120000)
                {
                    System.Threading.Thread.Sleep(1);
                    if (Threads.Running(uids) < 0) continue;
                    foreach (int uid in uids)
                    {
                        if (uid == 0) continue;
                        lock (gate) if (found.ContainsKey(uid)) continue;
                        string name = Threads.ThreadName(uid) ?? "";
                        bool worker = name.Contains("Worker Thread"), gfx = name.Contains("GfxDevice");
                        lock (gate) found[uid] = worker || gfx ? name : null;
                        string value;
                        lock (gate) value = worker ? workers : gfx ? render : null;
                        if (value != null) Set(uid, name, value);
                    }
                    int known;
                    lock (gate) known = found.Values.Count(n => n != null);
                    if (known >= 3) return;
                }
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_GFX_THREADS finder stopped: " + exception.Message);
            }
        }

        static void Apply(string part, string value)
        {
            var uids = new List<KeyValuePair<int, string>>();
            lock (gate)
                foreach (KeyValuePair<int, string> pair in found)
                    if (pair.Value != null && pair.Value.Contains(part)) uids.Add(pair);
            foreach (KeyValuePair<int, string> pair in uids) Set(pair.Key, pair.Value, value);
        }

        static void Set(int uid, string name, string value)
        {
            int priority = 0, mask = 0;
            if (value == "game")
            {
                int[] was;
                lock (gate) if (!original.TryGetValue(uid, out was)) return;
                priority = was[0];
                mask = was[1];
            }
            else
            {
                string[] parts = value.Split(':');
                if (parts.Length != 2) return;
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out priority);
                foreach (char c in parts[1])
                    if (c >= '0' && c <= '2') mask |= 0x10000 << (c - '0');
            }
            var old = new int[2];
            int result = Threads.SetNow(uid, priority, mask, old);
            lock (gate) if (result >= 0 && !original.ContainsKey(uid)) original[uid] = old;
            Debug.Log("CROW_GFX_THREADS " + name + " " + value + " (was priority 0x" + old[0].ToString("x") + " cpus 0x" + old[1].ToString("x") + ")" +
                      (result < 0 ? " failed 0x" + result.ToString("X8") : ""));
        }
    }

    // -crow-light-report: the forward renderer's passes, worked out as Unity assigns lights to each
    // visible renderer: lights whose culling mask has its layer and whose range (and spot cone) reach
    // its bounds; Important ones first, then by brightness at its centre; the first
    // max(pixelLightCount, Important ones) are drawn per pixel, the brightest directional one among
    // them in the base pass, every other one in an additive pass per material. The rest go to the
    // base pass's 4 vertex lights (point lights) and spherical harmonics. Only materials whose shader
    // has a ForwardAdd pass count (Standard, SFBStandard and their kind).
    // CROW_LIGHTS: the totals, with the additive draws at 1-4 pixel lights; CROW_LIGHT: per light,
    // the additive draws it causes and how many renderers it reaches, the costliest first.
    static class LightReport
    {
        static float every, lastReport;
        static bool detail;

        public static void Install(float seconds)
        {
            every = seconds;
            SceneManager.sceneLoaded += (scene, mode) => detail = true;
            Application.onBeforeRender += Tick;
        }

        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastReport < every) return;
            lastReport = now;
            try
            {
                Report(detail);
                detail = false;
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_LIGHTS failed: " + exception);
            }
        }

        static readonly string[] litShaders = { "SFBStandard", "Standard", "Standard (Specular setup)", "ProBuilder/Standard Vertex Color", "Particles/Standard Surface", "Autodesk Interactive" };

        static bool HasAddPass(Material material)
        {
            if (material == null || material.shader == null) return false;
            string name = material.shader.name;
            return Array.IndexOf(litShaders, name) >= 0 || (name.StartsWith("Legacy Shaders/") && !name.Contains("Particles") && !name.Contains("Unlit"));
        }

        static float Luminance(Color c)
        {
            return c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
        }

        class Stat
        {
            public Light light;
            public int reached, add, pixel;
        }

        static void Report(bool perLight)
        {
            var lights = UnityEngine.Object.FindObjectsOfType<Light>()
                .Where(l => l.isActiveAndEnabled && l.intensity > 0f && l.type != LightType.Area)
                .Select(l => new Stat { light = l }).ToList();
            int pixelCount = QualitySettings.pixelLightCount;
            int renderers = 0, litMaterials = 0, unlitMaterials = 0, baseDraws = 0, vertexLit = 0;
            var addAt = new int[5];
            var shaders = new Dictionary<string, int>();
            var reaching = new List<KeyValuePair<Stat, float>>();
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled || !renderer.isVisible) continue;
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                renderers++;
                int lit = 0;
                foreach (Material material in renderer.sharedMaterials)
                {
                    baseDraws++;
                    if (HasAddPass(material)) lit++;
                    else unlitMaterials++;
                    if (perLight && material != null && material.shader != null)
                    {
                        int count;
                        shaders.TryGetValue(material.shader.name, out count);
                        shaders[material.shader.name] = count + 1;
                    }
                }
                litMaterials += lit;
                if (lit == 0) continue;
                Bounds bounds = renderer.bounds;
                int layer = 1 << renderer.gameObject.layer;
                reaching.Clear();
                foreach (Stat stat in lights)
                {
                    Light light = stat.light;
                    if ((light.cullingMask & layer) == 0) continue;
                    float score = light.intensity * Luminance(light.color);
                    if (light.type != LightType.Directional)
                    {
                        Vector3 position = light.transform.position;
                        float squared = bounds.SqrDistance(position);
                        if (squared > light.range * light.range) continue;
                        if (light.type == LightType.Spot)
                        {
                            Vector3 to = bounds.center - position;
                            float distance = to.magnitude, radius = bounds.extents.magnitude;
                            if (distance > radius)
                            {
                                float slack = Mathf.Asin(Mathf.Clamp01(radius / distance)) * Mathf.Rad2Deg;
                                if (Vector3.Angle(light.transform.forward, to) - slack > light.spotAngle * 0.5f) continue;
                            }
                        }
                        float centre = (bounds.center - position).sqrMagnitude / (light.range * light.range);
                        score /= 1f + 25f * centre;
                    }
                    if (light.renderMode == LightRenderMode.ForcePixel) score += 1e6f;
                    if (light.renderMode == LightRenderMode.ForceVertex) score -= 1e6f;
                    stat.reached++;
                    reaching.Add(new KeyValuePair<Stat, float>(stat, score));
                }
                reaching.Sort((a, b) => b.Value.CompareTo(a.Value));
                int important = reaching.Count(p => p.Key.light.renderMode == LightRenderMode.ForcePixel);
                for (int count = 1; count <= 4; count++)
                {
                    int pixel = Pixel(reaching, count, important);
                    addAt[count] += lit * Math.Max(0, pixel - (HasDirectional(reaching, pixel) ? 1 : 0));
                }
                int pixels = Pixel(reaching, pixelCount, important);
                bool main = HasDirectional(reaching, pixels);
                Light mainLight = null;
                for (int i = 0; i < pixels && main; i++)
                    if (reaching[i].Key.light.type == LightType.Directional)
                    {
                        mainLight = reaching[i].Key.light;
                        break;
                    }
                for (int i = 0; i < pixels; i++)
                {
                    reaching[i].Key.pixel++;
                    if (reaching[i].Key.light != mainLight) reaching[i].Key.add += lit;
                }
                if (reaching.Count > pixels) vertexLit++;
            }
            var line = new StringBuilder("CROW_LIGHTS scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            line.Append(" pixelLights=").Append(pixelCount).Append(" lights=").Append(lights.Count);
            line.Append(" renderers=").Append(renderers).Append(" baseDraws=").Append(baseDraws);
            line.Append(" litMaterials=").Append(litMaterials).Append(" unlitMaterials=").Append(unlitMaterials);
            line.Append(" add=").Append(lights.Sum(s => s.add));
            for (int count = 1; count <= 4; count++) line.Append(" add@").Append(count).Append('=').Append(addAt[count]);
            line.Append(" withVertexLights=").Append(vertexLit);
            line.Append(" shadowDistance=").Append(QualitySettings.shadowDistance).Append(" shadows=").Append(QualitySettings.shadows);
            Debug.Log(line.ToString());
            if (!perLight) return;
            Debug.Log("CROW_LIGHTS shaders " + string.Join(" ", shaders.OrderByDescending(p => p.Value).Select(p => p.Key.Replace(' ', '_') + "=" + p.Value).ToArray()));
            foreach (Stat stat in lights.OrderByDescending(s => s.add).ThenByDescending(s => s.reached).Take(30))
            {
                Light light = stat.light;
                var text = new StringBuilder("CROW_LIGHT add=").Append(stat.add).Append(" pixel=").Append(stat.pixel).Append(" reached=").Append(stat.reached);
                text.Append(' ').Append(light.type).Append(" mode=").Append(light.renderMode).Append(" shadows=").Append(light.shadows);
                text.Append(" range=").Append(light.range.ToString("F1")).Append(" intensity=").Append(light.intensity.ToString("F2"));
                text.Append(" color=").Append(ColorUtility.ToHtmlStringRGB(light.color)).Append(" path=").Append(Path(light.transform).Replace(' ', '_'));
                Debug.Log(text.ToString());
            }
        }

        static int Pixel(List<KeyValuePair<Stat, float>> reaching, int count, int important)
        {
            int perPixel = reaching.Count(p => p.Key.light.renderMode != LightRenderMode.ForceVertex);
            return Math.Min(perPixel, Math.Max(count, important));
        }

        static bool HasDirectional(List<KeyValuePair<Stat, float>> reaching, int pixel)
        {
            for (int i = 0; i < pixel; i++)
                if (reaching[i].Key.light.type == LightType.Directional) return true;
            return false;
        }

        static string Path(Transform transform)
        {
            return transform.parent == null ? transform.name : Path(transform.parent) + "/" + transform.name;
        }
    }
}
