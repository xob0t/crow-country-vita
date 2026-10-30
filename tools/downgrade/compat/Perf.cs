using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// Settings tried in turn in one run (-crow-cycle), the physics settings, the particle flags, and the installer of the
// PS Vita defaults listed last (Perf.Install), whose flags their own files describe:
//   -crow-particles-report <s>  (Particles.cs) every s seconds (CROW_PARTICLES): the particle systems in loaded scenes,
//                               playing, alive and visible, their particles, and the busiest of them with
//                               the settings that cost (simulation space, max particles, collision, noise,
//                               sub-emitters, trails, lights, prewarm, automatic culling)
//   -crow-particles game|off    (Particles.cs) off stops and clears every particle system, every second (to measure
//                               what they cost); game leaves them
//   -crow-particle-noise game|low|medium
//                               the noise module's quality on every particle system of the loaded scenes
//                               that has it (Particles.cs' ParticleNoise; low, the PS Vita's default: Unity's 1D noise
//                               instead of the game's 3D; game keeps it); -crow-cycle takes pnoise=
//   -crow-physics <changes>     physics settings: iter=<n> (default solver iterations), sync=0|1
//                               (Physics.autoSyncTransforms), comma-separated; the step and the longest
//                               frame are FixedStep's fixeddt= and physcap=
//   -crow-cycle <s> <sets>      after the first area loads (Scenes.IsArea: not Logos, Title, CrowMara),
//                               every s seconds apply the next change set of <sets> (sets separated by
//                               '/', changes in a set by ','; Quality.Change's keys and these: particles=,
//                               iter=, sync=, gpu=<MHz>, vsync=<n>, camera=624|550,
//                               merge=on|off|merged|report (Merge.cs, in the loaded scenes),
//                               rewired=pad|game (RewiredPad), split=ui (PlayerSplit, from then on),
//                               time=<scale>, 0 to hold a moment for screenshots), with a CROW_LOOP
//                               line (-crow-loop-profile) for each set's window, labelled with the set.
//                               E.g. -crow-cycle 10 gpu=222/gpu=166/resolution=0/resolution=1 with
//                               -crow-loop-profile 1000, so that only the sets' windows report.
//   RewiredPad (Pad.cs: -crow-rewired, rewired=), MapPan (MapPan.cs: -crow-map-pan, -crow-map-arrows, mappan=,
//   maparrows=), PlayerSplit, SceneSplit and SplitFollow (SceneSplit.cs: -crow-player-split, -crow-scene-split,
//   -crow-split-follow, split=, scenesplit=, splitfollow=), ScaledColliders (ScaledColliders.cs:
//   -crow-scaled-colliders, scaled=)
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    static class Perf
    {
        public static void Install(string[] args)
        {
            // The PS Vita defaults each under a Guard of their own (Diag.Guard; see Quality.Install).
            Diag.Guard("RewiredPad", () => RewiredPad.Install(args));
            Diag.Guard("MapPan", () => MapPan.Install(args));
            Diag.Guard("PlayerSplit", () => PlayerSplit.Install(args));
            Diag.Guard("ScaledColliders", () => ScaledColliders.Install(args));
            Diag.Guard("SceneSplit", () => SceneSplit.Install(args));
            float particles = CrowFlags.Number(args, "-crow-particles-report", -1f);
            if (particles > 0f) ParticleReport.Install(particles);
            Diag.Guard("ParticleNoise", () => ParticleNoise.Install(args));
            string rule = CrowFlags.Value(args, "-crow-particles");
            if (rule != null) Change("particles", rule);
            string changes = CrowFlags.Value(args, "-crow-physics");
            if (changes != null)
                foreach (string change in changes.Split(','))
                {
                    string[] pair = change.Split('=');
                    if (pair.Length == 2 && !Change(pair[0], pair[1])) Debug.Log("CROW_PERF -crow-physics: no change " + change);
                }
            string sets = CrowFlags.Word(args, "-crow-cycle", 2);
            if (sets != null) Cycle.Install(CrowFlags.Number(args, "-crow-cycle", 10f), sets);
        }

        // A change Quality.Change doesn't know; false when it isn't one of these either.
        internal static bool Change(string key, string value)
        {
            float number;
            bool numeric = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            switch (key)
            {
                case "particles":
                    ParticleRule.Set(value);
                    return true;
                case "hide":
                    Draws.Hide(value);
                    return true;
                case "pnoise":
                    ParticleNoise.Set(value);
                    return true;
                case "merge":
                    Merge.Set(value);
                    return true;
                case "iter":
                    if (numeric && number >= 1f) Physics.defaultSolverIterations = (int)number;
                    break;
                case "sync":
                    Physics.autoSyncTransforms = value == "1";
                    break;
                case "gpu":
                    if (numeric) Clock.SetGpu((int)number);
                    break;
                case "time":
                    if (numeric && number >= 0f) Time.timeScale = number;
                    break;
                case "camera":
                    if (numeric) CamEffect.SetCameraWidth((int)number);
                    return true;
                case "vsync":
                    if (numeric) QualitySettings.vSyncCount = (int)number;
                    break;
                case "mappan":
                    MapPan.Set(value == "on");
                    return true;
                case "maparrows":
                    MapPan.SetArrows(value == "canvas");
                    return true;
                case "rewired":
                    RewiredPad.Set(value == "pad");
                    return true;
                case "split":
                    PlayerSplit.Set(value);
                    return true;
                case "scaled":
                    ScaledColliders.Set(value);
                    return true;
                case "scenesplit":
                    SceneSplit.Set(value);
                    return true;
                case "splitfollow":
                    SplitFollow.Set(value);
                    return true;
                case "splittoggle":
                    SceneSplit.Toggle(value);
                    return true;
                default:
                    return false;
            }
            Debug.Log("CROW_PERF " + key + "=" + value + " (fixed=" + Time.fixedDeltaTime.ToString("R", CultureInfo.InvariantCulture) +
                      " maxdt=" + Time.maximumDeltaTime.ToString("R", CultureInfo.InvariantCulture) + " iter=" + Physics.defaultSolverIterations +
                      " sync=" + (Physics.autoSyncTransforms ? 1 : 0) + " vsync=" + QualitySettings.vSyncCount + " clocks=" + (Clock.Describe() ?? "?") + ")");
            return true;
        }
    }

    // -crow-cycle: one run measures several settings in turn.
    static class Cycle
    {
        static float every, start = -1f;
        static string[] sets;
        static int step = -1;

        public static void Install(float seconds, string list)
        {
            every = Mathf.Max(2f, seconds);
            sets = list.Split('/');
            CrowEvents.Loaded("Cycle.sceneLoaded", (scene, mode) =>
            {
                if (start >= 0f || !Scenes.IsArea(scene)) return;
                start = Time.realtimeSinceStartup + every;
                Debug.Log("CROW_CYCLE " + sets.Length + " sets every " + every + " s from " + scene.name);
            });
            CrowEvents.BeforeRender("Cycle.Tick", Tick);
        }

        static void Tick()
        {
            if (start < 0f || step >= sets.Length) return;
            float now = Time.realtimeSinceStartup;
            // The first window (before any set) settles the load; then each set gets a window.
            if (now < start + every * (step + 1)) return;
            if (step < 0) LoopProfile.Flush("settle");
            else LoopProfile.Flush(sets[step].Replace(',', '+'));
            step++;
            if (step < sets.Length)
            {
                Debug.Log("CROW_CYCLE set " + (step + 1) + "/" + sets.Length + ": " + sets[step]);
                Quality.Change(sets[step]);
            }
            else Debug.Log("CROW_CYCLE done");
        }
    }
}
