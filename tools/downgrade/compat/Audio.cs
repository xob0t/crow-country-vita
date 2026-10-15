using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

// Sound on the PS Vita: what the mixer costs, when it falls behind, and settings to try without a build.
// FMOD's mixer, stream and file threads, Unity's loading thread (UnityPreload) and the render worker
// are threads scripts never see; the CrowClock plugin (tools/downgrade/native/clock/threads.c) reads
// the kernel's accounting of each thread's run time and each CPU's idle time.
//   -crow-audio [s]           every s seconds (default 2) a CROW_AUDIO line: frames and the worst frame,
//                             the DSP clock's advance against the wall clock's, the sources playing
//                             (streamed and with reverb filters among them), clips loading, each CPU's
//                             load, and a CROW_THREADS line with the CPU share of every thread of the
//                             game's that used more than 1% (found by sampling which thread each CPU runs).
//   -crow-audio-tap           time the mixer's blocks from inside the mixer (an OnAudioFilterRead on a
//                             silent source of the probe's): blocks, gaps over two blocks, the longest gap.
//   -crow-audio-filters <types>  which filter components FilterGate turns off: any of reverb, lowpass,
//                             highpass, chorus, echo, distortion, all (comma-separated); with idle among
//                             them, only while their source is idle (the PS Vita's default: reverb,idle);
//                             -crow-audio-game keeps every filter on, as the game has them.
//   -crow-reverb-max <n>      with idle, at most n of those filters on at once (default 3).
//   -crow-loader-cpus <cpus>  the CPUs of Unity's loading thread (UnityPreload), digits 0-2 (the PS
//                             Vita's default: 1; - leaves it on the player's choice, CPU 2, FMOD's).
//   -crow-dsp-buffer <n>, -crow-real-voices <n>, -crow-sample-rate <hz>
//                             AudioSettings.Reset with these before the first scene.
//   -crow-load-priority <p>   the loading thread's priority (High, Normal, BelowNormal, Low) for the
//                             player's scene read in the background on the title (Loading.cs; default High).
//   -crow-thread <name>:<priority>:<cpus>  with -crow-audio, set the priority (0 keeps it; 64 highest ..
//                             191) and the CPUs (digits 0-2, e.g. 1 or 12; - keeps them) of each thread
//                             whose name has <name> ("FMOD mixer", UnityPreload) when the probe finds it;
//                             repeatable.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Audio
    {
        static float every;
        static bool tap;
        static readonly Stopwatch wall = Stopwatch.StartNew();
        static long lastWall;
        static double lastDsp;
        static int frames;
        static float worst;
        static float nextReport;
        static int outputRate;

        static string[] Args { get { return ProbeFiles.Args; } }

        static string Value(string flag)
        {
            string[] args = Args;
            int index = Array.IndexOf(args, flag);
            return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("-crow-") ? args[index + 1] : null;
        }

        static int Int(string flag, int fallback)
        {
            int value;
            string text = Value(flag);
            return text != null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        public static bool WantsTap { get { return Args.Contains("-crow-audio-tap"); } }

        // The background loading priority for the player's scene (Loading.cs's MaraScene).
        public static UnityEngine.ThreadPriority LoadPriority(UnityEngine.ThreadPriority fallback)
        {
            string text = Value("-crow-load-priority");
            if (text == null) return fallback;
            try
            {
                return (UnityEngine.ThreadPriority)Enum.Parse(typeof(UnityEngine.ThreadPriority), text, true);
            }
            catch (ArgumentException)
            {
                Debug.Log("CROW_AUDIO unknown -crow-load-priority " + text);
                return fallback;
            }
        }

        // From ProbeFiles.Install (BeforeSceneLoad, PS Vita only): the mixer's configuration before
        // anything plays.
        internal static void Configure()
        {
            int buffer = Int("-crow-dsp-buffer", 0), voices = Int("-crow-real-voices", 0), rate = Int("-crow-sample-rate", 0);
            AudioConfiguration config = AudioSettings.GetConfiguration();
            string before = Describe(config);
            if (buffer <= 0 && voices <= 0 && rate <= 0)
            {
                if (Args.Contains("-crow-audio")) Debug.Log("CROW_AUDIO config " + before);
                return;
            }
            if (buffer > 0) config.dspBufferSize = buffer;
            if (voices > 0) config.numRealVoices = voices;
            if (rate > 0) config.sampleRate = rate;
            bool done = AudioSettings.Reset(config);
            Debug.Log("CROW_AUDIO config " + before + " -> " + Describe(AudioSettings.GetConfiguration()) + (done ? "" : " (Reset failed)"));
        }

        static string Describe(AudioConfiguration config)
        {
            return "rate=" + config.sampleRate + " buffer=" + config.dspBufferSize + " realVoices=" + config.numRealVoices +
                   " virtualVoices=" + config.numVirtualVoices + " speakers=" + config.speakerMode + " output=" + AudioSettings.outputSampleRate;
        }

        // From Diag.Install, for every run: the PS Vita's defaults (FilterGate, the loading thread's CPU).
        internal static void InstallDefaults()
        {
            FilterGate.Install(Args);
            Threads.MoveLoader(Args);
        }

        // From Diag.Install, after the probe's object has its Diag (and its AudioSource, for the tap).
        internal static void Install(GameObject probe)
        {
            string[] args = Args;
            int index = Array.IndexOf(args, "-crow-audio");
            if (index >= 0)
            {
                float seconds;
                every = index + 1 < args.Length && float.TryParse(args[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ? seconds : 2f;
            }
            outputRate = AudioSettings.outputSampleRate;
            Threads.Install(args, every > 0f);
            tap = WantsTap && probe != null && probe.GetComponent<AudioSource>() != null;
            if (tap)
            {
                AudioSource source = probe.GetComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = true;
                source.priority = 0;
                source.spatialBlend = 0f;
                source.bypassReverbZones = true;
                // A silent clip: the PS Vita player crashes on a source playing without one
                // (AudioManager::ProcessScheduledSources reads the clip's compression format).
                source.clip = AudioClip.Create("CrowAudioTap", 4800, 1, 48000, false);
                source.Play();
            }
            if (every > 0f)
            {
                SceneManager.sceneLoaded += (scene, mode) => Debug.Log("CROW_AUDIO scene " + scene.name + " (" + mode + ") at " + Time.realtimeSinceStartup.ToString("F2", CultureInfo.InvariantCulture));
                Debug.Log("CROW_AUDIO probe every " + every + " s" + (tap ? ", mixer tap" : "") + "; output " + outputRate + " Hz");
            }
        }

        // From Diag.Update.
        internal static void Frame()
        {
            if (every <= 0f) return;
            frames++;
            float delta = Time.unscaledDeltaTime * 1000f;
            if (delta > worst) worst = delta;
            float now = Time.realtimeSinceStartup;
            if (now < nextReport) return;
            nextReport = now + every;
            Report(now);
        }

        static void Report(float now)
        {
            long ticks = wall.ElapsedTicks;
            double dsp = AudioSettings.dspTime;
            double wallMs = (ticks - lastWall) * 1000.0 / Stopwatch.Frequency;
            double dspMs = (dsp - lastDsp) * 1000.0;
            bool first = lastWall == 0;
            lastWall = ticks;
            lastDsp = dsp;
            if (first)
            {
                frames = 0;
                worst = 0f;
                Threads.Report(true);
                return;
            }
            var line = new StringBuilder("CROW_AUDIO t=");
            line.Append(now.ToString("F1", CultureInfo.InvariantCulture));
            line.Append(" scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            line.Append(" fps=").Append((frames * 1000.0 / wallMs).ToString("F1", CultureInfo.InvariantCulture));
            line.Append(" worst=").Append(worst.ToString("F0", CultureInfo.InvariantCulture));
            line.Append(" dsp=").Append(dspMs.ToString("F0", CultureInfo.InvariantCulture)).Append('/').Append(wallMs.ToString("F0", CultureInfo.InvariantCulture));
            frames = 0;
            worst = 0f;
            if (tap)
            {
                long blocks, late, samples;
                double gap;
                lock (tapGate)
                {
                    blocks = tapBlocks;
                    late = tapLate;
                    samples = tapSamples;
                    gap = tapMaxGap * 1000.0 / Stopwatch.Frequency;
                    tapBlocks = tapLate = tapSamples = 0;
                    tapMaxGap = 0;
                }
                line.Append(" blocks=").Append(blocks).Append(" late=").Append(late).Append(" maxgap=").Append(gap.ToString("F0", CultureInfo.InvariantCulture));
                line.Append(" mixed=").Append(outputRate > 0 ? (samples * 1000.0 / outputRate).ToString("F0", CultureInfo.InvariantCulture) : "?");
                line.Append(" block=").Append(tapBlockFrames);
            }
            AudioSource[] sources = UnityEngine.Object.FindObjectsOfType<AudioSource>();
            int playing = 0, streamed = 0, reverb = 0, filtered = 0;
            foreach (AudioSource source in sources)
            {
                if (!source.isPlaying) continue;
                playing++;
                AudioClip clip = source.clip;
                if (clip != null && clip.loadType == AudioClipLoadType.Streaming) streamed++;
                AudioReverbFilter r = source.GetComponent<AudioReverbFilter>();
                if (r != null && r.enabled) reverb++;
                if (source.GetComponent<AudioLowPassFilter>() != null || source.GetComponent<AudioHighPassFilter>() != null) filtered++;
            }
            line.Append(" sources=").Append(sources.Length).Append(" playing=").Append(playing).Append(" streamed=").Append(streamed);
            line.Append(" reverb=").Append(reverb).Append(" passfilter=").Append(filtered);
            int loading = 0, loaded = 0;
            foreach (AudioClip clip in Resources.FindObjectsOfTypeAll<AudioClip>())
            {
                AudioDataLoadState state = clip.loadState;
                if (state == AudioDataLoadState.Loading) loading++;
                else if (state == AudioDataLoadState.Loaded) loaded++;
            }
            line.Append(" clipsLoaded=").Append(loaded).Append(" clipsLoading=").Append(loading);
            line.Append(" loadPriority=").Append(Application.backgroundLoadingPriority).Append(FilterGate.Stats());
            Threads.AppendCpu(line);
            Debug.Log(line.ToString());
            Threads.Report(false);
        }

        // -crow-audio-tap: the mixer thread's blocks, from the probe's silent source.
        static readonly object tapGate = new object();
        static long tapLast, tapBlocks, tapLate, tapSamples, tapMaxGap;
        static int tapBlockFrames;
        static bool tapNamed;

        internal static void Tap(float[] data, int channels)
        {
            long now = wall.ElapsedTicks;
            int count = channels > 0 ? data.Length / channels : data.Length;
            if (!tapNamed)
            {
                tapNamed = true;
                Threads.Name(Threads.Self(), "(tap)");
            }
            lock (tapGate)
            {
                tapBlockFrames = count;
                tapBlocks++;
                tapSamples += count;
                if (tapLast != 0)
                {
                    long gap = now - tapLast;
                    if (gap > tapMaxGap) tapMaxGap = gap;
                    // Late: over two blocks' time since the last (the output's buffers ran down).
                    if (outputRate > 0 && gap * (double)outputRate > 2.0 * count * Stopwatch.Frequency) tapLate++;
                }
                tapLast = now;
            }
        }
    }


    // The game's audio filters cost FMOD's mixer CPU 2's time on the PS Vita whether their source plays
    // or not: once a source with an AudioReverbFilter has played, its reverb (FMOD's SFX reverb, about
    // 20% of the CPU at 444 MHz) runs on with the source idle. Driving Intro's five crows' reverbs took
    // the mixer from 20% to 90% of the CPU and the DSP clock to two thirds of real time (crackling); the
    // title's menu sounds did the same at New Game. FilterGate keeps each reverb filter on while its
    // source plays and for its decay time after (the tail), and off otherwise. A filter switched on
    // while its source plays joins the playing sound at the next mix.
    public static class FilterGate
    {
        struct Entry
        {
            public Behaviour Filter;
            public AudioSource Source;
            public float Tail, OffAt;
        }

        static readonly HashSet<string> types = new HashSet<string>();
        static bool idleOnly, active;
        // At most this many filters on at once (-crow-reverb-max): each reverb costs about 20% of CPU 2
        // at 444 MHz, the mixer without them 15-20%; the title's New Game sounds start five at once.
        static int max = 3;
        static readonly List<Entry> entries = new List<Entry>();
        static float nextFind;
        static int switches;

        internal static void Install(string[] args)
        {
            int index = Array.IndexOf(args, "-crow-audio-filters");
            string given = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            if (given == null)
            {
                if (Application.platform != RuntimePlatform.PSP2 || args.Contains("-crow-audio-game")) return;
                given = "reverb,idle";
            }
            foreach (string type in given.ToLowerInvariant().Split(','))
                if (type == "idle") idleOnly = true;
                else if (type.Length > 0) types.Add(type);
            if (types.Count == 0) return;
            int limit = Array.IndexOf(args, "-crow-reverb-max");
            if (limit >= 0 && limit + 1 < args.Length && !int.TryParse(args[limit + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out max)) max = 3;
            if (!idleOnly) max = 0;
            active = true;
            SceneManager.sceneLoaded += (scene, mode) => nextFind = 0f;
            Canvas.willRenderCanvases += Update;
            Debug.Log("CROW_AUDIO filters off: " + string.Join(",", types.ToArray()) + (idleOnly ? " while their source is idle, at most " + max + " on" : ""));
        }

        static void Find()
        {
            entries.Clear();
            var list = new List<Type>();
            bool all = types.Contains("all");
            if (all || types.Contains("reverb")) list.Add(typeof(AudioReverbFilter));
            if (all || types.Contains("lowpass")) list.Add(typeof(AudioLowPassFilter));
            if (all || types.Contains("highpass")) list.Add(typeof(AudioHighPassFilter));
            if (all || types.Contains("chorus")) list.Add(typeof(AudioChorusFilter));
            if (all || types.Contains("echo")) list.Add(typeof(AudioEchoFilter));
            if (all || types.Contains("distortion")) list.Add(typeof(AudioDistortionFilter));
            foreach (Type type in list)
                foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(type))
                {
                    var filter = found as Behaviour;
                    if (filter == null || !filter.gameObject.scene.IsValid()) continue;
                    AudioSource source = filter.GetComponent<AudioSource>();
                    // A filter without a source on its object filters the listener's whole mix: left alone.
                    if (source == null && idleOnly) continue;
                    var reverb = filter as AudioReverbFilter;
                    entries.Add(new Entry { Filter = filter, Source = source, Tail = reverb != null ? reverb.decayTime + 0.3f : 0.1f });
                }
            nextFind = Time.realtimeSinceStartup + 3f;
        }

        // Canvas.willRenderCanvases: every frame, before the frame's audio update.
        static void Update()
        {
            if (!active) return;
            float now = Time.realtimeSinceStartup;
            if (now >= nextFind) Find();
            // Filters that are on and still wanted keep their place; others get one while fewer than
            // max are on (a sound that finds none plays dry until one frees up).
            int on = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < entries.Count; i++)
                {
                    Entry entry = entries[i];
                    if (entry.Filter == null) continue;
                    bool want = false;
                    if (idleOnly && entry.Source != null && entry.Source.isActiveAndEnabled)
                    {
                        if (entry.Source.isPlaying)
                        {
                            want = true;
                            entry.OffAt = now + entry.Tail;
                            entries[i] = entry;
                        }
                        else want = now < entry.OffAt;
                    }
                    bool enabled = entry.Filter.enabled;
                    if (pass == 0)
                    {
                        // Keep: on and wanted. Off: anything on and no longer wanted.
                        if (enabled && want) on++;
                        else if (enabled)
                        {
                            entry.Filter.enabled = false;
                            switches++;
                        }
                    }
                    else if (want && !enabled && on < max)
                    {
                        entry.Filter.enabled = true;
                        switches++;
                        on++;
                    }
                }
        }

        // For the probe: filters handled, and switches since the last call.
        internal static string Stats()
        {
            if (!active) return "";
            int count = switches;
            switches = 0;
            return " gated=" + entries.Count + "/" + count;
        }
    }

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
            if (Application.platform != RuntimePlatform.PSP2) return;
            try
            {
                Name(CrowThreadSelf(), null);
                available = true;
            }
            catch (Exception e)
            {
                Debug.Log("CROW_THREADS no CrowClock thread functions (" + e.GetType().Name + ": " + e.Message + ")");
                return;
            }
            new Thread(Sample) { IsBackground = true, Name = "CrowThreadSample" }.Start();
        }

        // Unity's loading thread (UnityPreload) runs on CPU 2 by the player's choice, beside FMOD's mixer,
        // stream and file threads, and at the priority of the mixer (0x40) when backgroundLoadingPriority
        // is High: the title's background load of the player's scene (Loading.cs) took the mixer from the
        // 58% of CPU 2 it needs to 15%, and the DSP clock ran at a quarter of real time for 30 s (the
        // stutter on the title). CPU 1 has the render worker alone (10-40%). A thread that samples which
        // thread each CPU runs, every millisecond, finds UnityPreload by its name when it first runs (the
        // title's prefetch, Loading.cs) and moves it (-crow-loader-cpus; default 1).
        // Whether UnityPreload left CPU 2: background loads can then run at Normal priority (below the
        // render worker's, on CPU 1) without starving FMOD (Loading.cs).
        public static volatile bool LoaderMoved;

        internal static void MoveLoader(string[] args)
        {
            if (Application.platform != RuntimePlatform.PSP2) return;
            int index = Array.IndexOf(args, "-crow-loader-cpus");
            string cpus = index >= 0 && index + 1 < args.Length ? args[index + 1] : "1";
            int mask = 0;
            foreach (char c in cpus)
                if (c >= '0' && c <= '2') mask |= 0x10000 << (c - '0');
            if (mask == 0) return;
            try
            {
                CrowThreadSelf();
            }
            catch (Exception e)
            {
                Debug.Log("CROW_THREADS no CrowClock thread functions (" + e.GetType().Name + ": " + e.Message + ")");
                return;
            }
            new Thread(() => FindLoader(mask)) { IsBackground = true, Name = "CrowLoaderCpu" }.Start();
        }

        static void FindLoader(int mask)
        {
            var uids = new int[3];
            var info = new int[10];
            var name = new byte[32];
            var seen = new HashSet<int>();
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 120000)
            {
                Thread.Sleep(1);
                if (CrowThreadsRunning(uids) < 0) continue;
                foreach (int uid in uids)
                {
                    if (uid == 0 || !seen.Add(uid) || CrowThreadQuery(uid, info, name) < 0) continue;
                    string text = Encoding.ASCII.GetString(name);
                    if (text.IndexOf("UnityPreload", StringComparison.Ordinal) < 0) continue;
                    int result = CrowThreadSet(uid, 0, mask);
                    LoaderMoved = result >= 0;
                    Debug.Log("CROW_THREADS UnityPreload to CPUs " + Cpus(mask) + " after " + clock.ElapsedMilliseconds + " ms" +
                              (result < 0 ? " failed 0x" + result.ToString("X8") : ""));
                    return;
                }
            }
            Debug.Log("CROW_THREADS UnityPreload not found");
        }

        internal static int Self()
        {
            try { return available ? CrowThreadSelf() : 0; }
            catch (Exception) { return 0; }
        }

        // For Quality's -crow-loop-cpu: the calling thread's UID (0 without the plugin), and a
        // thread's run time in microseconds (-1 on failure). Works without -crow-audio.
        internal static int SelfAnyway()
        {
            if (Application.platform != RuntimePlatform.PSP2) return 0;
            try { return CrowThreadSelf(); }
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
            if (!available) return;
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
                string kernelName = Encoding.ASCII.GetString(name).TrimEnd('\0');
                int end = kernelName.IndexOf('\0');
                if (end >= 0) kernelName = kernelName.Substring(0, end);
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
            if (quiet) return;
            Debug.Log("CROW_THREADS " + string.Join(" ", rows.OrderByDescending(r => r.Key).Select(r => r.Value).ToArray()));
        }

        // "82810B90 Unity_main_thread" -> "Unity_main_thread"; spaces to underscores.
        static string Short(string name)
        {
            if (name.Length > 9 && name[8] == ' ') name = name.Substring(9);
            return name.Replace(' ', '_');
        }

        static string Cpus(int mask)
        {
            var text = new StringBuilder();
            for (int cpu = 0; cpu < 4; cpu++)
                if ((mask & (0x10000 << cpu)) != 0) text.Append(cpu);
            return text.Length > 0 ? text.ToString() : "?";
        }

        // -crow-thread: once per thread, when its name matches.
        static void Apply(int uid, Known entry, string kernelName)
        {
            if (entry.Set) return;
            foreach (string[] setting in settings)
            {
                if (kernelName.IndexOf(setting[0], StringComparison.OrdinalIgnoreCase) < 0) continue;
                entry.Set = true;
                int priority, mask = 0;
                int.TryParse(setting[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out priority);
                foreach (char c in setting[2])
                    if (c >= '0' && c <= '2') mask |= 0x10000 << (c - '0');
                int result = CrowThreadSet(uid, priority, mask);
                Debug.Log("CROW_THREADS set " + kernelName + " priority=" + (priority > 0 ? "0x" + priority.ToString("x") : "kept") +
                          " cpus=" + (mask > 0 ? Cpus(mask) : "kept") + (result < 0 ? " failed 0x" + result.ToString("X8") : ""));
                return;
            }
        }
    }
}
