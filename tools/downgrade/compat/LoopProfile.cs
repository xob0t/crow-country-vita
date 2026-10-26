using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.LowLevel;
using UnityEngine.SceneManagement;

// Where the main thread's frame goes: the player loop's steps timed (LoopProfile, -crow-loop-profile, also under
// -crow-fight and -crow-load-trace) and, on the PS Vita, where it waits (FrameProbe, -crow-frame-probe,
// -crow-sample-dump). The flags are listed in Quality.cs.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
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
        internal static List<string> Labels { get { return labels; } }
        internal static long[] Totals { get { return totals; } }
        // Loading's -crow-load-trace: called at each marker with its index (Labels), so the trace times the steps on
        // these markers. 2018.2 has no GetCurrentPlayerLoop, so one installer builds the loop for all of them: a
        // second SetPlayerLoop from the default loop would drop the first's markers.
        internal static Action<int> OnMark;

        // Once: a second SetPlayerLoop would put a second set of markers in (each label twice). A later caller
        // only shortens the reports' interval (Fight's and Loading's ask for none: 1e9 s).
        public static void Install(float seconds)
        {
            if (Installed)
            {
                every = Mathf.Min(every, seconds);
                return;
            }
            every = seconds;
            if (CrowFlags.Has(ProbeFiles.Args, "-crow-loop-cpu")) mainUid = Threads.SelfAnyway();
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
                    // -crow-physics-sync-probe: the transforms' sync with the physics scene, which each
                    // physics step does first, done (and timed) on its own just before the step.
                    if (name == "PhysicsFixedUpdate" && CrowFlags.Has(ProbeFiles.Args, "-crow-physics-sync-probe"))
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
            if (CrowFlags.Has(ProbeFiles.Args, "-crow-frame-probe")) FrameProbe.Start();
            CrowEvents.PreCull("LoopProfile.onPreCull", camera => CameraMark(camera, 0));
            CrowEvents.PreRender("LoopProfile.onPreRender", camera => CameraMark(camera, 1));
            CrowEvents.PostRender("LoopProfile.onPostRender", camera => CameraMark(camera, 2));
            Debug.Log("CROW_LOOP installed " + labels.Count + " markers");
        }

        // Per camera: cull (onPreCull to onPreRender), draw (to onPostRender) and after (from
        // onPostRender to the next camera or the frame's end: its image effects, OnRenderImage, the
        // overlay canvases, the frame's end). after splits into image (CamEffect.RenderImage, the
        // camera effect's passes) and late (from the effect's end to the next camera or the frame's
        // end); the rest of after is before the effect (Unity's own image-effect setup).
        static readonly Dictionary<string, long[]> cameras = new Dictionary<string, long[]>();
        // Each camera's slot above, so its name is read once a camera, not three times a frame: Object.name makes a
        // new string at each read (CrowCompat's code has no names cache), ~3 KB/s of the fight bench's allocKBs.
        static readonly Dictionary<Camera, long[]> slots = new Dictionary<Camera, long[]>();
        static long[] lastCamera;
        static long cameraMark, imageMark;

        static void CameraMark(Camera camera, int stage)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (stage == 0) CameraEnd(now);
            long[] slot;
            if (!slots.TryGetValue(camera, out slot))
            {
                string name = camera.name;
                if (!cameras.TryGetValue(name, out slot)) cameras[name] = slot = new long[6];
                slots[camera] = slot;
            }
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
            if (OnMark != null) OnMark(index);
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
            foreach (Camera gone in slots.Keys.Where(c => c == null).ToList()) slots.Remove(gone);
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
        // A key per loop step and camera stage (step * Stages + stage); per key, PerKey counters: samples, main
        // waiting, main waiting while the render thread runs
        const int Stages = 8, PerKey = 3;  // stages: as many as the stages table has
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
                int key = Step * Stages + Stage;
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
                    if (key * PerKey + 2 >= perKey.Length)
                    {
                        var grown = new long[(key + 16) * PerKey];
                        Array.Copy(perKey, grown, perKey.Length);
                        perKey = grown;
                    }
                    samples++;
                    perKey[key * PerKey]++;
                    workerCpu += workers;
                    if (!haveRender) renderMissing++;
                    else if (renderWaits) renderWait++;
                    else renderRun++;
                    if (renderWaits) Count(renderObjects, render[1], render[2]);
                    if (!mainWaits) continue;
                    mainWait++;
                    perKey[key * PerKey + 1]++;
                    if (haveRender && !renderWaits) perKey[key * PerKey + 2]++;
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
            const string flag = "-crow-sample-dump";
            if (CrowFlags.Word(args, flag, 4) == null) return;
            string where = CrowFlags.Word(args, flag, 1);
            dumpWait = CrowFlags.Word(args, flag, 2) != "run";
            dumpMs = CrowFlags.NumberAt(args, flag, 3, 0f);
            dumpAfter = CrowFlags.NumberAt(args, flag, 4, 0f);
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
            int stage = key % Stages, step = key / Stages;
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
                for (int k = 0; k * PerKey + 2 < perKey.Length; k++) if (perKey[k * PerKey + 1] > 0) keys.Add(k);
                foreach (int k in keys.OrderByDescending(k => perKey[k * PerKey + 1]).Take(8))
                {
                    int step = k / Stages, stage = k % Stages;
                    string name = step < LoopProfile.Labels.Count ? LoopProfile.Labels[step] : "?";
                    name = name.Substring(name.IndexOf('.') + 1);
                    line.Append(' ').Append(name).Append(stages[stage]).Append('=').Append(ms(perKey[k * PerKey + 1]))
                        .Append("(r").Append(ms(perKey[k * PerKey + 2])).Append(')');
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
}
