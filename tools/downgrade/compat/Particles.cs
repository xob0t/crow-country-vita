using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// The particle systems (Perf.Install installs these; the flags are listed in Perf.cs): -crow-particles off|game
// (ParticleRule), -crow-particle-noise (ParticleNoise, low on the PS Vita), -crow-particles-report (ParticleReport).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
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
                CrowEvents.Loaded("ParticleRule.sceneLoaded", (scene, mode) => last = -10f);
                CrowEvents.BeforeRender("ParticleRule.Tick", Tick);
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

    // The particle systems' noise module at a lower quality. Corridor B's two window dust systems (the
    // motes in the light, 75 and 50 a second for 8-16 s: 900 and 600 particles alive, everywhere in the
    // area, since a system with noise is never culled) move each particle by 3D noise on separate axes
    // (quality High), and the particle update took 14-19 ms of the main thread's frame there (Corridor
    // B standing: 40-47 ms a frame, Crypt 3.4 ms of particles). The noise moves them by at most 3 cm;
    // Low (1D noise) looks the same at the camera's 15 m. Set on every system of the loaded scenes
    // after each load (and those instantiated later keep their own); game puts the saved qualities back.
    static class ParticleNoise
    {
        static string rule = "game";
        static bool hooked;
        static readonly Dictionary<ParticleSystem, ParticleSystemNoiseQuality> saved = new Dictionary<ParticleSystem, ParticleSystemNoiseQuality>();
        static readonly List<ParticleSystem> dead = new List<ParticleSystem>();

        public static void Install(string[] args)
        {
            string value = CrowFlags.Value(args, "-crow-particle-noise", "low");
            if (value != null) Set(value);
        }

        internal static void Set(string value)
        {
            rule = value == "low" || value == "medium" ? value : "game";
            if (!hooked)
            {
                hooked = true;
                CrowEvents.Loaded("ParticleNoise.sceneLoaded", (scene, mode) => Apply(scene));
            }
            if (rule == "game")
            {
                int restored = 0;
                foreach (KeyValuePair<ParticleSystem, ParticleSystemNoiseQuality> pair in saved)
                    if (pair.Key != null)
                    {
                        ParticleSystem.NoiseModule noise = pair.Key.noise;
                        noise.quality = pair.Value;
                        restored++;
                    }
                saved.Clear();
                Debug.Log("CROW_PARTICLES noise game: " + restored + " systems back");
                return;
            }
            for (int i = 0; i < SceneManager.sceneCount; i++) Apply(SceneManager.GetSceneAt(i));
        }

        static void Apply(Scene scene)
        {
            if (rule == "game" || !scene.isLoaded) return;
            ParticleSystemNoiseQuality quality = rule == "low" ? ParticleSystemNoiseQuality.Low : ParticleSystemNoiseQuality.Medium;
            int changed = 0;
            foreach (ParticleSystem system in Resources.FindObjectsOfTypeAll<ParticleSystem>())
            {
                if (system == null || system.gameObject.scene != scene) continue;
                ParticleSystem.NoiseModule noise = system.noise;
                if (!noise.enabled || noise.quality == quality) continue;
                if (!saved.ContainsKey(system)) saved[system] = noise.quality;
                noise.quality = quality;
                changed++;
            }
            // The unloaded areas' systems out of the saved list.
            dead.Clear();
            foreach (ParticleSystem system in saved.Keys) if (system == null) dead.Add(system);
            foreach (ParticleSystem system in dead) saved.Remove(system);
            if (changed > 0) Debug.Log("CROW_PARTICLES noise " + rule + ": " + changed + " systems in " + scene.name);
        }
    }

    static class ParticleReport
    {
        static float every, lastReport;

        public static void Install(float seconds)
        {
            every = seconds;
            CrowEvents.BeforeRender("ParticleReport.Tick", Tick);
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
                text.Append(" path=").Append(Hierarchy.Path(system.transform, 4));
                Debug.Log(text.ToString());
            }
        }
    }
}
