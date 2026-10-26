using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

// Idle AudioSources off (PS Vita default; -crow-idle-audio off keeps them, on turns it on elsewhere).
// Unity's audio update walks every enabled AudioSource each frame, playing or not: Roadside has 283
// that wait for a sound, Mansion 392. With them off, Roadside's audio update took 0.57 ms instead of
// 0.90 and the main thread 23.1 ms instead of 23.4 (PS Vita, standing). IdleAudio disables a source
// once it has been idle (not playing, at the start of its clip) for 2 s, and enables it again before
// the game plays it: the rewriter (rewriter/IdleAudio.cs) sends every AudioSource.Play and PlayOneShot
// call in the game's code here first (Wake; it has no PlayDelayed, PlayScheduled or UnPause), and every
// Behaviour.enabled read and write too, so the game still reads true for a source IdleAudio
// disabled, and a source the game itself disables or enables is the game's again.
// Left alone: sources that play on enable (playOnAwake: enabling one starts it), sources the game's
// code never reaches (the probe's mixer tap; CrowCompat's VideoPlayer wakes its own), and every
// source while AudioListener.pause is on (a paused source's isPlaying is false).
// -crow-idle-audio-log logs each pass (CROW_IDLE_AUDIO), with the sources woken that play.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class IdleAudio
    {
        const float IdleSeconds = 2f;
        const int PerFrame = 16;  // sources looked at a frame (isPlaying is a native call)

        static bool on, log;
        static readonly List<AudioSource> watched = new List<AudioSource>();
        static readonly List<float> idleSince = new List<float>();
        static readonly Dictionary<AudioSource, int> index = new Dictionary<AudioSource, int>();
        static readonly HashSet<AudioSource> asleep = new HashSet<AudioSource>();
        // Per watched source (by index): whether it is in asleep, so Frame passes over a sleeping one with an array
        // read. Until 22 Oct each visit asked the engine for its enabled state and the set (most of Station Square's
        // and Roadside's watched sources sleep: 0.38% of a fight's main thread, the most of any willRenderCanvases
        // handler); the same sources are visited on the same frames.
        static readonly List<bool> sleeping = new List<bool>();
        static float rescanAt;
        static int next, disabled, woken, heard;
        static readonly List<AudioSource> justWoken = new List<AudioSource>();

        internal static void Install(string[] args)
        {
            on = CrowFlags.On(args, "-crow-idle-audio");
            if (!on) return;
            log = CrowFlags.Has(args, "-crow-idle-audio-log");
            CrowEvents.Loaded("IdleAudio.sceneLoaded", (scene, loadMode) => rescanAt = Time.realtimeSinceStartup + 2f);
            CrowEvents.Canvases("IdleAudio.Frame", Frame);
            Debug.Log("CROW_IDLE_AUDIO on: idle sources off after " + IdleSeconds + " s");
        }

        // Before the game plays a source: enabled again if IdleAudio disabled it.
        public static void Wake(AudioSource source)
        {
            if (!on || ReferenceEquals(source, null) || !asleep.Remove(source)) return;
            source.enabled = true;
            int at;
            if (index.TryGetValue(source, out at))
            {
                idleSince[at] = Time.realtimeSinceStartup;
                sleeping[at] = false;
            }
            woken++;
            if (log) justWoken.Add(source);
        }

        // The rewriter's targets: AudioSource's play calls with the source first.
        public static void Play(AudioSource source)
        {
            Wake(source);
            source.Play();
        }

        public static void Play(AudioSource source, ulong delay)
        {
            Wake(source);
#pragma warning disable 618
            source.Play(delay);
#pragma warning restore 618
        }

        public static void PlayOneShot(AudioSource source, AudioClip clip)
        {
            Wake(source);
            Hold(source, clip);
            source.PlayOneShot(clip);
        }

        public static void PlayOneShot(AudioSource source, AudioClip clip, float volumeScale)
        {
            Wake(source);
            Hold(source, clip);
            source.PlayOneShot(clip, volumeScale);
        }

        // CrowCompat's VideoPlayer (Video.cs) resuming its sound; the game has no UnPause call.
        internal static void UnPause(AudioSource source)
        {
            Wake(source);
            source.UnPause();
        }

        // A one-shot's sound may not show in isPlaying: the source counts as busy for the clip's length.
        static void Hold(AudioSource source, AudioClip clip)
        {
            int at;
            if (!on || ReferenceEquals(source, null) || clip == null || !index.TryGetValue(source, out at)) return;
            float pitch = Mathf.Abs(source.pitch);
            float until = Time.realtimeSinceStartup + clip.length / (pitch > 0.01f ? pitch : 0.01f);
            if (until > idleSince[at]) idleSince[at] = until;
        }

        // Behaviour.enabled as the game sees it: a source IdleAudio disabled reads as enabled, and a
        // write makes the source the game's again.
        public static bool GetEnabled(Behaviour behaviour)
        {
            if (on && asleep.Count > 0)
            {
                var source = behaviour as AudioSource;
                if (!ReferenceEquals(source, null) && asleep.Contains(source)) return true;
            }
            return behaviour.enabled;
        }

        public static void SetEnabled(Behaviour behaviour, bool value)
        {
            if (on && asleep.Count > 0)
            {
                var source = behaviour as AudioSource;
                if (!ReferenceEquals(source, null) && asleep.Remove(source))
                {
                    int at;
                    if (index.TryGetValue(source, out at))
                    {
                        idleSince[at] = Time.realtimeSinceStartup;
                        sleeping[at] = false;
                    }
                }
            }
            behaviour.enabled = value;
        }

        // The sources to watch: enabled ones in the loaded scenes that the game plays by calls.
        static void Rescan(float now)
        {
            watched.Clear();
            idleSince.Clear();
            sleeping.Clear();
            index.Clear();
            asleep.RemoveWhere(s => s == null);
            foreach (AudioSource source in Resources.FindObjectsOfTypeAll<AudioSource>())
            {
                if (source == null || !source.gameObject.scene.IsValid() || source.playOnAwake) continue;
                if (!source.enabled && !asleep.Contains(source)) continue;
                if (source.GetComponent<Diag>() != null) continue;  // the probe's mixer tap
                index[source] = watched.Count;
                watched.Add(source);
                idleSince.Add(now);
                sleeping.Add(asleep.Contains(source));
            }
            next = 0;
            if (log) Debug.Log("CROW_IDLE_AUDIO watching " + watched.Count + " sources, " + asleep.Count + " off; " + disabled + " turned off, " + woken + " woken so far (" + heard + " playing the frame after)");
        }

        // Canvas.willRenderCanvases: once a frame, a few sources at a time.
        static void Frame()
        {
            float now = Time.realtimeSinceStartup;
            // With the log: the sources woken since the last frame that play now (a one-shot may not show).
            for (int i = 0; i < justWoken.Count; i++)
                if (justWoken[i] != null && justWoken[i].isPlaying) heard++;
            justWoken.Clear();
            if (rescanAt > 0f && now >= rescanAt && CrowEvents.Turn())
            {
                rescanAt = now + 60f;  // and every 60 s, for sources the game makes or enables later
                Rescan(now);
            }
            if (watched.Count == 0 || AudioListener.pause) return;
            for (int n = 0; n < PerFrame && n < watched.Count; n++)
            {
                if (next >= watched.Count) next = 0;
                int i = next++;
                if (sleeping[i]) continue;  // asleep.Contains(watched[i])
                AudioSource source = watched[i];
                if (source == null || !source.enabled) continue;
                if (source.isPlaying || source.timeSamples != 0 || !source.gameObject.activeInHierarchy)
                {
                    idleSince[i] = now;
                    continue;
                }
                if (now - idleSince[i] < IdleSeconds) continue;
                source.enabled = false;
                asleep.Add(source);
                sleeping[i] = true;
                disabled++;
            }
        }
    }
}
