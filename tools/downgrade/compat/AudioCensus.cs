using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// What plays in an area, for sound measurements (no default; brought back on 26 Oct from the old AudioProbe.cs,
// whose other probes are gone):
//   -crow-audio-census <s>    s seconds after each area load (and at the cycle key acensus=1), CROW_ACENSUS lines:
//                             src, every playing source: clip (length, rate, channels, load type), volume, pitch,
//                             3D settings, distance from the listener, its gain there by its rolloff (dB), loop,
//                             priority, mixer group, filters; sum, the totals (loud, quiet under -40 dB, silent;
//                             filtered, streamed; by mixer group, filter type and clip rate), the voices and the
//                             listener's volume and filters; reverb, every reverb filter of the loaded scenes, on
//                             or off, with its settings and SharedReverb group; zone, the AudioReverbZones.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    static class AudioCensus
    {
        static float after = -1f, at = -1f;

        // From Audio.InstallDefaults (every run).
        internal static void Install(string[] args)
        {
            after = CrowFlags.Number(args, "-crow-audio-census", -1f);
            if (after < 0f) return;
            CrowEvents.Loaded("AudioCensus.sceneLoaded", (scene, mode) =>
            {
                if (Scenes.IsArea(scene)) at = Time.realtimeSinceStartup + after;
            });
            CrowEvents.Canvases("AudioCensus.Frame", () =>
            {
                if (at < 0f || Time.realtimeSinceStartup < at) return;
                at = -1f;
                Census("load");
            });
            Debug.Log("CROW_ACENSUS on: " + after + " s after each area load");
        }

        // Quality.Change's key (-crow-cycle, -crow-fight-cycle, -crow-ab): acensus=1, a census now.
        internal static bool Change(string key, string value)
        {
            if (key != "acensus") return false;
            Census("key");
            return true;
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

        // The filter components on an object, "reverb(1.49s)+lowpass(5000)", "(off)" after one switched off; "-" for none.
        static string Filters(GameObject go)
        {
            var text = new StringBuilder();
            foreach (Behaviour b in go.GetComponents<Behaviour>())
            {
                string name = null;
                if (b is AudioReverbFilter) name = "reverb(" + F(((AudioReverbFilter)b).decayTime) + "s)";
                else if (b is AudioLowPassFilter) name = "lowpass(" + F(((AudioLowPassFilter)b).cutoffFrequency, "F0") + ")";
                else if (b is AudioHighPassFilter) name = "highpass(" + F(((AudioHighPassFilter)b).cutoffFrequency, "F0") + ")";
                else if (b is AudioEchoFilter) name = "echo(" + F(((AudioEchoFilter)b).delay, "F0") + ")";
                else if (b is AudioChorusFilter) name = "chorus";
                else if (b is AudioDistortionFilter) name = "distortion";
                if (name == null) continue;
                if (text.Length > 0) text.Append('+');
                text.Append(name).Append(b.enabled ? "" : "(off)");
            }
            return text.Length > 0 ? text.ToString() : "-";
        }

        static void Count<T>(Dictionary<T, int> counts, T key)
        {
            int count;
            counts.TryGetValue(key, out count);
            counts[key] = count + 1;
        }

        internal static void Census(string why)
        {
            AudioListener listener = Object.FindObjectOfType<AudioListener>();
            Vector3 ear = listener != null ? listener.transform.position : Vector3.zero;
            string scene = SceneManager.GetActiveScene().name.Replace(' ', '_');
            AudioSource[] sources = Object.FindObjectsOfType<AudioSource>();
            int playing = 0, loud = 0, quiet = 0, silent = 0, filtered = 0, streamed = 0, compressedMem = 0;
            var byGroup = new Dictionary<string, int>();
            var filterCounts = new Dictionary<string, int>();
            var rates = new Dictionary<int, int>();
            foreach (AudioSource source in sources)
            {
                if (!source.isPlaying) continue;
                playing++;
                float distance = Vector3.Distance(source.transform.position, ear);
                float gain = Gain(source, distance);
                float db = gain > 0f ? 20f * Mathf.Log10(gain) : -999f;
                if (source.mute || gain <= 0f) silent++;
                else if (db < -40f) quiet++;
                else loud++;
                AudioClip clip = source.clip;
                string group = source.outputAudioMixerGroup != null ? source.outputAudioMixerGroup.name : "-";
                Count(byGroup, group);
                string filters = Filters(source.gameObject);
                if (filters != "-") filtered++;
                foreach (Behaviour b in source.GetComponents<Behaviour>())
                {
                    if (!b.enabled) continue;
                    string type = b is AudioReverbFilter ? "reverb" : b is AudioLowPassFilter ? "lowpass" : b is AudioHighPassFilter ? "highpass" :
                                  b is AudioEchoFilter ? "echo" : b is AudioChorusFilter ? "chorus" : b is AudioDistortionFilter ? "distortion" : null;
                    if (type != null) Count(filterCounts, type);
                }
                if (clip != null)
                {
                    if (clip.loadType == AudioClipLoadType.Streaming) streamed++;
                    else if (clip.loadType == AudioClipLoadType.CompressedInMemory) compressedMem++;
                    Count(rates, clip.frequency);
                }
                Vector3 p = source.transform.position;
                Debug.Log("CROW_ACENSUS src scene=" + scene + " " + Hierarchy.Path(source.transform, 3) +
                          " clip=" + (clip != null ? clip.name.Replace(' ', '_') + "/" + F(clip.length, "F1") + "s/" + clip.frequency + "Hz/" + clip.channels + "ch/" + clip.loadType : "none") +
                          " vol=" + F(source.volume) + " pitch=" + F(source.pitch) + " blend=" + F(source.spatialBlend) + " d=" + F(distance, "F1") +
                          " pos=" + F(p.x, "F1") + "," + F(p.y, "F1") + "," + F(p.z, "F1") +
                          " min=" + F(source.minDistance, "F1") + " max=" + F(source.maxDistance, "F0") + " roll=" + source.rolloffMode +
                          " gain=" + F(db, "F0") + "dB loop=" + (source.loop ? 1 : 0) + " prio=" + source.priority + " doppler=" + F(source.dopplerLevel) +
                          " spread=" + F(source.spread, "F0") + " group=" + group.Replace(' ', '_') + " zonemix=" + F(source.reverbZoneMix) +
                          (source.bypassReverbZones ? " nozones" : "") + (source.bypassEffects ? " bypassfx" : "") + (source.mute ? " muted" : "") +
                          " vmode=" + source.velocityUpdateMode + " filters=" + filters);
            }
            var line = new StringBuilder("CROW_ACENSUS sum scene=" + scene + " why=" + why + " sources=" + sources.Length + " playing=" + playing +
                                         " loud=" + loud + " quiet=" + quiet + " silent=" + silent + " filtered=" + filtered + " streamed=" + streamed +
                                         " compressedInMemory=" + compressedMem);
            line.Append(" groups=").Append(string.Join(",", byGroup.Select(g => g.Key.Replace(' ', '_') + ":" + g.Value).ToArray()));
            line.Append(" filtersOn=").Append(string.Join(",", filterCounts.Select(f => f.Key + ":" + f.Value).ToArray()));
            line.Append(" rates=").Append(string.Join(",", rates.Select(r => r.Key + ":" + r.Value).ToArray()));
            AudioConfiguration config = AudioSettings.GetConfiguration();
            line.Append(" realVoices=").Append(config.numRealVoices).Append(" virtualVoices=").Append(config.numVirtualVoices).Append(" buffer=").Append(config.dspBufferSize);
            line.Append(" ear=").Append(F(ear.x, "F1")).Append(',').Append(F(ear.y, "F1")).Append(',').Append(F(ear.z, "F1"));
            line.Append(" listenerVolume=").Append(F(AudioListener.volume)).Append(" listener=")
                .Append(listener != null ? Hierarchy.Path(listener.transform, 3) + ":" + Filters(listener.gameObject) : "none");
            Debug.Log(line.ToString());
            // Every reverb filter in the loaded scenes, on or off, with its settings (are the presets shared?).
            foreach (Object found in Resources.FindObjectsOfTypeAll(typeof(AudioReverbFilter)))
            {
                var r = found as AudioReverbFilter;
                if (r == null || !r.gameObject.scene.IsValid()) continue;
                AudioSource source = r.GetComponent<AudioSource>();
                Debug.Log("CROW_ACENSUS reverb scene=" + scene + " " + Hierarchy.Path(r.transform, 3) + " on=" + (r.enabled ? 1 : 0) + " active=" + (r.gameObject.activeInHierarchy ? 1 : 0) +
                          " playing=" + (source != null && source.isPlaying ? 1 : 0) + " clip=" + (source != null && source.clip != null ? source.clip.name.Replace(' ', '_') : "-") +
                          " d=" + F(Vector3.Distance(r.transform.position, ear), "F1") + " shared=" + SharedReverb.GroupOf(r) + " preset=" + r.reverbPreset +
                          " dry=" + F(r.dryLevel, "F0") + " room=" + F(r.room, "F0") + " roomHF=" + F(r.roomHF, "F0") + " roomLF=" + F(r.roomLF, "F0") +
                          " decay=" + F(r.decayTime) + " decayHF=" + F(r.decayHFRatio) + " refl=" + F(r.reflectionsLevel, "F0") + " reflDelay=" + F(r.reflectionsDelay, "F3") +
                          " rev=" + F(r.reverbLevel, "F0") + " revDelay=" + F(r.reverbDelay, "F3") + " hf=" + F(r.hfReference, "F0") + " lf=" + F(r.lfReference, "F0") +
                          " diffusion=" + F(r.diffusion, "F0") + " density=" + F(r.density, "F0"));
            }
            foreach (AudioReverbZone zone in Object.FindObjectsOfType<AudioReverbZone>())
                Debug.Log("CROW_ACENSUS zone scene=" + scene + " " + Hierarchy.Path(zone.transform, 3) + " on=" + (zone.enabled ? 1 : 0) + " preset=" + zone.reverbPreset +
                          " min=" + F(zone.minDistance, "F1") + " max=" + F(zone.maxDistance, "F1") + " d=" + F(Vector3.Distance(zone.transform.position, ear), "F1") +
                          " room=" + F(zone.room, "F0") + " decay=" + F(zone.decayTime));
        }
    }
}
