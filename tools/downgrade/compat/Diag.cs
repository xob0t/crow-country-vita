using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// The measurement probe: off unless a -crow-* flag is given. Each flag's line in Help says what it
// does. Heartbeats go to the player log as "HEARTBEAT key=value ...", for tools to read.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public class Diag : MonoBehaviour
    {
        const string Help =
            "-crow-scene <name>       load this scene once the first scene has loaded (an area: the scene loads the player)\n" +
            "-crow-heartbeat <s>      every s seconds, log frame times, GC and memory, visible renderers and lights\n" +
            "-crow-quit-after <s>     quit s seconds after the probe starts\n" +
            "-crow-shot <s> <path>    save a screenshot s seconds after the last scene load\n" +
            "-crow-no-vsync           vSyncCount 0 and no frame cap, for unsynchronized frame times";

        static string[] args;
        static string sceneToLoad;
        static float heartbeat, quitAfter = -1f, shotAfter = -1f;
        static string shotPath;
        static bool noVsync;

        float started, lastBeat, lastLoad, worst;
        int frames, collections;
        double frameSum;
        bool shotTaken, sceneRequested;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            Quality.Install();
            args = Environment.GetCommandLineArgs();
            if (!args.Any(a => a.StartsWith("-crow-"))) return;
            sceneToLoad = Value("-crow-scene");
            heartbeat = Number("-crow-heartbeat", 0f);
            quitAfter = Number("-crow-quit-after", -1f);
            shotAfter = Number("-crow-shot", -1f);
            int shot = Array.IndexOf(args, "-crow-shot");
            shotPath = shot >= 0 && shot + 2 < args.Length ? args[shot + 2] : null;
            noVsync = args.Contains("-crow-no-vsync");
            if (args.Contains("-crow-help")) Debug.Log("CROW_DIAG flags:\n" + Help);
            var probe = new GameObject("CrowDiag");
            DontDestroyOnLoad(probe);
            probe.AddComponent<Diag>();
        }

        static string Value(string flag)
        {
            int index = Array.IndexOf(args, flag);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        static float Number(string flag, float fallback)
        {
            string value = Value(flag);
            float number;
            return value != null && float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out number) ? number : fallback;
        }

        void Start()
        {
            started = lastBeat = lastLoad = Time.realtimeSinceStartup;
            collections = GC.CollectionCount(0);
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                lastLoad = Time.realtimeSinceStartup;
                shotTaken = false;
                Debug.Log("CROW_DIAG scene loaded " + scene.name + " (" + mode + ") at " + lastLoad.ToString("F2") + " s");
            };
            Debug.Log("CROW_DIAG started: " + string.Join(" ", args.Where(a => a.StartsWith("-crow-")).ToArray()));
        }

        void Update()
        {
            if (noVsync)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }
            float now = Time.realtimeSinceStartup;
            if (sceneToLoad != null && !sceneRequested)
            {
                sceneRequested = true;
                Debug.Log("CROW_DIAG loading " + sceneToLoad);
                SceneManager.LoadScene(sceneToLoad);
            }
            float delta = Time.unscaledDeltaTime * 1000f;
            frames++;
            frameSum += delta;
            if (delta > worst) worst = delta;
            if (heartbeat > 0f && now - lastBeat >= heartbeat) Beat(now);
            if (shotAfter >= 0f && !shotTaken && shotPath != null && now - lastLoad >= shotAfter)
            {
                shotTaken = true;
                ScreenCapture.CaptureScreenshot(shotPath);
                Debug.Log("CROW_DIAG screenshot " + shotPath);
            }
            if (quitAfter >= 0f && now - started >= quitAfter)
            {
                Debug.Log("CROW_DIAG quitting");
                Application.Quit();
            }
        }

        void Beat(float now)
        {
            int gc = GC.CollectionCount(0);
            int visible = 0, renderers = 0, lit = 0;
            var lights = new Dictionary<LightType, int>();
            var active = new List<Light>();
            foreach (Light light in FindObjectsOfType<Light>())
            {
                if (!light.enabled || light.intensity <= 0f || !light.gameObject.activeInHierarchy) continue;
                active.Add(light);
                lit++;
                int count;
                lights.TryGetValue(light.type, out count);
                lights[light.type] = count + 1;
            }
            // Estimated draw calls, before the engine's batching: each visible renderer's materials
            // in the base pass, and an additive pass per point or spot light whose range reaches its
            // bounds, up to the quality level's pixel lights (the brightest directional light is in
            // the base pass).
            int drawBase = 0, drawAdd = 0, pixelLights = QualitySettings.pixelLightCount;
            foreach (Renderer renderer in FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled) continue;
                renderers++;
                if (!renderer.isVisible) continue;
                visible++;
                int materials = renderer.sharedMaterials.Length;
                drawBase += materials;
                if (renderer is ParticleSystemRenderer || renderer is SpriteRenderer) continue;
                Bounds bounds = renderer.bounds;
                int reaching = 0;
                foreach (Light light in active)
                {
                    if (light.type == LightType.Directional) continue;
                    if (bounds.SqrDistance(light.transform.position) <= light.range * light.range) reaching++;
                }
                drawAdd += Math.Min(reaching, pixelLights) * materials;
            }
            var shadowed = new Dictionary<LightType, int>();
            foreach (Light light in active)
            {
                if (light.shadows == LightShadows.None) continue;
                int count;
                shadowed.TryGetValue(light.type, out count);
                shadowed[light.type] = count + 1;
            }
            var line = new StringBuilder("HEARTBEAT");
            line.Append(" t=").Append((now - started).ToString("F1"));
            line.Append(" scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            line.Append(" fps=").Append((frames / (now - lastBeat)).ToString("F1"));
            line.Append(" ms=").Append((frameSum / Math.Max(frames, 1)).ToString("F2"));
            line.Append(" worst=").Append(worst.ToString("F1"));
            line.Append(" gc=").Append(gc - collections);
            line.Append(" heapMB=").Append((GC.GetTotalMemory(false) / 1048576.0).ToString("F1"));
            line.Append(" renderers=").Append(renderers).Append(" visible=").Append(visible);
            line.Append(" lights=").Append(lit);
            foreach (KeyValuePair<LightType, int> pair in lights) line.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            line.Append(" drawBase=").Append(drawBase).Append(" drawAdd=").Append(drawAdd);
            foreach (KeyValuePair<LightType, int> pair in shadowed) line.Append(" shadow").Append(pair.Key).Append('=').Append(pair.Value);
            Debug.Log(line.ToString());
            lastBeat = now;
            frames = 0;
            frameSum = 0;
            worst = 0f;
            collections = gc;
        }
    }
}
