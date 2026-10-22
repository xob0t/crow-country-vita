using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// CrowCompat's start (Install: every module's defaults and flags) and the measurement probe, which is off
// unless a -crow-* flag is given (on the PS Vita, in ux0:data/crow/args.txt; see ProbeFiles). Each of
// Diag's own flags has a line in Help. Heartbeats go to the player log as "HEARTBEAT key=value ...", for
// tools to read.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public class Diag : MonoBehaviour
    {
        const string Help =
            "-crow-scene <name>       load this scene once the Title and the player's scene (CrowMara) have loaded (an area)\n" +
            "-crow-scene-then <name> <s>  s seconds (default 30) after that scene loaded, load this one as the game's\n" +
            "                         area changes do (PlayMaker's LoadScene action), with the player in play;\n" +
            "                         \"A>B>C\" loads them in turn, each s seconds after the one before\n" +
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
        float lastInputReport = -10f;

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
            Guard("PhysCost", PhysCost.Install);  // -crow-phys-census: what a physics step works on (PhysCost.cs)
            Guard("EnemySplit", EnemySplit.Install);  // the enemies' death debris out (EnemySplit.cs, EnemyRebuild.cs)
            Guard("FastPaths", FastPaths.Install);  // -crow-fast-property, -crow-lit-camera (FastPaths.cs)
            Guard("Decor", Decor.Install);  // decoration off screen, -crow-motion-report (Decor.cs)
            Guard("Cuts", Cuts.Install);  // the fights' cuts (Cuts.cs; PS Vita defaults), after Decor
            Guard("Tweens", TweenProbe.Install);  // -crow-tween-log: DOTween's update timed (Tweens.cs)
            Guard("Pad", Pad.Install);  // the PS Vita's pad without Rewired's updates (Pad.cs; PS Vita default)
            Guard("FsmTrim", FsmTrim.Install);  // PlayMaker's updates that do nothing, skipped (FsmTrim.cs; PS Vita default)
            Guard("FsmCompile", FsmCompile.Install);  // PlayMaker's FSM code compiled (FsmCompile.cs; PS Vita default)
            Guard("Audit", Audit.Install);  // -crow-audit, -crow-renderdoc: the fight frame audit (Audit.cs)
            if (!args.Any(a => a.StartsWith("-crow-"))) return;
            sceneToLoad = CrowFlags.Value(args, "-crow-scene") ?? Fight.Scene;
            sceneThen = CrowFlags.Value(args, "-crow-scene-then");
            int then = Array.IndexOf(args, "-crow-scene-then");
            if (then < 0 || then + 2 >= args.Length || !float.TryParse(args[then + 2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out sceneThenAfter))
                sceneThenAfter = 30f;
            heartbeat = CrowFlags.Number(args, "-crow-heartbeat", 0f);
            quitAfter = CrowFlags.Number(args, "-crow-quit-after", -1f);
            shotAfter = CrowFlags.Number(args, "-crow-shot", -1f);
            int shot = Array.IndexOf(args, "-crow-shot");
            shotPath = shot >= 0 && shot + 2 < args.Length ? args[shot + 2] : null;
            noVsync = args.Contains("-crow-no-vsync");
            mute = args.Contains("-crow-mute");
            input = args.Contains("-crow-input");
            if (mute) AudioListener.volume = 0f;
            string find = CrowFlags.Value(args, "-crow-find");
            if (find != null)
            {
                findPattern = new System.Text.RegularExpressions.Regex(find);
                int at = Array.IndexOf(args, "-crow-find");
                float seconds;
                findAfter = at + 2 < args.Length && float.TryParse(args[at + 2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out seconds) ? seconds : 5f;
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
                int at = Array.IndexOf(args, "-crow-teleport");
                float seconds;
                teleportAfter = at + 2 < args.Length && float.TryParse(args[at + 2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out seconds) ? seconds : 3f;
            }
            language = CrowFlags.Value(args, "-crow-language");
            stallDump = CrowFlags.Number(args, "-crow-stall-dump", -1f);
            stallAfter = CrowFlags.Number(args, "-crow-stall-after", 0f);
            if (stallDump > 0f) new System.Threading.Thread(WatchForStall) { IsBackground = true, Name = "CrowStallWatch" }.Start();
            if (args.Contains("-crow-help")) Debug.Log("CROW_DIAG flags:\n" + Help);
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
            if (Doppler.Owns(this)) return;  // a -crow-doppler test tone's filter (Audio.cs)
            started = lastBeat = lastLoad = Time.realtimeSinceStartup;
            collections = GC.CollectionCount(0);
            CrowEvents.Loaded("Diag.sceneLoaded", (scene, mode) =>
            {
                lastLoad = Time.realtimeSinceStartup;
                shotTaken = false;
                found = false;
                teleported = false;
                Debug.Log("CROW_DIAG scene loaded " + scene.name + " (" + mode + ") at " + lastLoad.ToString("F2") + " s");
            });
            Debug.Log("CROW_DIAG started: " + string.Join(" ", args.Where(a => a.StartsWith("-crow-")).ToArray()));
        }

        // -crow-audio-tap: called on FMOD's mixer thread for each block of the probe's silent source.
        void OnAudioFilterRead(float[] data, int channels)
        {
            if (!Doppler.Filter(this, data, channels)) Audio.Tap(data, channels);
        }

        // -crow-doppler: the physics steps of each frame.
        void FixedUpdate()
        {
            Doppler.Step(this);
        }

        static float lastUpdateAt = -1f;

        void Update()
        {
            if (Doppler.Owns(this)) return;
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
            bool titleUp = sceneToLoad != null && !sceneRequested && now - lastLoad >= 2f && SceneManager.GetActiveScene().name == "Title";
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
            if (input)
            {
                LogInput();
                LogInputState();
                if (now - lastInputReport >= 5f)
                {
                    lastInputReport = now;
                    ReportInput();
                }
            }
            if (shotAfter >= 0f && !shotTaken && shotPath != null && now - lastLoad >= shotAfter)
            {
                shotTaken = true;
                ScreenCapture.CaptureScreenshot(shotPath);
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
            GameObject body = GameObject.Find("character RIGIDBODY");
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
                var path = new StringBuilder(renderer.name);
                for (Transform t = renderer.transform.parent; t != null; t = t.parent) path.Insert(0, t.name + "/");
                if (!findPattern.IsMatch(path.ToString())) continue;
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

        static object Get(object target, string name)
        {
            try
            {
                Type type = target.GetType();
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null) return property.GetValue(target, null);
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field == null ? null : field.GetValue(target);
            }
            catch (Exception exception)
            {
                return "<" + exception.GetType().Name + ">";
            }
        }

        static string Count(object list)
        {
            var collection = list as System.Collections.ICollection;
            return collection == null ? "null" : collection.Count.ToString();
        }

        // -crow-input: Unity's joystick buttons (any joystick, then joysticks 1-2) as they go down,
        // and the keys and mouse buttons Unity reports, to tell input that never reaches Unity from
        // input that Rewired doesn't map.
        static void LogInput()
        {
            if (!Input.anyKeyDown) return;
            var down = new List<string>();
            for (KeyCode key = KeyCode.Backspace; key <= KeyCode.Joystick2Button19; key++)
                if (Enum.IsDefined(typeof(KeyCode), key) && Input.GetKeyDown(key)) down.Add(key.ToString());
            Debug.Log("CROW_INPUT down " + string.Join(" ", down.ToArray()));
        }

        // -crow-input, every frame, logged when it changes: Unity's raw axes past 0.2 (Joy1-4, axes 1-29,
        // as Rewired's Input Manager entries name them: JoyNAxisM is Unity's axis M-1 of joystick N) and
        // the joystick buttons held; each Rewired joystick's active elements (its hardware map's view);
        // player 0's active actions (what the game's PlayMaker actions read). Moving a stick shows which
        // of the three loses it.
        static string[] axisNames;
        static string lastUnity = "", lastElements = "", lastActions = "";
        static object[] actionIds;
        static string[] actionNames;
        static MethodInfo getAxisRaw;
        static readonly Dictionary<string, PropertyInfo> properties = new Dictionary<string, PropertyInfo>();

        static void LogInputState()
        {
            if (axisNames == null)
            {
                var names = new List<string>();
                for (int joystick = 1; joystick <= 4; joystick++)
                    for (int axis = 1; axis <= 29; axis++)
                    {
                        string name = "Joy" + joystick + "Axis" + axis;
                        try
                        {
                            Input.GetAxisRaw(name);
                            names.Add(name);
                        }
                        catch (ArgumentException)
                        {
                        }
                    }
                axisNames = names.ToArray();
                Debug.Log("CROW_INPUT probing " + axisNames.Length + " Unity axes");
            }
            var unity = new StringBuilder();
            foreach (string name in axisNames)
            {
                float value = Input.GetAxisRaw(name);
                if (Mathf.Abs(value) > 0.2f) unity.Append(' ').Append(name).Append('=').Append(value.ToString("0.0"));
            }
            for (KeyCode key = KeyCode.Joystick1Button0; key <= KeyCode.Joystick4Button19; key++)
                if (Input.GetKey(key)) unity.Append(' ').Append(key);
            Changed(ref lastUnity, "unity", unity.ToString());
            // Rewired reads Unity's input: with none there and nothing active at the last look, there
            // is nothing new to see, and the reflection below allocates (about 0.3 MB/s at 17 fps).
            if (lastUnity.Length == 0 && lastElements.Length == 0 && lastActions.Length == 0) return;

            Type reInput = Type.GetType("Rewired.ReInput, Rewired_Core");
            if (reInput == null || !Equals(GetStatic(reInput, "isReady"), true)) return;
            var elements = new StringBuilder();
            object controllers = GetStatic(reInput, "controllers");
            var joysticks = controllers == null ? null : Get(controllers, "Joysticks") as System.Collections.IEnumerable;
            if (joysticks != null)
            {
                int index = 0;
                foreach (object joystick in joysticks)
                {
                    var axes = Cached(joystick, "Axes") as System.Collections.IEnumerable;
                    if (axes != null)
                        foreach (object axis in axes)
                        {
                            object value = Cached(axis, "value");
                            if (value is float && Mathf.Abs((float)value) > 0.2f)
                                elements.Append(" j").Append(index).Append(':').Append(ElementName(axis)).Append('=').Append(((float)value).ToString("0.0"));
                        }
                    var buttons = Cached(joystick, "Buttons") as System.Collections.IEnumerable;
                    if (buttons != null)
                        foreach (object button in buttons)
                            if (Equals(Cached(button, "value"), true))
                                elements.Append(" j").Append(index).Append(':').Append(ElementName(button));
                    index++;
                }
            }
            Changed(ref lastElements, "rewired elements", elements.ToString());
            string served = Pad.Active();  // the pad reader's actions while it serves (Rewired's are stale then)
            if (served != null)
            {
                Changed(ref lastActions, "player0 actions (pad reader)", served);
                return;
            }

            object players = GetStatic(reInput, "players");
            object player = null;
            try
            {
                MethodInfo getPlayer = players == null ? null : players.GetType().GetMethod("GetPlayer", new[] { typeof(int) });
                player = getPlayer == null ? null : getPlayer.Invoke(players, new object[] { 0 });
            }
            catch (Exception)
            {
            }
            if (player == null) return;
            if (actionIds == null)
            {
                object mapping = GetStatic(reInput, "mapping");
                var actions = mapping == null ? null : Get(mapping, "Actions") as System.Collections.IEnumerable;
                var ids = new List<object>();
                var names = new List<string>();
                if (actions != null)
                    foreach (object action in actions)
                    {
                        ids.Add(Get(action, "id"));
                        names.Add(Get(action, "name") as string);
                    }
                actionIds = ids.ToArray();
                actionNames = names.ToArray();
                getAxisRaw = player.GetType().GetMethod("GetAxisRaw", new[] { typeof(int) });
                Debug.Log("CROW_INPUT probing " + actionIds.Length + " Rewired actions: " + string.Join(", ", actionNames));
            }
            if (getAxisRaw == null) return;
            var active = new StringBuilder();
            for (int i = 0; i < actionIds.Length; i++)
            {
                object value;
                try
                {
                    value = getAxisRaw.Invoke(player, new[] { actionIds[i] });
                }
                catch (Exception)
                {
                    continue;
                }
                if (value is float && Mathf.Abs((float)value) > 0.2f)
                    active.Append(" [").Append(actionNames[i]).Append("]=").Append(((float)value).ToString("0.0"));
            }
            Changed(ref lastActions, "player0 actions", active.ToString());
        }

        static void Changed(ref string last, string what, string now)
        {
            if (now == last) return;
            last = now;
            Debug.Log("CROW_INPUT " + what + ":" + (now.Length == 0 ? " none" : now));
        }

        static string ElementName(object element)
        {
            object identifier = Cached(element, "elementIdentifier");
            object name = identifier == null ? null : Cached(identifier, "name");
            return name as string ?? "?";
        }

        // Get with the property looked up once per type (LogInputState reads hundreds a frame).
        static object Cached(object target, string name)
        {
            Type type = target.GetType();
            string key = type.FullName + ":" + name;
            PropertyInfo property;
            if (!properties.TryGetValue(key, out property))
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                try
                {
                    property = type.GetProperty(name, flags);
                }
                catch (AmbiguousMatchException)
                {
                    property = type.GetProperties(flags).FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0);
                }
                properties[key] = property;
            }
            try
            {
                return property == null ? null : property.GetValue(target, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Unity's joystick names and Rewired's state (by reflection: CrowCompat doesn't reference Rewired).
        static void ReportInput()
        {
            var line = new StringBuilder("CROW_INPUT unity joysticks=[").Append(string.Join("|", Input.GetJoystickNames())).Append(']');
            Type reInput = Type.GetType("Rewired.ReInput, Rewired_Core");
            if (reInput == null)
            {
                Debug.Log(line.Append(" no Rewired.ReInput").ToString());
                return;
            }
            object ready = GetStatic(reInput, "isReady");
            line.Append(" rewired ready=").Append(ready);
            if (Equals(ready, true))
            {
                object controllers = GetStatic(reInput, "controllers");
                line.Append(" joystickCount=").Append(controllers == null ? "?" : Get(controllers, "joystickCount"));
                var joysticks = controllers == null ? null : Get(controllers, "Joysticks") as System.Collections.IEnumerable;
                if (joysticks != null)
                    foreach (object joystick in joysticks)
                        line.Append(" [").Append(Get(joystick, "name")).Append(" / ").Append(Get(joystick, "hardwareName"))
                            .Append(" id ").Append(Get(joystick, "id")).Append(" unityId ").Append(Get(joystick, "unityId"))
                            .Append(" axes ").Append(Count(Get(joystick, "Axes")))
                            .Append(" buttons ").Append(Count(Get(joystick, "Buttons"))).Append(']');
                object keyboard = controllers == null ? null : Get(controllers, "Keyboard");
                line.Append(" keyboard=").Append(keyboard == null ? "null" : Get(keyboard, "enabled"));
                object players = GetStatic(reInput, "players");
                var all = players == null ? null : Get(players, "AllPlayers") as System.Collections.IEnumerable;
                if (all != null)
                    foreach (object player in all)
                    {
                        object playerControllers = Get(player, "controllers");
                        line.Append(" player ").Append(Get(player, "name")).Append(" joysticks=")
                            .Append(playerControllers == null ? "?" : Get(playerControllers, "joystickCount"))
                            .Append(" hasKeyboard=").Append(playerControllers == null ? "?" : Get(playerControllers, "hasKeyboard"));
                        if (Equals(Get(player, "id"), 0)) Changed(ref lastMaps, "player0 maps", Maps(playerControllers));
                    }
            }
            Debug.Log(line.ToString());
        }

        static string lastMaps = "";

        // A player's controller maps: controller type and id, category, layout, hardware GUID (the
        // Dual Analog Gamepad template is 83b427e4, the PS Vita controller d2aef070), enabled, entries.
        static string Maps(object playerControllers)
        {
            object maps = playerControllers == null ? null : Get(playerControllers, "maps");
            if (maps == null) return " ?";
            System.Collections.IEnumerable all;
            try
            {
                MethodInfo getAllMaps = maps.GetType().GetMethods().FirstOrDefault(m =>
                    m.Name == "GetAllMaps" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                all = getAllMaps == null ? null : getAllMaps.Invoke(maps, null) as System.Collections.IEnumerable;
            }
            catch (Exception exception)
            {
                return " <" + exception.GetType().Name + ">";
            }
            if (all == null) return " ?";
            var line = new StringBuilder();
            foreach (object map in all)
            {
                string guid = Convert.ToString(Get(map, "hardwareGuid"));
                line.Append(" [").Append(Get(map, "controllerType")).Append(' ').Append(Get(map, "controllerId"))
                    .Append(" cat ").Append(Get(map, "categoryId")).Append(" layout ").Append(Get(map, "layoutId"))
                    .Append(' ').Append(guid.Length > 8 ? guid.Substring(0, 8) : guid)
                    .Append(Equals(Get(map, "enabled"), true) ? " on" : " off")
                    .Append(' ').Append(Get(map, "elementMapCount")).Append(']');
            }
            return line.ToString();
        }

        static object GetStatic(Type type, string name)
        {
            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return property == null ? null : property.GetValue(null, null);
            }
            catch (Exception e)
            {
                return "<" + e.GetType().Name + ">";
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
            // The draw estimate (drawBase, drawAdd) and the shadowed lights by type went on 21 Oct: it counted
            // an additive pass per point light in reach whatever the shader, and VitaLit's one pass has none.
            foreach (Renderer renderer in FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled) continue;
                renderers++;
                if (renderer.isVisible) visible++;
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
            Debug.Log(line.ToString());
            lastBeat = now;
            frames = 0;
            frameSum = 0;
            worst = 0f;
            collections = gc;
        }
    }

    // The engine events CrowCompat's modules hang their work on: Canvas.willRenderCanvases (every frame),
    // Application.onBeforeRender (every frame), SceneManager.sceneLoaded and sceneUnloaded (every load and
    // unload), and the cameras' Camera.onPreCull, onPreRender and onPostRender (every camera's render). Each is
    // one multicast delegate that Unity 2018.2 invokes with no try/catch, so a handler that throws skips every
    // handler after it, for that frame, load or render: 26 CrowCompat handlers share willRenderCanvases, MaraScene's
    // Poll and ScenePrefetch's Request among them; the gap's drain is the last onBeforeRender handler (hooked at the
    // first area change); SceneGap.Arrived (the area made active: its fog) comes after Quality's, Perf's and VitaLit's
    // load handlers; NearPlane's fog put back and Cameras.AfterRender (the skipped render-texture cameras on again)
    // share onPostRender; EnemySplit.Purge shares sceneUnloaded with up to ten others.
    // One module failing every frame would hold the player on a black screen with an exception a frame in the log.
    // So every module subscribes to those here: each handler runs in its own try/catch, a failure is logged with the
    // handler's name (CROW_HOOK_FAIL: the first with its stack, then a count every 1000th) and the handlers after
    // it run. Each is subscribed where it was, so the order is the same (21 Oct; the camera callbacks and
    // sceneUnloaded since the second round that day). Left raw: activeSceneChanged (the load trace's one handler).
    // -crow-hook-cost [s] (22 Oct; off by default, and then the handlers are subscribed exactly as without it):
    // each handler timed (two Stopwatch reads a call), and every s seconds (default 10) a CROW_HOOKS line: the
    // frames, the handlers' total, and per handler by name, the most first, its ms a frame on average over the
    // window, its ms in its worst frame and its calls (name=mean/worst/calls; handlers of the same name add up).
    // A sceneLoaded handler's worst frame is its load's. Not in it: MonoBehaviours' own Updates (Diag, Memory...)
    // and the rewriter's per-call hooks.
    // Turn: the periodic scans (a FindObjectsOfType or the like every few seconds) take turns, at most one a
    // frame (22 Oct): their timers start from load-relative times, so several came due in the same frame for a
    // whole session (VitaLit's light list, FixedStep's bodies, FilterGate's filters, Draws' casters, IdleAudio's
    // sources, Cameras' render-texture cameras, Rewired's joysticks). A scan that is due waits for a later frame
    // without one (a few frames at most; VitaLit's list, for its next selection, 0.25 s); the first after a load runs
    // at once (urgent) and takes the frame's turn.
    public static class CrowEvents
    {
        static readonly Dictionary<string, int> failures = new Dictionary<string, int>();
        // The wrapper of each willRenderCanvases handler, for CanvasesOff (Delegate equality: method and target).
        static readonly Dictionary<Action, Canvas.WillRenderCanvases> canvases = new Dictionary<Action, Canvas.WillRenderCanvases>();
        static int turnFrame = -1;

        // Whether a periodic scan that is due runs in this frame: urgent, or no other has run in it.
        internal static bool Turn(bool urgent = false)
        {
            int frame = Time.frameCount;
            if (!urgent && frame == turnFrame) return false;
            turnFrame = frame;
            return true;
        }

        internal static void Canvases(string name, Action handler)
        {
            Canvas.WillRenderCanvases wrapped;
            if (!canvases.TryGetValue(handler, out wrapped))
            {
                Action timed = Timed(name, handler);
                canvases[handler] = wrapped = () =>
                {
                    try
                    {
                        timed();
                    }
                    catch (Exception exception)
                    {
                        Fail(name, exception);
                    }
                };
            }
            Canvas.willRenderCanvases += wrapped;
        }

        internal static void CanvasesOff(Action handler)
        {
            Canvas.WillRenderCanvases wrapped;
            if (canvases.TryGetValue(handler, out wrapped)) Canvas.willRenderCanvases -= wrapped;
        }

        internal static void BeforeRender(string name, Action handler)
        {
            handler = Timed(name, handler);
            Application.onBeforeRender += () =>
            {
                try
                {
                    handler();
                }
                catch (Exception exception)
                {
                    Fail(name, exception);
                }
            };
        }

        internal static void Loaded(string name, Action<Scene, LoadSceneMode> handler)
        {
            handler = Timed(name, handler);
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                try
                {
                    handler(scene, mode);
                }
                catch (Exception exception)
                {
                    Fail(name, exception);
                }
            };
        }

        internal static void Unloaded(string name, Action<Scene> handler)
        {
            handler = Timed(name, handler);
            SceneManager.sceneUnloaded += scene =>
            {
                try
                {
                    handler(scene);
                }
                catch (Exception exception)
                {
                    Fail(name, exception);
                }
            };
        }

        internal static void PreCull(string name, Camera.CameraCallback handler)
        {
            Camera.onPreCull += Guarded(name, handler);
        }

        internal static void PreRender(string name, Camera.CameraCallback handler)
        {
            Camera.onPreRender += Guarded(name, handler);
        }

        internal static void PostRender(string name, Camera.CameraCallback handler)
        {
            Camera.onPostRender += Guarded(name, handler);
        }

        static Camera.CameraCallback Guarded(string name, Camera.CameraCallback handler)
        {
            handler = Timed(name, handler);
            return camera =>
            {
                try
                {
                    handler(camera);
                }
                catch (Exception exception)
                {
                    Fail(name, exception);
                }
            };
        }

        static void Fail(string name, Exception exception)
        {
            int count;
            failures.TryGetValue(name, out count);
            failures[name] = ++count;
            if (count == 1) Debug.LogError("CROW_HOOK_FAIL " + name + ": " + exception);
            else if (count % 1000 == 0) Debug.LogError("CROW_HOOK_FAIL " + name + " (" + count + " times): " + exception.GetType().Name + " " + exception.Message);
        }

        // ---------------------------------------------------------------- -crow-hook-cost

        class Cost
        {
            public string Name;
            public long Ticks, FrameTicks, WorstFrame;
            public int Calls, Frame = -1;

            public void Add(long ticks)
            {
                int frame = Time.frameCount;
                if (frame != Frame)
                {
                    if (FrameTicks > WorstFrame) WorstFrame = FrameTicks;
                    FrameTicks = 0;
                    Frame = frame;
                }
                FrameTicks += ticks;
                Ticks += ticks;
                Calls++;
            }
        }

        static int timing = -1;  // -1 not read yet, 0 off, 1 on
        static float costEvery, costAt;
        static int costFrame;
        static readonly List<Cost> costs = new List<Cost>();
        static readonly Dictionary<string, Cost> costByName = new Dictionary<string, Cost>();

        static Cost CostOf(string name)
        {
            if (timing < 0)
            {
                timing = 0;
                try
                {
                    string[] args = ProbeFiles.Args;
                    if (CrowFlags.Has(args, "-crow-hook-cost"))
                    {
                        float every;
                        string value = CrowFlags.Optional(args, "-crow-hook-cost");
                        costEvery = value != null && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out every) && every > 0f ? every : 10f;
                        timing = 1;
                        costAt = Time.realtimeSinceStartup + costEvery;
                        costFrame = Time.frameCount;
                        Application.onBeforeRender += CostReport;
                        Debug.Log("CROW_HOOKS every " + costEvery + " s");
                    }
                }
                catch (Exception exception)
                {
                    Debug.Log("CROW_HOOKS off: " + exception.GetType().Name + " " + exception.Message);
                }
            }
            if (timing == 0) return null;
            Cost cost;
            if (!costByName.TryGetValue(name, out cost))
            {
                costByName[name] = cost = new Cost { Name = name };
                costs.Add(cost);
            }
            return cost;
        }

        // The handler itself without -crow-hook-cost; with it, the handler between two Stopwatch reads.
        static Action Timed(string name, Action handler)
        {
            Cost cost = CostOf(name);
            if (cost == null) return handler;
            return () =>
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    handler();
                }
                finally
                {
                    cost.Add(System.Diagnostics.Stopwatch.GetTimestamp() - start);
                }
            };
        }

        static Action<Scene, LoadSceneMode> Timed(string name, Action<Scene, LoadSceneMode> handler)
        {
            Cost cost = CostOf(name);
            if (cost == null) return handler;
            return (scene, mode) =>
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    handler(scene, mode);
                }
                finally
                {
                    cost.Add(System.Diagnostics.Stopwatch.GetTimestamp() - start);
                }
            };
        }

        static Action<Scene> Timed(string name, Action<Scene> handler)
        {
            Cost cost = CostOf(name);
            if (cost == null) return handler;
            return scene =>
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    handler(scene);
                }
                finally
                {
                    cost.Add(System.Diagnostics.Stopwatch.GetTimestamp() - start);
                }
            };
        }

        static Camera.CameraCallback Timed(string name, Camera.CameraCallback handler)
        {
            Cost cost = CostOf(name);
            if (cost == null) return handler;
            return camera =>
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    handler(camera);
                }
                finally
                {
                    cost.Add(System.Diagnostics.Stopwatch.GetTimestamp() - start);
                }
            };
        }

        // onBeforeRender (subscribed at the first timed handler, so before them): the window's line, then a new window.
        static void CostReport()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now < costAt) return;
                costAt = now + costEvery;
                int frames = Math.Max(1, Time.frameCount - costFrame);
                costFrame = Time.frameCount;
                double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                long total = 0;
                foreach (Cost cost in costs)
                {
                    if (cost.FrameTicks > cost.WorstFrame) cost.WorstFrame = cost.FrameTicks;
                    total += cost.Ticks;
                }
                var line = new StringBuilder("CROW_HOOKS frames=").Append(frames)
                    .Append(" scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'))
                    .Append(" ms/frame=").Append((total * ms / frames).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                foreach (Cost cost in costs.Where(c => c.Calls > 0).OrderByDescending(c => c.Ticks))
                    line.Append(' ').Append(cost.Name.Replace(' ', '_')).Append('=')
                        .Append((cost.Ticks * ms / frames).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append('/')
                        .Append((cost.WorstFrame * ms).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('/')
                        .Append(cost.Calls);
                Debug.Log(line.ToString());
                foreach (Cost cost in costs)
                {
                    cost.Ticks = cost.FrameTicks = cost.WorstFrame = 0;
                    cost.Calls = 0;
                }
            }
            catch (Exception exception)
            {
                Fail("CrowEvents.CostReport", exception);
            }
        }
    }
}
