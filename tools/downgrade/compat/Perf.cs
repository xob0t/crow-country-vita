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
//   -crow-physics <changes>     physics settings: iter=<n> (default solver iterations), sync=0|1
//                               (Physics.autoSyncTransforms), comma-separated; the step and the longest
//                               frame are FixedStep's fixeddt= and physcap= (fixed= and maxdt=, which set
//                               Time.fixedDeltaTime and maximumDeltaTime behind FixedStep, which put its
//                               own back the next frame, are gone since 21 Oct)
//   -crow-cycle <s> <sets>      after the first area loads (Scenes.IsArea: not Logos, Title, CrowMara),
//                               every s seconds apply the next change set of <sets> (sets separated by
//                               '/', changes in a set by ','; Quality.Change's keys and these: particles=,
//                               iter=, sync=, gpu=<MHz>, vsync=<n>, camera=624|550|416,
//                               merge=on|off|merged|report (Merge.cs, in the loaded scenes),
//                               rewired=pad|game (RewiredPad), split=ui (PlayerSplit, from then on),
//                               time=<scale>, 0 to hold a moment for screenshots), with a CROW_LOOP
//                               line (-crow-loop-profile) for each set's window, labelled with the set.
//                               E.g. -crow-cycle 10 gpu=222/gpu=166/resolution=0/resolution=1 with
//                               -crow-loop-profile 1000, so that only the sets' windows report.
//                               (Gone since 21 Oct, measured or covered elsewhere: sort=, probes=,
//                               reflections= and mouse=, no change; audio=idle-off, which IdleAudio.cs
//                               does for good; -crow-moving-report, Decor.cs' -crow-motion-report; and
//                               -crow-physics-report, PhysCost.cs' -crow-phys-census.)
//   -crow-rewired game|pad      Rewired's keyboard and mouse controllers on (game) or off (pad, the PS
//                               Vita's default; see RewiredPad)
//   -crow-player-split game|ui  ui (the PS Vita's default) makes the player's menus a hierarchy of their
//                               own (see PlayerSplit)
//   -crow-scaled-colliders game|box|report
//                               box (the PS Vita's default) gives each mesh collider whose scale keeps
//                               changing a box instead (see ScaledColliders); report only logs them
//                               (CROW_SCALED); -crow-cycle takes scaled=
//   -crow-scene-split off|on|check|report
//                               the PS Vita build's split areas (SceneSplit.cs): off puts the parts
//                               back in their places, on takes them out again (-crow-cycle takes
//                               scenesplit=), check compares each part with its old place every second
//                               (CROW_SPLIT_CHECK), report logs what the split would take (CROW_SPLIT)
//   -crow-split-follow off      the split parts no longer follow their old places when those move
//                               (SceneSplit.cs' SplitFollow); -crow-cycle takes splitfollow=
//   -crow-map-pan on|game       on (the PS Vita's default): the map's scrolled content moves in whole
//                               cells of the UI's pixel grid, and its pixelated images keep their meshes
//                               while it moves (MapPan.cs); game rebuilds every image at every frame of a
//                               scroll, as the game does; -crow-cycle takes mappan=
//   -crow-map-arrows canvas|game
//                               canvas (the PS Vita's default): the map's bobbing scroll arrows under a
//                               canvas of their own (MapPan.cs); -crow-cycle takes maparrows=
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
            string rule = CrowFlags.Value(args, "-crow-particles");
            if (rule != null) Change("particles", rule);
            string changes = CrowFlags.Value(args, "-crow-physics");
            if (changes != null)
                foreach (string change in changes.Split(','))
                {
                    string[] pair = change.Split('=');
                    if (pair.Length == 2 && !Change(pair[0], pair[1])) Debug.Log("CROW_PERF -crow-physics: no change " + change);
                }
            int cycle = Array.IndexOf(args, "-crow-cycle");
            if (cycle >= 0 && cycle + 2 < args.Length) Cycle.Install(CrowFlags.Number(args, "-crow-cycle", 10f), args[cycle + 2]);
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
            string value = CrowFlags.Value(args, "-crow-rewired");
            if (value != null ? value == "pad" : Application.platform == RuntimePlatform.PSP2) Set(true);
        }

        internal static void Set(bool on)
        {
            pad = on;
            next = 0f;
            if (hooked) return;
            hooked = true;
            CrowEvents.Loaded("RewiredPad.sceneLoaded", (scene, mode) => next = 0f);
            CrowEvents.BeforeRender("RewiredPad.Tick", Tick);
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
            string value = CrowFlags.Value(args, "-crow-player-split");
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
                CrowEvents.Loaded("PlayerSplit.sceneLoaded", (scene, mode) => Split());
            }
            Split();
        }

        // At each scene load: an area's MaraLoader has activated the player in its Awake by then.
        static void Split()
        {
            GameObject player = MaraScene.FindPlayer();
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
                text.Append(" path=").Append(Perf.Path(system.transform, 4));
                Debug.Log(text.ToString());
            }
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
            string value = CrowFlags.Value(args, "-crow-scaled-colliders");
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
            CrowEvents.Loaded("ScaledColliders.sceneLoaded", (scene, loadMode) =>
            {
                next = Time.realtimeSinceStartup + 2f;
                rescan = true;
                frame = -1;
            });
            CrowEvents.BeforeRender("ScaledColliders.Tick", Tick);
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
                // Not one the game switched off meanwhile: its box would collide where nothing should.
                if (mode == "box" && collider.enabled) Swap(collider);
                BoxCollider box;
                Bounds bounds = swapped.TryGetValue(collider, out box) ? box.bounds : collider.bounds;
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
