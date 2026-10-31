using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// CrowCompat's start (Install: every module's defaults and flags) and the measurement probe, which is off
// unless a -crow-* flag is given (on the PS Vita, in ux0:data/crow/args.txt; see ProbeFiles). Each of
// Diag's own flags has a line in Help (-crow-input's reader is InputProbe.cs). Heartbeats go to the player log as
// "HEARTBEAT key=value ...", for tools to read. The engine events the modules use are CrowEvents.cs'.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public class Diag : MonoBehaviour
    {
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        const string Help =
            "-crow-scene <name>       load this scene once the Title and the player's scene (CrowMara) have loaded (an area)\n" +
            "-crow-scene-then <name> <s>  s seconds (default 30) after that scene loaded, load this one as the game's\n" +
            "                         area changes do (PlayMaker's LoadScene action), with the player in play;\n" +
            "                         \"A>B>C\" loads them in turn, each s seconds after the one before\n" +
            "-crow-globals \"<n>=<v>;...\"  PlayMaker globals set just before -crow-scene's load (PlayerExit=42: Underground NW\n" +
            "                         starts in its elevator, as the ride down from Corridor A does)\n" +
            "-crow-heartbeat <s>      every s seconds, log frame times, GC and memory, visible renderers and lights\n" +
            "-crow-quit-after <s>     quit s seconds after the probe starts\n" +
            "-crow-shot <s> <path>    save a screenshot s seconds after the last scene load\n" +
            "-crow-no-vsync           vSyncCount 0 and no frame cap, for unsynchronized frame times\n" +
            "-crow-mute               no sound (AudioListener.volume 0, kept there), for test runs on a PC in use\n" +
            "-crow-input              log Unity's joystick buttons as they go down, and every 5 s the joysticks Unity and Rewired see\n" +
            "                         and player 0's maps; as they change, Unity's raw axes past 0.2 and held joystick buttons,\n" +
            "                         Rewired's active joystick elements and player 0's active actions\n" +
            "-crow-stall-dump <s>     crash the player when a frame has taken s seconds, for a core dump with the main thread's stack\n" +
            "-crow-stall-after <t>    with -crow-stall-dump, only frames that start t seconds or more after startup (skips earlier stalls)\n" +
            "-crow-language <name>    keep I2 Localization on this language (English, French, Japanese, Korean, \"Chinese (Simplified)\", ...)\n" +
            "-crow-find <regex> <s>   s seconds after each scene load, log every renderer (active or not) whose hierarchy path matches\n" +
            "                         the regex: active, enabled, isVisible, bounds, mesh, materials and shaders (CROW_FIND), for\n" +
            "                         what looks missing in play\n" +
            "-crow-teleport <x,y,z> <s>  s seconds (default 3) after each scene load, move the player (its \"character\n" +
            "                         RIGIDBODY\") to world position x,y,z, to look at a place without walking there\n" +
            "-crow-hook-cost [s]      every s seconds (default 10), each CrowEvents handler's time: ms a frame on average and in\n" +
            "                         its worst frame, calls (CROW_HOOKS, the most first)";

        static string[] args;
        static string sceneToLoad, sceneThen;
        static float sceneThenAfter;
        static float heartbeat, quitAfter = -1f, shotAfter = -1f;
        static string shotPath;
        static bool noVsync, mute, input;
        static System.Text.RegularExpressions.Regex findPattern;
        static float findAfter = -1f;
        static Vector3 teleportTo;
        static float teleportAfter = -1f;
        static string language;
        static PropertyInfo currentLanguage;
        static float lastLanguageCheck = -10f;
        static float stallDump = -1f, stallAfter;
        static volatile float frameStartTime;  // realtimeSinceStartup at frameStartTicks
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static long frameStartTicks;

        float started, lastBeat, lastLoad, worst;
        int frames, collections;
        double frameSum;
        bool shotTaken, sceneRequested, waitLogged, found, teleported;

        // CrowCompat's start: every module's defaults (the PS Vita's among them) and flags, then the probe's
        // own. Each module on its own: one that throws (a flag it can't read) is logged and the rest go on.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            args = ProbeFiles.Args;
            Guard("Quality", Quality.Install);
            Guard("Memory", Memory.Install);
            Guard("Audio", Audio.InstallDefaults);  // before Loading's title prefetch, whose loading thread it moves
            Guard("Loading", Loading.Install);
            Guard("Clock", Clock.Install);
            Guard("Alloc", Alloc.Install);
            Guard("IdleAudio", IdleAudio.Install);
            Guard("Fight", Fight.Install);  // -crow-fight: the fight benchmark (Fight.cs)
            Guard("EnemyCost", EnemyCost.Install);  // the enemies' costs (PS Vita defaults)
            Guard("FixedStep", FixedStep.Install);  // -crow-fixed-dt: the physics step
            Guard("EnemySplit", EnemySplit.Install);  // the enemies' death debris out (EnemySplit.cs, EnemyRebuild.cs)
            Guard("FastPaths", FastPaths.Install);  // -crow-fast-property, -crow-lit-camera (FastPaths.cs)
            Guard("Decor", Decor.Install);  // decoration off screen (Decor.cs)
            Guard("Cuts", Cuts.Install);  // the fights' cuts (Cuts.cs; PS Vita defaults), after Decor
            Guard("Tweens", TweenProbe.Install);  // -crow-tween-log: DOTween's update timed (Tweens.cs)
            Guard("Pad", Pad.Install);  // the PS Vita's pad without Rewired's updates (Pad.cs; PS Vita default)
            Guard("FsmTrim", FsmTrim.Install);  // PlayMaker's updates that do nothing, skipped (FsmTrim.cs; PS Vita default)
            Guard("FsmCompile", FsmCompile.Install);  // PlayMaker's FSM code compiled (FsmCompile.cs; PS Vita default)
            if (!args.Any(a => a.StartsWith("-crow-"))) return;
            sceneToLoad = CrowFlags.Value(args, "-crow-scene") ?? Fight.Scene;
            sceneThen = CrowFlags.Value(args, "-crow-scene-then");
            sceneThenAfter = CrowFlags.NumberAt(args, "-crow-scene-then", 2, 30f);
            heartbeat = CrowFlags.Number(args, "-crow-heartbeat", 0f);
            quitAfter = CrowFlags.Number(args, "-crow-quit-after", -1f);
            shotAfter = CrowFlags.Number(args, "-crow-shot", -1f);
            shotPath = CrowFlags.Word(args, "-crow-shot", 2);
            noVsync = CrowFlags.Has(args, "-crow-no-vsync");
            mute = CrowFlags.Has(args, "-crow-mute");
            input = CrowFlags.Has(args, "-crow-input");
            if (mute) AudioListener.volume = 0f;
            string find = CrowFlags.Value(args, "-crow-find");
            if (find != null)
            {
                findPattern = new System.Text.RegularExpressions.Regex(find);
                findAfter = CrowFlags.NumberAt(args, "-crow-find", 2, 5f);
            }
            string teleport = CrowFlags.Value(args, "-crow-teleport");
            float[] xyz = teleport == null ? null : teleport.Split(',').Select(v =>
            {
                float number;
                return float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) ? number : float.NaN;
            }).ToArray();
            if (xyz != null && (xyz.Length < 3 || xyz.Any(float.IsNaN)))
            {
                Debug.Log("CROW_DIAG bad -crow-teleport " + teleport);
                xyz = null;
            }
            if (xyz != null)
            {
                teleportTo = new Vector3(xyz[0], xyz[1], xyz[2]);
                teleportAfter = CrowFlags.NumberAt(args, "-crow-teleport", 2, 3f);
            }
            language = CrowFlags.Value(args, "-crow-language");
            stallDump = CrowFlags.Number(args, "-crow-stall-dump", -1f);
            stallAfter = CrowFlags.Number(args, "-crow-stall-after", 0f);
            if (stallDump > 0f) new System.Threading.Thread(WatchForStall) { IsBackground = true, Name = "CrowStallWatch" }.Start();
            if (CrowFlags.Has(args, "-crow-help")) Debug.Log("CROW_DIAG flags:\n" + Help);
            var probe = new GameObject("CrowDiag");
            DontDestroyOnLoad(probe);
            if (Audio.WantsTap) probe.AddComponent<AudioSource>();  // the mixer tap's silent source (Audio.cs)
            probe.AddComponent<Diag>();
            Audio.Install(probe);
        }

        internal static void Guard(string module, Action install)
        {
            try
            {
                install();
            }
            catch (Exception exception)
            {
                Debug.LogError("CROW_INSTALL_FAIL " + module + ": " + exception);
            }
        }

        static void Guard(string module, Action<string[]> install)
        {
            Guard(module, () => install(args));
        }

        // -crow-stall-dump: a frame that never ends in the logs (the PS Vita's 40-60 s frames after a
        // scene load) can't say what it spends its time on, and the console has no sampling profiler.
        // Crashing the player mid-frame leaves a core dump with every thread's stack, the main
        // thread's among them (vita.py crashes --pull --thread main).
        static void WatchForStall()
        {
            while (true)
            {
                System.Threading.Thread.Sleep(Math.Max(10, Math.Min(250, (int)(stallDump * 250f))));  // a quarter of the limit: short stalls too
                long started = System.Threading.Interlocked.Read(ref frameStartTicks);
                if (started == 0) continue;
                double seconds = (clock.ElapsedTicks - started) / (double)System.Diagnostics.Stopwatch.Frequency;
                if (seconds < stallDump || frameStartTime < stallAfter) continue;
                Debug.Log("CROW_STALL_DUMP a frame has taken " + seconds.ToString("F1") + " s: crashing for a dump");
                ProbeFiles.Flush(3000);  // the queued lines, before the crash loses them
                System.Runtime.InteropServices.Marshal.WriteInt32(new IntPtr(16), 0);  // unmapped: a data abort
                Environment.FailFast("CrowCompat stall dump");
            }
        }

        void Start()
        {
            started = lastBeat = lastLoad = Time.realtimeSinceStartup;
            collections = GC.CollectionCount(0);
            CrowEvents.Loaded("Diag.sceneLoaded", (scene, mode) =>
            {
                lastLoad = Time.realtimeSinceStartup;
                shotTaken = false;
                found = false;
                teleported = false;
                Debug.Log("CROW_DIAG scene loaded " + scene.name + " (" + mode + ") at " + lastLoad.ToString("F2", Inv) + " s");
            });
            Debug.Log("CROW_DIAG started: " + string.Join(" ", args.Where(a => a.StartsWith("-crow-")).ToArray()));
        }

        // -crow-audio-tap: called on FMOD's mixer thread for each block of the probe's silent source.
        void OnAudioFilterRead(float[] data, int channels)
        {
            Audio.Tap(data, channels);
        }

        static float lastUpdateAt = -1f;
        static Diag probe;

        // A screenshot of the frame as shown (-crow-shot, -crow-pad-press's shot, -crow-ab). On the PS Vita
        // ScreenCapture writes nothing (and vitacompanion's screenshot can run out of memory), so there the
        // frame is read back at its end and saved as a PNG (960x544 RGB, about 1.5 MB while it is made).
        internal static void Shot(string path)
        {
            if (Application.platform != RuntimePlatform.PSP2 || probe == null) ScreenCapture.CaptureScreenshot(path);
            else probe.StartCoroutine(SaveFrame(path));
        }

        static System.Collections.IEnumerator SaveFrame(string path)
        {
            yield return new WaitForEndOfFrame();
            var picture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            try
            {
                picture.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
                picture.Apply(false);
                System.IO.File.WriteAllBytes(path, picture.EncodeToPNG());
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_DIAG screenshot " + path + " failed: " + exception.Message);
            }
            Destroy(picture);
        }

        void Awake()
        {
            probe = this;
        }

        void Update()
        {
            Audio.Frame();
            Alloc.Frame();
            Fight.Frame();
            frameStartTime = Time.realtimeSinceStartup;
            System.Threading.Interlocked.Exchange(ref frameStartTicks, clock.ElapsedTicks);
            if (mute) AudioListener.volume = 0f;  // the game sets it from its sound settings
            if (noVsync)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }
            float now = Time.realtimeSinceStartup;
            // Only once the Logos scene's own load of the Title is over (2 s after it), so two scenes are
            // never in flight at once (that overstated memory: Roadside ran out of it that way).
            // The scene's name is a new string at each read: read only while it matters.
            bool titleUp = sceneToLoad != null && !sceneRequested && now - lastLoad >= 2f && SceneManager.GetActiveScene().name == Scenes.Title;
            if (sceneToLoad != null && !sceneRequested && (titleUp || now - started >= 180f))
            {
                MaraScene.Request();  // the player's scene, which the title would have loaded (Loading.cs)
                if (MaraScene.Settled)
                {
                    sceneRequested = true;
                    Debug.Log("CROW_DIAG loading " + sceneToLoad);
                    Fight.BeforeLoad();  // the globals the area's FSMs read as it starts
                    if (!SceneGap.Load(sceneToLoad, LoadSceneMode.Single)) SceneManager.LoadScene(sceneToLoad);  // as the game's loads (Loading.cs)
                }
                else if (!waitLogged)
                {
                    waitLogged = true;
                    Debug.Log("CROW_DIAG waiting for " + MaraScene.Scene + " before loading " + sceneToLoad);
                }
            }
            if (sceneThen != null && sceneRequested && now - lastLoad >= sceneThenAfter && SceneManager.GetActiveScene().name == sceneToLoad)
            {
                // "A>B>C": a chain of area loads, each s seconds after the one before.
                int split = sceneThen.IndexOf('>');
                string next = split < 0 ? sceneThen : sceneThen.Substring(0, split);
                sceneThen = split < 0 ? null : sceneThen.Substring(split + 1);
                Debug.Log("CROW_DIAG loading " + next + " from " + sceneToLoad);
                sceneToLoad = next;
                ScenePrefetch.LoadScene(next, LoadSceneMode.Single);
            }
            // By the wall clock: Time.unscaledDeltaTime is clamped to maximumDeltaTime (-crow-physics-cap).
            float delta = lastUpdateAt >= 0f ? (now - lastUpdateAt) * 1000f : Time.unscaledDeltaTime * 1000f;
            lastUpdateAt = now;
            frames++;
            frameSum += delta;
            if (delta > worst) worst = delta;
            if (heartbeat > 0f && now - lastBeat >= heartbeat) Beat(now);
            if (input) InputProbe.Frame(now);
            if (shotAfter >= 0f && !shotTaken && shotPath != null && now - lastLoad >= shotAfter)
            {
                shotTaken = true;
                Shot(shotPath);
                Debug.Log("CROW_DIAG screenshot " + shotPath);
            }
            if (findPattern != null && !found && now - lastLoad >= findAfter)
            {
                found = true;
                FindRenderers();
            }
            if (teleportAfter >= 0f && !teleported && now - lastLoad >= teleportAfter)
            {
                teleported = true;
                Teleport();
            }
            if (language != null && now - lastLanguageCheck >= 0.5f)
            {
                lastLanguageCheck = now;
                KeepLanguage();
            }
            if (quitAfter >= 0f && now - started >= quitAfter)
            {
                Debug.Log("CROW_DIAG quitting");
                Application.Quit();
            }
        }

        // -crow-teleport: the player's body (the rigidbody PlayMaker moves) and its transform put at
        // teleportTo, at rest; the game's camera follows it.
        static void Teleport()
        {
            GameObject body = MaraScene.FindBody();
            if (body == null)
            {
                Debug.Log("CROW_DIAG teleport: no active \"character RIGIDBODY\"");
                return;
            }
            var rigidbody = body.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                rigidbody.velocity = Vector3.zero;
                rigidbody.position = teleportTo;
            }
            body.transform.position = teleportTo;
            Debug.Log("CROW_DIAG teleport to " + teleportTo.ToString("F2"));
        }

        // -crow-find: the renderers whose hierarchy path matches, whatever their state.
        static void FindRenderers()
        {
            int count = 0;
            foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (!renderer.gameObject.scene.IsValid()) continue;  // assets and prefabs
                string path = Hierarchy.Full(renderer.transform);
                if (!findPattern.IsMatch(path)) continue;
                count++;
                var line = new StringBuilder("CROW_FIND ").Append(path).Append(" [").Append(renderer.gameObject.scene.name).Append("]")
                    .Append(" active=").Append(renderer.gameObject.activeInHierarchy ? 1 : 0)
                    .Append(" enabled=").Append(renderer.enabled ? 1 : 0)
                    .Append(" visible=").Append(renderer.isVisible ? 1 : 0)
                    .Append(" layer=").Append(renderer.gameObject.layer)
                    .Append(" bounds=").Append(renderer.bounds.center.ToString("F2")).Append(renderer.bounds.size.ToString("F2"));
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter != null)
                {
                    Mesh mesh = filter.sharedMesh;
                    line.Append(" mesh=").Append(mesh == null ? "none" : mesh.name + "(" + mesh.vertexCount + "v," + mesh.subMeshCount + "sub)");
                }
                foreach (Material material in renderer.sharedMaterials)
                    line.Append(" mat=").Append(material == null ? "none" : material.name + "/" + (material.shader == null ? "none" : material.shader.name)
                        + "/q" + material.renderQueue + (material.HasProperty("_Color") ? "/" + material.color.ToString("F2") : ""));
                Debug.Log(line.ToString());
            }
            Debug.Log("CROW_FIND " + count + " renderers match " + findPattern);
        }

        // -crow-language: the game sets I2's language from its settings save at startup, so this sets
        // LocalizationManager.CurrentLanguage (by reflection) whenever it differs, every half second.
        static void KeepLanguage()
        {
            if (currentLanguage == null)
            {
                Type manager = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("I2.Loc.LocalizationManager", false)).FirstOrDefault(t => t != null);
                currentLanguage = manager == null ? null : manager.GetProperty("CurrentLanguage", BindingFlags.Static | BindingFlags.Public);
                if (currentLanguage == null)
                {
                    Debug.Log("CROW_DIAG no I2.Loc.LocalizationManager.CurrentLanguage");
                    language = null;
                    return;
                }
            }
            try
            {
                if (Equals(currentLanguage.GetValue(null, null), language)) return;
                Debug.Log("CROW_DIAG language " + currentLanguage.GetValue(null, null) + " -> " + language);
                currentLanguage.SetValue(null, language, null);
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_DIAG language: " + exception);
                language = null;
            }
        }

        void Beat(float now)
        {
            int gc = GC.CollectionCount(0);
            int visible = 0, renderers = 0, lit = 0;
            var lights = new Dictionary<LightType, int>();
            foreach (Light light in FindObjectsOfType<Light>())
            {
                if (!light.enabled || light.intensity <= 0f || !light.gameObject.activeInHierarchy) continue;
                lit++;
                int count;
                lights.TryGetValue(light.type, out count);
                lights[light.type] = count + 1;
            }
            // No draw estimate: VitaLit's one pass has no additive passes to count.
            foreach (Renderer renderer in FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled) continue;
                renderers++;
                if (renderer.isVisible) visible++;
            }
            var line = new StringBuilder("HEARTBEAT");
            line.Append(" t=").Append((now - started).ToString("F1", Inv));
            line.Append(" scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            line.Append(" fps=").Append((frames / (now - lastBeat)).ToString("F1", Inv));
            line.Append(" ms=").Append((frameSum / Math.Max(frames, 1)).ToString("F2", Inv));
            line.Append(" worst=").Append(worst.ToString("F1", Inv));
            line.Append(" gc=").Append(gc - collections);
            line.Append(" heapMB=").Append((GC.GetTotalMemory(false) / 1048576.0).ToString("F1", Inv));
            line.Append(" renderers=").Append(renderers).Append(" visible=").Append(visible);
            line.Append(" lights=").Append(lit);
            foreach (KeyValuePair<LightType, int> pair in lights) line.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            Debug.Log(line.ToString());
            lastBeat = now;
            frames = 0;
            frameSum = 0;
            worst = 0f;
            collections = gc;
        }
    }

}
