using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.LowLevel;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

// Load time: where a scene load's long frames go, for the PS Vita, which has no profiler.
//   -crow-load-trace [ms]   each frame of ms or more (default 200) as a CROW_LOADFRAME line: its time
//                           per player loop step, the costliest first; a CROW_LOAD line when a scene
//                           loads or unloads (with the step and the time into the frame it happens
//                           at: before it are the read and the objects' Awake and OnEnable), and a
//                           CROW_LOADDONE line when the frames after a load are short again (the
//                           load's long frames summed). Replaces the player loop, as
//                           -crow-loop-profile does: use one of the two.
//   -crow-mcs-time          time MeshCombineStudio's run-time combining: each MeshCombiner that would
//                           combine in its Start combines when its scene has loaded, under a
//                           stopwatch, instead (CROW_MCS lines, per combiner and per scene).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Loading
    {
        // From Diag.Install (a RuntimeInitializeOnLoadMethod: the build lists those, so quickcode can't add one).
        internal static void Install()
        {
            string[] args = ProbeFiles.Args;
            int trace = Array.IndexOf(args, "-crow-load-trace");
            if (trace >= 0 && !args.Contains("-crow-loop-profile"))
            {
                float ms = 0f;
                bool given = trace + 1 < args.Length && float.TryParse(args[trace + 1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out ms);
                Trace.Install(given ? ms : 200f);
            }
            if (args.Contains("-crow-mcs-time")) CombineTimer.Install();
            MaraScene.Install();
            ScenePrefetch.Install(args);
        }

        static class Trace
        {
            static readonly List<string> labels = new List<string>();
            static long[] ticks;
            static long last, frameStart, threshold, loadTicks;
            static int previous = -1, frame, loadFrames, quietFrames;
            static string loading;
            static readonly double Ms = 1000.0 / Stopwatch.Frequency;

            public static void Install(float ms)
            {
                threshold = (long)(ms / Ms);
                PlayerLoopSystem root = PlayerLoop.GetDefaultPlayerLoop();
                PlayerLoopSystem[] phases = root.subSystemList;
                for (int p = 0; p < phases.Length; p++)
                {
                    string phase = phases[p].type == null ? "?" : phases[p].type.Name;
                    PlayerLoopSystem[] steps = phases[p].subSystemList ?? new PlayerLoopSystem[0];
                    var list = new List<PlayerLoopSystem>();
                    foreach (PlayerLoopSystem step in steps)
                    {
                        list.Add(Marker(phase + "." + (step.type == null ? "?" : step.type.Name)));
                        list.Add(step);
                    }
                    list.Add(Marker(phase + ".(end)"));
                    phases[p].subSystemList = list.ToArray();
                }
                root.subSystemList = phases;
                ticks = new long[labels.Count];
                PlayerLoop.SetPlayerLoop(root);
                SceneManager.sceneLoaded += (scene, mode) => Event("loaded " + scene.name + " (" + mode + ")");
                SceneManager.sceneUnloaded += scene => Event("unloaded " + scene.name);
                SceneManager.activeSceneChanged += (from, to) => Event("active " + to.name);
                Debug.Log("CROW_LOAD trace on: frames of " + ms + " ms or more, " + labels.Count + " markers");
            }

            static PlayerLoopSystem Marker(string label)
            {
                int index = labels.Count;
                labels.Add(label);
                return new PlayerLoopSystem { type = typeof(Loading), updateDelegate = () => Mark(index) };
            }

            // A scene event: the step it happens in and how far into the frame.
            static void Event(string what)
            {
                long now = Stopwatch.GetTimestamp();
                if (what.StartsWith("loaded ")) loading = what.Substring(7);
                Debug.Log("CROW_LOAD " + what + " at " + Time.realtimeSinceStartup.ToString("F2") + " s, frame " + frame +
                          " +" + ((now - frameStart) * Ms).ToString("F0") + " ms in " + (previous >= 0 ? labels[previous] : "?") +
                          " (" + (previous >= 0 ? ((now - last) * Ms).ToString("F0") : "?") + " ms into the step)");
            }

            static void Mark(int index)
            {
                long now = Stopwatch.GetTimestamp();
                if (previous >= 0) ticks[previous] += now - last;
                previous = index;
                last = now;
                if (index != 0) return;
                if (frameStart != 0) EndFrame(now - frameStart);
                frameStart = now;
                frame++;
            }

            static void EndFrame(long total)
            {
                if (total >= threshold)
                {
                    var line = new StringBuilder("CROW_LOADFRAME frame=").Append(frame).Append(" ms=").Append((total * Ms).ToString("F0"));
                    line.Append(" scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
                    foreach (int i in Enumerable.Range(0, ticks.Length).OrderByDescending(i => ticks[i]).Take(6))
                    {
                        double ms = ticks[i] * Ms;
                        if (ms < 5) break;
                        line.Append(' ').Append(labels[i]).Append('=').Append(ms.ToString("F0"));
                    }
                    Debug.Log(line.ToString());
                    loadTicks += total;
                    loadFrames++;
                    quietFrames = 0;
                }
                else if (loadFrames > 0 && ++quietFrames >= 60)
                {
                    Debug.Log("CROW_LOADDONE " + (loading ?? "?").Replace(' ', '_') + " long_frames=" + loadFrames + " long_ms=" +
                              (loadTicks * Ms).ToString("F0") + " at " + Time.realtimeSinceStartup.ToString("F2") + " s");
                    loadFrames = 0;
                    loadTicks = 0;
                    loading = null;
                }
                Array.Clear(ticks, 0, ticks.Length);
            }
        }

        // -crow-mcs-time: what MeshCombineStudio's run-time combining costs. Its combiners combine in
        // Start, all at once on the main thread (the game's job settings: CombineAtOnce, one thread);
        // sceneLoaded comes before Start, so they combine here instead, the same way (the job manager,
        // the first combiner's job settings, CombineAll), one by one under a stopwatch.
        static class CombineTimer
        {
            static Type combinerType, managerType;
            static FieldInfo instancesField, inRuntimeField, onStartField, jobSettingsField, managerField;
            static MethodInfo initManager, combineAll, setJobMode;

            public static void Install()
            {
                combinerType = Type.GetType("MeshCombineStudio.MeshCombiner, MeshCombineStudio.Runtime");
                managerType = Type.GetType("MeshCombineStudio.MeshCombineJobManager, MeshCombineStudio.Runtime");
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic;
                if (combinerType == null || managerType == null)
                {
                    Debug.Log("CROW_MCS no MeshCombineStudio");
                    return;
                }
                instancesField = combinerType.GetField("instances", any | BindingFlags.Static);
                inRuntimeField = combinerType.GetField("combineInRuntime", any | BindingFlags.Instance);
                onStartField = combinerType.GetField("combineOnStart", any | BindingFlags.Instance);
                jobSettingsField = combinerType.GetField("jobSettings", any | BindingFlags.Instance);
                managerField = managerType.GetField("instance", any | BindingFlags.Static);
                initManager = combinerType.GetMethod("InitMeshCombineJobManager", any | BindingFlags.Instance);
                combineAll = combinerType.GetMethod("CombineAll", any | BindingFlags.Instance);
                setJobMode = managerType.GetMethod("SetJobMode", any | BindingFlags.Instance);
                if (instancesField == null || inRuntimeField == null || onStartField == null || jobSettingsField == null ||
                    managerField == null || initManager == null || combineAll == null || setJobMode == null)
                {
                    Debug.Log("CROW_MCS MeshCombineStudio's members not found");
                    return;
                }
                SceneManager.sceneLoaded += (scene, mode) => CombineNow(scene);
            }

            static void CombineNow(Scene scene)
            {
                var instances = instancesField.GetValue(null) as System.Collections.IList;
                if (instances == null) return;
                var all = Stopwatch.StartNew();
                int count = 0;
                bool first = true;
                foreach (object instance in instances.Cast<object>().ToArray())
                {
                    var behaviour = instance as Behaviour;
                    if (behaviour == null || behaviour.gameObject.scene != scene || !behaviour.isActiveAndEnabled) continue;
                    if (!(bool)inRuntimeField.GetValue(instance) || !(bool)onStartField.GetValue(instance)) continue;
                    inRuntimeField.SetValue(instance, false);  // its Start then does nothing
                    var one = Stopwatch.StartNew();
                    try
                    {
                        initManager.Invoke(instance, null);
                        if (first) setJobMode.Invoke(managerField.GetValue(null), new[] { jobSettingsField.GetValue(instance) });
                        first = false;
                        combineAll.Invoke(instance, new object[] { true });
                    }
                    catch (Exception exception)
                    {
                        Debug.Log("CROW_MCS " + behaviour.name + " failed: " + exception);
                    }
                    Debug.Log("CROW_MCS " + behaviour.name.Replace(' ', '_') + " ms=" + one.ElapsedMilliseconds);
                    count++;
                }
                Debug.Log("CROW_MCS scene=" + scene.name.Replace(' ', '_') + " combiners=" + count + " ms=" + all.ElapsedMilliseconds);
            }
        }
    }

    // The player prefab (CharacterCameraPrefab: the player, the camera and every menu, 10,000
    // GameObjects and 8,000 behaviours, most of them PlayMaker FSMs) as a scene of its own. The game
    // loads the prefab through Addressables and MaraLoader.InstantiateMara clones it in the first
    // area it loads; the prefab stayed in memory for good (it was among the player's preloaded
    // assets, read before the first scene), and the clone (each behaviour written out and read
    // back, each FSM's actions made again) was a third of the first area load at the PS Vita's
    // speed. The build (DowngradeBuild) saves an inactive instance in the scene CrowMara instead;
    // when the title has loaded, it loads in the background (the reading, and PlayMaker's action
    // loading, on the loading thread), and the instance is kept across loads, inactive, until
    // InstantiateMara takes it (the rewriter sends its Object.Instantiate call here). An area load
    // that comes first waits for it, as Unity finishes pending loads before a synchronous one.
    // Builds without the scene (test builds without it, desktop players) keep the clone.
    public static class MaraScene
    {
        public const string Scene = "CrowMara", Key = "CharacterCameraPrefab";
        static bool available, requested, taken;
        static GameObject ready;

        // Whether the player's scene is done loading, or not in the build. Diag's -crow-scene waits for
        // it: a synchronous area load while it still loaded in the background hung the PS Vita player.
        public static bool Settled
        {
            get { return !available || taken; }
        }
        static readonly Stopwatch clock = new Stopwatch();

        internal static void Install()
        {
            available = Application.CanStreamedLevelBeLoaded(Scene);
            if (!available) return;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (scene.name == Scene) Take(scene);
                else if (scene.name == "Title" && !requested) RequestSoon();
            };
        }

        // Whether CrowCompat's Addressables should leave this key to the scene (no prefab in the build).
        public static bool Serves(object key)
        {
            return available && Key.Equals(key as string);
        }

        // A second after the title's first frames: a load started from sceneLoaded joins the
        // synchronous load that is still finishing (Unity completes every queued load before it
        // returns), and the title would wait for the player's scene too.
        static float requestAt = -1f;

        static void RequestSoon()
        {
            requestAt = Time.realtimeSinceStartup + 1f;
            Canvas.willRenderCanvases += Poll;
        }

        static void Poll()
        {
            if (requested) { Canvas.willRenderCanvases -= Poll; return; }
            if (Time.realtimeSinceStartup < requestAt) return;
            Canvas.willRenderCanvases -= Poll;
            Request();
        }

        // Start loading the scene in the background (Diag's -crow-scene does before its load).
        public static void Request()
        {
            if (!available || requested) return;
            requested = true;
            clock.Start();
            // The loading thread at high priority: at Normal it got so little of the PS Vita's CPU
            // beside the title's frames that the scene took 133 s (27 s when waited for), and a
            // synchronous load before the end (a Resources.Load in the title's menus) waits for the rest.
            // With the loading thread on CPU 1 (Audio.cs's Threads.MoveLoader), Normal: below the render
            // worker there, so the title keeps 30 fps, and the scene took 28.7 s instead of 25.5.
            Application.backgroundLoadingPriority = Audio.LoadPriority(Threads.LoaderMoved ? ThreadPriority.Normal : ThreadPriority.High);  // -crow-load-priority
            SceneManager.LoadSceneAsync(Scene, LoadSceneMode.Additive);
            Debug.Log("CROW_MARA loading " + Scene + " in the background");
        }

        static void Take(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            ready = roots.Length > 0 ? roots[0] : null;
            taken = true;
            if (ready != null) UnityEngine.Object.DontDestroyOnLoad(ready);
            Application.backgroundLoadingPriority = ThreadPriority.Normal;
            // The scene, empty now, goes with the next single-mode load (unloading it from its own
            // sceneLoaded would wait on the load that is calling it).
            Debug.Log("CROW_MARA " + (ready != null ? ready.name : "no instance") + " ready after " + clock.ElapsedMilliseconds + " ms");
        }

        // MaraLoader.InstantiateMara's Object.Instantiate(original, parent).
        public static GameObject Instantiate(GameObject original, Transform parent)
        {
            if (ready == null)
            {
                if (original != null) return UnityEngine.Object.Instantiate(original, parent);
                Debug.LogError("CROW_MARA no player instance: " + Scene + " was not loaded before the first area");
                return null;
            }
            GameObject instance = ready;
            ready = null;
            if (parent != null) instance.transform.SetParent(parent, false);
            return instance;
        }
    }

    // The title, read while the logos play. The logos scene's FSM loads the title with PlayMaker's
    // LoadScene action, a synchronous load: on the PS Vita the loading thread then spent 11 s reading
    // the title (176 FSMs, their actions made on that thread as each FSM is read) while the logos
    // stood still. The title now starts loading in the background, held before activation, at the
    // logos' first frame; the action's SceneManager.LoadScene call comes here (the rewriter sends
    // it) and activates it. Any other load activates it first, as a load queued behind a held one
    // would never start. Not with -crow-scene, which loads its own scene from the logos.
    // Not from Install (RuntimeInitializeOnLoadMethod AfterSceneLoad): the first scene's load is
    // still finishing there, and Unity completes every queued load before it returns, held or not
    // (see MaraScene.RequestSoon). Requested there, the title was read and activated before the
    // logos' first frame: the logos never showed (on the PS Vita, 10.5 s of the boot picture, then
    // the title), the logos' FSMs were destroyed before they started, and one of them, starting from
    // its OnDisable after the logos' Rewired Input Manager had gone, threw "Rewired is not initialized"
    // and a NullReferenceException (RewiredGetJoystickCount).
    public static class ScenePrefetch
    {
        const string First = "Title";
        static AsyncOperation held;
        static string heldName;
        static bool waiting;  // installed, the request not made yet
        static readonly Stopwatch clock = new Stopwatch();

        internal static void Install(string[] args)
        {
            if (args.Contains("-crow-scene") || args.Contains("-crow-no-prefetch")) return;
            if (SceneManager.GetActiveScene().buildIndex != 0 || !Application.CanStreamedLevelBeLoaded(First)) return;
            waiting = true;
            Canvas.willRenderCanvases += Request;  // every frame, canvases or not
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (scene.name != First || clock.ElapsedMilliseconds == 0) return;
                held = null;  // activated: nothing left to hold
                Application.backgroundLoadingPriority = ThreadPriority.Normal;
                Debug.Log("CROW_PREFETCH " + First + " loaded " + clock.ElapsedMilliseconds + " ms after the request");
                clock.Reset();
            };
        }

        static void Request()
        {
            Canvas.willRenderCanvases -= Request;
            if (!waiting) return;
            waiting = false;
            heldName = First;
            held = SceneManager.LoadSceneAsync(First, LoadSceneMode.Single);
            held.allowSceneActivation = false;
            clock.Start();
            Debug.Log("CROW_PREFETCH reading " + First + " in the background");
        }

        // PlayMaker's LoadScene action's SceneManager.LoadScene(name, mode).
        public static void LoadScene(string name, LoadSceneMode mode)
        {
            waiting = false;  // a load before the logos' first frame: no prefetch
            if (held != null)
            {
                AsyncOperation operation = held;
                bool same = name == heldName && mode == LoadSceneMode.Single;
                held = null;
                Debug.Log("CROW_PREFETCH " + heldName + " activated (" + (operation.progress * 100).ToString("F0") + "% read, " +
                          clock.ElapsedMilliseconds + " ms after the request)" + (same ? "" : " before " + name));
                Application.backgroundLoadingPriority = ThreadPriority.High;  // integrate in big steps, as a synchronous load does
                operation.allowSceneActivation = true;
                if (same) return;
            }
            // A load from the title before the player's scene was requested (a second after the title
            // appeared): request it first, so Unity finishes it before this load and MaraLoader finds it.
            if (name != First) MaraScene.Request();
            if (SceneGap.Load(name, mode)) return;
            SceneManager.LoadScene(name, mode);
        }
    }

    // Area loads by way of an empty scene, on the PS Vita. A single-mode load reads the whole new area
    // (its objects, assets and managed objects, PlayMaker's FSMs) while the old one is still loaded, and
    // unloads the old one only then: the peak is both areas at once, and the garbage collector's heap,
    // which never gives memory back, keeps the managed part of that peak for good. Here the load first
    // makes an empty scene (CrowGap) the active one and unloads the others, then frees their assets
    // (Resources.UnloadUnusedAssets) and collects, and only then loads the area, so the peak is the
    // larger of the two. Textures matter most: the old area's fill the video memory (CDRAM), and the new
    // area's then go to main memory and stay there. Station Square from the title: 25 MB of main
    // memory free after the load this way, 5 MB without. Costs about 3 s on the PS Vita (2.4 s of
    // unused-asset pass, 0.6 s of collection). The player (DontDestroyOnLoad) stays. On the PS Vita
    // unless -crow-no-gap; on other players with -crow-gap. CROW_GAP lines time the steps.
    public static class SceneGap
    {
        public const string Scene = "CrowGap";
        static bool installed, on;
        static string target;
        static int pending;
        static readonly Stopwatch clock = new Stopwatch();

        static bool On
        {
            get
            {
                if (installed) return on;
                installed = true;
                string[] args = ProbeFiles.Args;
                on = Application.platform == RuntimePlatform.PSP2 ? !args.Contains("-crow-no-gap") : args.Contains("-crow-gap");
                return on;
            }
        }

        // Takes a single-mode load from an area or the title (not from the logos, the first load).
        public static bool Load(string name, LoadSceneMode mode)
        {
            if (!On || mode != LoadSceneMode.Single) return false;
            // A name no build scene has (a save that failed to load gave ""): Unity's own load only logs
            // it and the game stays where it is; through the gap it would unload the area and the player
            // first and leave an empty scene, a black screen for good.
            if (!Application.CanStreamedLevelBeLoaded(name)) return false;
            if (target != null)
            {
                Debug.Log("CROW_GAP " + name + " requested while loading " + target + ": loads instead");
                target = name;
                return true;
            }
            Scene active = SceneManager.GetActiveScene();
            if (active.buildIndex == 0) return false;
            target = name;
            clock.Reset();
            clock.Start();
            Scene gap = SceneManager.GetSceneByName(Scene);  // a load right after the last gap's, before it went
            if (!gap.IsValid()) gap = SceneManager.CreateScene(Scene);
            SceneManager.SetActiveScene(gap);
            var scenes = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene != gap && scene.isLoaded) scenes.Add(scene);
            }
            pending = 0;
            foreach (Scene scene in scenes)
            {
                AsyncOperation unload = SceneManager.UnloadSceneAsync(scene);
                if (unload == null) continue;
                pending++;
                unload.completed += Unloaded;
            }
            Debug.Log("CROW_GAP " + name + ": unloading " + string.Join(", ", scenes.Select(s => s.name).ToArray()) + " first");
            if (pending == 0) Unloaded(null);
            return true;
        }

        // Steps on the operations' completed events (CrowCompat can't add a MonoBehaviour to run them
        // without a build).
        static void Unloaded(AsyncOperation operation)
        {
            if (operation != null && --pending > 0) return;
            Debug.Log("CROW_GAP scenes unloaded after " + clock.ElapsedMilliseconds + " ms");
            Resources.UnloadUnusedAssets().completed += AssetsUnloaded;
        }

        static void AssetsUnloaded(AsyncOperation operation)
        {
            long assets = clock.ElapsedMilliseconds;
            GC.Collect();
            string name = target;
            target = null;
            Debug.Log("CROW_GAP assets unloaded after " + assets + " ms, collected after " + clock.ElapsedMilliseconds + " ms; loading " + name);
            SceneManager.LoadScene(name, LoadSceneMode.Single);
        }
    }

    // typeof(T) as a static field: the rewriter sends PlayMaker's typeof()s here (CacheTypeOf).
    public static class TypeOf<T>
    {
        public static readonly Type Type = typeof(T);
    }
}
