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

// Sound on the PS Vita, the measurements that change no default (Audio.cs has the defaults, the -crow-audio
// probe and Threads; these moved from there as they were, 21 Oct):
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
//   -crow-reverb-null <dir> [s]  Windows players: the area's mix rendered offline with a reverb per source and
//                             shared, into WAVs for a null test (ReverbNull).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    // -crow-reverb-null <dir> [s] (Windows players): SharedReverb's null test. s seconds (default 10) after the first
    // area loads, the sources playing then are rendered offline (AudioRenderer, which sends the device silence while
    // it records) with each reverb filter as the game has it (per) and with SharedReverb's groups (shared), passes
    // per (a first pass: a clip's first play starts a sample apart), per, per, shared, shared, per, per
    // (-crow-reverb-null-passes; dry: every filter off, a control), into float WAVs <dir>/<scene>-<n>-<mode>.wav.
    // Each pass: the sources stopped, the mode set, 6 s rendered and dropped (the tails out),
    // every source played from its start in the same mix block, 12 s rendered in one frame. Streamed clips stay stopped (a stream's start
    // isn't sample-exact) and the game's clock is held (Time.timeScale 0), so nothing else starts; CROW_RNULL lines.
    // -crow-mute holds the listener's volume at 0, and the renderer records after it: the volume is 1 only inside
    // the frame that renders a pass, while the renderer has the device. Compare passes of the same parity (2 with 4,
    // 3 with 5; per against per: 2 with 6, 3 with 7): the start of a pass alternates between two states a few samples
    // apart, whatever the mode; past the first seconds (work/scratch/sharedreverb/compare.py).
    // -crow-reverb-null-quit quits after the test.
    public static class ReverbNull
    {
        static string dir;
        static float delay, startAt = -1f;
        static IEnumerator<bool> run;
        static bool done;
        static float volume;  // the listener's volume before a pass's frame (0 under -crow-mute)

        internal static void Install(string[] args)
        {
            dir = CrowFlags.Optional(args, "-crow-reverb-null");
            if (dir == null) return;
            if (Application.platform != RuntimePlatform.WindowsPlayer)
            {
                Debug.Log("CROW_RNULL Windows players only");
                return;
            }
            delay = 10f;
            int index = Array.IndexOf(args, "-crow-reverb-null");
            float seconds;
            if (index + 2 < args.Length && float.TryParse(args[index + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) delay = seconds;
            CrowEvents.Loaded("ReverbNull.sceneLoaded", (scene, mode) =>
            {
                if (startAt < 0f && !done && Scenes.IsArea(scene)) startAt = Time.realtimeSinceStartup + delay;
            });
            CrowEvents.Canvases("ReverbNull.Frame", Frame);
            Debug.Log("CROW_RNULL to " + dir + " " + delay + " s after the first area");
        }

        static void Frame()
        {
            if (run == null)
            {
                if (startAt < 0f || done || Time.realtimeSinceStartup < startAt) return;
                done = true;
                run = Run();
            }
            bool more;
            try
            {
                more = run.MoveNext();
            }
            catch (Exception e)
            {
                Debug.Log("CROW_RNULL failed: " + e);
                more = false;
                AudioListener.volume = volume;
            }
            if (!more) run = null;
        }

        static int Channels()
        {
            switch (AudioSettings.speakerMode)
            {
                case AudioSpeakerMode.Mono: return 1;
                case AudioSpeakerMode.Quad: return 4;
                case AudioSpeakerMode.Surround: return 5;
                case AudioSpeakerMode.Mode5point1: return 6;
                case AudioSpeakerMode.Mode7point1: return 8;
                default: return 2;
            }
        }

        // frames sample frames rendered (a block of 1024 at a time), into the list or dropped.
        static void Render(int frames, int channels, List<float> into)
        {
            const int block = 1024;
            var buffer = new Unity.Collections.NativeArray<float>(block * channels, Unity.Collections.Allocator.Temp);
            try
            {
                for (int got = 0; got < frames; got += block)
                {
                    AudioRenderer.Render(buffer);
                    if (into != null)
                        for (int i = 0; i < buffer.Length; i++) into.Add(buffer[i]);
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        static IEnumerator<bool> Run()
        {
            string scene = SceneManager.GetActiveScene().name.Replace(' ', '_');
            string[] passes = (CrowFlags.Optional(ProbeFiles.Args, "-crow-reverb-null-passes") ?? "per,per,per,shared,shared,per,per").Split(',');
            float seconds = CrowFlags.Number(ProbeFiles.Args, "-crow-reverb-null-seconds", 12f);
            int rate = AudioSettings.outputSampleRate, channels = Channels();
            var sources = new List<AudioSource>();
            var streams = new List<AudioSource>();
            foreach (AudioSource source in UnityEngine.Object.FindObjectsOfType<AudioSource>())
            {
                if (!source.isPlaying || source.clip == null) continue;
                if (source.clip.loadType == AudioClipLoadType.Streaming) streams.Add(source);
                else sources.Add(source);
            }
            foreach (AudioSource source in sources)
            {
                var reverb = source.GetComponent<AudioReverbFilter>();
                Debug.Log("CROW_RNULL source " + SharedReverb.Path(source.transform) + " clip=" + source.clip.name.Replace(' ', '_') + " vol=" + source.volume.ToString("F3", CultureInfo.InvariantCulture) +
                          " blend=" + source.spatialBlend.ToString("F2", CultureInfo.InvariantCulture) + " group=" + (source.outputAudioMixerGroup != null ? source.outputAudioMixerGroup.name.Replace(' ', '_') : "-") +
                          " reverb=" + (reverb == null ? "-" : SharedReverb.GroupOf(reverb)));
            }
            foreach (AudioSource source in streams)
            {
                Debug.Log("CROW_RNULL stream left out " + SharedReverb.Path(source.transform) + " clip=" + source.clip.name.Replace(' ', '_'));
                source.Stop();
            }
            if (!AudioRenderer.Start())
            {
                Debug.Log("CROW_RNULL AudioRenderer.Start failed");
                yield break;
            }
            float scale = Time.timeScale;
            bool sharedBefore = SharedReverb.Active;
            // The sources' reverb filters on as the game has them (per), for the dry passes' switch.
            var filters = sources.Select(s => s.GetComponent<AudioReverbFilter>()).Where(r => r != null).ToList();
            Time.timeScale = 0f;
            SharedReverb.Hold(true);
            System.IO.Directory.CreateDirectory(dir);
            var rec = new List<float>((int)(seconds * rate * channels) + 4096);
            for (int n = 0; n < passes.Length; n++)
            {
                string mode = passes[n];
                foreach (AudioSource source in sources) source.Stop();
                if (mode == "shared")
                {
                    SharedReverb.Set(true);
                    SharedReverb.Find();  // the groups' mixers given their parameters before the flush
                }
                else
                {
                    SharedReverb.Set(false);
                    // dry: no reverb at all, a control (how much of the mix the reverbs are)
                    foreach (AudioReverbFilter filter in filters) filter.enabled = mode != "dry";
                }
                yield return true;
                Render(rate * 6, channels, null);
                if (mode == "shared") SharedReverb.RouteAll();
                foreach (AudioSource source in sources)
                {
                    IdleAudio.Wake(source);
                    source.timeSamples = 0;
                    source.Play();
                }
                yield return true;
                // Within this frame only: the volume at 1 while the renderer records (the device gets silence).
                rec.Clear();
                volume = AudioListener.volume;
                AudioListener.volume = 1f;
                Render(1024, channels, null);  // the volume's ramp
                Render((int)(seconds * rate), channels, rec);
                AudioListener.volume = volume;
                int extra = UnityEngine.Object.FindObjectsOfType<AudioSource>().Count(s => s.isPlaying && !sources.Contains(s));
                string path = System.IO.Path.Combine(dir, scene + "-" + (n + 1) + "-" + mode + ".wav");
                WriteFloatWav(path, rec, channels, rate);
                Debug.Log("CROW_RNULL pass " + (n + 1) + " " + mode + " sources=" + sources.Count + " extra=" + extra + " shared=" + (SharedReverb.Active ? "on" : "off") +
                          " peak=" + rec.Select(v => Math.Abs(v)).DefaultIfEmpty(0f).Max().ToString("F4", CultureInfo.InvariantCulture) + " " + path);
            }
            AudioRenderer.Stop();
            foreach (AudioReverbFilter filter in filters) filter.enabled = true;
            Time.timeScale = scale;
            SharedReverb.Set(sharedBefore);
            SharedReverb.Hold(false);
            foreach (AudioSource source in sources.Concat(streams)) source.Play();
            Debug.Log("CROW_RNULL done " + scene);
            if (ProbeFiles.Args.Contains("-crow-reverb-null-quit")) Application.Quit();
        }

        static void WriteFloatWav(string path, List<float> samples, int channels, int rate)
        {
            using (var w = new System.IO.BinaryWriter(System.IO.File.Create(path)))
            {
                int bytes = samples.Count * 4;
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + bytes);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);
                w.Write((short)3);
                w.Write((short)channels);
                w.Write(rate);
                w.Write(rate * channels * 4);
                w.Write((short)(channels * 4));
                w.Write((short)32);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(bytes);
                foreach (float f in samples) w.Write(f);
            }
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
            CrowEvents.Loaded("MixProbe.sceneLoaded", (scene, mode) =>
            {
                nextCheck = 0f;
                if (censusAfter >= 0f) censusAt = Time.realtimeSinceStartup + censusAfter;
            });
            Debug.Log("CROW_MIX probe:" + (mute ? " listener volume held at 0" : "") + (recordDir != null ? " recordings to " + recordDir : "") +
                      (censusAfter >= 0f ? " census " + censusAfter + " s after each load" : ""));
            CrowEvents.Canvases("MixProbe.Frame", Frame);
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
                          " d=" + F(Vector3.Distance(r.transform.position, at), "F1") + " shared=" + SharedReverb.GroupOf(r) + " preset=" + r.reverbPreset + " dry=" + F(r.dryLevel, "F0") + " room=" + F(r.room, "F0") +
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
            CrowEvents.Loaded("Doppler.sceneLoaded", (scene, mode) => placeAt = Time.realtimeSinceStartup + 14f);
            CrowEvents.Canvases("Doppler.Frame", Frame);
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
