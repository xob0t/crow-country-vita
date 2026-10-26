using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.LowLevel;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

// Load time: where a scene load's long frames go, for the PS Vita, which has no profiler.
//   -crow-load-trace [ms]   each frame of ms or more (default 200) as a CROW_LOADFRAME line: its time
//                           per player loop step, the costliest first; a CROW_LOAD line when a scene
//                           loads or unloads (with the step and the time into the frame it happens
//                           at: before it are the read and the objects' Awake and OnEnable), and a
//                           CROW_LOADDONE line when the frames after a load are short again (the
//                           load's long frames summed). It runs on LoopProfile's markers (LoopProfile.cs),
//                           beside -crow-loop-profile or alone.
//   -crow-load-cpu [ms]     through each area load, every ms (default 250), each CPU's load and each
//                           game thread's share (CROW_LOADCPU, from a thread of its own; with
//                           -crow-gc-log the collections in the window, with -crow-load-stats the sums)
//   -crow-load-stats        PlayMaker's load-time methods timed per thread (CROW_LOADSTATS after each
//                           area load): OnAfterDeserialize, InitData, LoadActions, CreateAction, ...
//   -crow-read-test <files> read build files at startup and log the card's MB/s (CROW_READ)
// Loading.Install also installs SceneFlow.cs' PS Vita defaults: the player's scene beside the title (MaraScene), the
// title read while the logos play (ScenePrefetch; -crow-no-prefetch leaves it to the logos) and the empty scene
// between areas (SceneGap; -crow-no-gap leaves it out on the PS Vita, -crow-gap puts it in elsewhere).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Loading
    {
        // From Diag.Install (a RuntimeInitializeOnLoadMethod: the build lists those, so quickcode can't add one).
        // Each module under a Guard of its own (Diag.Guard), so a probe that throws as it installs doesn't take
        // the PS Vita defaults after it with it (the player's scene, the title's prefetch, the gap).
        internal static void Install()
        {
            string[] args = ProbeFiles.Args;
            if (CrowFlags.Has(args, "-crow-load-trace"))
                Diag.Guard("Trace", () => Trace.Install(CrowFlags.Number(args, "-crow-load-trace", 200f)));
            if (CrowFlags.Has(args, "-crow-load-stats")) Diag.Guard("LoadStats", LoadStats.Install);
            string read = CrowFlags.Value(args, "-crow-read-test");
            if (read != null) Diag.Guard("ReadTest", () => ReadTest(read));
            if (CrowFlags.Has(args, "-crow-load-cpu"))
            {
                if (CrowFlags.Has(args, "-crow-audio")) Debug.Log("CROW_LOADCPU off: -crow-audio reads the threads' shares (one reader at a time)");
                else Diag.Guard("LoadCpu", () => LoadCpu.Install(CrowFlags.Int(args, "-crow-load-cpu", 250)));
            }
            Diag.Guard("MaraScene", MaraScene.Install);
            Diag.Guard("ScenePrefetch", () => ScenePrefetch.Install(args));
            Diag.Guard("SceneGap", () => SceneGap.Install(args));
        }

        static class Trace
        {
            static List<string> labels;
            static long[] ticks;
            static long last, frameStart, threshold, loadTicks;
            static int previous = -1, frame, loadFrames, quietFrames;
            static string loading;
            static readonly double Ms = 1000.0 / Stopwatch.Frequency;

            public static void Install(float ms)
            {
                threshold = (long)(ms / Ms);
                // LoopProfile's markers (one before every step of the player loop and one at each phase's end),
                // not a loop of its own: one SetPlayerLoop would drop the other's (Quality.cs, LoopProfile.OnMark).
                LoopProfile.Install(1e9f);  // no reports of its own
                labels = LoopProfile.Labels;
                ticks = new long[labels.Count];
                LoopProfile.OnMark += Mark;
                CrowEvents.Loaded("Trace.sceneLoaded", (scene, mode) => Event("loaded " + scene.name + " (" + mode + ")"));
                CrowEvents.Unloaded("Trace.sceneUnloaded", scene => Event("unloaded " + scene.name));
                SceneManager.activeSceneChanged += (from, to) => Event("active " + to.name);
                Debug.Log("CROW_LOAD trace on: frames of " + ms + " ms or more, " + labels.Count + " markers");
            }

            // A scene event: the step it happens in and how far into the frame.
            static void Event(string what)
            {
                long now = Stopwatch.GetTimestamp();
                if (what.StartsWith("loaded ")) loading = what.Substring(7);
                Debug.Log("CROW_LOAD " + what + " at " + Time.realtimeSinceStartup.ToString("F2") + " s, frame " + frame +
                          " +" + ((now - frameStart) * Ms).ToString("F0") + " ms in " + (previous >= 0 ? labels[previous] : "?") +
                          " (" + (previous >= 0 ? ((now - last) * Ms).ToString("F0") : "?") + " ms into the step)");
            }

            static void Mark(int index)
            {
                long now = Stopwatch.GetTimestamp();
                if (previous >= 0) ticks[previous] += now - last;
                previous = index;
                last = now;
                if (index != 0) return;
                if (frameStart != 0) EndFrame(now - frameStart);
                frameStart = now;
                frame++;
            }

            static void EndFrame(long total)
            {
                if (total >= threshold)
                {
                    var line = new StringBuilder("CROW_LOADFRAME frame=").Append(frame).Append(" ms=").Append((total * Ms).ToString("F0"));
                    line.Append(" scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
                    foreach (int i in Enumerable.Range(0, ticks.Length).OrderByDescending(i => ticks[i]).Take(6))
                    {
                        double ms = ticks[i] * Ms;
                        if (ms < 5) break;
                        line.Append(' ').Append(labels[i]).Append('=').Append(ms.ToString("F0"));
                    }
                    Debug.Log(line.ToString());
                    loadTicks += total;
                    loadFrames++;
                    quietFrames = 0;
                }
                else if (loadFrames > 0 && ++quietFrames >= 60)
                {
                    Debug.Log("CROW_LOADDONE " + (loading ?? "?").Replace(' ', '_') + " long_frames=" + loadFrames + " long_ms=" +
                              (loadTicks * Ms).ToString("F0") + " at " + Time.realtimeSinceStartup.ToString("F2") + " s");
                    loadFrames = 0;
                    loadTicks = 0;
                    loading = null;
                }
                Array.Clear(ticks, 0, ticks.Length);
            }
        }

        // -crow-read-test <files>: how fast the card reads the build's data. On a thread of its own at
        // startup, each file (comma-separated, under Application.dataPath, e.g. level5,sharedassets5.assets)
        // read whole, in 1 MB blocks and then in 64 KB blocks (CROW_READ lines: MB, ms, MB/s).
        static void ReadTest(string files)
        {
            string root = Application.dataPath;
            new System.Threading.Thread(() =>
            {
                foreach (int block in new[] { 1 << 20, 64 << 10 })
                    foreach (string file in files.Split(','))
                    {
                        string path = root + "/" + file;
                        try
                        {
                            var buffer = new byte[block];
                            var watch = Stopwatch.StartNew();
                            long total = 0;
                            using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read, 4096))
                            {
                                int n;
                                while ((n = stream.Read(buffer, 0, block)) > 0) total += n;
                            }
                            double ms = watch.Elapsed.TotalMilliseconds;
                            Debug.Log("CROW_READ " + file + " block=" + (block >> 10) + "KB MB=" + (total / 1048576.0).ToString("F1") + " ms=" + ms.ToString("F0") +
                                      " MB/s=" + (total / 1048576.0 / (ms / 1000.0)).ToString("F1"));
                        }
                        catch (Exception e)
                        {
                            Debug.Log("CROW_READ " + path + " failed: " + e.GetType().Name + " " + e.Message);
                        }
                    }
            }) { IsBackground = true, Name = "CrowReadTest" }.Start();
        }

        // -crow-load-cpu [ms]: through each area load (from the gap's first step, or any scene load,
        // to 8 s after the area's sceneLoaded), every ms (default 250) a CROW_LOADCPU line from a thread
        // of its own: each CPU's load and each game thread's share of a CPU over the window (the main
        // thread, UnityPreload, the render worker, FMOD's), from the kernel (Audio.cs's Threads).
        // The main thread busy with UnityPreload idle is integration (Awake, OnEnable) or the
        // unused-asset pass; UnityPreload busy is reading and deserializing. A window with no line is a
        // garbage collection (it stops this thread too). Not with -crow-audio (one reader of the shares).
        internal static class LoadCpu
        {
            static readonly Stopwatch clock = Stopwatch.StartNew();
            static long begun = -1, endAt = -1;
            static int seenRuns;

            internal static void Install(int ms)
            {
                if (!Threads.Start())
                {
                    Debug.Log("CROW_LOADCPU no thread accounting on this player");
                    return;
                }
                CrowEvents.Loaded("LoadCpu.sceneLoaded", (scene, mode) =>
                {
                    if (scene.name == SceneGap.Scene || scene.name == MaraScene.Scene) return;
                    Begin();
                    endAt = clock.ElapsedMilliseconds + 8000;
                });
                new System.Threading.Thread(() => Run(Math.Max(20, ms))) { IsBackground = true, Name = "CrowLoadCpu" }.Start();
                Debug.Log("CROW_LOADCPU every " + ms + " ms through loads");
            }

            internal static void Begin()
            {
                if (begun < 0) begun = clock.ElapsedMilliseconds;
                endAt = long.MaxValue;
            }

            static void Run(int ms)
            {
                var line = new StringBuilder();
                bool was = false;
                while (true)
                {
                    System.Threading.Thread.Sleep(ms);
                    long now = clock.ElapsedMilliseconds;
                    bool on = begun >= 0 && now < endAt;
                    if (!on)
                    {
                        if (was) begun = -1;
                        was = false;
                        continue;
                    }
                    if (!was)
                    {
                        Threads.Shares();  // start the window here
                        seenRuns = Alloc.RunsEnded;
                    }
                    was = true;
                    line.Length = 0;
                    line.Append("CROW_LOADCPU +").Append(now - begun).Append(" ms");
                    int runs;
                    double paused = Alloc.PausesSince(seenRuns, out runs);
                    seenRuns += runs;
                    if (runs > 0) line.Append(" gc=").Append(runs).Append('x').Append(paused.ToString("F0")).Append("ms");
                    Threads.AppendCpu(line);
                    line.Append(' ').Append(Threads.Shares());
                    if (LoadStats.On) line.Append(" | ").Append(LoadStats.Line());
                    Debug.Log(line.ToString());
                }
            }
        }
    }

    // -crow-load-stats: PlayMaker's load-time methods timed (the rewriter's TimeLoadMethods,
    // rewriter/LoadStats.cs, wraps each in Start and Stop), summed per method and per thread (the
    // main thread or another: OnAfterDeserialize runs on the loading thread while a scene is read).
    // Loading's -crow-load-cpu lines carry the sums so far; a CROW_LOADSTATS line follows each area load.
    public static class LoadStats
    {
        public static readonly string[] Names = { "fsmDeserialize", "fsmAwake", "fsmEnable", "fsmStart", "initData", "loadActions", "fsmInit", "preprocess", "createAction", "loadField" };
        static readonly long[] ticks = new long[32];
        static readonly int[] counts = new int[32];
        static volatile bool on;
        static System.Threading.Thread main;

        internal static bool On { get { return on; } }

        internal static void Install()
        {
            main = System.Threading.Thread.CurrentThread;
            on = true;
            CrowEvents.Loaded("LoadStats.sceneLoaded", (scene, mode) =>
            {
                if (scene.name == SceneGap.Scene) return;
                Debug.Log("CROW_LOADSTATS " + scene.name.Replace(' ', '_') + " " + Line());
                Array.Clear(ticks, 0, ticks.Length);
                Array.Clear(counts, 0, counts.Length);
            });
        }

        public static long Start()
        {
            return on ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void Stop(long start, int slot)
        {
            if (start == 0L) return;
            if (System.Threading.Thread.CurrentThread != main) slot += 16;
            System.Threading.Interlocked.Add(ref ticks[slot], Stopwatch.GetTimestamp() - start);
            System.Threading.Interlocked.Increment(ref counts[slot]);
        }

        // "name=ms/calls" for each slot used, the loading thread's with an @ (fsmDeserialize@=...).
        internal static string Line()
        {
            var line = new StringBuilder();
            for (int i = 0; i < 32; i++)
            {
                if (counts[i] == 0 || (i & 15) >= Names.Length) continue;
                if (line.Length > 0) line.Append(' ');
                line.Append(Names[i & 15]).Append(i >= 16 ? "@" : "").Append('=')
                    .Append((ticks[i] * 1000.0 / Stopwatch.Frequency).ToString("F0")).Append('/').Append(counts[i]);
            }
            return line.ToString();
        }
    }

}
