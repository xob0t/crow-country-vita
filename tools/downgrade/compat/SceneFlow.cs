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

// The scenes as the game goes through them: the player's scene loaded beside the title (MaraScene), the scenes
// that aren't areas (Scenes), the title read while the logos play (ScenePrefetch) and the empty scene between two
// areas (SceneGap). Their flags are listed in Loading.cs.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
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
        // The player's instance by name: the build (DowngradeBuild) names the instance it saves in CrowMara as
        // Instantiate names a clone, so builds with and without the scene find it alike.
        public const string PlayerName = Key + "(Clone)";
        // Parts of the player's prefab the modules look for by name: Mara's body, the camera rig (a root of its
        // own with RigPark), and the menus' canvases (a root of their own on the PS Vita, PlayerSplit).
        internal const string Body = "character RIGIDBODY", Rig = "camera follow player", Canvases = "UI Canvases";
        // Unity's UI layer, where the player's menus' 3D objects (portrait, equipped gun, item, monitor) and lights are.
        internal const int UiLayer = 5;

        // The player's instance while it is active (GameObject.Find: null while it is held inactive before the
        // first area takes it, or switched off by CharacterAndCamera's Deactivate).
        internal static GameObject FindPlayer()
        {
            return GameObject.Find(PlayerName);
        }

        // Mara's body while it is active, or null.
        internal static GameObject FindBody()
        {
            return GameObject.Find(Body);
        }

        // Whether a hierarchy root is the player's prefab (its instance; PlayerSplit's and RigPark's roots have
        // other names).
        internal static bool IsPlayerRoot(GameObject root)
        {
            return root.name.StartsWith(Key, StringComparison.Ordinal);
        }
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
                else if (scene.name == Scenes.Title && !requested) RequestSoon();
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
            // With the loading thread on CPU 1 (Threads.cs' Threads.MoveLoader), Normal: below the render
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

    // The scenes that aren't areas: the logos, the title, the player's scene (MaraScene, loaded beside the
    // title) and the scene gap's empty scene (SceneGap); every other scene counts as an area. One list since
    // 21 Oct: twelve sceneLoaded handlers in eight modules kept their own, and two had drifted (ReverbNull's
    // and Cycle's left out CrowGap; the same there, as the gap is made by CreateScene, which raises no
    // sceneLoaded).
    public static class Scenes
    {
        public const string Logos = "Logos", Title = "Title";
        // The scene Unity puts DontDestroyOnLoad objects in (the player's instance, once an area takes it).
        internal const string Kept = "DontDestroyOnLoad";

        internal static bool IsArea(Scene scene)
        {
            string name = scene.name;
            return name != Logos && name != Title && name != MaraScene.Scene && name != SceneGap.Scene;
        }
    }

    // The title, read while the logos play. The logos scene's FSM loads the title with PlayMaker's
    // LoadScene action, a synchronous load: on the PS Vita the loading thread then spent 11 s reading
    // the title (176 FSMs, their actions made on that thread as each FSM is read) while the logos
    // stood still. The title now starts loading in the background, held before activation, at the
    // logos' first frame; the action's SceneManager.LoadScene call comes here (the rewriter sends
    // it) and activates it. Any other load activates it first, as a load queued behind a held one
    // would never start. -crow-scene runs prefetch it too (Diag loads their scene once the title is
    // up), so they boot as play does; -crow-no-prefetch leaves the title to the logos' synchronous load.
    // Not from Install (RuntimeInitializeOnLoadMethod AfterSceneLoad): the first scene's load is
    // still finishing there, and Unity completes every queued load before it returns, held or not
    // (see MaraScene.RequestSoon). Requested there, the title was read and activated before the
    // logos' first frame: the logos never showed (on the PS Vita, 10.5 s of the boot picture, then
    // the title), the logos' FSMs were destroyed before they started, and one of them, starting from
    // its OnDisable after the logos' Rewired Input Manager had gone, threw "Rewired is not initialized"
    // and a NullReferenceException (RewiredGetJoystickCount).
    public static class ScenePrefetch
    {
        const string First = Scenes.Title;
        static AsyncOperation held;
        static string heldName;
        static bool waiting;  // installed, the request not made yet
        static readonly Stopwatch clock = new Stopwatch();

        internal static void Install(string[] args)
        {
            if (CrowFlags.Has(args, "-crow-no-prefetch")) return;
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
    // From the gap the area loads additively, not as a single-mode load. A
    // single-mode load runs Resources.UnloadUnusedAssets and a collection again once the area is in
    // (on the PS Vita 3.2-3.7 s at the end of the load's frame, after sceneLoaded): a second pass over
    // every object, with nothing left to free after the gap's, at the load's peak of memory (both PS
    // Vita crashes of 15-16 Oct, Entrance -> Station Square and Station Square -> Pool, ran out of
    // main memory in that pass, growing its object tables by 3 MB). The area becomes the active scene
    // in Arrived, before the game's sceneLoaded handlers but after a dozen of CrowCompat's (Quality's,
    // Memory's, Audio's: CrowEvents.cs), which still see the gap active; what its Awake and OnEnable
    // made went into the gap, which was active then, and moves into the area before the gap is
    // unloaded. The areas
    // have no lightmaps or light probes, which an additive load would treat otherwise.
    // Before the old scenes go, the gap drains the renderer: the player's camera draws nothing (culling
    // mask 0) and the old scenes' own cameras and canvases are switched off for two frames, then the
    // main thread waits for the render thread to run out of commands (GetNativeTexturePtr syncs with
    // it), and only then the unloads start; the mask comes back once the old assets are gone. The user's game
    // crashed on the render thread (GfxDeviceGXM::DrawBuffers in SetTextures, from the device worker's
    // queue) while the main thread was destroying Roadside's objects in UnloadSceneOperation, with
    // graphics jobs on: the worker was still drawing the frame before with what the unload freed.
    public static class SceneGap
    {
        public const string Scene = "CrowGap";
        static bool installed, on;
        static string target, arriving;
        static int pending;
        static readonly Stopwatch clock = new Stopwatch();
        static readonly HashSet<int> before = new HashSet<int>();

        internal static void Install(string[] args)
        {
            if (installed) return;
            on = Enabled(args);
            installed = true;
            Sweep.Install(args);
            CrowEvents.Loaded("SceneGap.Arrived", Arrived);
        }

        // Read again here if a load comes before Install (none does: the logos' first load isn't taken).
        static bool On
        {
            get
            {
                if (!installed) Install(ProbeFiles.Args);
                return on;
            }
        }

        static bool Enabled(string[] args)
        {
            return CrowFlags.Vita ? !CrowFlags.Has(args, "-crow-no-gap") : CrowFlags.Has(args, "-crow-gap");
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
            Sweep.Settle();
            Loading.LoadCpu.Begin();  // -crow-load-cpu
            StartDrain();
            return true;
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
                if (!canvas.enabled || !scene.IsValid() || scene.name == Scenes.Kept || scene.name == Scene) continue;
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
                if (scene.IsValid() && scene.name != Scenes.Kept && scene.name != Scene)
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
            if (Sweep.Needed(target)) Resources.UnloadUnusedAssets().completed += AssetsUnloaded;
            else AssetsUnloaded(null);
        }

        // operation: the unused-asset pass's, null when Sweep left it out.
        static void AssetsUnloaded(AsyncOperation operation)
        {
            long assets = clock.ElapsedMilliseconds;
            RestoreMasks();
            GC.Collect();
            if (operation != null) Sweep.Swept();
            string name = target;
            target = null;
            Debug.Log("CROW_GAP assets " + (operation != null ? "unloaded" : "kept") + " after " + assets + " ms, collected after " +
                      clock.ElapsedMilliseconds + " ms; loading " + name + " additively");
            Sweep.Watch();
            before.Clear();
            Scene gap = SceneManager.GetSceneByName(Scene);
            if (gap.IsValid())
                foreach (GameObject root in gap.GetRootGameObjects()) before.Add(root.GetInstanceID());
            arriving = name;
            SceneManager.LoadScene(name, LoadSceneMode.Additive);
        }

        // The area in (additive), before the game's sceneLoaded handlers (hooked at startup, from Loading.Install,
        // after Quality's, Memory's and Audio's, which run while the gap is still the active scene).
        static void Arrived(Scene scene, LoadSceneMode mode)
        {
            if (arriving == null || mode != LoadSceneMode.Additive || !Named(scene, arriving)) return;
            arriving = null;
            Sweep.Arrived(scene.name);
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

    // The gap's unused-asset pass, only where memory needs it (-crow-gap-sweep auto|on|off: auto on the PS Vita,
    // on elsewhere, off for tests). The pass walks every object and the managed heap, 1.7-2.1 s of every PS Vita
    // load, and by then the old scenes' unload has freed their objects: what it frees is the old area's assets.
    // Kept, the ones the new area shares load no more (Underground SE -> Machine Centre: 2.5 s instead of 6), and
    // the others take free memory beside the new area's load. Unity keeps the memory a load took beyond what it
    // already held: after two passes left out and a load down to 39 MB free, the next loads started 7-14 MB lower
    // than with every pass (30 Oct). So the gap leaves the pass out only where the load stays far from that: the
    // free main memory now, less the most this area's loads took before (from the additive load's start to 10 s
    // after it, Start's allocations included; a first visit always passes), less what Unity holds beyond the last
    // pass's state (the assets a pass would free now, those of passes left out before them too), must stay at
    // ReserveMB or more. The big areas' loads, which take 40-90 MB, always pass. CROW_GAP lines give the numbers.
    static class Sweep
    {
        const float ReserveMB = 48f;
        const int WatchMs = 10000;
        static string mode;
        static readonly Dictionary<string, float> took = new Dictionary<string, float>();
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static float cleanMB = -1f, startMB;
        static volatile float lowestMB;
        static volatile int endMs;
        static volatile bool watching;
        static string area;
        static bool sampling;

        internal static void Install(string[] args)
        {
            mode = CrowFlags.Value(args, "-crow-gap-sweep", "auto", "on");
            if (mode == "auto" && VitaMemory.FreeLPDDR == null) mode = "on";
        }

        static float AllocatedMB()
        {
            return UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576f;
        }

        // Whether the gap runs the pass before loading the area (its name, or its path as the saves give it).
        internal static bool Needed(string name)
        {
            if (mode != "auto") return mode != "off";
            string key = name.Substring(name.LastIndexOf('/') + 1);
            float needMB, freeMB = VitaMemory.FreeMainMB();
            if (cleanMB < 0f || freeMB < 0f || !took.TryGetValue(key, out needMB))
            {
                Debug.Log("CROW_GAP sweeping: " + key + " not loaded before");
                return true;
            }
            float heldMB = Math.Max(0f, AllocatedMB() - cleanMB), lowMB = freeMB - needMB - heldMB;
            bool needed = lowMB < ReserveMB;
            Debug.Log("CROW_GAP " + (needed ? "sweeping" : "not sweeping") + ": " + freeMB.ToString("F0") + " MB free, " + key + " took " +
                      needMB.ToString("F0") + " MB, " + heldMB.ToString("F1") + " MB held since the last sweep: " + lowMB.ToString("F0") +
                      " MB at the lowest");
            return needed;
        }

        // After the pass and the collection: Unity's allocations as the pass leaves them.
        internal static void Swept()
        {
            cleanMB = AllocatedMB();
        }

        // The area's additive load starts: its lowest free memory is watched until 10 s after it arrived.
        internal static void Watch()
        {
            if (mode != "auto") return;
            startMB = VitaMemory.FreeMainMB();
            lowestMB = startMB;
            area = null;
            endMs = int.MaxValue;
            watching = true;
            if (sampling) return;
            sampling = true;
            new System.Threading.Thread(Sample) { IsBackground = true, Name = "CrowGapWatch" }.Start();
        }

        internal static void Arrived(string name)
        {
            if (!watching) return;
            area = name;
            endMs = (int)clock.ElapsedMilliseconds + WatchMs;
        }

        // The next load starts: what the last one took, the most of its visits kept.
        internal static void Settle()
        {
            watching = false;
            if (area == null) return;
            float needMB = startMB - lowestMB, before;
            if (!took.TryGetValue(area, out before) || needMB > before) took[area] = needMB;
            Debug.Log("CROW_GAP " + area + " took " + needMB.ToString("F0") + " MB (" + startMB.ToString("F0") + " MB free at its load, " +
                      lowestMB.ToString("F0") + " at the lowest)");
            area = null;
        }

        static void Sample()
        {
            while (true)
            {
                System.Threading.Thread.Sleep(100);
                if (!watching) continue;
                if (clock.ElapsedMilliseconds > endMs)
                {
                    watching = false;
                    continue;
                }
                float freeMB = VitaMemory.FreeMainMB();
                if (freeMB >= 0f && freeMB < lowestMB) lowestMB = freeMB;
            }
        }
    }
}
