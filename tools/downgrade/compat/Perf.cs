using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// Frame time in play: particle systems, physics and settings tried in turn in one run.
//   -crow-moving-report <s>     every s seconds (CROW_MOVING): the enabled renderers whose transform
//                               changed during 30 frames (what RenderManager.UpdateAllRenderers
//                               updates), by top-level object, and the ones that moved most; and
//                               (CROW_HIERARCHY) each root's transforms (Unity's TransformHierarchy:
//                               every system that follows transform changes walks a changed one), how
//                               many changed in the next frame, and where the changes start (changed
//                               transforms whose parent didn't change), with how many each takes along
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
//                               batch=all (Draws.cs's static batching, now), hide=static|safe|none
//                               (StaticBatch.Hide: its candidates' renderers off, a cost test),
//                               merge=on|off|merged|report (Merge.cs, in the loaded scenes),
//                               sort=default|nodistance|
//                               fronttoback (the cameras' opaque sort), probes=off and reflections=off
//                               (every renderer's light and reflection probe use; the scene's light
//                               probe count is logged), rewired=pad|game (RewiredPad), split=ui
//                               (PlayerSplit, from then on), audio=idle-off (disable the enabled
//                               AudioSources that aren't playing, once: what idle sources cost; a test,
//                               they no longer play), mouse=off (every camera's eventMask 0: no
//                               OnMouse* raycasts in SendMouseEvents),
//                               time=<scale>, 0 to hold a moment for screenshots), with a CROW_LOOP
//                               line (-crow-loop-profile) for each set's window, labelled with the set.
//                               E.g. -crow-cycle 10 gpu=222/gpu=166/resolution=0/resolution=1 with
//                               -crow-loop-profile 1000, so that only the sets' windows report.
//   -crow-rewired game|pad      Rewired's keyboard and mouse controllers on (game) or off (pad, the PS
//                               Vita's default; see RewiredPad)
//   -crow-player-split game|ui  ui (the PS Vita's default) makes the player's menus a hierarchy of their
//                               own (see PlayerSplit)
//   -crow-scaled-colliders game|box|report
//                               box (the PS Vita's default) gives each mesh collider whose scale keeps
//                               changing a box instead (see ScaledColliders); report only logs them
//                               (CROW_SCALED); -crow-cycle takes scaled=
//   -crow-scene-split off|on|check|report
//                               the PS Vita build's split areas (see SceneSplit): off puts the parts
//                               back in their places, on takes them out again (-crow-cycle takes
//                               scenesplit=), check compares each part with its old place every second
//                               (CROW_SPLIT_CHECK), report logs what the split would take (CROW_SPLIT)
//   -crow-split-follow off      the split parts no longer follow their old places when those move (see
//                               SplitFollow); -crow-cycle takes splitfollow=
//   -crow-map-pan on|game       on (the PS Vita's default): the map's scrolled content moves in whole
//                               cells of the UI's pixel grid, and its pixelated images keep their meshes
//                               while it moves (see MapPan); game rebuilds every image at every frame of a
//                               scroll, as the game does; -crow-cycle takes mappan=
//   -crow-map-arrows canvas|game
//                               canvas (the PS Vita's default): the map's bobbing scroll arrows under a
//                               canvas of their own (see MapPan); -crow-cycle takes maparrows=
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    static class Perf
    {
        public static void Install(string[] args)
        {
            RewiredPad.Install(args);
            MapPan.Install(args);
            PlayerSplit.Install(args);
            ScaledColliders.Install(args);
            SceneSplit.Install(args);
            float moving = Quality.Number(args, "-crow-moving-report", -1f);
            if (moving > 0f) MovingReport.Install(moving);
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

        internal static string Value(string[] args, string flag)
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
                case "hide":
                    StaticBatch.Hide(value);
                    return true;
                case "merge":
                    Merge.Set(value);
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
                case "mouse":
                    foreach (Camera camera in Camera.allCameras) camera.eventMask = value == "off" ? 0 : -1;
                    break;
                case "audio":
                {
                    int idle = 0, playing = 0;
                    foreach (AudioSource source in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                    {
                        if (!source.enabled) continue;
                        if (source.isPlaying) playing++;
                        else if (value == "idle-off")
                        {
                            source.enabled = false;
                            idle++;
                        }
                    }
                    Debug.Log("CROW_PERF audio " + value + ": " + idle + " idle sources disabled, " + playing + " playing");
                    return true;
                }
                case "sort":
                    foreach (Camera camera in Camera.allCameras)
                        camera.opaqueSortMode = value == "nodistance" ? UnityEngine.Rendering.OpaqueSortMode.NoDistanceSort
                            : value == "fronttoback" ? UnityEngine.Rendering.OpaqueSortMode.FrontToBack : UnityEngine.Rendering.OpaqueSortMode.Default;
                    break;
                case "probes":
                case "reflections":
                {
                    int changed = 0;
                    foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
                    {
                        if (key == "probes" && renderer.lightProbeUsage != UnityEngine.Rendering.LightProbeUsage.Off)
                        {
                            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                            changed++;
                        }
                        if (key == "reflections" && renderer.reflectionProbeUsage != UnityEngine.Rendering.ReflectionProbeUsage.Off)
                        {
                            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                            changed++;
                        }
                    }
                    LightProbes probes = LightmapSettings.lightProbes;
                    Debug.Log("CROW_PERF " + key + " off on " + changed + " renderers; light probes in the scene: " + (probes == null ? 0 : probes.count) +
                              ", reflection probes: " + UnityEngine.Object.FindObjectsOfType<ReflectionProbe>().Length);
                    return true;
                }
                default:
                    return Cameras.Change(key, value);  // rtcams= (Cameras.cs)
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

    // Rewired's keyboard and mouse controllers, off on the PS Vita, which has neither: Rewired reads
    // them through Unity's input every frame (each mapped key, the mouse's axes and buttons) for
    // nothing. Roadside: Update 7.7 -> 7.2 ms and the main thread's work per frame 39.2 -> 38.4 ms;
    // Driving Intro: Update 4.8 -> 4.45 ms. The pad is a Rewired joystick and doesn't change.
    static class RewiredPad
    {
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        static bool pad, hooked;
        static float next = -1f;
        static System.Reflection.PropertyInfo ready, controllers;

        public static void Install(string[] args)
        {
            string value = Perf.Value(args, "-crow-rewired");
            if (value != null ? value == "pad" : Application.platform == RuntimePlatform.PSP2) Set(true);
        }

        internal static void Set(bool on)
        {
            pad = on;
            next = 0f;
            if (hooked) return;
            hooked = true;
            SceneManager.sceneLoaded += (scene, mode) => next = 0f;
            Application.onBeforeRender += Tick;
        }

        // Once a second until Rewired is ready, and again after each scene load.
        static void Tick()
        {
            if (next < 0f || Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + 1f;
            if (ready == null)
            {
                Type input = Type.GetType("Rewired.ReInput, Rewired_Core");
                if (input == null)
                {
                    next = -1f;
                    return;
                }
                ready = input.GetProperty("isReady", Any | System.Reflection.BindingFlags.Static);
                controllers = input.GetProperty("controllers", Any | System.Reflection.BindingFlags.Static);
                if (ready == null || controllers == null)
                {
                    next = -1f;
                    Debug.Log("CROW_REWIRED no ReInput.isReady or controllers");
                    return;
                }
            }
            if (!(bool)ready.GetValue(null, null)) return;
            next = -1f;
            object helper = controllers.GetValue(null, null);
            var changed = new List<string>();
            foreach (string name in new[] { "Keyboard", "Mouse" })
            {
                System.Reflection.PropertyInfo property = helper == null ? null : helper.GetType().GetProperty(name, Any | System.Reflection.BindingFlags.Instance);
                object controller = property == null ? null : property.GetValue(helper, null);
                System.Reflection.PropertyInfo enabled = controller == null ? null : controller.GetType().GetProperty("enabled", Any | System.Reflection.BindingFlags.Instance);
                if (enabled == null || (bool)enabled.GetValue(controller, null) == !pad) continue;
                enabled.SetValue(controller, !pad, null);
                changed.Add(name);
            }
            if (changed.Count > 0) Debug.Log("CROW_REWIRED " + string.Join(" and ", changed.ToArray()) + (pad ? " off" : " on"));
        }
    }

    // The player's prefab instance (CharacterCameraPrefab, kept across areas) is one transform hierarchy
    // of 10,251 transforms; 6,304 of them are its menus' canvases (UI Canvases: the gameplay UI, pause,
    // map and inventory; screen-space overlays, nearly all inactive). Mara's breathing moves about 560
    // of the others every frame, and each engine system that follows transform changes (renderers,
    // particle systems...) then walks the whole hierarchy. On the PS Vita, UI Canvases becomes a root of
    // its own once the player is kept (in DontDestroyOnLoad), right after the player's root, so the
    // overlay canvases sort as before. A placeholder in its old place (SplitMirror) makes it follow the
    // player's activation (CharacterAndCamera's Activate, Deactivate and Reset) and destruction. No FSM
    // looks children up from the player's root (180 child lookups checked in Roadside), and the game's
    // scripts reach the menus through references. Roadside: UpdateAllRenderers 2.7 -> 1.9 ms,
    // ParticleSystemBeginUpdateAll 1.6 -> 0.8 ms, the main thread's work per frame 40.4 -> 39.5 ms.
    // Checked on the PS Vita: the pause menu, examining, a dialogue, Roadside -> Station Square.
    // Not split: the camera rig (camera_follow_player, 2,890 transforms). With it a root of its own, a
    // Title FSM's Rewired action (RewiredGetJoystickCount) threw on the way back to the title.
    static class PlayerSplit
    {
        static readonly List<string> parts = new List<string>();
        static bool hooked;

        public static void Install(string[] args)
        {
            string value = Perf.Value(args, "-crow-player-split");
            if (value != null) Set(value);
            else if (Application.platform == RuntimePlatform.PSP2) Set("ui");
        }

        internal static void Set(string value)
        {
            foreach (string name in value.Split('+'))
            {
                // Children's names with '_' for a space.
                string part = name == "ui" ? "UI_Canvases" : name == "camera" ? "camera_follow_player" : null;
                if (part != null && !parts.Contains(part)) parts.Add(part);
            }
            if (parts.Count == 0) return;
            if (!hooked)
            {
                hooked = true;
                SceneManager.sceneLoaded += (scene, mode) => Split();
            }
            Split();
        }

        // At each scene load: an area's MaraLoader has activated the player in its Awake by then.
        static void Split()
        {
            GameObject player = GameObject.Find("CharacterCameraPrefab(Clone)");
            Transform root = player == null ? null : player.transform.root;
            if (root == null || root.gameObject.scene.name != "DontDestroyOnLoad") return;
            if (root.position != Vector3.zero || root.rotation != Quaternion.identity || root.lossyScale != Vector3.one)
            {
                Debug.Log("CROW_SPLIT the player's root isn't at the origin; left whole");
                return;
            }
            // The last child first, each made the root after the player's: they keep their order.
            var children = new List<Transform>();
            foreach (Transform child in root)
                if (parts.Contains(child.name.Replace(' ', '_'))) children.Add(child);
            foreach (Transform part in children.OrderByDescending(t => t.GetSiblingIndex()))
            {
                int before = root.hierarchyCount;
                var holder = new GameObject(part.name + " (split)");
                holder.SetActive(false);
                holder.transform.SetParent(root, false);
                holder.transform.SetSiblingIndex(part.GetSiblingIndex());
                var mirror = holder.AddComponent<SplitMirror>();
                mirror.part = part.gameObject;
                mirror.active = part.gameObject.activeSelf;
                part.SetParent(null, true);
                UnityEngine.Object.DontDestroyOnLoad(part.gameObject);
                part.SetSiblingIndex(root.GetSiblingIndex() + 1);
                if (!root.gameObject.activeInHierarchy) part.gameObject.SetActive(false);
                holder.SetActive(true);
                Debug.Log("CROW_SPLIT " + part.name + " a root of its own: the player's hierarchy " + before + " -> " + root.hierarchyCount + " transforms");
            }
        }
    }

    // -crow-moving-report: which renderers move every frame. Once a period, Transform.hasChanged is
    // cleared on every enabled renderer's transform, then checked and cleared at each of the next 30
    // frames' starts (onBeforeRender, after the scripts and animation).
    static class MovingReport
    {
        static float every, last;
        static int frame = -1;
        static Renderer[] renderers;
        static int[] counts;

        public static void Install(float seconds)
        {
            every = seconds;
            Application.onBeforeRender += Tick;
        }

        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (frame < 0)
            {
                if (now - last < every) return;
                last = now;
                Hierarchies(true);
                renderers = UnityEngine.Object.FindObjectsOfType<Renderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy).ToArray();
                counts = new int[renderers.Length];
                foreach (Renderer renderer in renderers) renderer.transform.hasChanged = false;
                frame = 0;
                return;
            }
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                Transform transform = renderers[i].transform;
                if (!transform.hasChanged) continue;
                counts[i]++;
                transform.hasChanged = false;
            }
            if (frame == 0) Hierarchies(false);
            if (++frame < 30) return;
            frame = -1;
            var roots = new Dictionary<string, int[]>();
            int moving = 0, always = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (counts[i] == 0 || renderers[i] == null) continue;
                moving++;
                if (counts[i] == 30) always++;
                string key = renderers[i].transform.root.name.Replace(' ', '_') + "(" + renderers[i].GetType().Name + ")";
                int[] slot;
                if (!roots.TryGetValue(key, out slot)) roots[key] = slot = new int[2];
                slot[0]++;
                slot[1] += counts[i];
            }
            var line = new StringBuilder("CROW_MOVING scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            line.Append(" renderers=").Append(renderers.Length).Append(" moved=").Append(moving).Append(" everyFrame=").Append(always);
            foreach (KeyValuePair<string, int[]> pair in roots.OrderByDescending(p => p.Value[1]).Take(15))
                line.Append(' ').Append(pair.Key).Append('=').Append(pair.Value[0]).Append('x').Append((pair.Value[1] / (float)pair.Value[0]).ToString("F0", CultureInfo.InvariantCulture));
            Debug.Log(line.ToString());
            var examples = Enumerable.Range(0, renderers.Length).Where(i => counts[i] > 0 && renderers[i] != null)
                .OrderByDescending(i => counts[i]).Take(12).Select(i => Perf.Path(renderers[i].transform, 4) + "=" + counts[i]);
            Debug.Log("CROW_MOVING examples " + string.Join(" ", examples.ToArray()));
        }

        static Transform[] all;

        // clear: every transform's hasChanged off; else report what changed since.
        static void Hierarchies(bool clear)
        {
            if (clear)
            {
                all = Resources.FindObjectsOfTypeAll<Transform>().Where(t => t != null && t.gameObject.scene.IsValid()).ToArray();
                foreach (Transform transform in all) transform.hasChanged = false;
                return;
            }
            var roots = new Dictionary<Transform, int[]>();
            var starts = new List<Transform>();
            foreach (Transform transform in all)
            {
                if (transform == null) continue;
                Transform root = transform.root;
                int[] slot;
                if (!roots.TryGetValue(root, out slot)) roots[root] = slot = new int[2];
                slot[0]++;
                if (!transform.hasChanged) continue;
                slot[1]++;
                if (transform.parent == null || !transform.parent.hasChanged) starts.Add(transform);
            }
            var line = new StringBuilder("CROW_HIERARCHY scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            foreach (KeyValuePair<Transform, int[]> pair in roots.OrderByDescending(p => p.Value[1]).ThenByDescending(p => p.Value[0]).Take(8))
                line.Append(' ').Append(pair.Key.name.Replace(' ', '_')).Append('=').Append(pair.Value[0]).Append("/changed").Append(pair.Value[1]);
            Debug.Log(line.ToString());
            var text = new StringBuilder("CROW_HIERARCHY changes start at");
            foreach (Transform start in starts.OrderByDescending(t => t.GetComponentsInChildren<Transform>(true).Length).Take(10))
                text.Append(' ').Append(Perf.Path(start, 6)).Append("(+").Append(start.GetComponentsInChildren<Transform>(true).Length).Append(')');
            text.Append(" and ").Append(Math.Max(0, starts.Count - 10)).Append(" more");
            Debug.Log(text.ToString());
            // The largest root's big subtrees (over 300 transforms), each with how many of its transforms
            // are active and how many changed.
            Transform largest = roots.OrderByDescending(p => p.Value[0]).First().Key;
            var big = new StringBuilder("CROW_HIERARCHY subtrees of ").Append(largest.name.Replace(' ', '_'));
            var queue = new Queue<Transform>();
            queue.Enqueue(largest);
            int listed = 0;
            while (queue.Count > 0 && listed < 40)
            {
                Transform node = queue.Dequeue();
                Transform[] below = node.GetComponentsInChildren<Transform>(true);
                if (below.Length < 300) continue;
                big.Append(' ').Append(Perf.Path(node, 3)).Append('=').Append(below.Length).Append("/active").Append(below.Count(t => t.gameObject.activeInHierarchy))
                   .Append("/changed").Append(below.Count(t => t.hasChanged));
                listed++;
                foreach (Transform child in node) queue.Enqueue(child);
            }
            Debug.Log(big.ToString());
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

    // A mesh collider whose transform's scale changes (its own or a parent's) is cooked again from its
    // mesh by PhysX at the next physics step (PhysicsManager::SyncTransforms -> MeshCollider::ScaleChanged
    // -> ExtractMeshGeometry -> PhysX cooking), every frame for one that animates. Station Square's
    // bunting: 18 ProBuilder flags (PrismPentagon, with ProBuilder's default mesh collider) under
    // swinging and stretching parents, about 12 ms of the PS Vita's main thread a frame in
    // FixedUpdate.PhysicsFixedUpdate (with Physics.autoSyncTransforms on, the same time went to the
    // scripts' first physics query instead). Two seconds after each scene load the loaded scenes'
    // non-trigger mesh colliders are listed (on or off), and then and every 15 s after, those that are
    // on are watched for 40 frames (nothing allocated); each whose shape (the world matrix's axis
    // lengths and angles) changed in 10 of them or more (the flags' sway pauses now and then) gets a
    // BoxCollider of its mesh's bounds on the same GameObject (same layer and rigidbody, same physics
    // material) and is switched off. A box takes a scale change as a shape update,
    // without cooking. Left alone: triggers, and colliders of non-kinematic rigidbodies (their shape
    // drives the simulation).
    static class ScaledColliders
    {
        const int Frames = 40, Often = 10;  // watched frames; changes that count as animated
        static string mode = "game";
        static bool hooked;
        static float next = -1f;
        static int frame = -1;
        static bool rescan;
        static readonly List<MeshCollider> candidates = new List<MeshCollider>();
        static float[] shapes = new float[0];
        static int[] changes = new int[0];
        static bool[] watching = new bool[0];
        static readonly float[] shape = new float[6];
        static readonly Dictionary<MeshCollider, BoxCollider> swapped = new Dictionary<MeshCollider, BoxCollider>();

        public static void Install(string[] args)
        {
            string value = Perf.Value(args, "-crow-scaled-colliders");
            if (value != null) Set(value);
            else if (Application.platform == RuntimePlatform.PSP2) Set("box");
        }

        internal static void Set(string value)
        {
            mode = value;
            if (mode == "game")
            {
                int restored = 0;
                foreach (KeyValuePair<MeshCollider, BoxCollider> pair in swapped)
                {
                    if (pair.Value != null) UnityEngine.Object.Destroy(pair.Value);
                    if (pair.Key == null) continue;
                    pair.Key.enabled = true;
                    restored++;
                }
                swapped.Clear();
                frame = -1;
                Debug.Log("CROW_SCALED game: " + restored + " mesh colliders back");
                return;
            }
            next = Time.realtimeSinceStartup;
            rescan = true;
            if (hooked) return;
            hooked = true;
            SceneManager.sceneLoaded += (scene, loadMode) =>
            {
                next = Time.realtimeSinceStartup + 2f;
                rescan = true;
                frame = -1;
            };
            Application.onBeforeRender += Tick;
        }

        static void Tick()
        {
            if (mode == "game") return;
            if (frame < 0)
            {
                if (next < 0f || Time.realtimeSinceStartup < next) return;
                next = Time.realtimeSinceStartup + 15f;
                if (rescan) List();
                rescan = false;
                // The candidates that are on now; nothing allocated.
                int on = 0;
                for (int i = 0; i < candidates.Count; i++)
                {
                    MeshCollider collider = candidates[i];
                    Rigidbody body = collider == null ? null : collider.attachedRigidbody;
                    watching[i] = collider != null && collider.enabled && collider.gameObject.activeInHierarchy && !collider.isTrigger &&
                                  !swapped.ContainsKey(collider) && (body == null || body.isKinematic);
                    changes[i] = 0;
                    if (!watching[i]) continue;
                    Shape(collider.transform, i);
                    on++;
                }
                if (on > 0) frame = 0;
                return;
            }
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!watching[i] || candidates[i] == null) continue;
                if (Shape(candidates[i].transform, i)) changes[i]++;
            }
            if (++frame < Frames) return;
            frame = -1;
            List<string> found = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                MeshCollider collider = candidates[i];
                if (!watching[i] || collider == null || changes[i] < Often) continue;
                if (found == null) found = new List<string>();
                if (mode == "box") Swap(collider);
                Bounds bounds = collider.enabled ? collider.bounds : swapped[collider].bounds;
                if (found.Count < 12)
                    found.Add(Perf.Path(collider.transform, 5) + "(" + collider.sharedMesh.vertexCount + "v" + (collider.convex ? ",convex" : "") + ",layer" +
                              collider.gameObject.layer + ",y" + bounds.min.y.ToString("F2", CultureInfo.InvariantCulture) + ".." +
                              bounds.max.y.ToString("F2", CultureInfo.InvariantCulture) + ")");
            }
            if (found == null) return;
            // The player's body for comparison: the rigidbody in DontDestroyOnLoad that isn't kinematic.
            string player = "";
            foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
                if (!body.isKinematic && body.gameObject.scene.name == "DontDestroyOnLoad")
                {
                    var bounds = new Bounds(body.position, Vector3.zero);
                    foreach (Collider collider in body.GetComponentsInChildren<Collider>()) if (collider.attachedRigidbody == body) bounds.Encapsulate(collider.bounds);
                    player = " player=" + Perf.Path(body.transform, 2) + "(y" + bounds.min.y.ToString("F2", CultureInfo.InvariantCulture) + ".." +
                             bounds.max.y.ToString("F2", CultureInfo.InvariantCulture) + ")";
                    break;
                }
            Debug.Log("CROW_SCALED scene=" + SceneManager.GetActiveScene().name.Replace(' ', '_') + " candidates=" + candidates.Count +
                      " animated=" + changes.Count(c => c >= Often) + (mode == "box" ? " boxed" : " (report)") + " total=" + swapped.Count + player +
                      " " + string.Join(" ", found.ToArray()));
        }

        // Once per area load: the loaded scenes' non-trigger mesh colliders, on or off (allocates once).
        static void List()
        {
            foreach (MeshCollider gone in swapped.Keys.Where(c => c == null).ToList()) swapped.Remove(gone);
            candidates.Clear();
            foreach (MeshCollider collider in Resources.FindObjectsOfTypeAll<MeshCollider>())
                if (collider.gameObject.scene.IsValid() && !collider.isTrigger && collider.sharedMesh != null) candidates.Add(collider);
            shapes = new float[candidates.Count * 6];
            changes = new int[candidates.Count];
            watching = new bool[candidates.Count];
        }

        // What a scale change alters and a rotation or move doesn't: the axes' squared lengths and dot
        // products. Stores them for candidate i; whether they changed.
        static bool Shape(Transform transform, int i)
        {
            Matrix4x4 m = transform.localToWorldMatrix;
            Vector3 x = m.GetColumn(0), y = m.GetColumn(1), z = m.GetColumn(2);
            shape[0] = x.sqrMagnitude;
            shape[1] = y.sqrMagnitude;
            shape[2] = z.sqrMagnitude;
            shape[3] = Vector3.Dot(x, y);
            shape[4] = Vector3.Dot(x, z);
            shape[5] = Vector3.Dot(y, z);
            // Relative to the largest axis: small props have world scales of a few hundredths.
            float tolerance = 1e-6f * Mathf.Max(shape[0], Mathf.Max(shape[1], shape[2]));
            bool changed = false;
            for (int k = 0; k < 6; k++)
            {
                if (Mathf.Abs(shape[k] - shapes[i * 6 + k]) > tolerance) changed = true;
                shapes[i * 6 + k] = shape[k];
            }
            return changed;
        }

        static void Swap(MeshCollider mesh)
        {
            Bounds bounds = mesh.sharedMesh.bounds;
            BoxCollider box = mesh.gameObject.AddComponent<BoxCollider>();
            box.center = bounds.center;
            box.size = Vector3.Max(bounds.size, new Vector3(0.001f, 0.001f, 0.001f));
            box.sharedMaterial = mesh.sharedMaterial;
            box.contactOffset = mesh.contactOffset;
            mesh.enabled = false;
            swapped[mesh] = box;
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

    // An area is one transform hierarchy of 20,000 transforms or more under its root (Station Square
    // 21,600, Pool 22,400), and a few of its parts move every frame (bunting, crows, flies, an NPC's
    // breathing, the pool's swirl). Each engine system that follows transform changes then walks the
    // whole hierarchy every frame: the renderers' update (PostLateUpdate.UpdateAllRenderers), physics'
    // transform sync (FixedUpdate.PhysicsFixedUpdate) and the particle systems. The PS Vita build takes
    // the parts nothing acts on out to the scene's root as it writes each scene (Merge.Split, called by
    // DowngradeBuild's CrowMergeScenes; CROW_SPLIT=on, the default, or off), each with a SplitPart
    // naming its old parent and place; below objects the game switches on and off, a SplitHolder in
    // the part's place switches it. A first run-time test that took out every subtree that didn't move
    // in 60 frames: main thread Station Square 30.6 -> 26.8 ms, Pool 31.0 -> 24.4 ms.
    //   -crow-scene-split off|on|check|report
    //       off: after each area load, every part goes back where it was (its old parent, place,
    //       sibling index and activity), for A/B runs on one build; on: out again (-crow-cycle takes
    //       scenesplit=off|on, now); check: every second and at each switch of a holder, whether each
    //       part is active exactly when it would be in its old place and where it would be there
    //       (CROW_SPLIT_CHECK, once per part that differs, and a count every 30 s); report: log what
    //       Merge.Split would take out of each area (for builds made without it); apply, apply-check:
    //       split each area as it loads, as the build would (and check); report-movers,
    //       apply-movers, apply-movers-check: the same with Merge.Split's movers (the parts that move on
    //       their own, below split parents, and the way below destroyed objects)
    public class SplitPart : MonoBehaviour
    {
        public Transform parent;
        public int sibling;
        public Vector3 position, scale;
        public Quaternion rotation;
        public bool active;  // its own activity before the split
        public SplitHolder holder;  // its place below a switched object, or none
    }

    // A split part's place below objects the game switches on and off: it stands for the part there
    // (its activeSelf is the part's own, so ActivateChildren on its parent switches it as it would the
    // part), and the part, built switched off, is on exactly while this is. While this is off because
    // of its parents, own keeps the part's own activity; switched off itself, the part is on again
    // when it is.
    public class SplitHolder : MonoBehaviour
    {
        public GameObject part;
        public bool own = true;

        void OnEnable()
        {
            if (part == null) return;
            part.SetActive(own);
            SceneSplit.Switched(this);
        }

        // Destroyed with what is above it (the game's DestroyObject, DestroySelf), the part goes too, as
        // it would have in its old place (the movers' split goes below destroyed objects).
        void OnDestroy()
        {
            if (part != null && gameObject.scene.isLoaded) Destroy(part);
        }

        void OnDisable()
        {
            if (part == null) return;
            // Torn down (its scene unloading, the game quitting) rather than switched off: then nothing
            // here or above is off itself (scene.isLoaded can still be true), and nothing is done.
            bool switchedOff = !gameObject.activeSelf;
            for (Transform t = transform.parent; t != null && !switchedOff; t = t.parent) switchedOff = !t.gameObject.activeSelf;
            if (!switchedOff) return;
            own = !gameObject.activeSelf || part.activeSelf;
            if (part.activeSelf) part.SetActive(false);
            SceneSplit.Switched(this);
        }
    }

    // The split parts' old places followed: in any frame an old parent's world pose changed (an elevator
    // moving the whole level, anything the split's analysis couldn't see), each part taken out from below
    // it is put back where it would be there (its old local pose below the old parent's world pose), so
    // it moves as it did before the split. Checked just before rendering, once per old parent: a matrix
    // read and a comparison. -crow-split-follow off (or -crow-cycle splitfollow=off) leaves the parts where
    // they are, for A/B; CROW_SPLIT_FOLLOW logs each old parent the first time it moves, and a count every
    // 30 s while any did.
    static class SplitFollow
    {
        sealed class Place
        {
            public Transform Old;
            public Matrix4x4 Last;
            public readonly List<SplitPart> Parts = new List<SplitPart>();
            public bool Logged;
            public int Moves;
        }

        static bool on = true, hooked, dirty = true;
        static readonly List<Place> places = new List<Place>();
        static int moves, followed;
        static float summaryAt;

        internal static void Install(string[] args)
        {
            if (Perf.Value(args, "-crow-split-follow") == "off") on = false;
            if (hooked) return;
            hooked = true;
            // Collected in the frame after a load: the split (at run time) and the holders' switching
            // happen while the scene loads.
            SceneManager.sceneLoaded += (scene, mode) => Dirty();
            SceneManager.sceneUnloaded += scene => Dirty();
            Application.onBeforeRender += Tick;
        }

        static int collectFrame;

        // Whether the parts are where following keeps them (not between a load and the next frame's step).
        internal static bool Ready
        {
            get { return !on || !dirty; }
        }

        static void Dirty()
        {
            dirty = true;
            collectFrame = Time.frameCount + 1;
        }

        internal static void Set(string value)
        {
            on = value != "off";
            Dirty();
            Debug.Log("CROW_SPLIT follow " + (on ? "on" : "off"));
        }

        static void Collect()
        {
            dirty = false;
            places.Clear();
            var byParent = new Dictionary<Transform, Place>();
            foreach (SplitPart part in Resources.FindObjectsOfTypeAll<SplitPart>())
            {
                if (part == null || part.parent == null || !part.gameObject.scene.IsValid() || part.transform.parent == part.parent) continue;
                Place place;
                if (!byParent.TryGetValue(part.parent, out place))
                {
                    // Where the old parent was when the part left it (from the part and its old local
                    // pose): it may have moved since, as the elevator rooms do while the area starts.
                    Matrix4x4 then = part.transform.localToWorldMatrix * Matrix4x4.TRS(part.position, part.rotation, part.scale).inverse;
                    byParent[part.parent] = place = new Place { Old = part.parent, Last = then };
                    places.Add(place);
                }
                place.Parts.Add(part);
            }
            if (places.Count > 0) Debug.Log("CROW_SPLIT_FOLLOW watching " + places.Count + " old parents of " + places.Sum(p => p.Parts.Count) + " parts");
        }

        internal static void Tick()
        {
            if (!on) return;
            try
            {
                if (dirty)
                {
                    if (Time.frameCount < collectFrame) return;
                    Collect();
                }
                for (int i = 0; i < places.Count; i++)
                {
                    Place place = places[i];
                    if (place.Old == null) continue;
                    Matrix4x4 now = place.Old.localToWorldMatrix;
                    if (now == place.Last) continue;
                    place.Last = now;
                    moves++;
                    place.Moves++;
                    // Each part where it would be below the old parent: its old local position through the
                    // parent's matrix, its rotation after the parent's (Unity's world rotation, also below a
                    // mirroring parent). Its scale stays: the rooms move and turn, they don't scale.
                    Quaternion rotation = place.Old.rotation;
                    int count = 0;
                    foreach (SplitPart part in place.Parts)
                    {
                        if (part == null) continue;
                        Transform t = part.transform;
                        if (t.parent == part.parent) continue;  // put back (-crow-scene-split off)
                        t.SetPositionAndRotation(now.MultiplyPoint3x4(part.position), rotation * part.rotation);
                        count++;
                    }
                    followed += count;
                    if (!place.Logged && count > 0)
                    {
                        place.Logged = true;
                        Debug.Log("CROW_SPLIT_FOLLOW " + Perf.Path(place.Old, 4) + " moved: " + count + " parts taken out from below it follow it");
                    }
                }
                float time = Time.realtimeSinceStartup;
                if (moves > 0 && time >= summaryAt)
                {
                    summaryAt = time + 30f;
                    Debug.Log("CROW_SPLIT_FOLLOW " + places.Count + " old parents watched, " + moves + " moves, " + followed + " parts placed; most: " +
                              string.Join(" ", places.Where(p => p.Moves > 0 && p.Old != null).OrderByDescending(p => p.Moves).Take(4)
                                  .Select(p => Perf.Path(p.Old, 3) + "=" + p.Moves).ToArray()));
                    moves = followed = 0;
                    foreach (Place p in places) p.Moves = 0;
                }
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_SPLIT_FOLLOW failed: " + exception);
                Dirty();
            }
        }
    }

    static class SceneSplit
    {
        static string mode;
        static bool hooked, checking;
        static float nextCheck, lastSummary;
        static int checks, differences, switches;
        static readonly HashSet<SplitPart> reported = new HashSet<SplitPart>();
        static readonly List<SplitPart> found = new List<SplitPart>();

        internal static void Install(string[] args)
        {
            string value = Perf.Value(args, "-crow-scene-split");
            Merge.MoverDebug = Array.IndexOf(args, "-crow-split-mover-log") >= 0;
            SplitFollow.Install(args);  // first: its frame step runs before the check's
            if (value != null) Set(value);
        }

        internal static void Set(string value)
        {
            mode = value;
            Apply();
            if (hooked) return;
            hooked = true;
            SceneManager.sceneLoaded += (scene, loadMode) =>
            {
                bool area = scene.name != "Logos" && scene.name != "Title" && scene.name != "CrowMara" && scene.name != "CrowGap";
                if (area && (mode.StartsWith("report") || mode.StartsWith("apply")))
                {
                    Merge.SplitMovers = mode.Contains("movers");
                    Debug.Log("CROW_" + Merge.Split(scene, !mode.StartsWith("report")));
                }
                else Apply();
            };
            checking = mode == "check" || mode.EndsWith("-check");
            if (checking) Application.onBeforeRender += Check;
        }

        static void Apply()
        {
            if (mode != "off" && mode != "on") return;
            int count = 0;
            foreach (SplitPart part in Resources.FindObjectsOfTypeAll<SplitPart>())
            {
                if (part == null || part.parent == null || !part.gameObject.scene.IsValid()) continue;
                Transform transform = part.transform;
                SplitHolder holder = part.holder;
                if (mode == "off" && transform.parent != part.parent)
                {
                    // Its own activity: the holder's, and below it the part's (or what the holder kept).
                    bool own = holder == null ? part.gameObject.activeSelf || (part.active && !part.parent.gameObject.activeInHierarchy)
                        : holder.gameObject.activeSelf && (holder.gameObject.activeInHierarchy ? part.gameObject.activeSelf : holder.own);
                    if (holder != null)
                    {
                        holder.part = null;
                        holder.gameObject.SetActive(false);
                        holder.transform.SetAsLastSibling();
                    }
                    transform.SetParent(part.parent, false);
                    transform.SetSiblingIndex(part.sibling);
                    transform.localPosition = part.position;
                    transform.localRotation = part.rotation;
                    transform.localScale = part.scale;
                    part.gameObject.SetActive(own);
                    count++;
                }
                else if (mode == "on" && transform.parent == part.parent)
                {
                    bool own = part.gameObject.activeSelf;
                    if (holder != null || (own && !part.parent.gameObject.activeInHierarchy)) part.gameObject.SetActive(false);
                    transform.SetParent(null, true);
                    if (holder != null)
                    {
                        holder.transform.SetSiblingIndex(part.sibling);
                        holder.own = true;
                        holder.part = part.gameObject;
                        holder.gameObject.SetActive(own);
                    }
                    count++;
                }
            }
            Debug.Log("CROW_SPLIT " + mode + ": " + count + " parts " + (mode == "off" ? "back in their places" : "out"));
        }

        // -crow-cycle splittoggle=parents-off|parents-on|self-off|self-on: every held part's old parent (as
        // an ancestor's ActivateGameObject would) or its holder (as ActivateChildren on its parent would)
        // switched off, or back as it was; with -crow-scene-split check, a test of the holders.
        static readonly List<KeyValuePair<GameObject, bool>> toggled = new List<KeyValuePair<GameObject, bool>>();

        internal static void Toggle(string value)
        {
            if (value.EndsWith("-on"))
            {
                for (int i = toggled.Count - 1; i >= 0; i--) if (toggled[i].Key != null) toggled[i].Key.SetActive(toggled[i].Value);
                Debug.Log("CROW_SPLIT toggle " + value + ": " + toggled.Count + " objects back");
                toggled.Clear();
                return;
            }
            foreach (SplitPart part in Resources.FindObjectsOfTypeAll<SplitPart>())
            {
                // Held parts only; parents: its old parent, also with the parts back in place (off).
                if (part == null || part.holder == null || part.parent == null || !part.gameObject.scene.IsValid()) continue;
                GameObject target = value.StartsWith("parents") ? part.parent.gameObject : part.holder.gameObject;
                toggled.Add(new KeyValuePair<GameObject, bool>(target, target.activeSelf));
                target.SetActive(false);
            }
            Debug.Log("CROW_SPLIT toggle " + value + ": " + toggled.Count + " objects off");
        }

        // From a holder's OnEnable and OnDisable: with check, the part against its place right away.
        internal static void Switched(SplitHolder holder)
        {
            if (!checking) return;
            switches++;
            var part = holder.part.GetComponent<SplitPart>();
            SplitFollow.Tick();  // the parts where following puts them this frame
            if (part != null) Compare(part);
        }

        // -crow-scene-split check: each part against its old place.
        static void Check()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextCheck) return;
            nextCheck = now + 1f;
            SplitFollow.Tick();
            found.Clear();
            foreach (SplitPart part in Resources.FindObjectsOfTypeAll<SplitPart>())
                if (part != null && part.parent != null && part.gameObject.scene.IsValid() && part.transform.parent != part.parent) found.Add(part);
            foreach (SplitPart part in found) Compare(part);
            if (now - lastSummary < 30f) return;
            lastSummary = now;
            Debug.Log("CROW_SPLIT_CHECK " + found.Count + " parts, " + checks + " checks, " + switches + " holder switches, " + differences + " differences, " +
                      reported.Count + " parts differed");
        }

        // Active exactly when it would be in its old place (its holder's, or its parents' with its own
        // activity), and where it would be there.
        static void Compare(SplitPart part)
        {
            if (!SplitFollow.Ready) return;  // a load's parts before following placed them
            checks++;
            SplitHolder holder = part.holder;
            bool active = part.gameObject.activeInHierarchy;
            // Nothing names a part's root, so it is on exactly while its holder, or its old parents with
            // its own first activity, would show it.
            bool wanted = holder != null ? holder.gameObject.activeInHierarchy : part.parent.gameObject.activeInHierarchy && part.active;
            Matrix4x4 there = holder != null ? holder.transform.localToWorldMatrix
                : part.parent.localToWorldMatrix * Matrix4x4.TRS(part.position, part.rotation, part.scale);
            Matrix4x4 here = part.transform.localToWorldMatrix;
            bool placed = true;
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(here[i] - there[i]) > 1e-3f * Mathf.Max(1f, Mathf.Abs(there[i]))) placed = false;
            if (active == wanted && placed) return;
            differences++;
            if (!reported.Add(part)) return;
            Debug.Log("CROW_SPLIT_CHECK " + Perf.Path(part.transform, 3) + " (from " + Perf.Path(part.parent, 3) + "): " +
                      (active == wanted ? "" : "active " + active + " where it would be " + wanted + " ") + (placed ? "" : "moved from its place (" + (here.GetColumn(3) - there.GetColumn(3)).magnitude.ToString("0.###", CultureInfo.InvariantCulture) + " m, parent " + (part.transform.parent == null ? "none" : part.transform.parent.name) + ")"));
        }
    }

    // In the place of a part PlayerSplit made a root of its own: the part follows the player's
    // activation and goes with it.
    public class SplitMirror : MonoBehaviour
    {
        internal GameObject part;
        internal bool active = true;

        void OnEnable()
        {
            if (part == null) return;
            part.SetActive(active);
            Debug.Log("CROW_SPLIT " + part.name + (active ? " on" : " stays off") + " with the player");
        }

        void OnDisable()
        {
            if (part == null) return;
            active = part.activeSelf;
            part.SetActive(false);
            Debug.Log("CROW_SPLIT " + part.name + " off with the player");
        }

        void OnDestroy()
        {
            if (part != null) Destroy(part);
        }
    }

    // The map (the player's "UI Canvas Map OFF": one overlay canvas, about 700 images with SFB's
    // UIPixelate mesh effect and 120 texts below the scrolled "map scroll me y0 / y-393") scrolls
    // vertically by MoveTowardsUI at 400 units a second while up or down is held. UIPixelate snaps each
    // image's pixel pattern to a 416x234 grid on the screen: its uv1 holds the image's offset from the
    // grid (its lower left corner's world position modulo a cell) and its size in cells, and its Update
    // dirties the image's mesh whenever its transform has changed. A scroll moves every image's
    // transform, so every image was rebuilt (OnPopulateMesh, ModifyMesh, the uv1 shim per vertex) at
    // every frame of a scroll: PostLateUpdate.PlayerUpdateCanvases 42-44 ms a frame on the PS Vita
    // (Fairy Forest, every map page known), the frame 80-83 ms. With the rule on, after the canvases'
    // update of each frame the scroller is put on the lattice of whole grid cells (in each axis) through
    // the position it had when the map was first shown (every image builds its mesh then, as the canvas
    // switches on, and again whenever it is switched on), so each image's offset from the grid, and with
    // it its mesh, stays what it was; and while the map is shown, UIPixelate.Update of the images below
    // the scroller returns at once (it starts with Skip, rewriter PatchUIPixelate: an instance ID looked
    // up, about 3 us; reading and clearing each moved transform's hasChanged instead cost about 100 us an
    // image on the PS Vita). Scrolling: the frame 82.6 -> 41.4 ms, the canvases 43.8 -> 7.2 ms, the
    // same as the map standing still. The scroll moves in steps of one grid cell (about 2 screen pixels)
    // and stops within half a cell of where the game's stops; the picture is the same. What an image's
    // Update also caught, a change of its own scale or of the camera's size while the map is shown, waits
    // for its next switch-on (none of the map's images scale while shown, but for the rare lure ring's
    // pulse; size changes rebuild through uGUI itself). CROW_MAP logs the scroller once found and, as the
    // map closes, the moves and the rebuilds left out; -crow-map-log every 5 s while it is shown.
    public static class MapPan
    {
        static bool on, hooked;
        static GameObject canvas;
        static RectTransform scroller;
        static HashSet<int> images;  // the instance IDs of the UIPixelates below the scroller
        static Type pixelate;
        static System.Reflection.FieldInfo toWidth, toHeight;
        static Vector2 origin, last, cell;
        static bool open;
        static float nextFind;
        static int moves, snapped, skipped;

        public static void Install(string[] args)
        {
            probe = Array.IndexOf(args, "-crow-map-log") >= 0;
            string arrowsValue = Perf.Value(args, "-crow-map-arrows");
            if (arrowsValue != null ? arrowsValue == "canvas" : Application.platform == RuntimePlatform.PSP2) SetArrows(true);
            string value = Perf.Value(args, "-crow-map-pan");
            if (value != null ? value == "on" : Application.platform == RuntimePlatform.PSP2) Set(true);
        }

        internal static void Set(bool value)
        {
            on = value;
            open = false;
            if (!hooked)
            {
                hooked = true;
                Canvas.willRenderCanvases += Tick;
                SceneManager.sceneLoaded += (scene, mode) => nextFind = 0f;
            }
            Debug.Log("CROW_MAP pan " + (on ? "on" : "game"));
        }

        static Transform Named(Transform root, string prefix, int depth)
        {
            if (root.name.StartsWith(prefix, StringComparison.Ordinal)) return root;
            if (depth == 0) return null;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = Named(root.GetChild(i), prefix, depth - 1);
                if (found != null) return found;
            }
            return null;
        }

        static void Find()
        {
            canvas = null;
            scroller = null;
            images = null;
            GameObject holder = GameObject.Find("UI Canvases");
            if (holder == null) return;
            Transform map = null;
            for (int i = 0; i < holder.transform.childCount; i++)
                if (holder.transform.GetChild(i).name == "UI Canvas Map OFF") map = holder.transform.GetChild(i);
            if (map == null) return;
            if (pixelate == null)
            {
                foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    pixelate = assembly.GetType("com.sfbgames.general.materials.UIPixelate");
                    if (pixelate != null) break;
                }
                if (pixelate == null)
                {
                    Debug.Log("CROW_MAP no UIPixelate type: off");
                    on = false;
                    return;
                }
                const System.Reflection.BindingFlags Field = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                toWidth = pixelate.GetField("_screenToPixelatedWidth", Field);
                toHeight = pixelate.GetField("_screenToPixelatedHeight", Field);
            }
            Transform found = Named(map, "map scroll me", 6);
            if (found == null || !(found is RectTransform)) return;
            canvas = map.gameObject;
            scroller = (RectTransform)found;
            images = new HashSet<int>(scroller.GetComponentsInChildren(pixelate, true).Select(c => c.GetInstanceID()));
            arrows = Named(map, "scroll arrows", 8);
            Debug.Log("CROW_MAP scroller " + scroller.name + ": " + images.Count + " pixelated images; arrows " + (arrows == null ? "none" : arrows.name));
            Arrows();
        }

        // -crow-map-arrows canvas|game: the map's scroll arrows (their FSM bobs them up and down, a
        // DOTween loop that never stops while the map is shown) under a canvas of their own (the PS
        // Vita's default), so their motion rebuilds the batches of 10 objects instead of the whole map's
        // canvas at every frame; the nested canvas takes the map canvas's shader channels (UIPixelate
        // reads uv1 and uv2) and draws where the arrows did, in hierarchy order. game: as the game has
        // them. -crow-cycle takes maparrows=.
        static bool arrowsOn;
        static Transform arrows;
        static Canvas arrowsCanvas;

        internal static void SetArrows(bool value)
        {
            arrowsOn = value;
            if (!hooked)
            {
                hooked = true;
                Canvas.willRenderCanvases += Tick;
                SceneManager.sceneLoaded += (scene, mode) => nextFind = 0f;
            }
            Arrows();
            Debug.Log("CROW_MAP arrows " + (arrowsOn ? "canvas" : "game"));
        }

        static void Arrows()
        {
            if (arrows == null || canvas == null) return;
            if (arrowsOn && arrowsCanvas == null && arrows.GetComponent<Canvas>() == null)
            {
                Canvas root = canvas.GetComponent<Canvas>();
                arrowsCanvas = arrows.gameObject.AddComponent<Canvas>();
                if (root != null) arrowsCanvas.additionalShaderChannels = root.additionalShaderChannels;
            }
            else if (!arrowsOn && arrowsCanvas != null)
            {
                UnityEngine.Object.Destroy(arrowsCanvas);
                arrowsCanvas = null;
            }
        }

        // The grid cell in the scroller's parent's units, from an image that has built its mesh.
        static bool Cell()
        {
            if (toWidth == null || toHeight == null) return false;
            Component built = scroller.GetComponentsInChildren(pixelate, false).FirstOrDefault(c => (float)toHeight.GetValue(c) > 0f);
            if (built == null) return false;
            Vector3 scale = scroller.parent == null ? Vector3.one : scroller.parent.lossyScale;
            float w = (float)toWidth.GetValue(built), h = (float)toHeight.GetValue(built);
            if (w <= 0f || scale.x <= 0f || scale.y <= 0f) return false;
            cell = new Vector2(1f / w / scale.x, 1f / h / scale.y);
            return true;
        }

        static float Snap(float value, float from, float step)
        {
            return from + Mathf.Round((value - from) / step) * step;
        }

        // After the canvases' update (their meshes built for this frame's dirt), before they draw.
        static void Tick()
        {
            if (!on && !open && !arrowsOn) return;
            if (scroller == null || canvas == null)
            {
                open = false;
                if (Time.realtimeSinceStartup < nextFind) return;
                nextFind = Time.realtimeSinceStartup + 2f;
                Find();
                if (scroller == null) return;
            }
            if (!on && !open) return;
            if (!on || !canvas.activeInHierarchy)
            {
                if (open && moves > 0) Debug.Log("CROW_MAP closed: " + moves + " moves, " + snapped + " snapped, " + skipped + " rebuilds left out, cell " + cell.x.ToString("0.###", CultureInfo.InvariantCulture) + "x" + cell.y.ToString("0.###", CultureInfo.InvariantCulture));
                open = false;
                return;
            }
            Vector2 at = scroller.anchoredPosition;
            if (!open)
            {
                // The first frame shown: every image built its mesh where the scroller is now.
                if (!Cell()) return;
                open = true;
                origin = last = at;
                moves = snapped = skipped = lastSkipped = lastMoves = 0;
                return;
            }
            Place();
            Log();
        }

        // The scroller on the lattice, if it moved since the last look.
        static void Place()
        {
            Vector2 at = scroller.anchoredPosition;
            if (at == last) return;
            moves++;
            var put = new Vector2(Snap(at.x, origin.x, cell.x), Snap(at.y, origin.y, cell.y));
            if (put != at)
            {
                scroller.anchoredPosition = put;
                snapped++;
            }
            last = put;
        }

        // From the start of every UIPixelate.Update (rewriter PatchUIPixelate): true skips the rest, which
        // would dirty the image's mesh because its transform changed.
        public static bool Skip(Component effect)
        {
            if (!open) return false;
            long start = probe ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            calls++;
            bool skip = images.Contains(effect.GetInstanceID());
            if (skip) skipped++;
            if (probe) ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            return skip;
        }

        // -crow-map-log: every 5 s while the map is open, the calls of Skip, their time, rebuilds left
        // out, moves and the scroller's place (CROW_MAP_LOG).
        static bool probe;
        static long ticks, calls;
        static float nextLog;
        static int lastSkipped, lastMoves;

        static void Log()
        {
            float now = Time.realtimeSinceStartup;
            if (!probe || now < nextLog) return;
            nextLog = now + 5f;
            Debug.Log("CROW_MAP_LOG calls=" + calls + " skipMs=" + (ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture) +
                      " skipped=" + (skipped - lastSkipped) + " moves=" + (moves - lastMoves) + " frames=" + Time.frameCount + " y=" + scroller.anchoredPosition.y.ToString("0.00", CultureInfo.InvariantCulture));
            calls = ticks = 0;
            lastSkipped = skipped;
            lastMoves = moves;
        }
    }
}
