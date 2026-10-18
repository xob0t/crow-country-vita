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
//   -crow-light-report <s>          every s seconds, the forward renderer's light passes as Unity
//                                   assigns them, per light (CROW_LIGHTS, CROW_LIGHT; see LightReport)
//   -crow-ab <s> <path> <changes>   s seconds after a scene load, stop time, save <path>-a.png, apply
//                                   the changes (comma-separated: lights=vita, pixel=<n>,
//                                   shadows=off|hard|all, distance=<m>, resolution=<0-3>, and
//                                   Perf.cs's) and save <path>-b.png of the same moment
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
            float profile = Number(args, "-crow-loop-profile", -1);
            if (profile > 0) LoopProfile.Install(profile);
            float report = Number(args, "-crow-light-report", -1);
            if (report > 0) LightReport.Install(report);
            LightRules.Install(args);
            Draws.Install(args);
            Perf.Install(args);
            VitaLit.Install(args);
            int ab =Array.IndexOf(args, "-crow-ab");
            if (ab >= 0 && ab + 3 < args.Length) AB.Install(Number(args, "-crow-ab", 10f), args[ab + 2], args[ab + 3]);
            float census = Number(args, "-crow-census", -1);
            int disable = Array.IndexOf(args, "-crow-disable");
            int disableIn = Array.IndexOf(args, "-crow-disable-in");
            if (census > 0 || disable >= 0)
                Census.Install(census, disable >= 0 && disable + 1 < args.Length ? args[disable + 1] : null,
                               disableIn >= 0 && disableIn + 1 < args.Length ? args[disableIn + 1] : null);
            pixelLights = (int)Number(args, "-crow-pixel-lights", -1);
            shadowResolution = (int)Number(args, "-crow-shadow-resolution", -1);
            shadowDistance = Number(args, "-crow-shadow-distance", -1);
            int index = Array.IndexOf(args, "-crow-shadows");
            shadows = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
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
            if (pixelLights < 0 && shadowResolution < 0 && shadowDistance < 0 && shadows == null) return;
            Apply();
            SceneManager.sceneLoaded += (scene, mode) => Apply();
        }

        internal static float Number(string[] args, string flag, float fallback)
        {
            int index = Array.IndexOf(args, flag);
            float value;
            return index >= 0 && index + 1 < args.Length &&
                   float.TryParse(args[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        // Applies a -crow-ab change list.
        internal static void Change(string changes)
        {
            foreach (string change in changes.Split(','))
            {
                string[] pair = change.Split('=');
                if (pair.Length != 2) continue;
                float number;
                float.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out number);
                if (pair[0] == "lights" && pair[1] == "vita") LightRules.Enable();
                else if (pair[0] == "lit") VitaLit.Set(pair[1]);
                else if (pair[0] == "litkeys" || pair[0] == "litrange") VitaLit.SetLimits(pair[0], number);
                else if (pair[0] == "pixel") pixelLights = (int)number;
                else if (pair[0] == "shadows") shadows = pair[1];
                else if (pair[0] == "distance") shadowDistance = number;
                else if (pair[0] == "resolution") shadowResolution = (int)number;
                else if (pair[0] == "casters") Draws.Casters(pair[1]);
                else if (!Perf.Change(pair[0], pair[1]) && !EnemyCost.Change(pair[0], pair[1]) && !FixedStep.Change(pair[0], pair[1]) &&
                         !FastPaths.Change(pair[0], pair[1]) && !Decor.Change(pair[0], pair[1]) && !SharedClosures.Change(pair[0], pair[1]) &&
                         !FilterGate.Change(pair[0], pair[1]) && !Cuts.Change(pair[0], pair[1]))
                    Debug.Log("CROW_QUALITY unknown change " + change);
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

    // Which lights the forward renderer may draw per pixel. A scene's second directional light is a
    // dim fill (Driving Intro: 0.1 against the main light's 0.2), but as a directional light it
    // outranks every point and spot light, so it takes one of the pixel light slots on every object
    // it reaches and adds a full-size additive pass to each (138 of Driving Intro's 384). The rule
    // draws it through the objects' spherical harmonics instead (LightRenderMode.ForceVertex): it
    // demotes a directional light when a brighter one reaches every layer it reaches (so the UI's
    // own light stays), and checks again every 2 s, since the game turns lights on and off. Not a
    // default: on the PS Vita it saved 3.5 ms of 59 in Driving Intro, but the scene drew about a
    // fifth darker (the fill light's share is not in the harmonics).
    //   -crow-lights game|vita   game (the default) keeps every light as the scene has it; vita
    //                            applies the rule
    static class LightRules
    {
        static readonly Dictionary<Light, LightRenderMode> original = new Dictionary<Light, LightRenderMode>();
        static float lastCheck = -10f;

        public static void Install(string[] args)
        {
            int index = Array.IndexOf(args, "-crow-lights");
            string value = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            if (value == "vita") Enable();
        }

        static bool enabled;

        public static void Enable()
        {
            if (enabled) return;
            enabled = true;
            SceneManager.sceneLoaded += (scene, mode) => lastCheck = -10f;
            Application.onBeforeRender += Tick;
            lastCheck = -10f;
            Tick();
            Debug.Log("CROW_QUALITY lights vita: secondary directional lights per vertex");
        }

        static float Score(Light light)
        {
            return light.intensity * (light.color.r * 0.3f + light.color.g * 0.59f + light.color.b * 0.11f);
        }

        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastCheck < 2f) return;
            lastCheck = now;
            var directional = UnityEngine.Object.FindObjectsOfType<Light>()
                .Where(l => l.type == LightType.Directional && l.isActiveAndEnabled && l.intensity > 0f).ToList();
            foreach (Light light in directional)
            {
                LightRenderMode mode;
                if (!original.TryGetValue(light, out mode)) original[light] = mode = light.renderMode;
                if (mode != LightRenderMode.Auto) continue;
                float score = Score(light);
                bool outranked = directional.Any(other => other != light && (other.cullingMask & light.cullingMask) == light.cullingMask &&
                                                          (Score(other) > score || (Score(other) == score && other.GetInstanceID() < light.GetInstanceID())));
                LightRenderMode wanted = outranked ? LightRenderMode.ForceVertex : LightRenderMode.Auto;
                if (light.renderMode != wanted) light.renderMode = wanted;
            }
            if (original.Count > 256)
                foreach (Light gone in original.Keys.Where(l => l == null).ToList()) original.Remove(gone);
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
        static float after, loaded;
        static string path, changes;
        static int step = -1, frame;

        public static void Install(float seconds, string prefix, string list)
        {
            after = seconds;
            path = prefix;
            changes = list;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (step >= 0 && step < 4) return;
                loaded = Time.realtimeSinceStartup;
                step = 0;
            };
            Application.onBeforeRender += Tick;
        }

        static void Tick()
        {
            if (step < 0 || step >= 4) return;
            if (step == 0)
            {
                if (Time.realtimeSinceStartup - loaded < after) return;
                Time.timeScale = 0f;
                step = 1;
                frame = Time.frameCount;
                return;
            }
            if (Time.frameCount - frame < 10) return;
            frame = Time.frameCount;
            if (step == 1) ScreenCapture.CaptureScreenshot(path + "-a.png");
            else if (step == 2) Quality.Change(changes);
            else
            {
                ScreenCapture.CaptureScreenshot(path + "-b.png");
                Debug.Log("CROW_AB saved " + path + "-a.png and -b.png (" + changes + ")");
            }
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
            Camera.onPreCull += camera => CameraMark(camera, 0);
            Camera.onPreRender += camera => CameraMark(camera, 1);
            Camera.onPostRender += camera => CameraMark(camera, 2);
            Debug.Log("CROW_LOOP installed " + labels.Count + " markers");
        }

        // Per camera: cull (onPreCull to onPreRender), draw (to onPostRender) and after (from
        // onPostRender to the next camera or the frame's end: its image effects, OnRenderImage).
        static readonly Dictionary<string, long[]> cameras = new Dictionary<string, long[]>();
        static long[] lastCamera;
        static long cameraMark;

        static void CameraMark(Camera camera, int stage)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (stage == 0) CameraEnd(now);
            long[] slot;
            if (!cameras.TryGetValue(camera.name, out slot)) cameras[camera.name] = slot = new long[4];
            if (stage == 1) slot[0] += now - cameraMark;
            if (stage == 2)
            {
                slot[1] += now - cameraMark;
                slot[3]++;
                lastCamera = slot;
            }
            cameraMark = now;
        }

        static void CameraEnd(long now)
        {
            if (lastCamera != null) lastCamera[2] += now - cameraMark;
            lastCamera = null;
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
                line.Append(" after=").Append((pair.Value[2] * ms / frames).ToString("F1")).Append(']');
                Array.Clear(pair.Value, 0, 4);
            }
            Debug.Log(line.ToString());
            Array.Clear(ticks, 0, ticks.Length);
            frames = 0;
            worst = 0;
            lastReport = time;
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
