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
//   -crow-reverb-max <n>      with idle, at most n of those filters on at once (default 0; a shared reverb counts
//                             as one), the most audible (FilterGate), filters already on at the load included; on the
//                             PS Vita a filter also comes on only while FMOD's mixer has room for one (FilterGate.Budget).
//   -crow-reverb-adapt off    keep that cap in every area, without the budget (by default it comes down where the
//                             mixer falls behind or goes over its budget).
//   -crow-reverb-silent off   give a filter to sources that can't be heard at the listener too (by default they
//                             get none: FilterGate.Silent).
//   -crow-workers-share off   keep Unity's job workers off CPU 2 while few filters are on (FilterGate.Share; the
//                             PS Vita's default shares it, unless -crow-gfx-workers is given).
//   -crow-loader-cpus <cpus>  the CPUs of Unity's loading thread (UnityPreload), digits 0-2 (the PS
//                             Vita's default: 1; - leaves it on the player's choice, CPU 2, FMOD's).
//   -crow-dsp-buffer <n>, -crow-real-voices <n>, -crow-sample-rate <hz>
//                             AudioSettings.Reset with these before the first scene.
//   -crow-load-priority <p>   the loading thread's priority (High, Normal, BelowNormal, Low) for the
//                             player's scene read in the background on the title (SceneFlow.cs' MaraScene; default Normal
//                             with the loading thread moved, else High).
//   -crow-shared-reverb on|off  sources whose reverbs are alike share one (SharedReverb; the PS Vita's default: on).
//   -crow-thread <name>:<priority>:<cpus>  with -crow-audio, set the priority (0 keeps it; 64 highest ..
//                             191) and the CPUs (digits 0-2, e.g. 1 or 12; - keeps them) of each thread
//                             whose name has <name> ("FMOD mixer", UnityPreload) when the probe finds it;
//                             repeatable.
//   aplay=<name>              (-crow-cycle, -crow-ab, -crow-fight-cycle) play the AudioSource on an active GameObject
//                             of that name, as the game does (the item pickup: aplay=new item).
// The mixer measurements of 19-21 Oct (-crow-doppler, -crow-mix-*, -crow-audio-census, -crow-reverb-null) are
// done and gone; their results are in docs/status-log.md.
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

        public static bool WantsTap { get { return CrowFlags.Has(Args, "-crow-audio-tap"); } }

        // The background loading priority for the player's scene (SceneFlow.cs' MaraScene).
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
                if (CrowFlags.Has(Args, "-crow-audio")) Debug.Log("CROW_AUDIO config " + before);
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
            Threads.MoveLoader(Args);
            AudioCensus.Install(Args);  // AudioCensus.cs: -crow-audio-census, a probe
        }

        // From Diag.Install, after the probe's object has its Diag (and its AudioSource, for the tap).
        internal static void Install(GameObject probe)
        {
            string[] args = Args;
            if (CrowFlags.Has(args, "-crow-audio")) every = CrowFlags.Number(args, "-crow-audio", 2f);
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
            public bool WasOn;  // on at the ranking

            public bool Valid { get { return Shared != null ? Shared.Alive : Filter != null; } }
            public bool On { get { return Shared != null ? Shared.Routed : Filter.enabled; } }
            public object Key { get { return Shared != null ? (object)Shared : Filter; } }

            public void Set(bool on)
            {
                if (Shared != null) Shared.Route(on);
                else Filter.enabled = on;
            }
        }

        static readonly HashSet<string> types = new HashSet<string>();
        static bool idleOnly, active;
        // At most this many filters on at once (-crow-reverb-max): each reverb costs about 20% of CPU 2
        // at 444 MHz, the mixer without them 15-20%; the title's New Game sounds start five at once. None by
        // default (30 Oct): one reverb took the mixer over its budget in Corridor S, Corridor B, Corridor D, Dig Site
        // and the title as soon as other sounds started (a pickup, a screen: 74-79% of a CPU, the DSP clock at 95%),
        // so the budget below let each area crackle once before it took the reverb off for good.
        static int max;
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
        // The mixer's budget (PS Vita; with the adaptive cap): the cap above came down only once the DSP clock had
        // fallen behind, so the mixer underran for seconds at most arrivals before it did (Corridor D: 30 times in 34
        // loads in the console logs of 15-29 Oct, the DSP clock at 67-98% of real time; the crackle at the transition
        // from Crow's Lounge, #8), and again when a second reverb came on in play (the item pickup's). The FMOD mixer
        // thread's own share of a CPU, read from the kernel every half second, tells it before: the mixer keeps up to
        // about 70% (Pool 65%, Corridor D's shared reverb 69-71% at 0.99-1.00) and falls behind from 75 (Corridor D:
        // 44-47% without its reverb, 74-78 with it, the DSP clock at 0.96-0.98 for 10 s), and a reverb costs it 20-30.
        // So a filter comes on only while the mixer's last half second left room for one under the budget, one per
        // half second, none in an area's first second (its sounds starting); over the budget, one goes off, for the
        // rest of the area's visits as the adaptive cap does. rbudget=off (-crow-ab, -crow-fight-cycle) leaves it out.
        // Nor while an area's clips are still loading (clipsLoading): a clip loads when it first plays (asset_rules sets
        // preloadAudioData 0), so the mixer's share is low until the area's loops have all started. Corridor D from the
        // title still loaded 6-7 clips 9 s after its arrival; the budget let its reverb on at 25-43%, and when the
        // loops started the mixer went to 77-78% (the DSP clock at 0.85-0.87 for 2 s, 249 ms short). The check runs
        // at each measure from a second after a scene load until no clip is loading, then not until the next load.
        const float Budget = 70f, ReverbCost = 30f;
        static volatile int mixerUid;
        static long mixerRun = -1, mixerTicks;
        static float mixerShare = -1f;  // percent of a CPU over the last half second; -1 unknown
        static bool budgetOn = true, room = true, clipsLoading;

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
                if (Application.platform != RuntimePlatform.PSP2 || CrowFlags.Has(args, "-crow-audio-game")) return;
                given = "reverb,idle";
            }
            foreach (string type in given.ToLowerInvariant().Split(','))
                if (type == "idle") idleOnly = true;
                else if (type.Length > 0) types.Add(type);
            if (types.Count == 0) return;
            max = CrowFlags.Int(args, "-crow-reverb-max", 0);
            if (!idleOnly) max = 0;
            adapt = CrowFlags.Value(args, "-crow-reverb-adapt") != "off";
            silentOn = CrowFlags.Value(args, "-crow-reverb-silent") != "off";
            shareAuto = Application.platform == RuntimePlatform.PSP2 && CrowFlags.Value(args, "-crow-gfx-workers") == null &&
                        CrowFlags.Value(args, "-crow-workers-share") != "off";
            ceiling = max;
            active = true;
            if (Application.platform == RuntimePlatform.PSP2 && adapt) Threads.Find("FMOD mixer", uid => mixerUid = uid);
            CrowEvents.Loaded("FilterGate.sceneLoaded", (scene, mode) =>
            {
                nextFind = 0f;
                // The new area's cap once it is the active scene; its first second (the load's tail) unwatched.
                resolve = true;
                watchFrom = Time.realtimeSinceStartup + 1f;
                windowDsp = -1.0;
                if (mixerShare >= 0f) room = false;  // the budget's first second
                clipsLoading = true;
            });
            CrowEvents.Unloaded("FilterGate.sceneUnloaded", scene => Destroyed.Forget(curves));
            CrowEvents.Canvases("FilterGate.Update", Update);
            Debug.Log("CROW_AUDIO filters off: " + string.Join(",", types.ToArray()) + (idleOnly ? " while their source is idle, at most " + max + " on" : ""));
        }

        // Each filter's and group's OffAt across a find, the only record of a tail: a reverb whose source stopped
        // keeps its decay.
        static readonly Dictionary<object, float> tails = new Dictionary<object, float>();

        static Entry Carried(Entry entry)
        {
            float offAt;
            if (tails.TryGetValue(entry.Key, out offAt)) entry.OffAt = offAt;
            return entry;
        }

        static void Find()
        {
            tails.Clear();
            float now = Time.realtimeSinceStartup;
            foreach (Entry entry in entries)
                if (entry.Valid && entry.OffAt > now) tails[entry.Key] = entry.OffAt;
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
            // One list of the reverb filters for both (each FindObjectsOfTypeAll is a new array).
            AudioReverbFilter[] reverbs = null;
            if (list.Contains(typeof(AudioReverbFilter)))
            {
                reverbs = Resources.FindObjectsOfTypeAll<AudioReverbFilter>();
                foreach (SharedReverb.Group group in SharedReverb.Find(reverbs))
                    entries.Add(Carried(new Entry { Shared = group, Tail = group.Tail }));
            }
            foreach (Type type in list)
                foreach (UnityEngine.Object found in type == typeof(AudioReverbFilter) ? reverbs : Resources.FindObjectsOfTypeAll(type))
                {
                    var filter = found as Behaviour;
                    if (filter == null || !filter.gameObject.scene.IsValid()) continue;
                    if (!SharedReverb.GameHasOn(filter)) continue;  // off in the game (Pool's water wheel and tank): stays off
                    AudioSource source = filter.GetComponent<AudioSource>();
                    // A filter without a source on its object filters the listener's whole mix: left alone.
                    if (source == null && idleOnly) continue;
                    var reverb = filter as AudioReverbFilter;
                    if (reverb != null && SharedReverb.Owns(reverb)) continue;  // its group's entry above
                    entries.Add(Carried(new Entry { Filter = filter, Source = source, Tail = reverb != null ? reverb.decayTime + 0.3f : 0.1f }));
                }
            tails.Clear();  // no filter held between finds
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
                entry.WasOn = false;
                if (entry.Shared != null)
                {
                    if (entry.Shared.Alive)
                        foreach (SharedReverb.Member member in entry.Shared.Members)
                            if (member.Source != null) score = Mathf.Min(score, Audibility(member.Source, ear));
                    entry.WasOn = entry.Shared.Routed;
                }
                else if (entry.Filter != null && entry.Source != null)
                {
                    score = Audibility(entry.Source, ear);
                    entry.WasOn = entry.Filter.enabled;
                }
                if (entry.WasOn) score *= 0.7f;
                entry.Score = score;
                entries[i] = entry;
            }
            order.Clear();
            for (int i = 0; i < entries.Count; i++) order.Add(i);
            // On a tie (2D sources all score 0) the filter that is on keeps its place: an item pickup's reverb, wanted while
            // a corridor's ambience has the one slot, plays dry rather than cut the ambience's reverb off mid-sound.
            order.Sort((a, b) =>
            {
                int byScore = entries[a].Score.CompareTo(entries[b].Score);
                return byScore != 0 ? byScore : entries[b].WasOn.CompareTo(entries[a].WasOn);
            });
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
            if (now >= nextFind && CrowEvents.Turn(nextFind <= 0f)) Find();  // the 3 s finds in a frame of their own
            silentRule = silentOn && idleOnly;
            if (adapt) Watch(now);
            if (now >= nextRank || order.Count != entries.Count)
            {
                Rank();
                if (now >= nextRank) Measure(now);
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
                    else if (keep && !enabled && on < max && room)
                    {
                        entry.Set(true);
                        switches++;
                        on++;
                        if (mixerShare >= 0f) room = false;  // one a half second: the next measure counts it
                    }
                }
            }
            onCount = on;
            Share(now);
        }

        // Unity's job workers on CPU 2 as well as CPU 1 (Threads.cs' GfxThreads, 64:12) while the mixer has CPU 2 to
        // spare: no filter on (a shared reverb counts as one; a reverb costs the mixer 18-20% of CPU 2, a shared one
        // 28, the mixer without one 19-40%) for a second, and the DSP clock never behind in this area. Back on CPU 1
        // alone (64:1, the PS Vita's default) in the frame a filter comes on. With one filter allowed (wsharemax=1):
        // Fairy Forest's steady fight on its shared reverb -0.4 ms a frame, the DSP clock down to 0.989; Corridor D's
        // 0.980-0.993 for a whole minute, over the cap's 0.97, so nothing took it back. At the same priority as the
        // mixer a worker's job delays a mix block by a few ms (the tap's longest gap 13 -> 18 ms, no late block); with
        // Pool's two reverbs on, sharing put the DSP clock at 99% of real time (adapting caps then take reverbs off)
        // and the render thread there at 96%.
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
        // Per source, its two custom curves as first read (GetCustomCurve returns a copy, which nothing changes) and what
        // their keys say: AnimationCurve.keys is a new array at each read, and Silent runs each frame for each playing
        // fully 3D source with a gated reverb.
        class Curves
        {
            public bool BlendBelow;  // the custom spatial blend curve has a key under 0.999
            public AnimationCurve Rolloff;
            public float LastHeard = float.NegativeInfinity;  // the latest time of a rolloff key above 0 (NaN times left out)
        }

        static readonly Dictionary<AudioSource, Curves> curves = new Dictionary<AudioSource, Curves>();

        static Curves CurvesOf(AudioSource source)
        {
            Curves found = new Curves { Rolloff = source.GetCustomCurve(AudioSourceCurveType.CustomRolloff) };
            AnimationCurve blend = source.GetCustomCurve(AudioSourceCurveType.SpatialBlend);
            if (blend != null)
                foreach (Keyframe key in blend.keys)
                    if (key.value < 0.999f) found.BlendBelow = true;
            if (found.Rolloff != null)
                foreach (Keyframe key in found.Rolloff.keys)
                    if (key.value > 0f && key.time > found.LastHeard) found.LastHeard = key.time;
            return found;
        }

        internal static bool Silent(AudioSource source, Vector3 ear)
        {
            if (source.volume <= 0f) return true;
            if (source.spatialBlend < 0.999f) return false;
            Curves pair;
            if (!curves.TryGetValue(source, out pair))
            {
                if (curves.Count > 2000) curves.Clear();  // a bound within an area (the unloads prune the rest)
                curves[source] = pair = CurvesOf(source);
            }
            if (pair.BlendBelow) return false;
            float distance = Vector3.Distance(source.transform.position, ear) - 0.5f;
            float max = source.maxDistance;
            if (max <= 0f) return false;
            switch (source.rolloffMode)
            {
                case AudioRolloffMode.Linear:
                    return distance >= max && max > source.minDistance;
                case AudioRolloffMode.Custom:
                    AnimationCurve rolloff = pair.Rolloff;
                    if (rolloff == null || rolloff.length == 0) return false;
                    float from = Mathf.Clamp01(distance / max);
                    for (int i = 0; i <= 8; i++)
                        if (rolloff.Evaluate(Mathf.Lerp(from, 1f, i / 8f)) > 0f) return false;
                    // A key at or past from with a value above 0 (Curves.LastHeard, read once).
                    return !(pair.LastHeard >= from);
            }
            return false;
        }

        // The mixer's budget (see Budget above): its share of a CPU since the last call, and whether a filter may come on.
        static void Measure(float now)
        {
            int uid = mixerUid;
            if (uid == 0 || !budgetOn) return;
            long run = Threads.RunMicros(uid), ticks = watch.ElapsedTicks;
            if (run < 0) return;
            if (mixerRun >= 0 && ticks > mixerTicks)
                mixerShare = (float)((run - mixerRun) * 100.0 * Stopwatch.Frequency / ((ticks - mixerTicks) * 1e6));
            mixerRun = run;
            mixerTicks = ticks;
            if (mixerShare < 0f) return;
            // The measure that finds the clips loaded still adds none: the next one has every loop playing.
            bool settling = clipsLoading;
            if (clipsLoading && now >= watchFrom) clipsLoading = ClipsLoading();
            room = now >= watchFrom && !settling && mixerShare + ReverbCost <= Budget;
            if (mixerShare <= Budget || onCount == 0 || now < watchFrom || resolve) return;
            string scene = SceneManager.GetActiveScene().name;
            max = Math.Max(0, Math.Min(max, onCount) - 1);
            learned[scene] = max;
            Debug.Log("CROW_AUDIO mixer at " + mixerShare.ToString("F0", CultureInfo.InvariantCulture) + "% of a CPU in " + scene +
                      " with " + onCount + " filters on: at most " + max + " now");
        }

        static bool ClipsLoading()
        {
            foreach (AudioClip clip in Resources.FindObjectsOfTypeAll<AudioClip>())
                if (clip.loadState == AudioDataLoadState.Loading) return true;
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
                case "rbudget":
                    budgetOn = value != "off";
                    room = true;
                    mixerShare = -1f;
                    mixerRun = -1;
                    Debug.Log("CROW_AUDIO rbudget=" + (budgetOn ? "on" : "off"));
                    return true;
                case "aplay":
                    // A test: the first AudioSource on an active GameObject of that name played as the game's code plays
                    // it (IdleAudio wakes it), for the sounds a run can't reach (the item pickup's: aplay=new item).
                    foreach (AudioSource source in Resources.FindObjectsOfTypeAll<AudioSource>())
                        if (source.name == value && source.gameObject.activeInHierarchy && source.gameObject.scene.IsValid())
                        {
                            IdleAudio.Play(source);
                            Debug.Log("CROW_AUDIO aplay " + value + ": " + (source.clip != null ? source.clip.name : "no clip") + " at " +
                                      Time.realtimeSinceStartup.ToString("F2", CultureInfo.InvariantCulture));
                            return true;
                        }
                    Debug.Log("CROW_AUDIO aplay " + value + ": no active source");
                    return true;
            }
            return false;
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
                    if (behaviour == null || !behaviour.enabled) continue;
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
            return " gated=" + entries.Count + "/" + count + " cap=" + max + " on=" + onCount + " shared=" + sharedOn + "/" + shared +
                   " mixer=" + (mixerShare >= 0f ? mixerShare.ToString("F0", CultureInfo.InvariantCulture) : "?");
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
    // rendered offline on Windows, per-source and shared differ by float rounding (-137 dB, status-log.md), 2D
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
            public Settings Key;
            public string Name;
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
        static readonly HashSet<Logged> logged = new HashSet<Logged>();  // filters that keep their own, logged once per reason
        static readonly HashSet<Settings> loggedFull = new HashSet<Settings>();  // settings told that every mixer is in use

        // A filter that keeps its own reverb, and why: the log's once-only set, a struct, so a find makes no string.
        struct Logged : IEquatable<Logged>
        {
            public int Id;
            public string Why;

            public bool Equals(Logged other)
            {
                return Id == other.Id && Why == other.Why;
            }

            public override bool Equals(object other)
            {
                return other is Logged && Equals((Logged)other);
            }

            public override int GetHashCode()
            {
                return Id * 31 + Why.GetHashCode();
            }
        }

        // What makes reverbs alike: the destination group and the 14 settings, compared as floats (0 and -0 alike, any
        // NaN alike). Until 22 Oct it was a string, the group's instance ID and each value's ToString("R"), which tells
        // the same values apart and no others (Mono writes -0 as 0, every NaN as NaN), but made 14 formatted numbers a
        // filter on every 3 s find: 13-26 KB/s of garbage on the PS Vita (sreverb A/Bs), a GC collection about a third
        // to a half sooner.
        public struct Settings : IEquatable<Settings>
        {
            int dest, hash;  // dest: the group's instance ID, 0 for none
            float dry, room, roomHF, decay, decayHF, reflections, reflectDelay, reverb, reverbDelay, diffusion, density, hfReference, roomLF, lfReference;

            internal static Settings Of(AudioReverbFilter r, AudioMixerGroup group)
            {
                var s = new Settings
                {
                    dest = group != null ? group.GetInstanceID() : 0,
                    dry = r.dryLevel, room = r.room, roomHF = r.roomHF, decay = r.decayTime, decayHF = r.decayHFRatio, reflections = r.reflectionsLevel,
                    reflectDelay = r.reflectionsDelay, reverb = r.reverbLevel, reverbDelay = r.reverbDelay, diffusion = r.diffusion, density = r.density,
                    hfReference = r.hfReference, roomLF = r.roomLF, lfReference = r.lfReference,
                };
                int h = s.dest;
                h = h * 31 + Bits(s.dry);
                h = h * 31 + Bits(s.room);
                h = h * 31 + Bits(s.roomHF);
                h = h * 31 + Bits(s.decay);
                h = h * 31 + Bits(s.decayHF);
                h = h * 31 + Bits(s.reflections);
                h = h * 31 + Bits(s.reflectDelay);
                h = h * 31 + Bits(s.reverb);
                h = h * 31 + Bits(s.reverbDelay);
                h = h * 31 + Bits(s.diffusion);
                h = h * 31 + Bits(s.density);
                h = h * 31 + Bits(s.hfReference);
                h = h * 31 + Bits(s.roomLF);
                h = h * 31 + Bits(s.lfReference);
                s.hash = h;
                return s;
            }

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
            struct FloatBits
            {
                [System.Runtime.InteropServices.FieldOffset(0)] public float Float;
                [System.Runtime.InteropServices.FieldOffset(0)] public int Int;
            }

            // The hash's bits of a value: one for 0 and -0, one for every NaN (Same's equality).
            static int Bits(float v)
            {
                if (v == 0f) return 0;
                if (float.IsNaN(v)) return 0x7FC00000;
                return new FloatBits { Float = v }.Int;
            }

            static bool Same(float a, float b)
            {
                return a == b || (float.IsNaN(a) && float.IsNaN(b));
            }

            public bool Equals(Settings o)
            {
                return hash == o.hash && dest == o.dest && Same(dry, o.dry) && Same(room, o.room) && Same(roomHF, o.roomHF) && Same(decay, o.decay) &&
                       Same(decayHF, o.decayHF) && Same(reflections, o.reflections) && Same(reflectDelay, o.reflectDelay) && Same(reverb, o.reverb) &&
                       Same(reverbDelay, o.reverbDelay) && Same(diffusion, o.diffusion) && Same(density, o.density) && Same(hfReference, o.hfReference) &&
                       Same(roomLF, o.roomLF) && Same(lfReference, o.lfReference);
            }

            public override bool Equals(object other)
            {
                return other is Settings && Equals((Settings)other);
            }

            public override int GetHashCode()
            {
                return hash;
            }
        }

        internal static bool Active { get { return on && !broken; } }

        internal static void Install(string[] args, bool gatedByFilterGate)
        {
            string mode = CrowFlags.Value(args, "-crow-shared-reverb");
            on = mode == null ? Application.platform == RuntimePlatform.PSP2 && !CrowFlags.Has(args, "-crow-audio-game") : mode != "off";
            gated = gatedByFilterGate;
            CrowEvents.Loaded("SharedReverb.sceneLoaded", (scene, loadMode) => nextFind = 0f);
            CrowEvents.Unloaded("SharedReverb.sceneUnloaded", scene => Destroyed.Forget(gameOn));
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
            if (gameOn.Count > 2000) Destroyed.Forget(gameOn);  // a bound within an area (the unloads prune the rest)
            gameOn[filter] = has = filter.enabled;
            if (!has) Debug.Log("CROW_AUDIO " + filter.GetType().Name + " " + Hierarchy.Path(filter.transform, 3) + " is off in the game: left alone");
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
            if (now < nextFind || !CrowEvents.Turn(nextFind <= 0f)) return;
            nextFind = now + 3f;
            RouteAll();
        }

        // Every group found now and on (standalone and the null test).
        internal static void RouteAll()
        {
            foreach (Group group in Find(Resources.FindObjectsOfTypeAll<AudioReverbFilter>()))
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
                filter.GetComponents(components);
                foreach (Component c in components)
                {
                    if (ReferenceEquals(c, filter))
                    {
                        after = true;
                        continue;
                    }
                    if (!after || c == null) continue;
                    if (c is AudioReverbFilter || c is AudioLowPassFilter || c is AudioHighPassFilter || c is AudioEchoFilter || c is AudioChorusFilter || c is AudioDistortionFilter)
                        why = Why(filterAfter, c.GetType(), "a ", " after it");
                    else if (c is MonoBehaviour && FiltersAudio(c.GetType()))
                        why = Why(scriptAfter, c.GetType(), "OnAudioFilterRead after it (", ")");
                    if (why != null) break;
                }
                components.Clear();
            }
            return why == null;
        }

        static readonly List<Component> components = new List<Component>();
        static readonly Dictionary<Type, string> filterAfter = new Dictionary<Type, string>(), scriptAfter = new Dictionary<Type, string>();

        // A reason naming a type, made once per type.
        static string Why(Dictionary<Type, string> made, Type type, string before, string after)
        {
            string why;
            if (!made.TryGetValue(type, out why)) made[type] = why = before + type.Name + after;
            return why;
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

        // The filters of one Settings in a find: in the order first seen, their destination, the first as the sample.
        // Reused by the next find (emptied at the end of each: no filter or mixer group held between finds), so a find
        // allocates little more than the engine's filter list.
        class Candidate
        {
            public Settings Key;
            public readonly List<Member> Members = new List<Member>();
            public AudioMixerGroup Dest;
            public AudioReverbFilter Sample;
        }

        static readonly Dictionary<Settings, Candidate> candidates = new Dictionary<Settings, Candidate>();
        static readonly List<Candidate> found = new List<Candidate>(), spare = new List<Candidate>();
        static readonly List<Group> alive = new List<Group>();
        static readonly List<AudioReverbFilter> gone = new List<AudioReverbFilter>();

        // The groups in the loaded scenes, kept, grown, made or released, from every reverb filter in memory
        // (FilterGate's find passes its list). The list is the next find's too: read it before.
        internal static List<Group> Find(AudioReverbFilter[] filters)
        {
            alive.Clear();
            Reuse();  // a find that threw
            if (!Active || !Load()) return alive;
            foreach (AudioReverbFilter filter in filters)
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
                        if (logged.Add(new Logged { Id = filter.GetInstanceID(), Why = why }))
                            Debug.Log("CROW_AUDIO shared reverb: " + Hierarchy.Path(filter.transform, 3) + " keeps its own (" + why + ")");
                        continue;
                    }
                    dest = source.outputAudioMixerGroup;
                }
                Settings key = Settings.Of(filter, dest);
                Candidate candidate;
                if (!candidates.TryGetValue(key, out candidate))
                {
                    if (spare.Count > 0)
                    {
                        candidate = spare[spare.Count - 1];
                        spare.RemoveAt(spare.Count - 1);
                    }
                    else candidate = new Candidate();
                    candidate.Key = key;
                    candidate.Dest = dest;
                    candidate.Sample = filter;
                    candidates[key] = candidate;
                    found.Add(candidate);
                }
                candidate.Members.Add(new Member { Filter = filter, Source = source });
            }
            // Groups whose key has fewer than two members left: released (their filters back), their mixer free.
            foreach (Group group in groups)
            {
                Candidate candidate;
                if (!candidates.TryGetValue(group.Key, out candidate) || candidate.Members.Count < 2) Release(group);
            }
            groups.RemoveAll(g => !g.Alive);
            foreach (KeyValuePair<AudioReverbFilter, Group> pair in owner)
                if (pair.Key == null || !pair.Value.Alive) gone.Add(pair.Key);
            foreach (AudioReverbFilter filter in gone) owner.Remove(filter);
            gone.Clear();
            foreach (Candidate candidate in found)
            {
                if (candidate.Members.Count < 2) continue;
                Group group = null;
                foreach (Group existing in groups)
                    if (existing.Key.Equals(candidate.Key))
                    {
                        group = existing;
                        break;
                    }
                if (group == null)
                {
                    int slot = FreeSlot();
                    if (slot < 0)
                    {
                        if (loggedFull.Add(candidate.Key)) Debug.Log("CROW_AUDIO shared reverb: all " + Slots + " mixers in use; " + candidate.Members.Count + " filters keep their own");
                        continue;
                    }
                    group = new Group { Key = candidate.Key, Dest = candidate.Dest, Slot = slot };
                    if (!Assign(group, candidate.Sample)) continue;
                    groups.Add(group);
                }
                // New members take the group's state; members gone drop out.
                group.Members.RemoveAll(m => m.Filter == null || m.Source == null);
                foreach (Member member in candidate.Members)
                {
                    bool known = false;
                    foreach (Member m in group.Members)
                        if (ReferenceEquals(m.Filter, member.Filter))
                        {
                            known = true;
                            break;
                        }
                    if (known) continue;
                    group.Members.Add(member);
                    owner[member.Filter] = group;
                    member.Filter.enabled = false;
                    member.Source.outputAudioMixerGroup = group.Routed ? inputs[group.Slot] : group.Dest;
                }
                alive.Add(group);
            }
            Reuse();
            return alive;
        }

        static void Reuse()
        {
            foreach (Candidate candidate in found)
            {
                candidate.Members.Clear();
                candidate.Dest = null;
                candidate.Sample = null;
                spare.Add(candidate);
            }
            found.Clear();
            candidates.Clear();
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

    }

}
