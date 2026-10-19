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
//   -crow-reverb-max <n>      with idle, at most n of those filters on at once (default 3), the most audible
//                             (FilterGate), filters already on at the load included.
//   -crow-loader-cpus <cpus>  the CPUs of Unity's loading thread (UnityPreload), digits 0-2 (the PS
//                             Vita's default: 1; - leaves it on the player's choice, CPU 2, FMOD's).
//   -crow-dsp-buffer <n>, -crow-real-voices <n>, -crow-sample-rate <hz>
//                             AudioSettings.Reset with these before the first scene.
//   -crow-load-priority <p>   the loading thread's priority (High, Normal, BelowNormal, Low) for the
//                             player's scene read in the background on the title (Loading.cs; default Normal
//                             with the loading thread moved, else High).
//   -crow-doppler [s]         every s seconds (default 5) a CROW_DOPPLER line: the pitch FMOD's mixer gives
//                             1 kHz test tones (Doppler level 1, volume 0.02 to 500 m), measured inside the
//                             mixer from each block's zero crossings: on Mara's body and on the nearest
//                             moving enemy, one with velocityUpdateMode Dynamic and one Auto each, the
//                             enemy's also switched off for 2 s and back on with a Play every 4 s (as
//                             IdleAudio does), and a still control at pitch 1.01; with the physics steps
//                             each frame ran. Per tone: blocks, mean/min/max pitch, the mean and largest
//                             change from one block to the next, blocks that changed by over 0.5%.
//   -crow-mix-mute, -crow-mix-record <dir>, -crow-audio-census <s>
//                             the listener volume held at 0, the mix recorded, and what plays (MixProbe below).
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

        public static bool WantsTap { get { return Args.Contains("-crow-audio-tap"); } }

        // The background loading priority for the player's scene (Loading.cs's MaraScene).
        public static UnityEngine.ThreadPriority LoadPriority(UnityEngine.ThreadPriority fallback)
        {
            string text = CrowFlags.Optional(Args, "-crow-load-priority");
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
            int buffer = CrowFlags.Int(Args, "-crow-dsp-buffer", 0), voices = CrowFlags.Int(Args, "-crow-real-voices", 0), rate = CrowFlags.Int(Args, "-crow-sample-rate", 0);
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
            Doppler.Install(args);
            MixProbe.Install(args);
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
    // The cap holds for filters already on too: several areas have more looping ambient sounds with a
    // reverb, playing from the load, than the mixer can afford (Corridor S and D 9, Pool 10, Dungeon 7,
    // Crypt 6): Corridor S's nine kept the mixer at 94% of CPU 2 and the DSP clock at 37% of real time
    // (the crackling in its corridor). When more are wanted than the cap, the most audible keep theirs:
    // 2D sounds first, then by distance from the listener over the source's range, ranked every half
    // second, a filter that is on counting as 30% nearer (so two alike don't swap back and forth).
    public static class FilterGate
    {
        struct Entry
        {
            public Behaviour Filter;
            public AudioSource Source;
            public float Tail, OffAt, Score;
        }

        static readonly HashSet<string> types = new HashSet<string>();
        static bool idleOnly, active;
        // At most this many filters on at once (-crow-reverb-max): each reverb costs about 20% of CPU 2
        // at 444 MHz, the mixer without them 15-20%; the title's New Game sounds start five at once.
        static int max = 3;
        static readonly List<Entry> entries = new List<Entry>();
        static readonly List<int> order = new List<int>();  // entries, the most audible first
        static readonly List<bool> wants = new List<bool>();
        static float nextFind, nextRank;
        static int switches;
        static Transform listener;
        // The cap adapts to the area: the mixer kept up with 3 reverbs on the title, in Driving Intro and
        // Roadside, but in Corridor S only with 1 (2: the DSP clock at 91-96% of real time). Each area starts at
        // -crow-reverb-max (the ceiling) or at what it came down to on an earlier visit; whenever the DSP clock
        // ran under 97% of the wall clock over a window of 1.5 s or more, the cap goes one below the reverbs
        // on (-crow-reverb-adapt off keeps it).
        static int ceiling, onCount;
        static bool adapt = true, resolve;
        static float watchFrom;
        static double windowDsp = -1.0;
        static long windowTicks;
        static readonly Stopwatch watch = Stopwatch.StartNew();
        static readonly Dictionary<string, int> learned = new Dictionary<string, int>();
        static string behindScene;
        // A playing source that can't be heard at the listener (Silent below) wants no filter: its reverb would
        // process silence. Pool's three reverbs on at its entry were all on such sources (pool moans 10 m away,
        // linear rolloff to 6 m): the mixer at 78% of CPU 2 and the DSP clock at 92% of real time, 26% with none.
        // -crow-reverb-silent off (rsilent=off) gives them their filter as before. Recomputed each frame (silentOn).
        static bool silentOn = true, silentRule;

        internal static void Install(string[] args)
        {
            string given = CrowFlags.Optional(args, "-crow-audio-filters");
            if (given == null)
            {
                if (Application.platform != RuntimePlatform.PSP2 || args.Contains("-crow-audio-game")) return;
                given = "reverb,idle";
            }
            foreach (string type in given.ToLowerInvariant().Split(','))
                if (type == "idle") idleOnly = true;
                else if (type.Length > 0) types.Add(type);
            if (types.Count == 0) return;
            max = CrowFlags.Int(args, "-crow-reverb-max", 3);
            if (!idleOnly) max = 0;
            adapt = CrowFlags.Value(args, "-crow-reverb-adapt") != "off";
            silentOn = CrowFlags.Value(args, "-crow-reverb-silent") != "off";
            shareAuto = Application.platform == RuntimePlatform.PSP2 && CrowFlags.Value(args, "-crow-gfx-workers") == null &&
                        CrowFlags.Value(args, "-crow-workers-share") != "off";
            ceiling = max;
            active = true;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                nextFind = 0f;
                // The new area's cap once it is the active scene; its first second (the load's tail) unwatched.
                resolve = true;
                watchFrom = Time.realtimeSinceStartup + 1f;
                windowDsp = -1.0;
            };
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
            // The listener Unity hears with: the one enabled (none or two: no listener, so no source counts as silent).
            listener = null;
            int enabled = 0;
            foreach (AudioListener ear in UnityEngine.Object.FindObjectsOfType<AudioListener>())
                if (ear.isActiveAndEnabled)
                {
                    enabled++;
                    listener = ear.transform;
                }
            if (enabled != 1) listener = null;
            nextFind = Time.realtimeSinceStartup + 3f;
            nextRank = 0f;
        }

        // The entries by how audible their source is, the most first (see the class comment).
        static void Rank()
        {
            Vector3 ear = listener != null ? listener.position : Vector3.zero;
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                float score = float.MaxValue;
                if (entry.Filter != null && entry.Source != null)
                {
                    AudioSource source = entry.Source;
                    score = source.spatialBlend < 0.5f || listener == null
                        ? 0f
                        : Vector3.Distance(source.transform.position, ear) / Mathf.Max(source.maxDistance, 0.01f);
                    if (entry.Filter.enabled) score *= 0.7f;
                }
                entry.Score = score;
                entries[i] = entry;
            }
            order.Clear();
            for (int i = 0; i < entries.Count; i++) order.Add(i);
            order.Sort((a, b) => entries[a].Score.CompareTo(entries[b].Score));
        }

        // Canvas.willRenderCanvases: every frame, before the frame's audio update.
        static void Update()
        {
            if (!active) return;
            float now = Time.realtimeSinceStartup;
            if (now >= nextFind) Find();
            silentRule = silentOn && idleOnly;
            if (adapt) Watch(now);
            if (now >= nextRank || order.Count != entries.Count)
            {
                Rank();
                nextRank = now + 0.5f;
            }
            // Which filters are wanted: their source plays and can be heard, or did within its tail.
            wants.Clear();
            Vector3 ear = listener != null ? listener.position : Vector3.zero;
            if (listener == null) silentRule = false;
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                bool want = false;
                if (idleOnly && entry.Filter != null && entry.Source != null && entry.Source.isActiveAndEnabled)
                {
                    if (entry.Source.isPlaying && !(silentRule && Silent(entry.Source, ear)))
                    {
                        want = true;
                        entry.OffAt = now + entry.Tail;
                        entries[i] = entry;
                    }
                    else want = now < entry.OffAt;
                }
                wants.Add(want);
            }
            // The first max wanted filters in the ranking are on, the rest off; off first, then on, so
            // the count never goes over.
            int on = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                int kept = 0;
                foreach (int i in order)
                {
                    Entry entry = entries[i];
                    if (entry.Filter == null) continue;
                    bool keep = wants[i] && kept < max;
                    if (keep) kept++;
                    bool enabled = entry.Filter.enabled;
                    if (pass == 0)
                    {
                        if (enabled && !keep)
                        {
                            entry.Filter.enabled = false;
                            switches++;
                        }
                        else if (enabled) on++;
                    }
                    else if (keep && !enabled && on < max)
                    {
                        entry.Filter.enabled = true;
                        switches++;
                        on++;
                    }
                }
            }
            onCount = on;
            Share(now);
        }

        // Unity's job workers on CPU 2 as well as CPU 1 (Quality.cs GfxThreads, 64:12) while the mixer has CPU 2 to
        // spare: no filter on (a reverb costs the mixer 18-20% of CPU 2, the mixer without one 19-28%) for a second,
        // and the DSP clock never behind in this area. Back on CPU 1 alone (64:1, the PS Vita's default) in the frame a
        // filter comes on. At the same priority as the mixer a worker's job delays a mix block by a few ms (the tap's
        // longest gap 13 -> 18 ms, no late block); with Pool's two reverbs on, sharing put the DSP clock at 99% of real
        // time (adapting caps then take reverbs off) and the render thread there at 96%.
        // -crow-workers-share off (wshare=off) keeps them on CPU 1; an explicit -crow-gfx-workers also turns it off.
        static bool shareAuto, shared, blockedHere;
        static float quietSince = -1f;
        static readonly HashSet<string> shareBlocked = new HashSet<string>();

        static void Share(float now)
        {
            if (!shareAuto) return;
            bool quiet = onCount == 0 && !blockedHere;
            if (!quiet) quietSince = -1f;
            else if (quietSince < 0f) quietSince = now;
            bool want = quiet && now - quietSince >= 1f;
            if (want == shared) return;
            shared = want;
            GfxThreads.Change("workers", want ? "64:12" : "64:1");
        }

        // Whether a source's sound reaches the listener at all: wholly 3D (spatialBlend 1, no custom blend curve
        // that falls below 1) and, by its volume rolloff, at zero volume at its distance less a margin (0.5 m, more
        // than anything moves in a frame) and beyond; or at volume 0. A logarithmic rolloff never reaches zero.
        // Its filters then get silence at their input and give silence (their tails kept: OffAt), so a reverb
        // switched off there changes nothing heard, and is back on in the frame the sound can reach the listener.
        static readonly Dictionary<AudioSource, AnimationCurve[]> curves = new Dictionary<AudioSource, AnimationCurve[]>();

        internal static bool Silent(AudioSource source, Vector3 ear)
        {
            if (source.volume <= 0f) return true;
            if (source.spatialBlend < 0.999f) return false;
            AnimationCurve[] pair;
            if (!curves.TryGetValue(source, out pair))
            {
                if (curves.Count > 2000) curves.Clear();
                curves[source] = pair = new[] { source.GetCustomCurve(AudioSourceCurveType.SpatialBlend), source.GetCustomCurve(AudioSourceCurveType.CustomRolloff) };
            }
            AnimationCurve blend = pair[0];
            if (blend != null)
                foreach (Keyframe key in blend.keys)
                    if (key.value < 0.999f) return false;
            float distance = Vector3.Distance(source.transform.position, ear) - 0.5f;
            float max = source.maxDistance;
            if (max <= 0f) return false;
            switch (source.rolloffMode)
            {
                case AudioRolloffMode.Linear:
                    return distance >= max && max > source.minDistance;
                case AudioRolloffMode.Custom:
                    AnimationCurve rolloff = pair[1];
                    if (rolloff == null || rolloff.length == 0) return false;
                    float from = Mathf.Clamp01(distance / max);
                    for (int i = 0; i <= 8; i++)
                        if (rolloff.Evaluate(Mathf.Lerp(from, 1f, i / 8f)) > 0f) return false;
                    foreach (Keyframe key in rolloff.keys)
                        if (key.time >= from && key.value > 0f) return false;
                    return true;
            }
            return false;
        }

        // The adaptive cap (see ceiling above).
        static void Watch(float now)
        {
            if (now < watchFrom) return;
            if (resolve)
            {
                resolve = false;
                int cap;
                max = learned.TryGetValue(SceneManager.GetActiveScene().name, out cap) ? cap : ceiling;
                blockedHere = shareBlocked.Contains(SceneManager.GetActiveScene().name);
            }
            double dsp = AudioSettings.dspTime;
            long ticks = watch.ElapsedTicks;
            if (windowDsp < 0.0)
            {
                windowDsp = dsp;
                windowTicks = ticks;
                return;
            }
            double wall = (ticks - windowTicks) / (double)Stopwatch.Frequency;
            if (wall < 1.5) return;
            double ratio = (dsp - windowDsp) / wall;
            windowDsp = dsp;
            windowTicks = ticks;
            // Over 5 s: a stall or the console suspended; no advance: the output paused. Neither says how
            // the mixer keeps up.
            if (wall > 5.0 || ratio < 0.05 || ratio >= 0.97) return;
            string scene = SceneManager.GetActiveScene().name;
            shareBlocked.Add(scene);
            blockedHere = true;
            if (onCount == 0)
            {
                // Nothing left to take off (logged once an area).
                if (behindScene != scene)
                    Debug.Log("CROW_AUDIO mixer behind in " + scene + " (DSP clock at " + (ratio * 100.0).ToString("F0", CultureInfo.InvariantCulture) + "% of real time) with no filters on");
                behindScene = scene;
                return;
            }
            int next = Math.Max(0, Math.Min(max, onCount) - 1);
            Debug.Log("CROW_AUDIO mixer behind in " + scene + " (DSP clock at " + (ratio * 100.0).ToString("F0", CultureInfo.InvariantCulture) +
                      "% of real time, " + onCount + " filters on): at most " + next + " now");
            max = next;
            learned[scene] = max;
        }

        // Quality.Change's keys (-crow-ab, -crow-fight-cycle), for measurements: rmax=<n> the cap; rzone=off|on
        // the loaded scenes' AudioReverbZones; lowpass=off|on the lowpass filters (on sources and the
        // listener) off, and the ones it switched off back on.
        static readonly Dictionary<Type, List<Behaviour>> switchedOff = new Dictionary<Type, List<Behaviour>>();

        internal static bool Change(string key, string value)
        {
            if (MixProbe.Change(key, value)) return true;
            switch (key)
            {
                case "rmax":
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out max);
                    ceiling = max;
                    learned.Clear();
                    Debug.Log("CROW_AUDIO rmax=" + max);
                    return true;
                case "wshare":
                    shareAuto = value != "off";
                    if (!shareAuto && shared)
                    {
                        shared = false;
                        GfxThreads.Change("workers", "64:1");
                    }
                    Debug.Log("CROW_AUDIO wshare=" + (shareAuto ? "on" : "off"));
                    return true;
                case "rsilent":
                    silentOn = value != "off";
                    Debug.Log("CROW_AUDIO rsilent=" + (silentOn ? "on" : "off"));
                    return true;
                case "rzone":
                    Switch(typeof(AudioReverbZone), value == "on");
                    return true;
                case "lowpass":
                    Switch(typeof(AudioLowPassFilter), value == "on");
                    return true;
            }
            return false;
        }

        internal static void SwitchType(Type type, bool on)
        {
            Switch(type, on);
        }

        static void Switch(Type type, bool on)
        {
            List<Behaviour> list;
            if (!switchedOff.TryGetValue(type, out list)) switchedOff[type] = list = new List<Behaviour>();
            int count = 0;
            if (on)
            {
                foreach (Behaviour behaviour in list)
                    if (behaviour != null)
                    {
                        behaviour.enabled = true;
                        count++;
                    }
                list.Clear();
            }
            else
                foreach (UnityEngine.Object found in UnityEngine.Object.FindObjectsOfType(type))
                {
                    var behaviour = found as Behaviour;
                    if (behaviour == null || !behaviour.enabled || MixProbe.Mine(behaviour)) continue;
                    behaviour.enabled = false;
                    list.Add(behaviour);
                    count++;
                }
            Debug.Log("CROW_AUDIO " + type.Name + " " + (on ? "on" : "off") + ": " + count);
        }

        // For the probe: filters handled, and switches since the last call.
        internal static string Stats()
        {
            if (!active) return "";
            int count = switches;
            switches = 0;
            return " gated=" + entries.Count + "/" + count + " cap=" + max;
        }
    }

    // What FMOD's mixer spends its time on, for measurements (no default changes anything):
    //   -crow-mix-mute            the listener volume held at 0, as -crow-mute does: on the PS Vita it costs the mixer
    //                             the same (Pool, lvol=0 against 1: mixer 78.7 and 78.0% of CPU 2, the DSP clock alike),
    //                             so FMOD makes no voice virtual for it and measurements stand. (An echo filter at
    //                             -80 dB on the listener, tried first, silences nothing there: see -crow-mix-record.)
    //   -crow-mix-record <dir>    the listener's mix recorded when a cycle's mixrec=<s> asks, into
    //                             <dir>/mix-<n>-<scene>.wav (16-bit PCM), and silenced after the recording point.
    //                             A managed filter on the mixer thread, which then stops with every garbage
    //                             collection: for recording launches only. On the PS Vita the listener's filters
    //                             get silence (its recordings are all zeros: the sources' channels are mixed after
    //                             them, in the NGS output), so this works on Windows players only.
    //   -crow-audio-census <s>    s seconds after each area load (and at acensus=1), CROW_ACENSUS lines: every
    //                             playing source (clip, rate, load type, volume, 3D settings, distance, its gain
    //                             at the listener by its rolloff, mixer group, its filters), the totals, every
    //                             reverb filter's settings, the reverb zones and the listener's filters.
    // Quality.Change's keys (-crow-fight-cycle, -crow-ab), through FilterGate.Change: mixrec=<s>, acensus=1,
    // aidle=off|on (every enabled source not playing off, and back: what idle sources cost the mixer),
    // highpass= / echo= / chorus= / distortion= off|on (that filter type off, and back), apause=on|off
    // (AudioListener.pause: the mixer with nothing playing), lvol=<v>|game (the listener volume held at v; PS Vita
    // only and never with -crow-mute, so a PC player can't be unmuted),
    // athread=<name>:<priority>:<cpus>|<name>:game (a known thread's priority and CPUs; needs -crow-audio).
    public static class MixProbe
    {
        static bool mute;
        static string recordDir;
        static float censusAfter = -1f, censusAt = -1f, nextCheck;
        static AudioListener ear;
        static Diag recorder;
        static float forcedVolume = -1f, gameVolume = -1f;
        // The recording: filled on the mixer thread, written on a thread of its own.
        static short[] rec;
        static volatile int recAt;
        static volatile bool recDone;
        static int recChannels = 2, recCount;
        static string recName;
        static readonly List<AudioSource> idleOff = new List<AudioSource>();

        internal static void Install(string[] args)
        {
            mute = args.Contains("-crow-mix-mute");
            recordDir = CrowFlags.Optional(args, "-crow-mix-record");
            censusAfter = CrowFlags.Number(args, "-crow-audio-census", -1f);
            if (!mute && recordDir == null && censusAfter < 0f) return;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                nextCheck = 0f;
                if (censusAfter >= 0f) censusAt = Time.realtimeSinceStartup + censusAfter;
            };
            Debug.Log("CROW_MIX probe:" + (mute ? " listener volume held at 0" : "") + (recordDir != null ? " recordings to " + recordDir : "") +
                      (censusAfter >= 0f ? " census " + censusAfter + " s after each load" : ""));
            Canvas.willRenderCanvases += Frame;
        }

        internal static bool Mine(Behaviour behaviour)
        {
            return behaviour != null && ReferenceEquals(behaviour, recorder);
        }

        internal static bool Owns(Diag diag)
        {
            return !ReferenceEquals(recorder, null) && ReferenceEquals(diag, recorder);
        }

        // On the mixer thread, the listener's whole mix (Diag.OnAudioFilterRead via Doppler.Filter).
        internal static bool Filter(Diag diag, float[] data, int channels)
        {
            if (!Owns(diag)) return false;
            short[] buffer = rec;
            if (buffer != null && !recDone)
            {
                recChannels = channels;
                int at = recAt, n = Math.Min(data.Length, buffer.Length - at);
                for (int i = 0; i < n; i++)
                {
                    float v = data[i] * 32767f;
                    buffer[at + i] = (short)(v > 32767f ? 32767f : v < -32768f ? -32768f : v);
                }
                recAt = at + n;
                if (recAt >= buffer.Length) recDone = true;
            }
            Array.Clear(data, 0, data.Length);
            return true;
        }

        static void Frame()
        {
            float now = Time.realtimeSinceStartup;
            if (forcedVolume >= 0f) AudioListener.volume = forcedVolume;
            if (mute) AudioListener.volume = 0f;
            if (now >= nextCheck || (ear == null && recordDir != null))
            {
                nextCheck = now + 2f;  // and every frame while no listener has the recorder (a new one is silenced at once)
                Attach();
            }
            if (censusAt > 0f && now >= censusAt)
            {
                censusAt = -1f;
                Census("load");
            }
            if (recDone && rec != null)
            {
                short[] buffer = rec;
                rec = null;
                string path = recName;
                int channels = recChannels, rate = AudioSettings.outputSampleRate;
                Debug.Log("CROW_MIXREC done " + path + " (" + (buffer.Length / (double)(channels * rate)).ToString("F1", CultureInfo.InvariantCulture) + " s)");
                new Thread(() => WriteWav(path, buffer, channels, rate)) { IsBackground = true, Name = "CrowMixWrite" }.Start();
            }
        }

        // The recorder on the listener in use (the player's moves with its scene).
        static void Attach()
        {
            if (recordDir == null) return;
            if (ear == null || !ear.isActiveAndEnabled)
            {
                AudioListener found = null;
                foreach (AudioListener listener in UnityEngine.Object.FindObjectsOfType<AudioListener>())
                    if (listener.isActiveAndEnabled) found = listener;
                if (found == null) return;
                ear = found;
            }
            GameObject go = ear.gameObject;
            if (recorder == null || recorder.gameObject != go)
            {
                recorder = go.AddComponent<Diag>();
                Debug.Log("CROW_MIX recorder on " + go.name);
            }
        }

        static void WriteWav(string path, short[] samples, int channels, int rate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Create))
                using (var writer = new System.IO.BinaryWriter(stream))
                {
                    int bytes = samples.Length * 2;
                    writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                    writer.Write(36 + bytes);
                    writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                    writer.Write(16);
                    writer.Write((short)1);
                    writer.Write((short)channels);
                    writer.Write(rate);
                    writer.Write(rate * channels * 2);
                    writer.Write((short)(channels * 2));
                    writer.Write((short)16);
                    writer.Write(Encoding.ASCII.GetBytes("data"));
                    writer.Write(bytes);
                    var chunk = new byte[65536];
                    for (int i = 0; i < samples.Length;)
                    {
                        int n = Math.Min(chunk.Length / 2, samples.Length - i);
                        Buffer.BlockCopy(samples, i * 2, chunk, 0, n * 2);
                        writer.Write(chunk, 0, n * 2);
                        i += n;
                    }
                }
                Debug.Log("CROW_MIXREC written " + path);
            }
            catch (Exception e)
            {
                Debug.Log("CROW_MIXREC failed " + path + ": " + e.Message);
            }
        }

        internal static bool Change(string key, string value)
        {
            switch (key)
            {
                case "mixrec":
                    float seconds;
                    float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
                    if (recordDir == null || recorder == null || seconds <= 0f)
                    {
                        Debug.Log("CROW_MIXREC needs -crow-mix-record <dir> and a listener");
                        return true;
                    }
                    if (rec != null) return true;  // one at a time
                    recCount++;
                    recName = System.IO.Path.Combine(recordDir, "mix-" + recCount + "-" + SceneManager.GetActiveScene().name.Replace(' ', '_') + ".wav");
                    recAt = 0;
                    recDone = false;
                    rec = new short[(int)(seconds * AudioSettings.outputSampleRate) * 2];
                    Debug.Log("CROW_MIXREC start " + recCount + " " + recName + " " + seconds + " s at t=" + Time.realtimeSinceStartup.ToString("F2", CultureInfo.InvariantCulture));
                    return true;
                case "acensus":
                    Census("key");
                    return true;
                case "aidle":
                    // A test: every enabled source not playing switched off (off), and back (on) without playing.
                    int count = 0;
                    if (value == "off")
                        foreach (AudioSource source in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                        {
                            if (!source.enabled || source.isPlaying || source.GetComponent<Diag>() != null) continue;
                            source.enabled = false;
                            idleOff.Add(source);
                            count++;
                        }
                    else
                    {
                        foreach (AudioSource source in idleOff)
                        {
                            if (source == null) continue;
                            bool awake = source.playOnAwake;
                            source.playOnAwake = false;
                            source.enabled = true;
                            source.playOnAwake = awake;
                            count++;
                        }
                        idleOff.Clear();
                    }
                    Debug.Log("CROW_AUDIO idle sources " + value + ": " + count);
                    return true;
                case "apause":
                    AudioListener.pause = value == "on";
                    Debug.Log("CROW_AUDIO listener pause " + AudioListener.pause);
                    return true;
                case "lvol":
                    // Never on a PC player: it would undo -crow-mute (listener volume 0) and play through the speakers.
                    if (Application.platform != RuntimePlatform.PSP2 || ProbeFiles.Args.Contains("-crow-mute"))
                    {
                        Debug.Log("CROW_AUDIO lvol ignored (PS Vita only, and never with -crow-mute)");
                        return true;
                    }
                    if (value == "game")
                    {
                        forcedVolume = -1f;
                        if (gameVolume >= 0f) AudioListener.volume = gameVolume;
                    }
                    else
                    {
                        if (forcedVolume < 0f) gameVolume = AudioListener.volume;
                        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out forcedVolume);
                        AudioListener.volume = forcedVolume;
                    }
                    Debug.Log("CROW_AUDIO listener volume " + AudioListener.volume.ToString("F2", CultureInfo.InvariantCulture) + " (game " + gameVolume.ToString("F2", CultureInfo.InvariantCulture) + ")");
                    return true;
                case "athread":
                    string[] parts = value.Split(':');
                    if (parts.Length == 2 && parts[1] == "game")
                        Debug.Log("CROW_THREADS athread " + Threads.SetByName(parts[0], 0, 0, true));
                    else if (parts.Length == 3)
                    {
                        int priority, mask = 0;
                        int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out priority);
                        foreach (char c in parts[2])
                            if (c >= '0' && c <= '2') mask |= 0x10000 << (c - '0');
                        Debug.Log("CROW_THREADS athread " + Threads.SetByName(parts[0], priority, mask, false));
                    }
                    return true;
                case "highpass":
                    FilterGate.SwitchType(typeof(AudioHighPassFilter), value == "on");
                    return true;
                case "echo":
                    FilterGate.SwitchType(typeof(AudioEchoFilter), value == "on");
                    return true;
                case "chorus":
                    FilterGate.SwitchType(typeof(AudioChorusFilter), value == "on");
                    return true;
                case "distortion":
                    FilterGate.SwitchType(typeof(AudioDistortionFilter), value == "on");
                    return true;
            }
            return false;
        }

        static string PathOf(Transform t)
        {
            string text = t.name;
            for (int i = 0; i < 2 && t.parent != null; i++)
            {
                t = t.parent;
                text = t.name + "/" + text;
            }
            return text.Replace(' ', '_');
        }

        static string F(float v, string format = "F2")
        {
            return v.ToString(format, CultureInfo.InvariantCulture);
        }

        // The source's gain at the listener: its volume and its rolloff by distance, blended by spatialBlend
        // (the mixer groups and the listener's volume left out).
        static float Gain(AudioSource source, float distance)
        {
            float min = Mathf.Max(source.minDistance, 0.0001f), max = Mathf.Max(source.maxDistance, min);
            float g;
            switch (source.rolloffMode)
            {
                case AudioRolloffMode.Linear:
                    g = distance <= min ? 1f : Mathf.Clamp01(1f - (distance - min) / Mathf.Max(max - min, 0.0001f));
                    break;
                case AudioRolloffMode.Custom:
                    AnimationCurve curve = source.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
                    g = curve != null ? Mathf.Max(0f, curve.Evaluate(distance / max)) : 1f;
                    break;
                default:
                    g = distance <= min ? 1f : min / Mathf.Min(distance, max);
                    break;
            }
            return source.volume * Mathf.Lerp(1f, g, source.spatialBlend);
        }

        static string Filters(GameObject go, bool detail)
        {
            var text = new StringBuilder();
            foreach (Behaviour b in go.GetComponents<Behaviour>())
            {
                string name = null;
                if (b is AudioReverbFilter) name = "reverb" + (detail ? "(" + F(((AudioReverbFilter)b).decayTime) + "s)" : "");
                else if (b is AudioLowPassFilter) name = "lowpass(" + F(((AudioLowPassFilter)b).cutoffFrequency, "F0") + ")";
                else if (b is AudioHighPassFilter) name = "highpass(" + F(((AudioHighPassFilter)b).cutoffFrequency, "F0") + ")";
                else if (b is AudioEchoFilter) name = "echo(" + F(((AudioEchoFilter)b).delay, "F0") + ")";
                else if (b is AudioChorusFilter) name = "chorus";
                else if (b is AudioDistortionFilter) name = "distortion";
                else if (b is Diag) name = Mine(b) ? "crowrec" : "diag";
                if (name == null) continue;
                if (text.Length > 0) text.Append('+');
                text.Append(name).Append(b.enabled ? "" : "(off)");
            }
            return text.Length > 0 ? text.ToString() : "-";
        }

        internal static void Census(string why)
        {
            AudioListener listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
            Vector3 at = listener != null ? listener.transform.position : Vector3.zero;
            string scene = SceneManager.GetActiveScene().name.Replace(' ', '_');
            AudioSource[] sources = UnityEngine.Object.FindObjectsOfType<AudioSource>();
            int playing = 0, loud = 0, quiet = 0, silent = 0, filtered = 0, streamed = 0, compressedMem = 0;
            var byGroup = new Dictionary<string, int>();
            var filterCounts = new Dictionary<string, int>();
            var rates = new Dictionary<int, int>();
            foreach (AudioSource source in sources)
            {
                if (!source.isPlaying) continue;
                playing++;
                float distance = Vector3.Distance(source.transform.position, at);
                float gain = Gain(source, distance);
                float db = gain > 0f ? 20f * Mathf.Log10(gain) : -999f;
                if (source.mute || gain <= 0f) silent++;
                else if (db < -40f) quiet++;
                else loud++;
                AudioClip clip = source.clip;
                string group = source.outputAudioMixerGroup != null ? source.outputAudioMixerGroup.name : "-";
                int count;
                byGroup.TryGetValue(group, out count);
                byGroup[group] = count + 1;
                string filters = Filters(source.gameObject, true);
                if (filters != "-") filtered++;
                foreach (Behaviour b in source.GetComponents<Behaviour>())
                {
                    if (!b.enabled) continue;
                    string type = b is AudioReverbFilter ? "reverb" : b is AudioLowPassFilter ? "lowpass" : b is AudioHighPassFilter ? "highpass" :
                                  b is AudioEchoFilter ? "echo" : b is AudioChorusFilter ? "chorus" : b is AudioDistortionFilter ? "distortion" : null;
                    if (type == null) continue;
                    filterCounts.TryGetValue(type, out count);
                    filterCounts[type] = count + 1;
                }
                if (clip != null)
                {
                    if (clip.loadType == AudioClipLoadType.Streaming) streamed++;
                    else if (clip.loadType == AudioClipLoadType.CompressedInMemory) compressedMem++;
                    rates.TryGetValue(clip.frequency, out count);
                    rates[clip.frequency] = count + 1;
                }
                Debug.Log("CROW_ACENSUS src scene=" + scene + " " + PathOf(source.transform) +
                          " clip=" + (clip != null ? clip.name.Replace(' ', '_') + "/" + F(clip.length, "F1") + "s/" + clip.frequency + "Hz/" + clip.channels + "ch/" + clip.loadType : "none") +
                          " vol=" + F(source.volume) + " pitch=" + F(source.pitch) + " blend=" + F(source.spatialBlend) + " d=" + F(distance, "F1") +
                          " pos=" + F(source.transform.position.x, "F1") + "," + F(source.transform.position.y, "F1") + "," + F(source.transform.position.z, "F1") +
                          " min=" + F(source.minDistance, "F1") + " max=" + F(source.maxDistance, "F0") + " roll=" + source.rolloffMode +
                          " gain=" + F(db, "F0") + "dB loop=" + (source.loop ? 1 : 0) + " prio=" + source.priority + " doppler=" + F(source.dopplerLevel) +
                          " spread=" + F(source.spread, "F0") + " group=" + group.Replace(' ', '_') + " zonemix=" + F(source.reverbZoneMix) +
                          (source.bypassReverbZones ? " nozones" : "") + (source.bypassEffects ? " bypassfx" : "") + (source.mute ? " muted" : "") +
                          " vmode=" + source.velocityUpdateMode + " filters=" + filters);
            }
            var line = new StringBuilder("CROW_ACENSUS sum scene=" + scene + " why=" + why + " sources=" + sources.Length + " playing=" + playing +
                                         " loud=" + loud + " quiet=" + quiet + " silent=" + silent + " filtered=" + filtered + " streamed=" + streamed +
                                         " compressedInMemory=" + compressedMem);
            line.Append(" groups=").Append(string.Join(",", byGroup.Select(p => p.Key.Replace(' ', '_') + ":" + p.Value).ToArray()));
            line.Append(" filtersOn=").Append(string.Join(",", filterCounts.Select(p => p.Key + ":" + p.Value).ToArray()));
            line.Append(" rates=").Append(string.Join(",", rates.Select(p => p.Key + ":" + p.Value).ToArray()));
            AudioConfiguration config = AudioSettings.GetConfiguration();
            line.Append(" realVoices=").Append(config.numRealVoices).Append(" virtualVoices=").Append(config.numVirtualVoices).Append(" buffer=").Append(config.dspBufferSize);
            line.Append(" ear=").Append(F(at.x, "F1")).Append(',').Append(F(at.y, "F1")).Append(',').Append(F(at.z, "F1"));
            line.Append(" listenerVolume=").Append(F(AudioListener.volume)).Append(" listener=").Append(listener != null ? PathOf(listener.transform) + ":" + Filters(listener.gameObject, true) : "none");
            Debug.Log(line.ToString());
            // Every reverb filter in the loaded scenes, on or off, with its settings (are the presets shared?).
            foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(typeof(AudioReverbFilter)))
            {
                var r = found as AudioReverbFilter;
                if (r == null || !r.gameObject.scene.IsValid()) continue;
                AudioSource source = r.GetComponent<AudioSource>();
                Debug.Log("CROW_ACENSUS reverb scene=" + scene + " " + PathOf(r.transform) + " on=" + (r.enabled ? 1 : 0) + " active=" + (r.gameObject.activeInHierarchy ? 1 : 0) +
                          " playing=" + (source != null && source.isPlaying ? 1 : 0) + " clip=" + (source != null && source.clip != null ? source.clip.name.Replace(' ', '_') : "-") +
                          " d=" + F(Vector3.Distance(r.transform.position, at), "F1") + " preset=" + r.reverbPreset + " dry=" + F(r.dryLevel, "F0") + " room=" + F(r.room, "F0") +
                          " roomHF=" + F(r.roomHF, "F0") + " roomLF=" + F(r.roomLF, "F0") + " decay=" + F(r.decayTime) + " decayHF=" + F(r.decayHFRatio) +
                          " refl=" + F(r.reflectionsLevel, "F0") + " reflDelay=" + F(r.reflectionsDelay, "F3") + " rev=" + F(r.reverbLevel, "F0") + " revDelay=" + F(r.reverbDelay, "F3") +
                          " hf=" + F(r.hfReference, "F0") + " lf=" + F(r.lfReference, "F0") + " diffusion=" + F(r.diffusion, "F0") + " density=" + F(r.density, "F0"));
            }
            foreach (AudioReverbZone zone in UnityEngine.Object.FindObjectsOfType<AudioReverbZone>())
                Debug.Log("CROW_ACENSUS zone scene=" + scene + " " + PathOf(zone.transform) + " on=" + (zone.enabled ? 1 : 0) + " preset=" + zone.reverbPreset +
                          " min=" + F(zone.minDistance, "F1") + " max=" + F(zone.maxDistance, "F1") + " d=" + F(Vector3.Distance(zone.transform.position, at), "F1") +
                          " room=" + F(zone.room, "F0") + " decay=" + F(zone.decayTime));
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
            string cpus = CrowFlags.Optional(args, "-crow-loader-cpus") ?? "1";
            int mask = 0;
            foreach (char c in cpus)
                if (c >= '0' && c <= '2') mask |= 0x10000 << (c - '0');
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
            try { return available ? SelfNative() : 0; }
            catch (Exception) { return 0; }
        }

        // For Quality's -crow-loop-cpu: the calling thread's UID (0 without the plugin), and a
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

        // For Quality's frame probe (any thread, its own buffers): a thread's status, wait type, the UID it
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

        // Quality's workers= and gfxthread= keys: a thread's priority and CPUs now (0 keeps either); the old
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

        // MixProbe's athread= key: every known thread whose name has <name> set to a priority and CPU mask now
        // (0 keeps either; game puts back what it had before the first change). The names set, or why none.
        static readonly Dictionary<int, int[]> before = new Dictionary<int, int[]>();

        internal static string SetByName(string name, int priority, int mask, bool restore)
        {
            if (!available) return "no thread sampler (-crow-audio)";
            KeyValuePair<int, Known>[] entries;
            lock (gate) entries = known.ToArray();
            var info = new int[10];
            var buffer = new byte[32];
            var done = new List<string>();
            foreach (KeyValuePair<int, Known> pair in entries)
            {
                if (CrowThreadQuery(pair.Key, info, buffer) < 0) continue;
                string kernelName = Encoding.ASCII.GetString(buffer);
                int end = kernelName.IndexOf('\0');
                if (end >= 0) kernelName = kernelName.Substring(0, end);
                if (kernelName.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0 &&
                    Short(kernelName).IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int[] was;
                if (!before.TryGetValue(pair.Key, out was)) before[pair.Key] = was = new[] { info[1], info[2] };
                int result = restore ? CrowThreadSet(pair.Key, was[0], was[1]) : CrowThreadSet(pair.Key, priority, mask);
                done.Add(Short(kernelName) + "[p" + info[1].ToString("x") + " c" + Cpus(info[2]) + "->" +
                         (restore ? "p" + was[0].ToString("x") + " c" + Cpus(was[1]) : (priority > 0 ? "p" + priority.ToString("x") : "p=") + " c" + (mask > 0 ? Cpus(mask) : "=")) +
                         (result < 0 ? " failed 0x" + result.ToString("X8") : "") + "]");
            }
            return done.Count > 0 ? string.Join(" ", done.ToArray()) : "no thread named " + name;
        }

        internal static string ThreadName(int uid)
        {
            var info = new int[10];
            var name = new byte[32];
            if (CrowThreadQuery(uid, info, name) < 0) return null;
            int end = Array.IndexOf(name, (byte)0);
            return System.Text.Encoding.ASCII.GetString(name, 0, end < 0 ? name.Length : end);
        }

        internal static string SyncName(int uid, int waitType)
        {
            var name = new byte[32];
            if (CrowSyncName(uid, waitType, name) < 0) return null;
            int end = Array.IndexOf(name, (byte)0);
            return System.Text.Encoding.ASCII.GetString(name, 0, end < 0 ? name.Length : end);
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
            return string.Join(" ", rows.OrderByDescending(r => r.Key).Select(r => r.Value).ToArray());
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
                // By the kernel's name or as CROW_THREADS shows it (spaces as underscores: Unity's job
                // workers are "Worker Thread", and the flag's value can't hold a space).
                if (kernelName.IndexOf(setting[0], StringComparison.OrdinalIgnoreCase) < 0 &&
                    Short(kernelName).IndexOf(setting[0], StringComparison.OrdinalIgnoreCase) < 0) continue;
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

    // -crow-doppler: the pitch the mixer gives sounds on moving bodies, measured on test tones (the header's
    // list). A source's velocity, which FMOD's Doppler reads, comes from its movement: per physics step for
    // an Auto source under a rigidbody (and Mara's AudioListener, under hers), per frame for a Dynamic one.
    // The game's bodies aren't interpolated, so a transform moves only in physics steps, and a frame with no
    // step or two reads a Dynamic source's velocity as 0 or twice what it is.
    public static class Doppler
    {
        internal const float Tone = 1000f;
        internal static int Rate;
        static float every, nextReport, placeAt = -1f, cycleStart, windowStart;
        static AudioClip clip;
        static readonly List<DopplerTap> taps = new List<DopplerTap>();
        static readonly List<DopplerTap> enemyTaps = new List<DopplerTap>();
        static readonly List<DopplerTap> wakeTaps = new List<DopplerTap>();
        static Transform mara, enemy, ear;
        static Vector3 maraLast, enemyLast, earLast;
        static float maraPath, enemyPath, earPath;
        static readonly int[] steps = new int[4];
        static int stepsThisFrame;
        static bool off;

        internal static void Install(string[] args)
        {
            int index = Array.IndexOf(args, "-crow-doppler");
            if (index < 0) return;
            float seconds;
            every = index + 1 < args.Length && float.TryParse(args[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ? seconds : 5f;
            Rate = AudioSettings.outputSampleRate;
            // A second at 48 kHz, 48 samples a cycle: the loop has no seam.
            var data = new float[48000];
            for (int i = 0; i < data.Length; i++) data[i] = Mathf.Sin(2f * Mathf.PI * (i % 48) / 48f);
            clip = AudioClip.Create("CrowDopplerTone", data.Length, 1, 48000, false);
            clip.SetData(data, 0);
            // Placed once the area has settled (Fight's setup is 12 s after the load).
            SceneManager.sceneLoaded += (scene, mode) => placeAt = Time.realtimeSinceStartup + 14f;
            Canvas.willRenderCanvases += Frame;
            Debug.Log("CROW_DOPPLER probe every " + every + " s; output " + Rate + " Hz");
        }

        // Diag's instances that are test tones' filters, and their tones (Diag.cs passes them here).
        static readonly Dictionary<Diag, DopplerTap> owners = new Dictionary<Diag, DopplerTap>();

        internal static bool Owns(Diag diag)
        {
            if (MixProbe.Owns(diag)) return true;
            if (clip == null) return false;
            lock (owners) return owners.ContainsKey(diag);
        }

        // On the mixer thread: false for Diag's own probe (the mixer tap's source).
        internal static bool Filter(Diag diag, float[] data, int channels)
        {
            if (MixProbe.Filter(diag, data, channels)) return true;
            if (clip == null) return false;
            DopplerTap tap;
            lock (owners)
                if (!owners.TryGetValue(diag, out tap)) return false;
            tap.Measure(data, channels);
            return true;
        }

        // Diag's FixedUpdate: the probe's own instance counts the steps.
        internal static void Step(Diag diag)
        {
            if (clip == null || Owns(diag)) return;
            stepsThisFrame++;
        }

        static void Frame()
        {
            float now = Time.realtimeSinceStartup;
            steps[Math.Min(stepsThisFrame, 3)]++;
            stepsThisFrame = 0;
            if (placeAt > 0f && now >= placeAt)
            {
                placeAt = -1f;
                Place();
                cycleStart = now;
                StartWindow(now);
                return;
            }
            if (taps.Count == 0) return;
            Track(mara, ref maraLast, ref maraPath);
            Track(enemy, ref enemyLast, ref enemyPath);
            Track(ear, ref earLast, ref earPath);
            // The wake tones: off for 2 s, then on and played again, every 4 s (IdleAudio's off and Wake).
            bool wantOff = (now - cycleStart) % 4f < 2f;
            if (wantOff != off)
            {
                off = wantOff;
                foreach (DopplerTap tap in wakeTaps)
                {
                    if (tap.Object == null) continue;
                    tap.Source.enabled = !off;
                    if (off) continue;
                    // The filter comes back with its component (a source switched on again starts without it).
                    tap.Filter.enabled = false;
                    tap.Filter.enabled = true;
                    tap.Restart();
                    tap.Source.Play();
                }
            }
            if (now < nextReport) return;
            Report(now);
            StartWindow(now);
        }

        static void StartWindow(float now)
        {
            nextReport = now + every;
            windowStart = now;
            maraPath = enemyPath = earPath = 0f;
            Array.Clear(steps, 0, steps.Length);
            foreach (DopplerTap tap in taps)
                if (tap.Object != null) tap.Take();
        }

        static void Track(Transform target, ref Vector3 last, ref float path)
        {
            if (target == null) return;
            Vector3 position = target.position;
            if (last != Vector3.zero) path += Vector3.Distance(position, last);
            last = position;
        }

        static void Place()
        {
            foreach (DopplerTap tap in taps)
                if (tap.Object != null) UnityEngine.Object.Destroy(tap.Object);
            taps.Clear();
            lock (owners) owners.Clear();
            enemyTaps.Clear();
            wakeTaps.Clear();
            GameObject body = GameObject.Find("character RIGIDBODY");
            mara = body != null ? body.transform : null;
            AudioListener listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
            ear = listener != null ? listener.transform : null;
            maraLast = enemyLast = earLast = Vector3.zero;
            var still = new GameObject("CrowDoppler still");
            still.transform.position = (ear != null ? ear.position : Vector3.zero) + new Vector3(0f, 0f, 3f);
            Add(still.transform, Vector3.zero, "still", AudioVelocityUpdateMode.Auto, 1.01f, 0f);
            Add(still.transform, Vector3.zero, "flat", AudioVelocityUpdateMode.Auto, 1.01f, 0f).Source.spatialBlend = 0f;
            if (mara != null)
            {
                Add(mara, new Vector3(0f, 0.8f, 1.5f), "mara-dyn", AudioVelocityUpdateMode.Dynamic, 1f, 1f);
                Add(mara, new Vector3(0f, 0.8f, 1.5f), "mara-auto", AudioVelocityUpdateMode.Auto, 1f, 1f);
            }
            enemy = PickEnemy(null);
            Transform holder = enemy != null ? enemy : still.transform;
            enemyTaps.Add(Add(holder, new Vector3(0f, 0.5f, 0f), "enemy-dyn", AudioVelocityUpdateMode.Dynamic, 1f, 1f));
            enemyTaps.Add(Add(holder, new Vector3(0f, 0.5f, 0f), "enemy-auto", AudioVelocityUpdateMode.Auto, 1f, 1f));
            wakeTaps.Add(Add(holder, new Vector3(0f, 0.5f, 0f), "wake-dyn", AudioVelocityUpdateMode.Dynamic, 1f, 1f));
            wakeTaps.Add(Add(holder, new Vector3(0f, 0.5f, 0f), "wake-auto", AudioVelocityUpdateMode.Auto, 1f, 1f));
            enemyTaps.AddRange(wakeTaps);
            off = false;
            Debug.Log("CROW_DOPPLER placed: mara=" + (mara != null) + " ear=" + (ear != null ? ear.name.Replace(' ', '_') : "none") +
                      " enemy=" + Describe(enemy) + " fixedDt=" + Time.fixedDeltaTime.ToString("F4", CultureInfo.InvariantCulture));
        }

        static string Describe(Transform target)
        {
            if (target == null) return "none";
            float distance = mara != null ? Vector3.Distance(target.position, mara.position) : -1f;
            Rigidbody body = target.GetComponent<Rigidbody>();
            return target.name.Replace(' ', '_') + "(" + distance.ToString("F1", CultureInfo.InvariantCulture) + "m," +
                   (body != null ? body.velocity.magnitude.ToString("F2", CultureInfo.InvariantCulture) : "?") + "m/s)";
        }

        // The nearest awake enemy body to Mara, moving ones first.
        static Transform PickEnemy(Transform not)
        {
            Transform best = null;
            float bestScore = float.MaxValue;
            Vector3 from = mara != null ? mara.position : Vector3.zero;
            foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
            {
                if (body.isKinematic || body.transform == not || !body.name.StartsWith("Enemy ", StringComparison.Ordinal)) continue;
                float score = Vector3.Distance(body.position, from) + (body.velocity.magnitude > 0.2f ? 0f : 1000f);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = body.transform;
                }
            }
            return best;
        }

        static DopplerTap Add(Transform parent, Vector3 local, string label, AudioVelocityUpdateMode mode, float pitch, float doppler)
        {
            var go = new GameObject("CrowDoppler " + label);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = local;
            AudioSource source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.playOnAwake = false;
            source.priority = 0;
            source.volume = 0.02f;
            source.pitch = pitch;
            source.spatialBlend = 1f;
            source.dopplerLevel = doppler;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 500f;
            source.maxDistance = 1000f;
            source.bypassReverbZones = true;
            source.velocityUpdateMode = mode;
            var tap = new DopplerTap { Label = label, Source = source, Object = go, Transform = go.transform };
            tap.Filter = go.AddComponent<Diag>();
            lock (owners) owners[tap.Filter] = tap;
            source.Play();
            taps.Add(tap);
            return tap;
        }

        static void Report(float now)
        {
            float seconds = Math.Max(now - windowStart, 0.001f);
            var line = new StringBuilder("CROW_DOPPLER t=");
            line.Append(now.ToString("F1", CultureInfo.InvariantCulture));
            line.Append(" scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            line.Append(" steps=").Append(steps[0]).Append('/').Append(steps[1]).Append('/').Append(steps[2]).Append('/').Append(steps[3]);
            line.Append(" speed mara=").Append((maraPath / seconds).ToString("F2", CultureInfo.InvariantCulture));
            line.Append(" enemy=").Append((enemyPath / seconds).ToString("F2", CultureInfo.InvariantCulture));
            line.Append(" ear=").Append((earPath / seconds).ToString("F2", CultureInfo.InvariantCulture));
            line.Append(" listenerVolume=").Append(AudioListener.volume.ToString("F2", CultureInfo.InvariantCulture));
            foreach (DopplerTap tap in taps)
                if (tap.Object != null) line.Append(" | ").Append(tap.Take());
            Debug.Log(line.ToString());
            // An enemy that stood still all window: the tones move to one that walks.
            if (enemy == null || enemyPath < 0.3f)
            {
                Transform next = PickEnemy(enemy);
                if (next != null && next != enemy)
                {
                    enemy = next;
                    enemyLast = Vector3.zero;
                    foreach (DopplerTap tap in enemyTaps)
                    {
                        if (tap.Object == null) continue;
                        tap.Transform.SetParent(next, false);
                        tap.Transform.localPosition = new Vector3(0f, 0.5f, 0f);
                        tap.Skip = 12;  // the move itself is a jump
                    }
                    Debug.Log("CROW_DOPPLER enemy -> " + Describe(next));
                }
            }
        }
    }

    // A Doppler test tone's pitch, block by block, from inside the mixer (Doppler above). Its filter is a
    // Diag component (a new MonoBehaviour would need a full build), which passes the blocks here.
    public class DopplerTap
    {
        internal string Label;
        internal AudioSource Source;
        internal GameObject Object;
        internal Transform Transform;
        internal Diag Filter;
        internal volatile int Skip;
        volatile bool restarted;
        readonly object gate = new object();
        long blocks, jumps, wakeBlocks, calls;
        float peak;
        double sum, min = 9.0, max, sumDelta, maxDelta, wakeMax, last = -1.0;
        int wake;

        internal void Restart()
        {
            restarted = true;
        }

        internal void Measure(float[] data, int channels)
        {
            double pitch = Pitch(data, channels);
            float loudest = 0f;
            for (int i = 0; i < data.Length; i++) loudest = Math.Max(loudest, Math.Abs(data[i]));
            lock (gate)
            {
                calls++;
                peak = Math.Max(peak, loudest);
                if (restarted)
                {
                    restarted = false;
                    last = -1.0;
                    wake = 6;  // the first ~6 blocks after the Play
                }
                if (pitch <= 0.0) return;
                if (Skip > 0)
                {
                    Skip--;
                    last = -1.0;
                    return;
                }
                if (wake > 0)
                {
                    wake--;
                    wakeBlocks++;
                    wakeMax = Math.Max(wakeMax, Math.Abs(pitch - 1.0));
                }
                blocks++;
                sum += pitch;
                min = Math.Min(min, pitch);
                max = Math.Max(max, pitch);
                if (last > 0.0)
                {
                    double delta = Math.Abs(pitch - last);
                    sumDelta += delta;
                    maxDelta = Math.Max(maxDelta, delta);
                    if (delta > 0.005) jumps++;
                }
                last = pitch;
            }
        }

        // The block's frequency over the tone's, from its rising zero crossings (interpolated between
        // samples), the channels summed (one panned sine).
        static double Pitch(float[] data, int channels)
        {
            if (channels <= 0) return -1.0;
            int frames = data.Length / channels;
            double first = -1.0, latest = -1.0;
            int count = 0;
            float previous = 0f;
            for (int i = 0; i < frames; i++)
            {
                float sample = 0f;
                for (int c = 0; c < channels; c++) sample += data[i * channels + c];
                if (i > 0 && previous < 0f && sample >= 0f)
                {
                    double at = i - 1 + previous / (double)(previous - sample);
                    if (count == 0) first = at;
                    latest = at;
                    count++;
                }
                previous = sample;
            }
            if (count < 3 || latest <= first) return -1.0;
            return (count - 1) / (latest - first) * Doppler.Rate / Doppler.Tone;
        }

        static string Percent(double pitch)
        {
            return ((pitch - 1.0) * 100.0).ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
        }

        // The window's numbers ("label: ..."), and a new window.
        internal string Take()
        {
            lock (gate)
            {
                var text = new StringBuilder(Label);
                text.Append("[").Append(Source != null ? Source.velocityUpdateMode.ToString() : "?").Append("]");
                text.Append(" calls=").Append(calls).Append(" peak=").Append(peak.ToString("F3", CultureInfo.InvariantCulture));
                text.Append(" n=").Append(blocks);
                if (blocks > 0)
                {
                    text.Append(" mean=").Append(Percent(sum / blocks)).Append("% min=").Append(Percent(min)).Append("% max=").Append(Percent(max));
                    text.Append("% dmean=").Append((sumDelta * 100.0 / Math.Max(blocks - 1, 1)).ToString("F3", CultureInfo.InvariantCulture));
                    text.Append("% dmax=").Append((maxDelta * 100.0).ToString("F2", CultureInfo.InvariantCulture)).Append("% jumps=").Append(jumps);
                }
                if (wakeBlocks > 0) text.Append(" wakeMax=").Append((wakeMax * 100.0).ToString("F2", CultureInfo.InvariantCulture)).Append('%');
                blocks = jumps = wakeBlocks = calls = 0;
                peak = 0f;
                sum = max = sumDelta = maxDelta = wakeMax = 0.0;
                min = 9.0;
                last = -1.0;
                return text.ToString();
            }
        }
    }
}
