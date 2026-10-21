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
//   -crow-load-cpu [ms]     through each area load, every ms (default 250), each CPU's load and each
//                           game thread's share (CROW_LOADCPU, from a thread of its own; with
//                           -crow-gc-log the collections in the window, with -crow-load-stats the sums)
//   -crow-load-stats        PlayMaker's load-time methods timed per thread (CROW_LOADSTATS after each
//                           area load): OnAfterDeserialize, InitData, LoadActions, CreateAction, ...
//   -crow-read-test <files> read build files at startup and log the card's MB/s (CROW_READ)
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
            if (args.Contains("-crow-load-stats")) LoadStats.Install();
            string read = CrowFlags.Value(args, "-crow-read-test");
            if (read != null) ReadTest(read);
            if (args.Contains("-crow-load-cpu"))
            {
                if (args.Contains("-crow-audio")) Debug.Log("CROW_LOADCPU off: -crow-audio reads the threads' shares (one reader at a time)");
                else LoadCpu.Install(CrowFlags.Int(args, "-crow-load-cpu", 250));
            }
            MaraScene.Install();
            ScenePrefetch.Install(args);
            SceneGap.Install(args);
        }

        static class Trace
        {
            static List<string> labels;
            static long[] ticks;
            static long last, frameStart, threshold, loadTicks;
            static int previous = -1, frame, loadFrames, quietFrames;
            static string loading;
            static readonly double Ms = 1000.0 / Stopwatch.Frequency;

            public static void Install(float ms)
            {
                threshold = (long)(ms / Ms);
                // LoopProfile's markers (one before every step of the player loop and one at each phase's end),
                // not a loop of its own: one SetPlayerLoop would drop the other's (Quality.cs, LoopProfile.OnMark).
                if (!LoopProfile.Installed) LoopProfile.Install(1e9f);  // no reports of its own
                labels = LoopProfile.Labels;
                ticks = new long[labels.Count];
                LoopProfile.OnMark += Mark;
                CrowEvents.Loaded("Trace.sceneLoaded", (scene, mode) => Event("loaded " + scene.name + " (" + mode + ")"));
                SceneManager.sceneUnloaded += scene => Event("unloaded " + scene.name);
                SceneManager.activeSceneChanged += (from, to) => Event("active " + to.name);
                Debug.Log("CROW_LOAD trace on: frames of " + ms + " ms or more, " + labels.Count + " markers");
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

        // -crow-read-test <files>: how fast the card reads the build's data. On a thread of its own at
        // startup, each file (comma-separated, under Application.dataPath, e.g. level5,sharedassets5.assets)
        // read whole, in 1 MB blocks and then in 64 KB blocks (CROW_READ lines: MB, ms, MB/s).
        static void ReadTest(string files)
        {
            string root = Application.dataPath;
            new System.Threading.Thread(() =>
            {
                foreach (int block in new[] { 1 << 20, 64 << 10 })
                    foreach (string file in files.Split(','))
                    {
                        string path = root + "/" + file;
                        try
                        {
                            var buffer = new byte[block];
                            var watch = Stopwatch.StartNew();
                            long total = 0;
                            using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read, 4096))
                            {
                                int n;
                                while ((n = stream.Read(buffer, 0, block)) > 0) total += n;
                            }
                            double ms = watch.Elapsed.TotalMilliseconds;
                            Debug.Log("CROW_READ " + file + " block=" + (block >> 10) + "KB MB=" + (total / 1048576.0).ToString("F1") + " ms=" + ms.ToString("F0") +
                                      " MB/s=" + (total / 1048576.0 / (ms / 1000.0)).ToString("F1"));
                        }
                        catch (Exception e)
                        {
                            Debug.Log("CROW_READ " + path + " failed: " + e.GetType().Name + " " + e.Message);
                        }
                    }
            }) { IsBackground = true, Name = "CrowReadTest" }.Start();
        }

        // -crow-load-cpu [ms]: through each area load (from the gap's first step, or any scene load,
        // to 8 s after the area's sceneLoaded), every ms (default 250) a CROW_LOADCPU line from a thread
        // of its own: each CPU's load and each game thread's share of a CPU over the window (the main
        // thread, UnityPreload, the render worker, FMOD's), from the kernel (Audio.cs's Threads).
        // The main thread busy with UnityPreload idle is integration (Awake, OnEnable) or the
        // unused-asset pass; UnityPreload busy is reading and deserializing. A window with no line is a
        // garbage collection (it stops this thread too). Not with -crow-audio (one reader of the shares).
        internal static class LoadCpu
        {
            static readonly Stopwatch clock = Stopwatch.StartNew();
            static long begun = -1, endAt = -1;
            static int seenRuns;

            internal static void Install(int ms)
            {
                if (!Threads.Start())
                {
                    Debug.Log("CROW_LOADCPU no thread accounting on this player");
                    return;
                }
                CrowEvents.Loaded("LoadCpu.sceneLoaded", (scene, mode) =>
                {
                    if (scene.name == SceneGap.Scene || scene.name == MaraScene.Scene) return;
                    Begin();
                    endAt = clock.ElapsedMilliseconds + 8000;
                });
                new System.Threading.Thread(() => Run(Math.Max(20, ms))) { IsBackground = true, Name = "CrowLoadCpu" }.Start();
                Debug.Log("CROW_LOADCPU every " + ms + " ms through loads");
            }

            internal static void Begin()
            {
                if (begun < 0) begun = clock.ElapsedMilliseconds;
                endAt = long.MaxValue;
            }

            static void Run(int ms)
            {
                var line = new StringBuilder();
                bool was = false;
                while (true)
                {
                    System.Threading.Thread.Sleep(ms);
                    long now = clock.ElapsedMilliseconds;
                    bool on = begun >= 0 && now < endAt;
                    if (!on)
                    {
                        if (was) begun = -1;
                        was = false;
                        continue;
                    }
                    if (!was)
                    {
                        Threads.Shares();  // start the window here
                        seenRuns = Alloc.RunsEnded;
                    }
                    was = true;
                    line.Length = 0;
                    line.Append("CROW_LOADCPU +").Append(now - begun).Append(" ms");
                    int runs;
                    double paused = Alloc.PausesSince(seenRuns, out runs);
                    seenRuns += runs;
                    if (runs > 0) line.Append(" gc=").Append(runs).Append('x').Append(paused.ToString("F0")).Append("ms");
                    Threads.AppendCpu(line);
                    line.Append(' ').Append(Threads.Shares());
                    if (LoadStats.On) line.Append(" | ").Append(LoadStats.Line());
                    Debug.Log(line.ToString());
                }
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
                CrowEvents.Loaded("CombineTimer.sceneLoaded", (scene, mode) => CombineNow(scene));
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
            CrowEvents.Loaded("MaraScene.sceneLoaded", (scene, mode) =>
            {
                if (scene.name == Scene) Take(scene);
                else if (scene.name == "Title" && !requested) RequestSoon();
            });
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
            CrowEvents.Canvases("MaraScene.Poll", Poll);
        }

        static void Poll()
        {
            if (requested) { CrowEvents.CanvasesOff(Poll); return; }
            if (Time.realtimeSinceStartup < requestAt) return;
            CrowEvents.CanvasesOff(Poll);
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
            CrowEvents.Canvases("ScenePrefetch.Request", Request);  // every frame, canvases or not
            CrowEvents.Loaded("ScenePrefetch.sceneLoaded", (scene, mode) =>
            {
                if (scene.name != First || clock.ElapsedMilliseconds == 0) return;
                held = null;  // activated: nothing left to hold
                Application.backgroundLoadingPriority = ThreadPriority.Normal;
                Debug.Log("CROW_PREFETCH " + First + " loaded " + clock.ElapsedMilliseconds + " ms after the request");
                clock.Reset();
            });
        }

        static void Request()
        {
            CrowEvents.CanvasesOff(Request);
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
    // From the gap the area loads additively (-crow-gap-single: as a single-mode load, as before). A
    // single-mode load runs Resources.UnloadUnusedAssets and a collection again once the area is in
    // (on the PS Vita 3.2-3.7 s at the end of the load's frame, after sceneLoaded): a second pass over
    // every object, with nothing left to free after the gap's, at the load's peak of memory (both PS
    // Vita crashes of 15-16 Oct, Entrance -> Station Square and Station Square -> Pool, ran out of
    // main memory in that pass, growing its object tables by 3 MB). The area becomes the active scene
    // in the first sceneLoaded handler, before the game's; what its Awake and OnEnable made went into
    // the gap, which was active then, and moves into the area before the gap is unloaded. The areas
    // have no lightmaps or light probes, which an additive load would treat otherwise.
    // Before the old scenes go, the gap drains the renderer (-crow-gap-drain on, the default; off as
    // before): the player's camera draws nothing (culling mask 0) and the old scenes' own cameras and
    // canvases are switched off for two frames, then the main thread waits for the render thread to
    // run out of commands (GetNativeTexturePtr syncs with it), and only then the unloads start; the
    // mask comes back once the old assets are gone. The user's game
    // crashed on the render thread (GfxDeviceGXM::DrawBuffers in SetTextures, from the device worker's
    // queue) while the main thread was destroying Roadside's objects in UnloadSceneOperation, with
    // graphics jobs on: the worker was still drawing the frame before with what the unload freed.
    public static class SceneGap
    {
        public const string Scene = "CrowGap";
        static bool installed, on, additive;
        static string target, arriving;
        static int pending;
        static readonly Stopwatch clock = new Stopwatch();
        static readonly HashSet<int> before = new HashSet<int>();

        internal static void Install(string[] args)
        {
            additive = !args.Contains("-crow-gap-single");
            if (additive) CrowEvents.Loaded("SceneGap.Arrived", Arrived);
        }

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
            Loading.LoadCpu.Begin();  // -crow-load-cpu
            if (Drain)
            {
                StartDrain();
                return true;
            }
            UnloadOld();
            return true;
        }

        // -crow-gap-drain: see above.
        static bool? drain;
        static bool Drain
        {
            get
            {
                if (drain == null) drain = CrowFlags.Value(ProbeFiles.Args, "-crow-gap-drain") != "off";
                return drain.Value;
            }
        }

        static readonly Dictionary<Camera, int> masks = new Dictionary<Camera, int>();
        static int drainFrame = -1, drainCanvases;
        static bool drainHooked;

        static void StartDrain()
        {
            masks.Clear();
            Quiet(Camera.allCameras);
            // The old scenes' own canvases (screen-space overlays draw without a camera); they go with
            // their scenes, so they stay off.
            int canvases = 0;
            foreach (Canvas canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                Scene scene = canvas.gameObject.scene;
                if (!canvas.enabled || !scene.IsValid() || scene.name == "DontDestroyOnLoad" || scene.name == Scene) continue;
                canvas.enabled = false;
                canvases++;
            }
            drainCanvases = canvases;
            drainFrame = Time.frameCount;
            if (!drainHooked)
            {
                drainHooked = true;
                CrowEvents.BeforeRender("SceneGap.DrainTick", DrainTick);
            }
        }

        static void DrainTick()
        {
            if (drainFrame < 0 || Time.frameCount < drainFrame + 2) return;
            drainFrame = -1;
            // Cameras the game switched on since: they draw nothing either.
            Quiet(Camera.allCameras);
            long start = clock.ElapsedMilliseconds;
            GL.Flush();
            Texture2D.whiteTexture.GetNativeTexturePtr();  // waits for the render thread's queue
            Debug.Log("CROW_GAP drained the renderer after " + clock.ElapsedMilliseconds + " ms (" + (clock.ElapsedMilliseconds - start) + " ms waiting; " +
                      masks.Count + " cameras culling nothing, " + oldCameras + " of the old scenes' cameras and " + drainCanvases + " of their canvases off)");
            oldCameras = 0;
            UnloadOld();
        }

        // A camera of a scene that goes is switched off (its effect's blits read render textures the
        // unload destroys); the player's (DontDestroyOnLoad) keeps rendering, culling nothing.
        static void Quiet(Camera[] cameras)
        {
            foreach (Camera camera in cameras)
            {
                if (masks.ContainsKey(camera)) continue;
                Scene scene = camera.gameObject.scene;
                if (scene.IsValid() && scene.name != "DontDestroyOnLoad" && scene.name != Scene)
                {
                    camera.enabled = false;
                    oldCameras++;
                    continue;
                }
                masks[camera] = MenuCull.OwnMask(camera);  // a menu's culling is the menu's to drop (Cameras.cs)
                camera.cullingMask = 0;
            }
        }

        static int oldCameras;

        static void RestoreMasks()
        {
            foreach (KeyValuePair<Camera, int> pair in masks)
                if (pair.Key != null && pair.Key.cullingMask == 0) pair.Key.cullingMask = pair.Value;
            masks.Clear();
        }

        static void UnloadOld()
        {
            string name = target;
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
            RestoreMasks();
            Prewarm.ProBuilder();  // the inventory's ProBuilder meshes, once (Prewarm.cs)
            GC.Collect();
            string name = target;
            target = null;
            Debug.Log("CROW_GAP assets unloaded after " + assets + " ms, collected after " + clock.ElapsedMilliseconds + " ms; loading " + name +
                      (additive ? " additively" : ""));
            if (!additive)
            {
                SceneManager.LoadScene(name, LoadSceneMode.Single);
                return;
            }
            before.Clear();
            Scene gap = SceneManager.GetSceneByName(Scene);
            if (gap.IsValid())
                foreach (GameObject root in gap.GetRootGameObjects()) before.Add(root.GetInstanceID());
            arriving = name;
            SceneManager.LoadScene(name, LoadSceneMode.Additive);
        }

        // The area in (additive), before the other sceneLoaded handlers (hooked at startup).
        static void Arrived(Scene scene, LoadSceneMode mode)
        {
            if (arriving == null || mode != LoadSceneMode.Additive || !Named(scene, arriving)) return;
            arriving = null;
            SceneManager.SetActiveScene(scene);
            Scene gap = SceneManager.GetSceneByName(Scene);
            int moved = 0;
            if (gap.IsValid() && gap != scene)
            {
                foreach (GameObject root in gap.GetRootGameObjects())
                {
                    if (before.Contains(root.GetInstanceID())) continue;
                    SceneManager.MoveGameObjectToScene(root, scene);
                    moved++;
                }
                SceneManager.UnloadSceneAsync(gap);
            }
            Debug.Log("CROW_GAP " + scene.name + " in after " + clock.ElapsedMilliseconds + " ms (additive, " + moved + " objects its load made moved in from the gap)");
        }

        // A load names its scene as LoadScene takes it: by name ("Corridor S") or by path without "Assets/" and
        // ".unity", as the saves keep it ("Scenes/Prefab/areas/Corridors/Save"); Scene.name is only the name. Until
        // 21 Oct a save's load never matched: the gap stayed the active scene (its lighting settings: no fog) and
        // stayed loaded until the next area change.
        static bool Named(Scene scene, string name)
        {
            if (scene.name == name) return true;
            string path = scene.path;
            return path == name || path == "Assets/" + name + ".unity";
        }
    }

    // -crow-load-stats: PlayMaker's load-time methods timed (the rewriter's TimeLoadMethods,
    // rewriter/LoadStats.cs, wraps each in Start and Stop), summed per method and per thread (the
    // main thread or another: OnAfterDeserialize runs on the loading thread while a scene is read).
    // Loading's -crow-load-cpu lines carry the sums so far; a CROW_LOADSTATS line follows each area load.
    public static class LoadStats
    {
        public static readonly string[] Names = { "fsmDeserialize", "fsmAwake", "fsmEnable", "fsmStart", "initData", "loadActions", "fsmInit", "preprocess", "createAction", "loadField" };
        static readonly long[] ticks = new long[32];
        static readonly int[] counts = new int[32];
        static volatile bool on;
        static System.Threading.Thread main;

        internal static bool On { get { return on; } }

        internal static void Install()
        {
            main = System.Threading.Thread.CurrentThread;
            on = true;
            CrowEvents.Loaded("LoadStats.sceneLoaded", (scene, mode) =>
            {
                if (scene.name == SceneGap.Scene) return;
                Debug.Log("CROW_LOADSTATS " + scene.name.Replace(' ', '_') + " " + Line());
                Array.Clear(ticks, 0, ticks.Length);
                Array.Clear(counts, 0, counts.Length);
            });
        }

        public static long Start()
        {
            return on ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void Stop(long start, int slot)
        {
            if (start == 0L) return;
            if (System.Threading.Thread.CurrentThread != main) slot += 16;
            System.Threading.Interlocked.Add(ref ticks[slot], Stopwatch.GetTimestamp() - start);
            System.Threading.Interlocked.Increment(ref counts[slot]);
        }

        // "name=ms/calls" for each slot used, the loading thread's with an @ (fsmDeserialize@=...).
        internal static string Line()
        {
            var line = new StringBuilder();
            for (int i = 0; i < 32; i++)
            {
                if (counts[i] == 0 || (i & 15) >= Names.Length) continue;
                if (line.Length > 0) line.Append(' ');
                line.Append(Names[i & 15]).Append(i >= 16 ? "@" : "").Append('=')
                    .Append((ticks[i] * 1000.0 / Stopwatch.Frequency).ToString("F0")).Append('/').Append(counts[i]);
            }
            return line.ToString();
        }
    }

    // For PlayMaker's ActionData, whose serialized lists the rewriter makes arrays (rewriter/
    // PlayMakerData.cs): one shared empty array per element type (the constructor's initial values, and
    // what LoadActions puts in place of each empty array the serializer made), and List<T>.Contains.
    public static class EmptyArray<T>
    {
        public static readonly T[] Value = new T[0];
    }

    public static class ArrayHelper
    {
        public static bool Contains<T>(T[] array, T item)
        {
            if (array == null) throw new NullReferenceException();
            return Array.IndexOf(array, item) >= 0;
        }
    }

    // typeof(T) as a static field: the rewriter sends PlayMaker's typeof()s here (CacheTypeOf).
    public static class TypeOf<T>
    {
        public static readonly Type Type = typeof(T);
    }
}
