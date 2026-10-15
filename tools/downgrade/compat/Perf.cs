using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// Frame time in play: particle systems, physics and settings tried in turn in one run.
//   -crow-particles-report <s>  every s seconds (CROW_PARTICLES): the particle systems in loaded scenes,
//                               playing, alive and visible, their particles, and the busiest of them with
//                               the settings that cost (simulation space, max particles, collision, noise,
//                               sub-emitters, trails, lights, prewarm, automatic culling)
//   -crow-particles game|off    off stops and clears every particle system, every second (to measure
//                               what they cost); game leaves them
//   -crow-physics-report <s>    every s seconds (CROW_PHYSICS): the fixed time step, the physics steps per
//                               frame, rigidbodies (awake, kinematic) and colliders by kind
//   -crow-physics <changes>     physics settings: fixed=<s> (Time.fixedDeltaTime), maxdt=<s>
//                               (Time.maximumDeltaTime), iter=<n> (default solver iterations), sync=0|1
//                               (Physics.autoSyncTransforms), comma-separated
//   -crow-cycle <s> <sets>      after the first area loads (any scene but Logos, Title and CrowMara),
//                               every s seconds apply the next change set of <sets> (sets separated by
//                               '/', changes in a set by ','; Quality.Change's keys and these: particles=,
//                               fixed=, maxdt=, iter=, sync=, gpu=<MHz>, vsync=<n>, camera=624|550|416,
//                               batch=all (Draws.cs's static batching, now),
//                               time=<scale>, 0 to hold a moment for screenshots), with a CROW_LOOP
//                               line (-crow-loop-profile) for each set's window, labelled with the set.
//                               E.g. -crow-cycle 10 gpu=222/gpu=166/resolution=0/resolution=1 with
//                               -crow-loop-profile 1000, so that only the sets' windows report.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    static class Perf
    {
        public static void Install(string[] args)
        {
            float particles = Quality.Number(args, "-crow-particles-report", -1f);
            if (particles > 0f) ParticleReport.Install(particles);
            string rule = Value(args, "-crow-particles");
            if (rule != null) Change("particles", rule);
            float physics = Quality.Number(args, "-crow-physics-report", -1f);
            if (physics > 0f) PhysicsReport.Install(physics);
            string changes = Value(args, "-crow-physics");
            if (changes != null)
                foreach (string change in changes.Split(','))
                {
                    string[] pair = change.Split('=');
                    if (pair.Length == 2) Change(pair[0], pair[1]);
                }
            int cycle = Array.IndexOf(args, "-crow-cycle");
            if (cycle >= 0 && cycle + 2 < args.Length) Cycle.Install(Quality.Number(args, "-crow-cycle", 10f), args[cycle + 2]);
        }

        static string Value(string[] args, string flag)
        {
            int index = Array.IndexOf(args, flag);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
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
                case "batch":
                    StaticBatch.Set(value, true);
                    return true;
                case "fixed":
                    if (numeric && number > 0f) Time.fixedDeltaTime = number;
                    break;
                case "maxdt":
                    if (numeric && number > 0f) Time.maximumDeltaTime = number;
                    break;
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
                default:
                    return false;
            }
            Debug.Log("CROW_PERF " + key + "=" + value + " (fixed=" + Time.fixedDeltaTime.ToString("R", CultureInfo.InvariantCulture) +
                      " maxdt=" + Time.maximumDeltaTime.ToString("R", CultureInfo.InvariantCulture) + " iter=" + Physics.defaultSolverIterations +
                      " sync=" + (Physics.autoSyncTransforms ? 1 : 0) + " vsync=" + QualitySettings.vSyncCount + " clocks=" + (Clock.Describe() ?? "?") + ")");
            return true;
        }

        internal static string Path(Transform transform, int depth)
        {
            string name = transform.name;
            for (Transform parent = transform.parent; parent != null && depth > 1; parent = parent.parent, depth--) name = parent.name + "/" + name;
            return name.Replace(' ', '_');
        }
    }

    static class ParticleRule
    {
        static string rule = "game";
        static bool hooked;
        static float last = -10f;
        static readonly HashSet<ParticleSystem> stopped = new HashSet<ParticleSystem>();

        public static void Set(string value)
        {
            rule = value;
            last = -10f;
            if (rule == "game")
            {
                int resumed = 0;
                foreach (ParticleSystem system in stopped)
                    if (system != null)
                    {
                        system.Play(true);
                        resumed++;
                    }
                stopped.Clear();
                Debug.Log("CROW_PARTICLES rule game: " + resumed + " systems playing again");
                return;
            }
            if (!hooked)
            {
                hooked = true;
                SceneManager.sceneLoaded += (scene, mode) => last = -10f;
                Application.onBeforeRender += Tick;
            }
            Tick();
        }

        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (rule != "off" || now - last < 1f) return;
            last = now;
            int count = 0;
            foreach (ParticleSystem system in UnityEngine.Object.FindObjectsOfType<ParticleSystem>())
            {
                if (!system.isPlaying && system.particleCount == 0) continue;
                // Root systems only: Stop(true) takes the children with it.
                if (system.transform.parent != null && system.transform.parent.GetComponentInParent<ParticleSystem>() != null) continue;
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                stopped.Add(system);
                count++;
            }
            if (count > 0) Debug.Log("CROW_PARTICLES rule off: stopped " + count);
        }
    }

    static class ParticleReport
    {
        static float every, lastReport;

        public static void Install(float seconds)
        {
            every = seconds;
            Application.onBeforeRender += Tick;
        }

        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastReport < every) return;
            lastReport = now;
            try
            {
                Report();
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_PARTICLES failed: " + exception);
            }
        }

        static void Report()
        {
            ParticleSystem[] systems = UnityEngine.Object.FindObjectsOfType<ParticleSystem>();
            int playing = 0, alive = 0, visible = 0, particles = 0, world = 0, collision = 0, noise = 0, culled = 0;
            foreach (ParticleSystem system in systems)
            {
                if (system.isPlaying) playing++;
                int count = system.particleCount;
                if (count > 0) alive++;
                particles += count;
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                if (renderer != null && renderer.enabled && renderer.isVisible) visible++;
                if (system.main.simulationSpace != ParticleSystemSimulationSpace.Local) world++;
                if (system.collision.enabled) collision++;
                if (system.noise.enabled) noise++;
                if (system.automaticCullingEnabled) culled++;
            }
            var line = new StringBuilder("CROW_PARTICLES scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            line.Append(" systems=").Append(systems.Length).Append(" playing=").Append(playing).Append(" alive=").Append(alive);
            line.Append(" visible=").Append(visible).Append(" particles=").Append(particles).Append(" worldSpace=").Append(world);
            line.Append(" collision=").Append(collision).Append(" noise=").Append(noise).Append(" autoCulled=").Append(culled);
            Debug.Log(line.ToString());
            foreach (ParticleSystem system in systems.Where(s => s.isPlaying || s.particleCount > 0 || s.collision.enabled)
                                                     .OrderByDescending(s => s.particleCount).Take(12))
            {
                ParticleSystem.MainModule main = system.main;
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                var text = new StringBuilder("CROW_PARTICLE n=").Append(system.particleCount).Append('/').Append(main.maxParticles);
                text.Append(" rate=").Append(system.emission.enabled ? system.emission.rateOverTimeMultiplier.ToString("F0", CultureInfo.InvariantCulture) : "off");
                text.Append(" life=").Append(main.startLifetimeMultiplier.ToString("F2", CultureInfo.InvariantCulture));
                text.Append(" space=").Append(main.simulationSpace).Append(" loop=").Append(main.loop ? 1 : 0).Append(" prewarm=").Append(main.prewarm ? 1 : 0);
                text.Append(" playing=").Append(system.isPlaying ? 1 : 0).Append(" autoCull=").Append(system.automaticCullingEnabled ? 1 : 0);
                if (system.collision.enabled)
                {
                    ParticleSystem.CollisionModule c = system.collision;
                    text.Append(" collision=").Append(c.type).Append('/').Append(c.quality).Append('/').Append(c.mode).Append(" collidesWith=").Append(c.collidesWith.value.ToString("X"));
                    text.Append(" maxShapes=").Append(c.maxCollisionShapes).Append(" messages=").Append(c.sendCollisionMessages ? 1 : 0);
                }
                if (system.noise.enabled) text.Append(" noise=").Append(system.noise.quality);
                if (system.subEmitters.enabled) text.Append(" sub=").Append(system.subEmitters.subEmittersCount);
                if (system.trails.enabled) text.Append(" trails=1");
                if (system.lights.enabled) text.Append(" lights=1");
                if (system.trigger.enabled) text.Append(" trigger=1");
                if (renderer != null)
                {
                    text.Append(" render=").Append(renderer.renderMode).Append(" visible=").Append(renderer.enabled && renderer.isVisible ? 1 : 0);
                    Material material = renderer.sharedMaterial;
                    if (material != null && material.shader != null) text.Append(" shader=").Append(material.shader.name.Replace(' ', '_'));
                }
                text.Append(" path=").Append(Perf.Path(system.transform, 4));
                Debug.Log(text.ToString());
            }
        }
    }

    static class PhysicsReport
    {
        static float every, lastReport, lastFixedTime;
        static int frames;

        public static void Install(float seconds)
        {
            every = seconds;
            Application.onBeforeRender += Tick;
        }

        static void Tick()
        {
            frames++;
            float now = Time.realtimeSinceStartup;
            if (now - lastReport < every) return;
            lastReport = now;
            try
            {
                Report();
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_PHYSICS failed: " + exception);
            }
        }

        static void Report()
        {
            float fixedTime = Time.fixedTime;
            double steps = lastFixedTime > 0f && frames > 0 ? (fixedTime - lastFixedTime) / Time.fixedDeltaTime / frames : 0;
            lastFixedTime = fixedTime;
            frames = 0;
            int bodies = 0, awake = 0, kinematic = 0;
            foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
            {
                bodies++;
                if (body.isKinematic) kinematic++;
                else if (!body.IsSleeping()) awake++;
            }
            var kinds = new Dictionary<string, int>();
            int triggers = 0, colliders = 0, onBodies = 0;
            foreach (Collider collider in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                if (!collider.enabled) continue;
                colliders++;
                if (collider.isTrigger) triggers++;
                if (collider.attachedRigidbody != null) onBodies++;
                string kind = collider.GetType().Name.Replace("Collider", "");
                var mesh = collider as MeshCollider;
                if (mesh != null) kind = mesh.convex ? "MeshConvex" : "Mesh";
                int count;
                kinds.TryGetValue(kind, out count);
                kinds[kind] = count + 1;
            }
            var line = new StringBuilder("CROW_PHYSICS scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            line.Append(" fixed=").Append(Time.fixedDeltaTime.ToString("R", CultureInfo.InvariantCulture));
            line.Append(" maxdt=").Append(Time.maximumDeltaTime.ToString("R", CultureInfo.InvariantCulture));
            line.Append(" stepsPerFrame=").Append(steps.ToString("F2", CultureInfo.InvariantCulture));
            line.Append(" iter=").Append(Physics.defaultSolverIterations).Append(" sync=").Append(Physics.autoSyncTransforms ? 1 : 0);
            line.Append(" bodies=").Append(bodies).Append(" awake=").Append(awake).Append(" kinematic=").Append(kinematic);
            line.Append(" colliders=").Append(colliders).Append(" triggers=").Append(triggers).Append(" onBodies=").Append(onBodies);
            foreach (KeyValuePair<string, int> pair in kinds.OrderByDescending(p => p.Value)) line.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            Debug.Log(line.ToString());
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
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (start >= 0f || scene.name == "Logos" || scene.name == "Title" || scene.name == "CrowMara") return;
                start = Time.realtimeSinceStartup + every;
                Debug.Log("CROW_CYCLE " + sets.Length + " sets every " + every + " s from " + scene.name);
            };
            Application.onBeforeRender += Tick;
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
