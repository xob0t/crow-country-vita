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
//   -crow-shared-reverb on|off  sources whose reverbs are alike share one (SharedReverb; the PS Vita's default: on).
//   -crow-thread <name>:<priority>:<cpus>  with -crow-audio, set the priority (0 keeps it; 64 highest ..
//                             191) and the CPUs (digits 0-2, e.g. 1 or 12; - keeps them) of each thread
//                             whose name has <name> ("FMOD mixer", UnityPreload) when the probe finds it;
//                             repeatable.
// The measurement-only probes (-crow-doppler, -crow-mix-mute, -crow-mix-record, -crow-audio-census,
// -crow-reverb-null) are in AudioProbe.cs (moved there as they were, 21 Oct).
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
            SharedReverb.Install(Args, FilterGate.GatesReverbs);
            ReverbNull.Install(Args);
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
                CrowEvents.Loaded("Audio.sceneLoaded", (scene, mode) => Debug.Log("CROW_AUDIO scene " + scene.name + " (" + mode + ") at " + Time.realtimeSinceStartup.ToString("F2", CultureInfo.InvariantCulture)));
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
        // A filter on a source, or a shared reverb (SharedReverb.Group: one reverb for its members' sum, on while
        // its members are routed to it), which counts as one filter.
        struct Entry
        {
            public Behaviour Filter;
            public AudioSource Source;
            public SharedReverb.Group Shared;
            public float Tail, OffAt, Score;

            public bool Valid { get { return Shared != null ? Shared.Alive : Filter != null; } }
            public bool On { get { return Shared != null ? Shared.Routed : Filter.enabled; } }

            public void Set(bool on)
            {
                if (Shared != null) Shared.Route(on);
                else Filter.enabled = on;
            }
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

        // Whether FilterGate switches the reverb filters (SharedReverb then leaves its groups' on and off to it).
        internal static bool GatesReverbs { get { return active && (types.Contains("reverb") || types.Contains("all")); } }
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
            CrowEvents.Loaded("FilterGate.sceneLoaded", (scene, mode) =>
            {
                nextFind = 0f;
                // The new area's cap once it is the active scene; its first second (the load's tail) unwatched.
                resolve = true;
                watchFrom = Time.realtimeSinceStartup + 1f;
                windowDsp = -1.0;
            });
            CrowEvents.Canvases("FilterGate.Update", Update);
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
            // The reverbs that share one (SharedReverb): an entry for each group, its members' filters left out below.
            if (list.Contains(typeof(AudioReverbFilter)))
                foreach (SharedReverb.Group group in SharedReverb.Find())
                    entries.Add(new Entry { Shared = group, Tail = group.Tail });
            foreach (Type type in list)
                foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(type))
                {
                    var filter = found as Behaviour;
                    if (filter == null || !filter.gameObject.scene.IsValid()) continue;
                    if (!SharedReverb.GameHasOn(filter)) continue;  // off in the game (Pool's water wheel and tank): stays off
                    AudioSource source = filter.GetComponent<AudioSource>();
                    // A filter without a source on its object filters the listener's whole mix: left alone.
                    if (source == null && idleOnly) continue;
                    var reverb = filter as AudioReverbFilter;
                    if (reverb != null && SharedReverb.Owns(reverb)) continue;  // its group's entry above
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
                if (entry.Shared != null)
                {
                    if (entry.Shared.Alive)
                        foreach (SharedReverb.Member member in entry.Shared.Members)
                            if (member.Source != null) score = Mathf.Min(score, Audibility(member.Source, ear));
                    if (entry.Shared.Routed) score *= 0.7f;
                }
                else if (entry.Filter != null && entry.Source != null)
                {
                    score = Audibility(entry.Source, ear);
                    if (entry.Filter.enabled) score *= 0.7f;
                }
                entry.Score = score;
                entries[i] = entry;
            }
            order.Clear();
            for (int i = 0; i < entries.Count; i++) order.Add(i);
            order.Sort((a, b) => entries[a].Score.CompareTo(entries[b].Score));
        }

        // 2D sources first (0), then by distance from the listener over the source's range.
        static float Audibility(AudioSource source, Vector3 ear)
        {
            return source.spatialBlend < 0.5f || listener == null ? 0f : Vector3.Distance(source.transform.position, ear) / Mathf.Max(source.maxDistance, 0.01f);
        }

        // Whether a source wants its filter now: playing and heard (or the silent rule off).
        static bool Wants(AudioSource source, Vector3 ear)
        {
            return source != null && source.isActiveAndEnabled && source.isPlaying && !(silentRule && Silent(source, ear));
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
                if (idleOnly && entry.Shared != null && entry.Shared.Alive)
                {
                    // A shared reverb: while any of its members wants one, or within the tail of the last that did.
                    foreach (SharedReverb.Member member in entry.Shared.Members)
                        if (Wants(member.Source, ear))
                        {
                            want = true;
                            break;
                        }
                    if (want)
                    {
                        entry.OffAt = now + entry.Tail;
                        entries[i] = entry;
                    }
                    else want = now < entry.OffAt;
                }
                else if (idleOnly && entry.Filter != null && entry.Source != null && entry.Source.isActiveAndEnabled)
                {
                    if (Wants(entry.Source, ear))
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
                    if (!entry.Valid) continue;
                    bool keep = wants[i] && kept < max;
                    if (keep) kept++;
                    bool enabled = entry.On;
                    if (pass == 0)
                    {
                        if (enabled && !keep)
                        {
                            entry.Set(false);
                            switches++;
                        }
                        else if (enabled) on++;
                    }
                    else if (keep && !enabled && on < max)
                    {
                        entry.Set(true);
                        switches++;
                        on++;
                    }
                }
            }
            onCount = on;
            Share(now);
        }

        // Unity's job workers on CPU 2 as well as CPU 1 (Quality.cs GfxThreads, 64:12) while the mixer has CPU 2 to
        // spare: no filter on (a shared reverb counts as one; a reverb costs the mixer 18-20% of CPU 2, a shared one
        // 28, the mixer without one 19-40%) for a second, and the DSP clock never behind in this area. Back on CPU 1
        // alone (64:1, the PS Vita's default) in the frame a filter comes on. With one filter allowed (wsharemax=1):
        // Fairy Forest's steady fight on its shared reverb -0.4 ms a frame, the DSP clock down to 0.989; Corridor D's
        // 0.980-0.993 for a whole minute, over the cap's 0.97, so nothing took it back. At the same priority as the mixer a worker's job delays a mix block by a few ms (the tap's
        // longest gap 13 -> 18 ms, no late block); with Pool's two reverbs on, sharing put the DSP clock at 99% of real
        // time (adapting caps then take reverbs off) and the render thread there at 96%.
        // -crow-workers-share off (wshare=off) keeps them on CPU 1; an explicit -crow-gfx-workers also turns it off.
        static bool shareAuto, shared, blockedHere;
        static int shareMaxOn;  // the filters on that still let the workers share (wsharemax=<n>, a test)
        static float quietSince = -1f;
        static readonly HashSet<string> shareBlocked = new HashSet<string>();

        static void Share(float now)
        {
            if (!shareAuto) return;
            bool quiet = onCount <= shareMaxOn && !blockedHere;
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
                case "wsharemax":
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out shareMaxOn);
                    Debug.Log("CROW_AUDIO wsharemax=" + shareMaxOn);
                    return true;
                case "sreverb":
                    SharedReverb.Set(value != "off");
                    nextFind = 0f;  // FilterGate's entries again, from the filters or the groups
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
            int count = switches, shared = 0, sharedOn = 0;
            switches = 0;
            foreach (Entry entry in entries)
                if (entry.Shared != null)
                {
                    shared++;
                    if (entry.Shared.Routed) sharedOn++;
                }
            return " gated=" + entries.Count + "/" + count + " cap=" + max + " on=" + onCount + " shared=" + sharedOn + "/" + shared;
        }
    }

    // One reverb for sources whose reverbs are alike (on by default on the PS Vita; -crow-shared-reverb on|off,
    // sreverb=on|off). The corridors' and Fairy Forest's ambient loops each carry an AudioReverbFilter with the same
    // settings (Corridor S: six loops, User: room 0 dB, no reflections, 1 s), and each filter costs FMOD's mixer
    // 18-20% of CPU 2 on the PS Vita. A reverb is linear and time-invariant, so one reverb on the sum of the
    // sounds is the sum of one reverb on each: the members of a group (same 14 settings, same destination mixer
    // group, reverb the last filter on the source: no filter or OnAudioFilterRead after it, effects not bypassed,
    // no spatializer) go to a mixer of their own (asset_rules.shared_reverb_mixers: Assets/Resources/
    // CrowSharedReverb/SharedReverb<n>.mixer, a Master group with an SFX Reverb whose 14 parameters are exposed,
    // set from the members' filters) whose output is their old group, and their filters are off. Unity applies a
    // source's volume, rolloff and pan before its filters, so the sum reaches the shared reverb as it reached each:
    // rendered offline on Windows, per-source and shared differ by float rounding (-137 dB; ReverbNull below), 2D
    // or 3D, moving or with volume fades. A group counts as one filter for FilterGate's cap, ranking (its most
    // audible member) and silent rule (on while any member wants its filter, or within the tail of the last that
    // did); off, its members go to their old group with no reverb, as their filters would be off. Without FilterGate
    // (other players), every group is on. A group of one stays a filter. A mixer with no input suspends (its
    // m_EnableSuspend), so the pool's spare mixers cost nothing.
    public static class SharedReverb
    {
        public struct Member
        {
            public AudioReverbFilter Filter;
            public AudioSource Source;
        }

        public class Group
        {
            public readonly List<Member> Members = new List<Member>();
            public string Key, Name;
            public AudioMixerGroup Dest;
            public int Slot = -1;
            public float Tail;
            public bool Routed, Alive = true;

            // On: the members into the shared reverb; off: into their old group, with no reverb. Their filters stay off.
            public void Route(bool on)
            {
                if (!Alive) return;
                Routed = on;
                foreach (Member member in Members)
                {
                    if (member.Filter != null) member.Filter.enabled = false;
                    if (member.Source != null) member.Source.outputAudioMixerGroup = on ? inputs[Slot] : Dest;
                }
            }
        }

        const string Folder = "CrowSharedReverb/SharedReverb";
        internal const int Slots = 12;
        // The SFX Reverb's exposed parameters (p_<name>), in AudioReverbFilter's terms below.
        static readonly string[] Params = { "DryLevel", "Room", "RoomHF", "DecayTime", "DecayHFRatio", "Reflections", "ReflectDelay", "Reverb", "ReverbDelay", "Diffusion", "Density", "HFReference", "RoomLF", "LFReference" };
        static AudioMixer[] mixers;
        static AudioMixerGroup[] inputs;
        static readonly Group[] slotOwner = new Group[Slots];
        static readonly float[] slotFreed = new float[Slots];
        static bool on, gated, broken, held;
        static float nextFind;
        static readonly List<Group> groups = new List<Group>();
        static readonly Dictionary<AudioReverbFilter, Group> owner = new Dictionary<AudioReverbFilter, Group>();
        static readonly HashSet<string> logged = new HashSet<string>();

        internal static bool Active { get { return on && !broken; } }

        internal static void Install(string[] args, bool gatedByFilterGate)
        {
            string mode = CrowFlags.Value(args, "-crow-shared-reverb");
            on = mode == null ? Application.platform == RuntimePlatform.PSP2 && !args.Contains("-crow-audio-game") : mode != "off";
            gated = gatedByFilterGate;
            CrowEvents.Loaded("SharedReverb.sceneLoaded", (scene, loadMode) => nextFind = 0f);
            CrowEvents.Canvases("SharedReverb.Frame", Frame);
            Debug.Log("CROW_AUDIO shared reverb " + (on ? "on" : "off") + (gated ? " (FilterGate switches the groups)" : ""));
        }

        // Whether the game has a filter on: its enabled state the first time CrowCompat sees it (both finds run before
        // either switches it). Pool's water wheel and water tank carry a reverb the scene has off (m_Enabled 0, the
        // only two of the rip's 193) and nothing in the game switches on; taken as on, SharedReverb grouped them and
        // FilterGate gave them a shared reverb (heard on two sounds the game plays dry, and a cap slot the area's
        // wanted reverbs lost), and Release left them on (21 Oct). Filters the game has off are left alone.
        static readonly Dictionary<Behaviour, bool> gameOn = new Dictionary<Behaviour, bool>();

        internal static bool GameHasOn(Behaviour filter)
        {
            bool has;
            if (gameOn.TryGetValue(filter, out has)) return has;
            if (gameOn.Count > 2000)
                foreach (Behaviour gone in gameOn.Keys.Where(k => k == null).ToList()) gameOn.Remove(gone);
            gameOn[filter] = has = filter.enabled;
            if (!has) Debug.Log("CROW_AUDIO " + filter.GetType().Name + " " + Path(filter.transform) + " is off in the game: left alone");
            return has;
        }

        internal static bool Owns(AudioReverbFilter filter)
        {
            Group group;
            return filter != null && owner.TryGetValue(filter, out group) && group.Alive;
        }

        internal static string GroupOf(AudioReverbFilter filter)
        {
            Group group;
            return filter != null && owner.TryGetValue(filter, out group) && group.Alive ? group.Name + (group.Routed ? "" : "(off)") : "-";
        }

        // sreverb=on|off, and the null test's switch.
        internal static void Set(bool value)
        {
            if (value == on) return;
            on = value;
            if (!on) ReleaseAll();
            nextFind = 0f;
            Debug.Log("CROW_AUDIO shared reverb " + (on ? "on" : "off"));
        }

        // The null test holds the groups as they are (no finds of its own between passes).
        internal static void Hold(bool value)
        {
            held = value;
        }

        // Without FilterGate's reverb switching: every group on, found after each load and every 3 s.
        static void Frame()
        {
            if (gated || held || !Active) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextFind) return;
            nextFind = now + 3f;
            RouteAll();
        }

        // Every group found now and on (standalone and the null test).
        internal static void RouteAll()
        {
            foreach (Group group in Find())
                if (!group.Routed) group.Route(true);
        }

        static bool Load()
        {
            if (mixers != null) return true;
            mixers = new AudioMixer[Slots];
            inputs = new AudioMixerGroup[Slots];
            for (int i = 0; i < Slots; i++)
            {
                mixers[i] = Resources.Load<AudioMixer>(Folder + i);
                AudioMixerGroup[] found = mixers[i] != null ? mixers[i].FindMatchingGroups("Master") : null;
                if (found == null || found.Length == 0)
                {
                    Debug.Log("CROW_AUDIO shared reverb: no mixer " + Folder + i + " in this build (asset_rules.shared_reverb_mixers); off");
                    broken = true;
                    return false;
                }
                inputs[i] = found[0];
            }
            return true;
        }

        static float[] Values(AudioReverbFilter r)
        {
            return new[] { r.dryLevel, r.room, r.roomHF, r.decayTime, r.decayHFRatio, r.reflectionsLevel, r.reflectionsDelay, r.reverbLevel, r.reverbDelay, r.diffusion, r.density, r.hfReference, r.roomLF, r.lfReference };
        }

        // Whether a filter can share: on a source, the last filter of its chain, effects not bypassed, no spatializer.
        static bool Eligible(AudioReverbFilter filter, out AudioSource source, out string why)
        {
            source = filter.GetComponent<AudioSource>();
            why = null;
            if (source == null) why = "no source";
            else if (source.bypassEffects) why = "effects bypassed";
            else if (source.spatialize) why = "spatializer";
            else
            {
                bool after = false;
                foreach (Component c in filter.GetComponents<Component>())
                {
                    if (ReferenceEquals(c, filter))
                    {
                        after = true;
                        continue;
                    }
                    if (!after || c == null) continue;
                    if (c is AudioReverbFilter || c is AudioLowPassFilter || c is AudioHighPassFilter || c is AudioEchoFilter || c is AudioChorusFilter || c is AudioDistortionFilter)
                        why = "a " + c.GetType().Name + " after it";
                    else if (c is MonoBehaviour && FiltersAudio(c.GetType()))
                        why = "OnAudioFilterRead after it (" + c.GetType().Name + ")";
                    if (why != null) break;
                }
            }
            return why == null;
        }

        static readonly Dictionary<Type, bool> filterTypes = new Dictionary<Type, bool>();

        // Whether a script type has OnAudioFilterRead (then it is a filter in its source's chain).
        static bool FiltersAudio(Type type)
        {
            bool has;
            if (!filterTypes.TryGetValue(type, out has))
                filterTypes[type] = has = type.GetMethod("OnAudioFilterRead", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) != null;
            return has;
        }

        static string Key(float[] values, AudioMixerGroup dest)
        {
            var text = new StringBuilder(dest != null ? dest.GetInstanceID().ToString(CultureInfo.InvariantCulture) : "none");
            foreach (float v in values) text.Append('|').Append(v.ToString("R", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        // The groups in the loaded scenes, kept, grown, made or released. FilterGate calls it from its find.
        internal static List<Group> Find()
        {
            var alive = new List<Group>();
            if (!Active || !Load()) return alive;
            var candidates = new Dictionary<string, List<Member>>();
            var dests = new Dictionary<string, AudioMixerGroup>();
            var sample = new Dictionary<string, AudioReverbFilter>();
            foreach (AudioReverbFilter filter in Resources.FindObjectsOfTypeAll<AudioReverbFilter>())
            {
                if (filter == null || !filter.gameObject.scene.IsValid() || !GameHasOn(filter)) continue;
                Group group;
                AudioSource source;
                AudioMixerGroup dest;
                if (owner.TryGetValue(filter, out group) && group.Alive)
                {
                    source = filter.GetComponent<AudioSource>();
                    dest = group.Dest;
                    if (source == null) continue;
                }
                else
                {
                    string why;
                    if (!Eligible(filter, out source, out why))
                    {
                        if (logged.Add(filter.GetInstanceID() + why)) Debug.Log("CROW_AUDIO shared reverb: " + Path(filter.transform) + " keeps its own (" + why + ")");
                        continue;
                    }
                    dest = source.outputAudioMixerGroup;
                }
                string key = Key(Values(filter), dest);
                List<Member> list;
                if (!candidates.TryGetValue(key, out list))
                {
                    candidates[key] = list = new List<Member>();
                    dests[key] = dest;
                    sample[key] = filter;
                }
                list.Add(new Member { Filter = filter, Source = source });
            }
            // Groups whose key has fewer than two members left: released (their filters back), their mixer free.
            foreach (Group group in groups)
            {
                List<Member> list;
                if (!candidates.TryGetValue(group.Key, out list) || list.Count < 2) Release(group);
            }
            groups.RemoveAll(g => !g.Alive);
            foreach (AudioReverbFilter gone in owner.Keys.Where(k => k == null || !owner[k].Alive).ToList()) owner.Remove(gone);
            foreach (KeyValuePair<string, List<Member>> pair in candidates)
            {
                if (pair.Value.Count < 2) continue;
                Group group = groups.FirstOrDefault(g => g.Key == pair.Key);
                if (group == null)
                {
                    int slot = FreeSlot();
                    if (slot < 0)
                    {
                        if (logged.Add("full" + pair.Key)) Debug.Log("CROW_AUDIO shared reverb: all " + Slots + " mixers in use; " + pair.Value.Count + " filters keep their own");
                        continue;
                    }
                    group = new Group { Key = pair.Key, Dest = dests[pair.Key], Slot = slot };
                    if (!Assign(group, sample[pair.Key])) continue;
                    groups.Add(group);
                }
                // New members take the group's state; members gone drop out.
                group.Members.RemoveAll(m => m.Filter == null || m.Source == null);
                foreach (Member member in pair.Value)
                {
                    if (group.Members.Any(m => ReferenceEquals(m.Filter, member.Filter))) continue;
                    group.Members.Add(member);
                    owner[member.Filter] = group;
                    member.Filter.enabled = false;
                    member.Source.outputAudioMixerGroup = group.Routed ? inputs[group.Slot] : group.Dest;
                }
                alive.Add(group);
            }
            return alive;
        }

        static int FreeSlot()
        {
            int best = -1;
            for (int i = 0; i < Slots; i++)
                if (slotOwner[i] == null && (best < 0 || slotFreed[i] < slotFreed[best])) best = i;  // the longest free: its tail gone
            return best;
        }

        static bool Assign(Group group, AudioReverbFilter from)
        {
            AudioMixer mixer = mixers[group.Slot];
            float[] values = Values(from);
            for (int i = 0; i < Params.Length; i++)
                if (!mixer.SetFloat("p_" + Params[i], values[i]))
                {
                    Debug.Log("CROW_AUDIO shared reverb: " + mixer.name + " has no exposed p_" + Params[i] + "; off");
                    broken = true;
                    return false;
                }
            mixer.outputAudioMixerGroup = group.Dest;
            slotOwner[group.Slot] = group;
            group.Tail = from.decayTime + 0.3f;
            group.Name = mixer.name;
            var settings = new StringBuilder();
            for (int i = 0; i < Params.Length; i++) settings.Append(' ').Append(Params[i]).Append('=').Append(values[i].ToString("R", CultureInfo.InvariantCulture));
            Debug.Log("CROW_AUDIO shared reverb " + group.Name + " -> " + (group.Dest != null ? group.Dest.name : "listener") + ":" + settings);
            return true;
        }

        static void Release(Group group)
        {
            if (!group.Alive) return;
            foreach (Member member in group.Members)
            {
                if (member.Source != null) member.Source.outputAudioMixerGroup = group.Dest;
                if (member.Filter != null) member.Filter.enabled = GameHasOn(member.Filter);  // the game's state; FilterGate switches it from here
                if (!ReferenceEquals(member.Filter, null)) owner.Remove(member.Filter);
            }
            group.Alive = group.Routed = false;
            if (group.Slot >= 0 && ReferenceEquals(slotOwner[group.Slot], group))
            {
                slotOwner[group.Slot] = null;
                slotFreed[group.Slot] = Time.realtimeSinceStartup;
            }
        }

        static void ReleaseAll()
        {
            foreach (Group group in groups) Release(group);
            groups.Clear();
            owner.Clear();
        }

        internal static string Path(Transform t)
        {
            string text = t.name;
            for (int i = 0; i < 2 && t.parent != null; i++)
            {
                t = t.parent;
                text = t.name + "/" + text;
            }
            return text.Replace(' ', '_');
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
}
