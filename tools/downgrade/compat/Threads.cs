using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

// The game's threads on the PS Vita, through the CrowClock plugin (tools/downgrade/native/clock/threads.c): each
// thread's CPU share (Threads, for -crow-audio, -crow-load-cpu, -crow-loop-cpu and the frame probe), the loading
// thread moved off FMOD's CPU (Threads.MoveLoader, -crow-loader-cpus), -crow-thread, and Unity's job workers'
// priority and CPUs (GfxThreads, -crow-gfx-workers, workers=). The flags are listed in Audio.cs and Quality.cs.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    // The game's threads' CPU use from the kernel (CrowClock plugin). A background thread samples which
    // thread each CPU runs every 10 ms, so the threads that use the CPU are found by their UIDs; each
    // report gives every known thread's run time over the window.
    public static class Threads
    {
        [DllImport("CrowClock")] static extern int CrowThreadSelf();
        [DllImport("CrowClock")] static extern int CrowThreadQuery(int uid, int[] info, byte[] name);
        [DllImport("CrowClock")] static extern int CrowThreadSet(int uid, int priority, int affinity);
        [DllImport("CrowClock")] static extern int CrowThreadsRunning(int[] uids);
        [DllImport("CrowClock")] static extern int CrowCpuIdle(int[] idle);
        [DllImport("CrowClock")] static extern int CrowThreadWait(int uid, int[] info);
        [DllImport("CrowClock")] static extern int CrowSyncName(int uid, int waitType, byte[] name);

        // The plugin exists on the PS Vita only. Mono looks a P/Invoke up when it compiles the method that
        // calls it, whichever branch then runs, and logs the failed library load ("Fallback handler could
        // not load library ...CrowClock"): methods that other players run call it through this.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int SelfNative()
        {
            return CrowThreadSelf();
        }

        class Known
        {
            public string Name, Tag;
            public long Run, Samples;
            public bool Set;
        }

        static bool available;
        static readonly Dictionary<int, Known> known = new Dictionary<int, Known>();
        static readonly object gate = new object();
        static readonly List<string[]> settings = new List<string[]>();
        static readonly Stopwatch wall = Stopwatch.StartNew();
        static long lastWall;
        static readonly long[] lastIdle = new long[3];
        static readonly int[] cpuSamples = new int[3];
        static int sampleRounds;

        internal static void Install(string[] args, bool report)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-crow-thread")
                {
                    string[] parts = args[i + 1].Split(':');
                    if (parts.Length == 3) settings.Add(parts);
                    else Debug.Log("CROW_THREADS -crow-thread wants <name>:<priority>:<cpus>, not " + args[i + 1]);
                }
            if (!report && settings.Count == 0) return;
            Start();
        }

        static bool started;

        // The sampler, once (-crow-audio and Loading's -crow-load-cpu both want it).
        internal static bool Start()
        {
            if (started) return available;
            started = true;
            if (Application.platform != RuntimePlatform.PSP2) return false;
            try
            {
                Name(SelfNative(), null);
                available = true;
            }
            catch (Exception e)
            {
                Debug.Log("CROW_THREADS no CrowClock thread functions (" + e.GetType().Name + ": " + e.Message + ")");
                return false;
            }
            new Thread(Sample) { IsBackground = true, Name = "CrowThreadSample" }.Start();
            return true;
        }

        // Unity's loading thread (UnityPreload) runs on CPU 2 by the player's choice, beside FMOD's mixer,
        // stream and file threads, and at the priority of the mixer (0x40) when backgroundLoadingPriority
        // is High: the title's background load of the player's scene (SceneFlow.cs' MaraScene) took the mixer from the
        // 58% of CPU 2 it needs to 15%, and the DSP clock ran at a quarter of real time for 30 s (the
        // stutter on the title). CPU 1 has the render worker alone (10-40%). A thread that samples which
        // thread each CPU runs, every millisecond, finds UnityPreload by its name when it first runs (the
        // title's prefetch, SceneFlow.cs' ScenePrefetch) and moves it (-crow-loader-cpus; default 1).
        // Whether UnityPreload left CPU 2: background loads can then run at Normal priority (below the
        // render worker's, on CPU 1) without starving FMOD (SceneFlow.cs' MaraScene).
        public static volatile bool LoaderMoved;

        internal static void MoveLoader(string[] args)
        {
            if (Application.platform != RuntimePlatform.PSP2) return;
            int mask = Mask(CrowFlags.Optional(args, "-crow-loader-cpus") ?? "1");
            if (mask == 0) return;
            try
            {
                SelfNative();
            }
            catch (Exception e)
            {
                Debug.Log("CROW_THREADS no CrowClock thread functions (" + e.GetType().Name + ": " + e.Message + ")");
                return;
            }
            CrowEvents.Loaded("Threads.loaderSearch", (scene, mode) =>
            {
                if (Scenes.IsArea(scene)) areaLoaded = true;
            });
            new Thread(() => FindLoader(mask)) { IsBackground = true, Name = "CrowLoaderCpu" }.Start();
        }

        // The search ends with the first area's load: UnityPreload has run by then (it reads every area), so a
        // search still going (-crow-no-prefetch, or a renamed thread) would only poll on. Every millisecond while
        // the title's prefetch would run, every 10 after.
        static volatile bool areaLoaded;
        const int FastMs = 30000;

        static void FindLoader(int mask)
        {
            var uids = new int[3];
            var info = new int[10];
            var name = new byte[32];
            var seen = new HashSet<int>();
            var clock = Stopwatch.StartNew();
            while (!areaLoaded)
            {
                Thread.Sleep(clock.ElapsedMilliseconds < FastMs ? 1 : 10);
                if (CrowThreadsRunning(uids) < 0) continue;
                foreach (int uid in uids)
                {
                    if (uid == 0 || !seen.Add(uid) || CrowThreadQuery(uid, info, name) < 0) continue;
                    if (CString(name).IndexOf("UnityPreload", StringComparison.Ordinal) < 0) continue;
                    int result = CrowThreadSet(uid, 0, mask);
                    LoaderMoved = result >= 0;
                    Debug.Log("CROW_THREADS UnityPreload to CPUs " + Cpus(mask) + " after " + clock.ElapsedMilliseconds + " ms" +
                              (result < 0 ? " failed 0x" + result.ToString("X8") : ""));
                    return;
                }
            }
            Debug.Log("CROW_THREADS UnityPreload not found by the first area's load (" + clock.ElapsedMilliseconds + " ms)");
        }

        // The UID of the first thread whose kernel name has name in it, handed to found from a thread of its own once a
        // CPU runs it (every millisecond for up to a minute; FilterGate's FMOD mixer, which runs every 10 ms). PS Vita only.
        internal static void Find(string name, Action<int> found)
        {
            new Thread(() =>
            {
                try
                {
                    var uids = new int[3];
                    var info = new int[10];
                    var buffer = new byte[32];
                    var seen = new HashSet<int>();
                    var clock = Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 60000)
                    {
                        Thread.Sleep(1);
                        if (CrowThreadsRunning(uids) < 0) continue;
                        foreach (int uid in uids)
                            if (uid != 0 && seen.Add(uid) && CrowThreadQuery(uid, info, buffer) >= 0 &&
                                CString(buffer).IndexOf(name, StringComparison.Ordinal) >= 0)
                            {
                                found(uid);
                                return;
                            }
                    }
                    Debug.Log("CROW_THREADS " + name + " not found in a minute");
                }
                catch (Exception e)
                {
                    Debug.Log("CROW_THREADS no CrowClock thread functions (" + e.GetType().Name + ": " + e.Message + ")");
                }
            }) { IsBackground = true, Name = "CrowThreadFind" }.Start();
        }

        internal static int Self()
        {
            try { return available ? SelfNative() : 0; }
            catch (Exception) { return 0; }
        }

        // For LoopProfile's -crow-loop-cpu: the calling thread's UID (0 without the plugin), and a
        // thread's run time in microseconds (-1 on failure). Works without -crow-audio.
        internal static int SelfAnyway()
        {
            if (Application.platform != RuntimePlatform.PSP2) return 0;
            try { return SelfNative(); }
            catch (Exception) { return 0; }
        }

        static readonly int[] runInfo = new int[10];
        static readonly byte[] runName = new byte[32];

        // Main thread only (shares its buffers).
        internal static long RunMicros(int uid)
        {
            if (CrowThreadQuery(uid, runInfo, runName) < 0) return -1;
            return (uint)runInfo[4] | ((long)runInfo[5] << 32);
        }

        // For LoopProfile's frame probe (any thread, its own buffers): a thread's status, wait type, the UID it
        // waits on and the CPU it last ran on into info[4]; which of our threads each CPU runs now into
        // uids[3]; a thread's name; a wait object's name. Below 0 on failure.
        internal static int Wait(int uid, int[] info)
        {
            return CrowThreadWait(uid, info);
        }

        internal static int Running(int[] uids)
        {
            return CrowThreadsRunning(uids);
        }

        // For GfxThreads (-crow-gfx-workers, workers=): a thread's priority and CPUs now (0 keeps either); the old
        // ones into was[0] (priority) and was[1] (CPU mask). Below 0 on failure.
        internal static int SetNow(int uid, int priority, int mask, int[] was)
        {
            var info = new int[10];
            var name = new byte[32];
            if (CrowThreadQuery(uid, info, name) < 0) return -1;
            was[0] = info[1];
            was[1] = info[2];
            return CrowThreadSet(uid, priority, mask);
        }

        internal static string ThreadName(int uid)
        {
            var info = new int[10];
            var name = new byte[32];
            if (CrowThreadQuery(uid, info, name) < 0) return null;
            return CString(name);
        }

        internal static string SyncName(int uid, int waitType)
        {
            var name = new byte[32];
            if (CrowSyncName(uid, waitType, name) < 0) return null;
            return CString(name);
        }

        // Adds a thread, with a tag to show after its name (or null).
        internal static void Name(int uid, string tag)
        {
            if (uid <= 0) return;
            lock (gate)
            {
                Known entry;
                if (!known.TryGetValue(uid, out entry)) known[uid] = entry = new Known();
                if (tag != null) entry.Tag = tag;
            }
        }

        static void Sample()
        {
            var uids = new int[3];
            while (true)
            {
                Thread.Sleep(10);
                if (CrowThreadsRunning(uids) < 0) continue;
                lock (gate)
                {
                    sampleRounds++;
                    for (int cpu = 0; cpu < 3; cpu++)
                    {
                        if (uids[cpu] == 0) continue;
                        cpuSamples[cpu]++;
                        Known entry;
                        if (!known.TryGetValue(uids[cpu], out entry)) known[uids[cpu]] = entry = new Known();
                        entry.Samples++;
                    }
                }
            }
        }

        // " cpu=a/b/c" with each CPU's load in percent over the window.
        internal static void AppendCpu(StringBuilder line)
        {
            if (!available) return;
            var idle = new int[6];
            if (CrowCpuIdle(idle) < 0) return;
            long now = wall.ElapsedTicks;
            double us = (now - lastWall) * 1e6 / Stopwatch.Frequency;
            line.Append(" cpu=");
            for (int cpu = 0; cpu < 3; cpu++)
            {
                long value = (uint)idle[cpu * 2] | ((long)idle[cpu * 2 + 1] << 32);
                if (cpu > 0) line.Append('/');
                line.Append(lastWall == 0 ? "?" : (100.0 - 100.0 * (value - lastIdle[cpu]) / us).ToString("F0", CultureInfo.InvariantCulture));
                lastIdle[cpu] = value;
            }
            lastWall = now;
        }

        static long threadWall;

        // A CROW_THREADS line: each known thread's share of a CPU over the window, the busiest first.
        internal static void Report(bool quiet)
        {
            string shares = Shares();
            if (shares == null || quiet) return;
            Debug.Log("CROW_THREADS " + shares);
        }

        // Each known thread's share of a CPU since the last call, the busiest first (null without the
        // plugin). One caller only: Report's (-crow-audio) or Loading's -crow-load-cpu thread.
        internal static string Shares()
        {
            if (!available) return null;
            long now = wall.ElapsedTicks;
            double us = (now - threadWall) * 1e6 / Stopwatch.Frequency;
            bool first = threadWall == 0;
            threadWall = now;
            var info = new int[10];
            var name = new byte[32];
            var rows = new List<KeyValuePair<double, string>>();
            KeyValuePair<int, Known>[] entries;
            int rounds;
            lock (gate)
            {
                entries = known.ToArray();
                rounds = sampleRounds;
                sampleRounds = 0;
            }
            foreach (KeyValuePair<int, Known> pair in entries)
            {
                Known entry = pair.Value;
                if (CrowThreadQuery(pair.Key, info, name) < 0) continue;
                string kernelName = CString(name);
                entry.Name = kernelName;
                Apply(pair.Key, entry, kernelName);
                long run = (uint)info[4] | ((long)info[5] << 32);
                double share = first || entry.Run == 0 ? -1 : 100.0 * (run - entry.Run) / us;
                entry.Run = run;
                long samples;
                lock (gate)
                {
                    samples = entry.Samples;
                    entry.Samples = 0;
                }
                if (share < 1.0) continue;
                rows.Add(new KeyValuePair<double, string>(share, Short(entry.Name) + entry.Tag + "=" + share.ToString("F0", CultureInfo.InvariantCulture) +
                    "%[p" + info[1].ToString("x") + " c" + Cpus(info[2]) + " s" + (rounds > 0 ? (100 * samples / rounds).ToString(CultureInfo.InvariantCulture) : "?") + "]"));
            }
            return string.Join(" ", rows.OrderByDescending(r => r.Key).Select(r => r.Value).ToArray());
        }

        // "82810B90 Unity_main_thread" -> "Unity_main_thread"; spaces to underscores.
        static string Short(string name)
        {
            if (name.Length > 9 && name[8] == ' ') name = name.Substring(9);
            return name.Replace(' ', '_');
        }

        // A kernel CPU affinity mask (SceKernelCpuMask): bit 16 + n is CPU n, so CPUs 0-2 are 0x70000.
        const int FirstCpuBit = 0x10000;

        // The mask of the CPUs a flag's digits name ("12": CPUs 1 and 2, 0x60000); 0 for none.
        internal static int Mask(string cpus)
        {
            int mask = 0;
            foreach (char c in cpus)
                if (c >= '0' && c <= '2') mask |= FirstCpuBit << (c - '0');
            return mask;
        }

        // A mask's CPUs as digits ("12"), "?" for none.
        static string Cpus(int mask)
        {
            var text = new StringBuilder();
            for (int cpu = 0; cpu < 4; cpu++)
                if ((mask & (FirstCpuBit << cpu)) != 0) text.Append(cpu);
            return text.Length > 0 ? text.ToString() : "?";
        }

        // A kernel name buffer's text, up to its first NUL.
        static string CString(byte[] name)
        {
            int end = Array.IndexOf(name, (byte)0);
            return Encoding.ASCII.GetString(name, 0, end < 0 ? name.Length : end);
        }

        // -crow-thread: once per thread, when its name matches.
        static void Apply(int uid, Known entry, string kernelName)
        {
            if (entry.Set) return;
            foreach (string[] setting in settings)
            {
                // By the kernel's name or as CROW_THREADS shows it (spaces as underscores: Unity's job
                // workers are "Worker Thread", and the flag's value can't hold a space).
                if (kernelName.IndexOf(setting[0], StringComparison.OrdinalIgnoreCase) < 0 &&
                    Short(kernelName).IndexOf(setting[0], StringComparison.OrdinalIgnoreCase) < 0) continue;
                entry.Set = true;
                int priority, mask = Mask(setting[2]);
                int.TryParse(setting[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out priority);
                int result = CrowThreadSet(uid, priority, mask);
                Debug.Log("CROW_THREADS set " + kernelName + " priority=" + (priority > 0 ? "0x" + priority.ToString("x") : "kept") +
                          " cpus=" + (mask > 0 ? Cpus(mask) : "kept") + (result < 0 ? " failed 0x" + result.ToString("X8") : ""));
                return;
            }
        }
    }

    // Unity's job workers ("Worker Thread", two): with graphics jobs they record the camera's draws
    // (ForwardRenderLoopJob, one job) while the main thread goes on, and the main thread waits for that
    // job, and its cleanup, at the frame's end (GfxDeviceClient::EndAsyncJobFrame; sample dumps,
    // status-log.md, Render step). The player starts them at priority 0x9f on CPUs 0-2, below the render
    // thread (0x40, CPU 1) and FMOD's mixer (0x40, CPU 2), and on CPU 0 below the main thread, so the job
    // ran late and the main thread waited about 4 ms a frame for it. -crow-gfx-workers <priority>:<cpus>
    // sets them once they first run (a thread polls which threads run, as MoveLoader finds UnityPreload);
    // the PS Vita's default is 64:1, the render thread's priority on its CPU (not the mixer's, which
    // keeps CPU 2 to itself: audio as before); game leaves them. -crow-cycle / -crow-fight-cycle / -crow-ab
    // take workers=<priority>:<cpus>|game. CROW_GFX_THREADS logs each change with what the thread had.
    static class GfxThreads
    {
        static readonly Dictionary<int, int[]> original = new Dictionary<int, int[]>();
        static readonly Dictionary<int, string> found = new Dictionary<int, string>();
        static readonly object gate = new object();
        static string workers;
        static bool finding;

        internal static void Install(string[] args)
        {
            string value = CrowFlags.Value(args, "-crow-gfx-workers", "64:1");
            if (value != null && value != "game") Change("workers", value);
        }

        internal static bool Change(string key, string value)
        {
            if (key != "workers") return false;
            if (Application.platform != RuntimePlatform.PSP2) return true;
            lock (gate) workers = value;
            Apply(value);
            if (!finding)
            {
                finding = true;
                new System.Threading.Thread(Find) { IsBackground = true, Name = "CrowGfxThreads" }.Start();
            }
            return true;
        }

        // The player's job workers; Find stops when it has seen them all, or after FindSeconds. The workers
        // run in every drawn frame, so a 1 ms poll sees both within the logos' first frames; after
        // FastSeconds it polls every 10 ms (a player that starts fewer workers would otherwise keep
        // it at 1 kHz for the whole time).
        const int Workers = 2, FastSeconds = 5, FindSeconds = 30;

        static void Find()
        {
            var uids = new int[3];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int known = 0;
            try
            {
                while (clock.ElapsedMilliseconds < FindSeconds * 1000)
                {
                    System.Threading.Thread.Sleep(clock.ElapsedMilliseconds < FastSeconds * 1000 ? 1 : 10);
                    if (Threads.Running(uids) < 0) continue;
                    foreach (int uid in uids)
                    {
                        if (uid == 0) continue;
                        lock (gate) if (found.ContainsKey(uid)) continue;
                        string name = Threads.ThreadName(uid) ?? "";
                        bool worker = name.Contains("Worker Thread");
                        string value;
                        lock (gate)
                        {
                            found[uid] = worker ? name : null;
                            value = worker ? workers : null;
                            if (worker) known++;
                        }
                        if (value != null) Set(uid, name, value);
                    }
                    if (known >= Workers) return;
                }
                Debug.Log("CROW_GFX_THREADS finder gave up after " + FindSeconds + " s with " + known + " of " + Workers + " workers");
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_GFX_THREADS finder stopped: " + exception.Message);
            }
        }

        static void Apply(string value)
        {
            var uids = new List<KeyValuePair<int, string>>();
            lock (gate)
                foreach (KeyValuePair<int, string> pair in found)
                    if (pair.Value != null) uids.Add(pair);
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
                mask = Threads.Mask(parts[1]);
            }
            var old = new int[2];
            int result = Threads.SetNow(uid, priority, mask, old);
            lock (gate) if (result >= 0 && !original.ContainsKey(uid)) original[uid] = old;
            Debug.Log("CROW_GFX_THREADS " + name + " " + value + " (was priority 0x" + old[0].ToString("x") + " cpus 0x" + old[1].ToString("x") + ")" +
                      (result < 0 ? " failed 0x" + result.ToString("X8") : ""));
        }
    }
}
