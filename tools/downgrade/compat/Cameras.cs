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
//   -crow-fog-far off|on    on: the main camera's far plane at the linear fog's end and its background
//                          the fog's colour (what lies wholly past the fog's end draws as flat fog
//                          colour, so the picture is the same but where nothing was within the far
//                          plane, black before); Pool has 53 of its 144 renderers in view there.
//                          -crow-cycle / -crow-ab take fogfar=.
//   player=hide|noshadow|show (-crow-cycle / -crow-ab, a cost test): the player's drawn renderers off,
//                          or their shadow casting off, or back as they were.
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

        // Called from Draws.Install.
        public static void Install(string[] args)
        {
            SysInfo();
            int index = Array.IndexOf(args, "-crow-camera-report");
            if (index >= 0)
            {
                float every;
                reportEvery = index + 1 < args.Length && float.TryParse(args[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out every) ? every : 0f;
                nextReport = 0f;
                Hook();
                Camera.onPreCull += Count;
            }
            string value = CrowFlags.Value(args, "-crow-rt-cameras") ?? (Application.platform == RuntimePlatform.PSP2 ? "on" : null);
            if (value != null) Set(value);
            string menu = CrowFlags.Value(args, "-crow-menu-cull") ?? (Application.platform == RuntimePlatform.PSP2 ? "on" : null);
            if (menu != null) MenuCull.Set(menu == "on");
            string fog = CrowFlags.Value(args, "-crow-fog-far");
            if (fog != null) FogFar(fog == "on");
            string near = CrowFlags.Value(args, "-crow-near-plane") ?? (Application.platform == RuntimePlatform.PSP2 ? "1" : null);
            if (CrowFlags.Value(args, "-crow-near-fog") == "off") NearPlane.FogMap(false);
            if (near != null) NearPlane.Set(near);
            UiPark.Install(args);  // UiPark.cs: the gameplay canvas's switched-off screens parked
        }

        // -crow-fog-far: the main camera's far plane and background, and what they were.
        static bool fogFar;
        static Camera fogCamera;
        static float gameFar, setFar = -1f;
        static Color gameBackground;
        static float nextFog;

        static void FogFar(bool on)
        {
            fogFar = on;
            Hook();
            nextFog = 0f;
            if (!on) UndoFog();
            Debug.Log("CROW_CAMERA fog far " + (on ? "on" : "off"));
        }

        static void UndoFog()
        {
            if (fogCamera != null && setFar > 0f && Mathf.Approximately(fogCamera.farClipPlane, setFar))
            {
                fogCamera.farClipPlane = gameFar;
                fogCamera.backgroundColor = gameBackground;
            }
            fogCamera = null;
            setFar = -1f;
        }

        // -crow-cycle's player=hide|noshadow|show (a cost test): the player's own renderers (Mara and what
        // she holds; not the menus' layer 5) switched off, or their shadow casting off, or back. Logs
        // what they are: renderers drawn, their materials and vertices.
        static readonly Dictionary<Renderer, UnityEngine.Rendering.ShadowCastingMode> playerChanged = new Dictionary<Renderer, UnityEngine.Rendering.ShadowCastingMode>();
        static readonly List<Renderer> playerHidden = new List<Renderer>();

        static void Player(string value)
        {
            foreach (Renderer renderer in playerHidden)
                if (renderer != null) renderer.enabled = true;
            playerHidden.Clear();
            foreach (KeyValuePair<Renderer, UnityEngine.Rendering.ShadowCastingMode> pair in playerChanged)
                if (pair.Key != null) pair.Key.shadowCastingMode = pair.Value;
            playerChanged.Clear();
            int drawn = 0, casters = 0, vertices = 0, skinned = 0;
            var sizes = new int[4];  // casters by bounds diagonal: under 0.1, 0.2, 0.4 m, larger
            var materials = new HashSet<Material>();
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled || renderer.gameObject.layer == 5 || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer)) continue;
                if (!renderer.transform.root.name.StartsWith("CharacterCameraPrefab")) continue;
                if (!renderer.isVisible) continue;
                drawn++;
                if (renderer is SkinnedMeshRenderer) skinned++;
                foreach (Material material in renderer.sharedMaterials) materials.Add(material);
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null) vertices += filter.sharedMesh.vertexCount;
                bool caster = renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;
                if (caster)
                {
                    casters++;
                    float size = renderer.bounds.size.magnitude;
                    sizes[size < 0.1f ? 0 : size < 0.2f ? 1 : size < 0.4f ? 2 : 3]++;
                }
                if (value == "hide")
                {
                    renderer.enabled = false;
                    playerHidden.Add(renderer);
                }
                else if (value == "noshadow" && caster)
                {
                    playerChanged[renderer] = renderer.shadowCastingMode;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
            }
            Debug.Log("CROW_CAMERA player " + value + ": " + drawn + " renderers drawn (" + skinned + " skinned), " + casters + " casting, " +
                      materials.Count + " materials, " + vertices + " vertices; casters by size <0.1m=" + sizes[0] + " <0.2m=" + sizes[1] +
                      " <0.4m=" + sizes[2] + " more=" + sizes[3]);
        }

        static void FogTick(float now)
        {
            if (now < nextFog) return;
            nextFog = now + 2f;
            Camera camera = Camera.main;
            if (camera == null || camera != fogCamera || (setFar > 0f && !Mathf.Approximately(camera.farClipPlane, setFar))) UndoFog();
            if (camera == null || !RenderSettings.fog || RenderSettings.fogMode != FogMode.Linear) return;
            float end = RenderSettings.fogEndDistance;
            if (fogCamera == null)
            {
                if (end <= camera.nearClipPlane || end >= camera.farClipPlane) return;
                fogCamera = camera;
                gameFar = camera.farClipPlane;
                gameBackground = camera.backgroundColor;
            }
            setFar = Mathf.Min(end, gameFar);
            camera.farClipPlane = setFar;
            Color fog = RenderSettings.fogColor;
            camera.backgroundColor = new Color(fog.r, fog.g, fog.b, gameBackground.a);
        }

        static void Hook()
        {
            if (hooked) return;
            hooked = true;
            CrowEvents.Loaded("Cameras.sceneLoaded", (scene, mode) =>
            {
                nextScan = nextShown = nextFog = 0f;
                if (nextReport >= 0f) nextReport = Time.realtimeSinceStartup + 5f;
                rendered.Clear();
            });
            CrowEvents.BeforeRender("Cameras.Tick", Tick);
            Camera.onPostRender += AfterRender;
        }

        // -crow-cycle's and -crow-ab's rtcams=<mode>.
        internal static bool Change(string key, string value)
        {
            if (key == "player")
            {
                Player(value);
                return true;
            }
            if (key == "menucull")
            {
                MenuCull.Set(value == "on");
                return true;
            }
            if (key == "fogfar")
            {
                FogFar(value == "on");
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
            if (RenderCost.Change(key, value) || UiPark.Change(key, value)) return true;
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
            if (fogFar) FogTick(now);
            if (mode == null || mode == "game") return;
            Restore();
            if (now >= nextScan)
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
                line.Append(" path=").Append(Perf.Path(camera.transform, 4));
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
                        .Append(" seen=").Append(Seen(entry) ? 1 : 0).Append(entry.Shown.Count > 0 ? " screen=" + Perf.Path(entry.Shown[0].transform, 3) : "");
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
            var corner = new Vector3[8];
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

    // Cost tests for the render step (-crow-cycle / -crow-fight-cycle / -crow-ab keys; each changes the
    // picture, so they measure, they are no defaults): canvases=off|on (every enabled screen-space
    // overlay canvas switched off), level=hide|half|show (the level's renderers drawn last frame
    // switched off: all, or every other one; not the player's, which live in DontDestroyOnLoad, nor the
    // enemies'), unseen=off|on (the level's enabled renderers that were not drawn switched off: what
    // culling them costs), ulights=off|on (the lights Unity still lights objects with, the directional
    // ones and VitaLit's key lights (culling mask 0) aside, switched off: the per-object light lists).
    // Each logs what it switched (CROW_RENDER_COST).
    static class RenderCost
    {
        static readonly Dictionary<string, List<Behaviour>> offBehaviours = new Dictionary<string, List<Behaviour>>();
        static readonly Dictionary<string, List<Renderer>> offRenderers = new Dictionary<string, List<Renderer>>();

        internal static bool Change(string key, string value)
        {
            if (key == "unseen" && value == "report")
            {
                UnseenReport();
                return true;
            }
            if (key == "level" && value == "report")
            {
                LevelReport();
                return true;
            }
            switch (key)
            {
                case "canvases":
                    Back(key);
                    foreach (Canvas canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
                    {
                        if (!canvas.enabled || !canvas.isRootCanvas) continue;
                        if (value == "report") Watch(canvas);
                        else if (value == "off" && canvas.renderMode == RenderMode.ScreenSpaceOverlay) Off(key, canvas);
                    }
                    break;
                case "uiout":
                    UiOut(value == "on");
                    return true;
                case "ulights":
                    Back(key);
                    if (value == "off")
                        foreach (Light light in UnityEngine.Object.FindObjectsOfType<Light>())
                            if (light.enabled && light.type != LightType.Directional && light.cullingMask != 0) Off(key, light);
                    break;
                case "level":
                case "unseen":
                {
                    Back(key);
                    if (value != "hide" && value != "half" && value != "off") break;
                    var list = offRenderers[key] = new List<Renderer>();
                    int n = 0;
                    foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
                    {
                        if (!renderer.enabled || renderer.gameObject.scene.name == "DontDestroyOnLoad" || Enemy(renderer.transform)) continue;
                        if (renderer.isVisible != (key == "level")) continue;
                        if (value == "half" && (n++ & 1) != 0) continue;
                        renderer.enabled = false;
                        list.Add(renderer);
                    }
                    break;
                }
                default:
                    return false;
            }
            List<Behaviour> behaviours;
            List<Renderer> renderers;
            int count = offBehaviours.TryGetValue(key, out behaviours) ? behaviours.Count : offRenderers.TryGetValue(key, out renderers) ? renderers.Count : 0;
            Debug.Log("CROW_RENDER_COST " + key + "=" + value + ": " + count + " off");
            return true;
        }

        // unseen=report: the enabled renderers not drawn last frame, by what keeps them from the picture
        // (CROW_UNSEEN): no mesh or an empty one, no material, a layer the main camera doesn't draw,
        // zero size, past the far plane, out of the view, or in the view (behind something: the GPU's
        // depth test isn't culling; in view and not drawn means culled by Unity's own tests), by root.
        static void UnseenReport()
        {
            Camera camera = Camera.main;
            if (camera == null) return;
            var planes = GeometryUtility.CalculateFrustumPlanes(camera);
            var reasons = new Dictionary<string, int>();
            var roots = new Dictionary<string, int>();
            int total = 0;
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.isVisible) continue;
                total++;
                string reason;
                var filter = renderer.GetComponent<MeshFilter>();
                Bounds bounds = renderer.bounds;
                float far = camera.farClipPlane;
                if (renderer is MeshRenderer && (filter == null || filter.sharedMesh == null)) reason = "nomesh";
                else if (renderer is MeshRenderer && filter.sharedMesh.vertexCount == 0) reason = "emptymesh";
                else if (renderer.sharedMaterials.All(m => m == null)) reason = "nomaterial";
                else if ((camera.cullingMask & (1 << renderer.gameObject.layer)) == 0) reason = "layer" + renderer.gameObject.layer;
                else if (bounds.size == Vector3.zero) reason = "zerosize";
                else if (bounds.SqrDistance(camera.transform.position) > far * far) reason = "pastfar";
                else if (!GeometryUtility.TestPlanesAABB(planes, bounds)) reason = "outofview";
                else reason = "inview";
                reason += renderer is MeshRenderer ? "" : ":" + renderer.GetType().Name;
                int n;
                reasons.TryGetValue(reason, out n);
                reasons[reason] = n + 1;
                string root = renderer.transform.root.name.Replace(' ', '_') + "/" + reason;
                roots.TryGetValue(root, out n);
                roots[root] = n + 1;
            }
            Debug.Log("CROW_UNSEEN total=" + total + " " + string.Join(" ", reasons.OrderByDescending(p => p.Value).Select(p => p.Key + "=" + p.Value).ToArray()));
            Debug.Log("CROW_UNSEEN roots " + string.Join(" ", roots.OrderByDescending(p => p.Value).Take(40).Select(p => p.Key + "=" + p.Value).ToArray()));
        }

        // level=report: the level's renderers drawn last frame (not the player's or the enemies'), each with
        // its root, kind, queue, vertices and material (CROW_LEVEL_DRAWN), and by material (CROW_LEVEL_MATS).
        static void LevelReport()
        {
            var byMaterial = new Dictionary<string, int>();
            int n = 0;
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled || !renderer.isVisible || renderer.gameObject.scene.name == "DontDestroyOnLoad" || Enemy(renderer.transform)) continue;
                n++;
                var filter = renderer.GetComponent<MeshFilter>();
                Material material = renderer.sharedMaterial;
                string mat = material == null ? "none" : material.name.Replace(' ', '_') + "@" + material.renderQueue;
                int count;
                byMaterial.TryGetValue(mat, out count);
                byMaterial[mat] = count + 1;
                Bounds b = renderer.bounds;
                Debug.Log("CROW_LEVEL_DRAWN " + Perf.Path(renderer.transform, 3) + " kind=" + renderer.GetType().Name +
                          " verts=" + (filter != null && filter.sharedMesh != null ? filter.sharedMesh.vertexCount : -1) +
                          " subs=" + renderer.sharedMaterials.Length + " mat=" + mat + " size=" + b.size.magnitude.ToString("F1") +
                          " at=" + b.center.x.ToString("F0") + "," + b.center.y.ToString("F0") + "," + b.center.z.ToString("F0"));
            }
            Debug.Log("CROW_LEVEL_MATS drawn=" + n + " materials=" + byMaterial.Count + " " +
                      string.Join(" ", byMaterial.OrderByDescending(p => p.Value).Select(p => p.Key + "=" + p.Value).ToArray()));
        }

        // uiout=on|off: the switched-off subtrees inside the enabled root canvases moved below an inactive
        // root of their own (local poses kept), and back to their places: what a canvas's inactive
        // RectTransforms cost its per-frame updates.
        static readonly List<KeyValuePair<Transform, KeyValuePair<Transform, int>>> movedOut = new List<KeyValuePair<Transform, KeyValuePair<Transform, int>>>();
        static GameObject outHolder;

        static void UiOut(bool on)
        {
            for (int i = movedOut.Count - 1; i >= 0; i--)
            {
                Transform part = movedOut[i].Key, parent = movedOut[i].Value.Key;
                if (part == null || parent == null) continue;
                part.SetParent(parent, false);
                part.SetSiblingIndex(movedOut[i].Value.Value);
            }
            int count = movedOut.Count, transforms = 0;
            movedOut.Clear();
            if (on)
            {
                if (outHolder == null)
                {
                    outHolder = new GameObject("CrowUiOut");
                    outHolder.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(outHolder);
                }
                var found = new List<Transform>();
                foreach (Canvas canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
                    if (canvas.enabled && canvas.isRootCanvas && canvas.renderMode == RenderMode.ScreenSpaceOverlay) Inactive(canvas.transform, found);
                foreach (Transform part in found)
                {
                    movedOut.Add(new KeyValuePair<Transform, KeyValuePair<Transform, int>>(part, new KeyValuePair<Transform, int>(part.parent, part.GetSiblingIndex())));
                    transforms += part.GetComponentsInChildren<Transform>(true).Length;
                }
                // Parents before children in the list, so putting back in reverse order restores sibling indices.
                foreach (Transform part in found) part.SetParent(outHolder.transform, false);
                count = movedOut.Count;
            }
            Debug.Log("CROW_RENDER_COST uiout=" + (on ? "on" : "off") + ": " + count + " subtrees" + (on ? ", " + transforms + " transforms" : " back"));
        }

        static void Inactive(Transform parent, List<Transform> found)
        {
            foreach (Transform child in parent)
            {
                if (!child.gameObject.activeSelf) found.Add(child);
                else Inactive(child, found);
            }
        }

        // canvases=report: each enabled root canvas, its place, mode and content, and in how many of the
        // next 30 frames its own transform and any of its graphics' transforms changed (CROW_CANVAS).
        static readonly List<Canvas> watched = new List<Canvas>();
        static readonly List<int> movedFrames = new List<int>(), dirtyFrames = new List<int>();
        static int watchFrames = -1;

        static void Watch(Canvas canvas)
        {
            if (watchFrames < 0)
            {
                watchFrames = 0;
                CrowEvents.BeforeRender("RenderCost.WatchTick", WatchTick);
            }
            if (watched.Count == 0) watchFrames = 0;
            canvas.transform.hasChanged = false;
            foreach (Graphic graphic in canvas.GetComponentsInChildren<Graphic>(false)) graphic.transform.hasChanged = false;
            watched.Add(canvas);
            movedFrames.Add(0);
            dirtyFrames.Add(0);
        }

        static void WatchTick()
        {
            if (watched.Count == 0) return;
            for (int i = 0; i < watched.Count; i++)
            {
                Canvas canvas = watched[i];
                if (canvas == null) continue;
                if (canvas.transform.hasChanged) movedFrames[i]++;
                canvas.transform.hasChanged = false;
                bool dirty = false;
                foreach (Graphic graphic in canvas.GetComponentsInChildren<Graphic>(false))
                {
                    if (graphic.transform.hasChanged) dirty = true;
                    graphic.transform.hasChanged = false;
                }
                if (dirty) dirtyFrames[i]++;
            }
            if (++watchFrames < 30) return;
            for (int i = 0; i < watched.Count; i++)
            {
                Canvas canvas = watched[i];
                if (canvas == null) continue;
                Graphic[] graphics = canvas.GetComponentsInChildren<Graphic>(false);
                Debug.Log("CROW_CANVAS " + Perf.Path(canvas.transform, 8) + " mode=" + canvas.renderMode + " order=" + canvas.sortingOrder +
                          " transforms=" + canvas.GetComponentsInChildren<RectTransform>(true).Length + " active=" + canvas.GetComponentsInChildren<RectTransform>(false).Length +
                          " graphics=" + graphics.Length + " drawn=" + graphics.Count(g => g.enabled && g.canvasRenderer != null && !g.canvasRenderer.cull) +
                          " canvases=" + canvas.GetComponentsInChildren<Canvas>(false).Length + " moved=" + movedFrames[i] + "/30 graphicsMoved=" + dirtyFrames[i] + "/30");
                var parts = new StringBuilder("CROW_CANVAS_PARTS " + canvas.name.Replace(' ', '_'));
                var subtrees = new List<KeyValuePair<Transform, int>>();
                var level = new List<Transform> { canvas.transform };
                for (int depth = 0; depth < 5; depth++)
                {
                    var next = new List<Transform>();
                    foreach (Transform parent in level)
                        foreach (Transform child in parent)
                        {
                            int size = child.GetComponentsInChildren<Transform>(true).Length;
                            if (size < 20) continue;
                            subtrees.Add(new KeyValuePair<Transform, int>(child, size));
                            next.Add(child);
                        }
                    level = next;
                }
                foreach (KeyValuePair<Transform, int> pair in subtrees.OrderByDescending(p => p.Value).Take(40))
                    parts.Append(' ').Append(Perf.Path(pair.Key, 2)).Append('=').Append(pair.Value).Append(pair.Key.gameObject.activeSelf ? "" : "(off)")
                         .Append('/').Append(pair.Key.GetComponentsInChildren<Transform>(false).Length);
                Debug.Log(parts.ToString());
            }
            watched.Clear();
            movedFrames.Clear();
            dirtyFrames.Clear();
        }

        static bool Enemy(Transform transform)
        {
            for (Transform t = transform; t != null; t = t.parent)
                if (t.name.StartsWith("Enemy ", StringComparison.Ordinal) && t.GetComponent<Rigidbody>() != null) return true;
            return false;
        }

        static void Off(string key, Behaviour behaviour)
        {
            List<Behaviour> list;
            if (!offBehaviours.TryGetValue(key, out list)) offBehaviours[key] = list = new List<Behaviour>();
            behaviour.enabled = false;
            list.Add(behaviour);
        }

        static void Back(string key)
        {
            List<Behaviour> behaviours;
            if (offBehaviours.TryGetValue(key, out behaviours))
                foreach (Behaviour behaviour in behaviours)
                    if (behaviour != null) behaviour.enabled = true;
            offBehaviours.Remove(key);
            List<Renderer> renderers;
            if (offRenderers.TryGetValue(key, out renderers))
                foreach (Renderer renderer in renderers)
                    if (renderer != null) renderer.enabled = true;
            offRenderers.Remove(key);
        }
    }

    // -crow-menu-cull: the Main Camera draws only the menus' layer while a full-screen menu is up.
    static class MenuCull
    {
        const int MenuLayers = 1 << 5;  // UI: the menus' 3D objects (portrait, equipped gun, item, monitor) and lights
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

        // The mask to give a camera back after a load's gap (Loading.cs SceneGap's drain, which culls nothing
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
            GameObject holder = GameObject.Find("UI Canvases");
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
                if (mask == 0 || mask == MenuLayers) return;  // a load's gap culls nothing (Loading.cs): leave it
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
                Camera.onPreCull += PreCull;
                Camera.onPostRender += PostRender;
                SceneManager.sceneUnloaded += scene =>
                {
                    foreach (Camera gone in changed.Keys.Where(c => c == null).ToList()) changed.Remove(gone);
                    foreach (Camera gone in limits.Keys.Where(c => c == null).ToList()) limits.Remove(gone);
                    left.RemoveWhere(c => c == null);
                };
            }
            // Every camera back to its own near plane, whatever comes next: PreCull starts again from the game's.
            // (Only for "game" before 21 Oct: 1 -> 0.5 m left the cameras at 1, and 1 -> 2 took 1 m for the game's
            // own, the fog mapped from there.)
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
