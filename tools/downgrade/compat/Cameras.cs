using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// The cameras: what renders each frame, and the cameras that film into render textures. Every area
// draws through one camera, the player's Main Camera (its effect, CamEffect.cs, then draws the
// picture to the screen; the UI's canvases are screen-space overlays and need no camera). A few
// areas also have cameras that film a corner of the scene into a texture another object shows:
// Arcade's four cabinet screens (orthographic, culling every layer), Office Pike's two security
// feeds (perspective, 1000 m, every layer: the office drawn again from Pike's side), Control Room's
// monitor. Each such camera culls the whole scene and draws what it sees in every frame, also while
// its screen is off the main camera's view; at the PS Vita's speed (Windows player, vitalike) Arcade's
// four took 8.3 ms of the main thread a frame against the main camera's 13.2.
//   -crow-rt-cameras game|visible|half|on
//                          visible: such a camera renders only while a renderer whose material shows
//                          its texture (or a RawImage showing it) was drawn in the frame before, or
//                          when nothing shows it that this can find (Arcade's screens are switched
//                          off until a cabinet is played, its cameras filming all along); half:
//                          each renders every other frame (15 fps at 30), the cameras of an area
//                          taking turns; on: both (the
//                          PS Vita's default); game: every frame, as the game has them (other
//                          players' default). -crow-cycle / -crow-ab take rtcams=<mode>.
//   -crow-menu-cull on|off  on (the PS Vita's default): while a full-screen menu covers the world (the
//                          inventory and status screen, the map, the pause menu: their canvases
//                          "UI Canvas Inventory OFF", "UI Canvas Map OFF", "UI Canvas Pause OFF" active)
//                          the Main Camera culls only layer 5 (UI), where every menu 3D object and the
//                          menus' lights are; the world it drew behind them (the inventory's black plane
//                          hid it by depth, the map's and pause's overlay images covered it) is left out:
//                          its culling, draws and shadow pass. The camera's own mask comes back when the
//                          menu closes. -crow-cycle / -crow-ab / -crow-fight-cycle take menucull=.
//   -crow-near-plane <m>|game  the near plane of the cameras that draw the game through its camera
//                          effect (CrowCountryCamEffect) with a far plane under 100 m (the player's
//                          Main Camera, 0.1-24 m; Ladder's; not the story and title cameras' 1000 m)
//                          at m metres at least (the PS Vita's default 1; game elsewhere), short of
//                          anything the camera carries itself (other cameras of Ladder, Results and
//                          CrowQuest Unlock hold a fade sprite 0.78 m in front). The PS Vita
//                          player gives a camera that renders into a render texture (the effect's
//                          picture) depth of about 16 bits, whatever it asks for (16, 24 or 32 bits, a
//                          Depth or Shadowmap texture: the same), and the camera sits 15-24 m from what
//                          it shows, so with the game's 0.1 m two surfaces 3-6 cm apart there fought
//                          (Fairy Forest's gate: a building's black end cap 0.3 mm in front of its
//                          wall's end, in stripes and steps); at 1 m the steps are ten times finer.
//                          Linear fog reads the clip-space depth, which on the PS Vita (GL-style clip
//                          space) moves with the near plane: for each such camera's render the fog's
//                          start and end are moved to where they give the same fog, and put back after
//                          (-crow-near-fog off leaves them, a test). -crow-cycle / -crow-ab /
//                          -crow-fight-cycle take near=<m>|game and nearfog=on|off.
//   -crow-camera-report [s] after each scene load (and every s seconds, if given), each camera that
//                          rendered (CROW_CAMERA): its path, target, depth, projection, clip planes,
//                          culling mask, clear flags, occlusion culling, layer cull distances, image
//                          effects, and for a render-texture camera the renderers showing its texture
//                          and how often it rendered; for the main camera the scene's fog against its
//                          far plane. And the player's settings once (CROW_SYSINFO: the graphics
//                          device and its threading, CPU count, quality settings that cost per draw).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    static class Cameras
    {
        static string mode;
        static bool hooked;
        static float reportEvery = -1f, nextReport = -1f;

        // Called from Quality.Install (after Draws' and Merge's). Each part under a Guard of its own (Diag.Guard),
        // so the report (a probe) or one default that throws doesn't take the PS Vita defaults after it with it.
        public static void Install(string[] args)
        {
            Diag.Guard("Cameras.SysInfo", SysInfo);
            if (CrowFlags.Has(args, "-crow-camera-report"))
                Diag.Guard("Cameras.Report", () =>
                {
                    reportEvery = CrowFlags.Number(args, "-crow-camera-report", 0f);
                    nextReport = 0f;
                    Hook();
                    CrowEvents.PreCull("Cameras.Count", Count);
                });
            string value = CrowFlags.Value(args, "-crow-rt-cameras", "on");
            if (value != null) Diag.Guard("Cameras.RtCameras", () => Set(value));
            string menu = CrowFlags.Value(args, "-crow-menu-cull", "on");
            if (menu != null) Diag.Guard("MenuCull", () => MenuCull.Set(menu == "on"));
            string near = CrowFlags.Value(args, "-crow-near-plane", "1");
            if (CrowFlags.Value(args, "-crow-near-fog") == "off") NearPlane.FogMap(false);
            if (near != null) Diag.Guard("NearPlane", () => NearPlane.Set(near));
            UiPark.Install(args);  // UiPark.cs: the gameplay canvas's switched-off screens and the rig's parked (each guarded there)
        }

        static void Hook()
        {
            if (hooked) return;
            hooked = true;
            CrowEvents.Loaded("Cameras.sceneLoaded", (scene, mode) =>
            {
                nextScan = nextShown = 0f;
                if (nextReport >= 0f) nextReport = Time.realtimeSinceStartup + 5f;
                rendered.Clear();
            });
            CrowEvents.BeforeRender("Cameras.Tick", Tick);
            CrowEvents.PostRender("Cameras.AfterRender", AfterRender);
        }

        // -crow-cycle's and -crow-ab's rtcams=<mode>.
        internal static bool Change(string key, string value)
        {
            if (key == "menucull")
            {
                MenuCull.Set(value == "on");
                return true;
            }
            if (key == "near")
            {
                NearPlane.Set(value);
                return true;
            }
            if (key == "nearfog")
            {
                NearPlane.FogMap(value != "off");
                return true;
            }
            if (key != "rtcams") return false;
            Set(value);
            return true;
        }

        internal static void Set(string value)
        {
            if (value != "game" && value != "visible" && value != "half" && value != "on")
            {
                Debug.Log("CROW_CAMERA -crow-rt-cameras wants game, visible, half or on, not " + value);
                return;
            }
            mode = value;
            Hook();
            Restore();
            nextScan = nextShown = 0f;
            Debug.Log("CROW_CAMERA rt cameras " + mode);
        }

        // The render-texture cameras of the loaded scenes, found again every 2 s and after each load
        // (Camera.allCameras lists the enabled ones; the game switches some on and off).
        class Filming
        {
            public Camera Camera;
            public RenderTexture Target;
            public readonly List<Renderer> Shown = new List<Renderer>();
            public readonly List<Graphic> ShownUi = new List<Graphic>();
            public int Turn;
            public bool Skipped;
        }

        static readonly List<Filming> filming = new List<Filming>();
        static float nextScan, nextShown;
        static readonly string[] TextureNames = { "_MainTex", "_EmissionMap", "_DetailAlbedoMap", "_BlurTex", "_Tex" };

        static void Scan()
        {
            Restore();
            var cameras = new List<Camera>();
            foreach (Camera camera in Resources.FindObjectsOfTypeAll<Camera>())
                if (camera != null && camera.gameObject.scene.IsValid() && camera.targetTexture != null && !IsMain(camera)) cameras.Add(camera);
            var old = filming.ToDictionary(f => f.Camera);
            bool same = cameras.Count == filming.Count && cameras.All(c => old.ContainsKey(c) && old[c].Target == c.targetTexture);
            float now = Time.realtimeSinceStartup;
            // The renderers showing the textures, switched on or off (Arcade's cabinet screens are off
            // until a cabinet is played, their cameras filming all along): after each load and when the
            // cameras change, not periodically: the pass walks every renderer, the inactive ones too, and
            // took a 190-240 ms frame on the PS Vita in Arcade and Office Pike.
            if (same && now < nextShown) return;
            nextShown = float.MaxValue;
            filming.Clear();
            if (cameras.Count == 0) return;
            var targets = new Dictionary<Texture, Filming>();
            foreach (Camera camera in cameras)
            {
                Filming entry;
                if (old.TryGetValue(camera, out entry) && entry.Target == camera.targetTexture) filming.Add(entry);
                else filming.Add(entry = new Filming { Camera = camera, Target = camera.targetTexture });
                entry.Shown.Clear();
                entry.ShownUi.Clear();
                entry.Turn = filming.Count - 1;
                targets[entry.Target] = entry;
            }
            foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (!renderer.gameObject.scene.IsValid()) continue;  // prefabs and assets
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    foreach (string name in TextureNames)
                    {
                        Filming entry;
                        if (!material.HasProperty(name)) continue;
                        Texture texture = material.GetTexture(name);
                        if (texture != null && targets.TryGetValue(texture, out entry) && !entry.Shown.Contains(renderer)) entry.Shown.Add(renderer);
                    }
                }
            }
            foreach (RawImage image in Resources.FindObjectsOfTypeAll<RawImage>())
            {
                Filming entry;
                if (image.gameObject.scene.IsValid() && image.texture != null && targets.TryGetValue(image.texture, out entry)) entry.ShownUi.Add(image);
            }
        }

        // The player's camera: the one with the game's camera effect (it sets its own target texture
        // for the frame, in OnPreCull), or tagged MainCamera.
        static bool IsMain(Camera camera)
        {
            return camera.CompareTag("MainCamera") || camera.GetComponent("CrowCountryCamEffect") != null;
        }

        static int frame;

        // Before the frame's cameras render: which render-texture cameras skip this frame.
        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (nextReport >= 0f && now >= nextReport)
            {
                Report();
                nextReport = reportEvery > 0f ? now + reportEvery : -1f;
            }
            if (mode == null || mode == "game") return;
            Restore();
            if (now >= nextScan && CrowEvents.Turn(nextScan <= 0f))  // the 2 s scans in a frame of their own
            {
                nextScan = now + 2f;
                Scan();
            }
            if (filming.Count == 0) return;
            frame++;
            bool visible = mode == "visible" || mode == "on", half = mode == "half" || mode == "on";
            for (int i = 0; i < filming.Count; i++)
            {
                Filming entry = filming[i];
                Camera camera = entry.Camera;
                if (camera == null || !camera.enabled) continue;
                bool skip = half && ((frame + entry.Turn) & 1) != 0;
                if (!skip && visible && (entry.Shown.Count > 0 || entry.ShownUi.Count > 0) && !Seen(entry)) skip = true;
                if (!skip) continue;
                camera.enabled = false;
                entry.Skipped = true;
                skipped++;
            }
        }

        static int skipped;

        // Whether anything showing the camera's texture was drawn last frame (isVisible: in any camera;
        // false for a renderer that is off or under an inactive object).
        static bool Seen(Filming entry)
        {
            foreach (Renderer renderer in entry.Shown)
                if (renderer != null && renderer.enabled && renderer.isVisible) return true;
            foreach (Graphic graphic in entry.ShownUi)
                if (graphic != null && graphic.isActiveAndEnabled) return true;
            return false;
        }

        // The cameras skipped this frame enabled again once the player's camera has rendered (it renders
        // after the render-texture cameras), so the game's scripts always see them as they left them.
        static void AfterRender(Camera camera)
        {
            if (skipped > 0 && IsMain(camera)) Restore();
        }

        static void Restore()
        {
            if (skipped == 0) return;
            foreach (Filming entry in filming)
                if (entry.Skipped)
                {
                    entry.Skipped = false;
                    if (entry.Camera != null) entry.Camera.enabled = true;
                }
            skipped = 0;
        }

        // -crow-camera-report: the cameras that rendered since the scene loaded, and how often.
        static readonly Dictionary<Camera, int> rendered = new Dictionary<Camera, int>();
        static int reportFrame;

        static void Count(Camera camera)
        {
            int n;
            rendered.TryGetValue(camera, out n);
            rendered[camera] = n + 1;
        }

        static void Report()
        {
            int frames = Math.Max(1, Time.frameCount - reportFrame);
            reportFrame = Time.frameCount;
            if (filming.Count == 0 && mode == null) Scan();
            foreach (KeyValuePair<Camera, int> pair in rendered.ToList())
            {
                Camera camera = pair.Key;
                if (camera == null) continue;
                var line = new StringBuilder("CROW_CAMERA scene=").Append(camera.gameObject.scene.name.Replace(' ', '_'));
                line.Append(" path=").Append(Hierarchy.Path(camera.transform, 4));
                line.Append(" renders=").Append((pair.Value / (double)frames).ToString("F2", CultureInfo.InvariantCulture)).Append("/frame");
                RenderTexture target = camera.targetTexture;
                line.Append(" main=").Append(IsMain(camera) ? 1 : 0);
                line.Append(" target=").Append(target == null ? "screen" : target.width + "x" + target.height);
                line.Append(" depth=").Append(camera.depth.ToString(CultureInfo.InvariantCulture));
                line.Append(camera.orthographic ? " ortho=" + camera.orthographicSize.ToString("F2", CultureInfo.InvariantCulture)
                                                : " fov=" + camera.fieldOfView.ToString("F1", CultureInfo.InvariantCulture));
                line.Append(" near=").Append(camera.nearClipPlane.ToString("F2", CultureInfo.InvariantCulture));
                line.Append(" far=").Append(camera.farClipPlane.ToString("F1", CultureInfo.InvariantCulture));
                line.Append(" mask=").Append(camera.cullingMask.ToString("X"));
                line.Append(" clear=").Append(camera.clearFlags);
                line.Append(" occlusion=").Append(camera.useOcclusionCulling ? 1 : 0);
                line.Append(" sort=").Append(camera.opaqueSortMode);
                line.Append(" layerCull=").Append(camera.layerCullDistances.Count(d => d > 0f));
                line.Append(" pos=").Append(V(camera.transform.position)).Append(" fwd=").Append(V(camera.transform.forward));
                var effects = camera.GetComponents<MonoBehaviour>().Where(b => b != null && b.enabled).Select(b => b.GetType().Name).ToArray();
                line.Append(" behaviours=").Append(string.Join(",", effects));
                Filming entry = filming.FirstOrDefault(f => f.Camera == camera);
                if (entry != null)
                    line.Append(" shownBy=").Append(entry.Shown.Count).Append('+').Append(entry.ShownUi.Count).Append("ui")
                        .Append(" seen=").Append(Seen(entry) ? 1 : 0).Append(entry.Shown.Count > 0 ? " screen=" + Hierarchy.Path(entry.Shown[0].transform, 3) : "");
                if (IsMain(camera))
                {
                    line.Append(" fog=").Append(RenderSettings.fog ? RenderSettings.fogMode + ":" + RenderSettings.fogStartDistance.ToString("F0", CultureInfo.InvariantCulture) +
                                                 "-" + RenderSettings.fogEndDistance.ToString("F0", CultureInfo.InvariantCulture) : "off");
                    Depths(camera, line);
                }
                Debug.Log(line.ToString());
            }
            rendered.Clear();
        }

        // The renderers in the camera's view by their nearest depth: past the linear fog's end they
        // draw as flat fog colour (what a nearer far plane would cull), and how many submeshes they are.
        static readonly Plane[] planes = new Plane[6];

        static void Depths(Camera camera, StringBuilder line)
        {
            GeometryUtility.CalculateFrustumPlanes(camera, planes);
            Matrix4x4 view = camera.worldToCameraMatrix;
            float fogEnd = RenderSettings.fog && RenderSettings.fogMode == FogMode.Linear ? RenderSettings.fogEndDistance : float.MaxValue;
            int inView = 0, fogged = 0, foggedDraws = 0, draws = 0;
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled || (camera.cullingMask & (1 << renderer.gameObject.layer)) == 0) continue;
                Bounds bounds = renderer.bounds;
                if (!GeometryUtility.TestPlanesAABB(planes, bounds)) continue;
                inView++;
                int count = renderer.sharedMaterials.Length;
                draws += count;
                Vector3 min = bounds.min, max = bounds.max;
                float nearest = float.MaxValue;
                for (int i = 0; i < 8; i++)
                {
                    var p = new Vector3((i & 1) != 0 ? max.x : min.x, (i & 2) != 0 ? max.y : min.y, (i & 4) != 0 ? max.z : min.z);
                    nearest = Mathf.Min(nearest, -view.MultiplyPoint3x4(p).z);
                }
                if (nearest <= fogEnd) continue;
                fogged++;
                foggedDraws += count;
            }
            line.Append(" inView=").Append(inView).Append('/').Append(draws).Append(" pastFog=").Append(fogged).Append('/').Append(foggedDraws);
        }

        static string V(Vector3 v)
        {
            return v.x.ToString("F1", CultureInfo.InvariantCulture) + "," + v.y.ToString("F1", CultureInfo.InvariantCulture) + "," + v.z.ToString("F1", CultureInfo.InvariantCulture);
        }

        static void SysInfo()
        {
            Debug.Log("CROW_SYSINFO gfx=\"" + SystemInfo.graphicsDeviceVersion + "\" device=" + SystemInfo.graphicsDeviceType +
                      " mt=" + (SystemInfo.graphicsMultiThreaded ? 1 : 0) + " cpus=" + SystemInfo.processorCount +
                      " instancing=" + (SystemInfo.supportsInstancing ? 1 : 0) + " shaderLevel=" + SystemInfo.graphicsShaderLevel +
                      " pixelLights=" + QualitySettings.pixelLightCount + " shadows=" + QualitySettings.shadows +
                      " cascades=" + QualitySettings.shadowCascades + " lodBias=" + QualitySettings.lodBias.ToString(CultureInfo.InvariantCulture) +
                      " realtimeReflectionProbes=" + (QualitySettings.realtimeReflectionProbes ? 1 : 0) +
                      " softParticles=" + (QualitySettings.softParticles ? 1 : 0));
        }
    }

    // -crow-menu-cull: the Main Camera draws only the menus' layer while a full-screen menu is up.
    static class MenuCull
    {
        const int MenuLayers = 1 << MaraScene.UiLayer;  // the menus' 3D objects and lights
        static readonly string[] Names = { "UI Canvas Inventory OFF", "UI Canvas Map OFF", "UI Canvas Pause OFF" };
        static readonly GameObject[] canvases = new GameObject[3];
        static bool on, hooked;
        static Camera camera;
        static int saved;
        static bool culling;
        static float nextFind;

        internal static void Set(bool value)
        {
            on = value;
            if (!hooked)
            {
                hooked = true;
                CrowEvents.BeforeRender("MenuCull.Tick", Tick);
                CrowEvents.Loaded("MenuCull.sceneLoaded", (scene, mode) => nextFind = 0f);
            }
            if (!on) Release();
            Debug.Log("CROW_CAMERA menu cull " + (on ? "on" : "off"));
        }

        static void Release()
        {
            if (culling && camera != null && camera.cullingMask == MenuLayers) camera.cullingMask = saved;
            culling = false;
        }

        // The mask to give a camera back after a load's gap (SceneFlow.cs SceneGap's drain, which culls nothing
        // until the old assets are gone): while a menu culls the camera, the mask the menu saved, and the gap
        // gives that back instead of the menu's. A load from the pause menu (Quit to Title) used to leave the
        // gap holding layer 5 only and the menu nothing to give back: in the next area the Main Camera drew
        // the UI layer and nothing else, a black screen but for the examinable items (21 Oct).
        internal static int OwnMask(Camera of)
        {
            if (!culling || of == null || of != camera || of.cullingMask != MenuLayers) return of == null ? 0 : of.cullingMask;
            culling = false;
            return saved;
        }

        // The canvases below the player's "UI Canvases" (a root of its own on the PS Vita, PlayerSplit),
        // looked up by name one level at a time.
        static void Find()
        {
            for (int i = 0; i < canvases.Length; i++) canvases[i] = null;
            GameObject holder = GameObject.Find(MaraScene.Canvases);
            if (holder == null) return;
            Transform root = holder.transform;
            for (int c = 0; c < root.childCount; c++)
            {
                Transform child = root.GetChild(c);
                int index = Array.IndexOf(Names, child.name);
                if (index >= 0) canvases[index] = child.gameObject;
            }
        }

        static void Tick()
        {
            if (!on) return;
            float now = Time.realtimeSinceStartup;
            if (camera == null || canvases[0] == null)
            {
                if (now < nextFind) return;
                nextFind = now + 1f;
                if (culling) culling = false;  // the camera went with a load: nothing to give back
                camera = Camera.main;
                Find();
                if (camera == null || canvases[0] == null) return;
            }
            bool covered = false;
            for (int i = 0; i < canvases.Length; i++)
                if (canvases[i] != null && canvases[i].activeInHierarchy) covered = true;
            if (covered && !culling)
            {
                int mask = camera.cullingMask;
                if (mask == 0 || mask == MenuLayers) return;  // a load's gap culls nothing (SceneFlow.cs): leave it
                saved = mask;
                camera.cullingMask = MenuLayers;
                culling = true;
            }
            else if (!covered && culling) Release();
        }
    }

    // -crow-near-plane: the game cameras' near plane further out, for depth precision (see the top).
    static class NearPlane
    {
        static float near = -1f;  // <= 0: the game's
        static bool fogMap = true, hooked, glClip;
        static readonly Dictionary<Camera, float> changed = new Dictionary<Camera, float>();  // camera: its own near plane
        static readonly Dictionary<Camera, float> limits = new Dictionary<Camera, float>();  // camera: the near plane it gets
        static readonly HashSet<Camera> left = new HashSet<Camera>();
        static Camera fogged;
        static float gameStart, gameEnd;

        internal static void Set(string value)
        {
            float metres;
            near = value != "game" && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out metres) ? metres : -1f;
            if (!hooked)
            {
                hooked = true;
                // OpenGL-style clip space (z from -w to w): the PS Vita's and OpenGL's; Direct3D's from 0 to w.
                UnityEngine.Rendering.GraphicsDeviceType device = SystemInfo.graphicsDeviceType;
                glClip = device == UnityEngine.Rendering.GraphicsDeviceType.PlayStationVita || device == UnityEngine.Rendering.GraphicsDeviceType.OpenGLCore ||
                         device == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES2 || device == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3;
                CrowEvents.PreCull("NearPlane.PreCull", PreCull);
                CrowEvents.PostRender("NearPlane.PostRender", PostRender);
                CrowEvents.Unloaded("NearPlane.sceneUnloaded", scene =>
                {
                    foreach (Camera gone in changed.Keys.Where(c => c == null).ToList()) changed.Remove(gone);
                    foreach (Camera gone in limits.Keys.Where(c => c == null).ToList()) limits.Remove(gone);
                    left.RemoveWhere(c => c == null);
                });
            }
            // Every camera back to its own near plane, whatever comes next: PreCull starts again from the game's
            // (restoring only for "game" left a camera at the old value when the plane moved, and took it for the
            // game's own).
            foreach (KeyValuePair<Camera, float> pair in changed)
                if (pair.Key != null) pair.Key.nearClipPlane = pair.Value;
            changed.Clear();
            left.Clear();
            limits.Clear();
            Debug.Log("CROW_CAMERA near plane " + (near > 0f ? near.ToString(CultureInfo.InvariantCulture) + " m" : "game") + (fogMap ? "" : ", fog not mapped"));
        }

        internal static void FogMap(bool on)
        {
            fogMap = on;
        }

        // The cameras the game draws through its camera effect, short-sighted (the story and title
        // cameras see 1000 m and keep theirs).
        static bool Applies(Camera camera)
        {
            if (camera.orthographic || camera.farClipPlane >= 100f) return false;
            foreach (MonoBehaviour behaviour in camera.GetComponents<MonoBehaviour>())
                if (behaviour != null && behaviour.GetType().Name == "CrowCountryCamEffect") return true;
            return false;
        }

        // The near plane a camera gets: short of anything it carries itself (Ladder's, Results' and
        // CrowQuest Unlock's other cameras hold a full-screen fade sprite 0.78 m in front), switched off
        // or not; nothing the game draws in the world comes that close to a 10-degree view.
        static float Limit(Camera camera, out string by)
        {
            float limit = near;
            by = null;
            Matrix4x4 view = camera.worldToCameraMatrix;
            foreach (Renderer renderer in camera.GetComponentsInChildren<Renderer>(true))
            {
                Bounds bounds;
                var filter = renderer.GetComponent<MeshFilter>();
                var sprite = renderer as SpriteRenderer;
                if (sprite != null && sprite.sprite != null) bounds = sprite.sprite.bounds;
                else if (filter != null && filter.sharedMesh != null) bounds = filter.sharedMesh.bounds;
                else bounds = new Bounds(Vector3.zero, Vector3.zero);
                Matrix4x4 toView = view * renderer.transform.localToWorldMatrix;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = bounds.center + Vector3.Scale(bounds.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                    float depth = -toView.MultiplyPoint3x4(local).z;  // the view looks down -z
                    if (depth > 0f && depth * 0.8f < limit)
                    {
                        limit = depth * 0.8f;
                        by = renderer.name;
                    }
                }
            }
            return limit;
        }

        // Linear fog's coordinate at a distance d, a d + b: the clip-space z where it runs from -w to w (the
        // PS Vita), the shaders' distance from it where it runs from 0 to w (Direct3D).
        static void Clip(float n, float f, out float a, out float b)
        {
            a = glClip ? (f + n) / (f - n) : f / (f - n);
            b = glClip ? -2f * f * n / (f - n) : -f * n / (f - n);
        }

        static void PreCull(Camera camera)
        {
            if (fogged != null) PostRender(fogged);  // a render that never ended: its fog given back
            if (near <= 0f || left.Contains(camera)) return;
            float own, set;
            if (!changed.TryGetValue(camera, out own) || !limits.TryGetValue(camera, out set))
            {
                string by = null;
                set = Applies(camera) ? Limit(camera, out by) : 0f;
                if (set <= camera.nearClipPlane)
                {
                    left.Add(camera);
                    return;
                }
                own = camera.nearClipPlane;
                changed[camera] = own;
                limits[camera] = set;
                Debug.Log("CROW_CAMERA near plane " + camera.name + " " + own.ToString(CultureInfo.InvariantCulture) + " -> " +
                          set.ToString("0.###", CultureInfo.InvariantCulture) + " m (far " + camera.farClipPlane.ToString(CultureInfo.InvariantCulture) +
                          (by == null ? "" : "; short of its own " + by) + ")");
            }
            camera.nearClipPlane = set;
            if (!fogMap || !RenderSettings.fog || RenderSettings.fogMode != FogMode.Linear) return;
            // The fog's start and end where the clip-space z at the new near plane equals the old one's at
            // the game's start and end: c' = k c + m for every distance.
            float far = camera.farClipPlane, a, b, a1, b1;
            Clip(own, far, out a, out b);
            Clip(set, far, out a1, out b1);
            float k = a1 / a, m = b1 - k * b;
            gameStart = RenderSettings.fogStartDistance;
            gameEnd = RenderSettings.fogEndDistance;
            RenderSettings.fogStartDistance = k * gameStart + m;
            RenderSettings.fogEndDistance = k * gameEnd + m;
            fogged = camera;
        }

        static void PostRender(Camera camera)
        {
            if (camera != fogged || fogged == null) return;
            RenderSettings.fogStartDistance = gameStart;
            RenderSettings.fogEndDistance = gameEnd;
            fogged = null;
        }
    }
}
