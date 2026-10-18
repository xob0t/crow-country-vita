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
            string value = Perf.Value(args, "-crow-rt-cameras") ?? (Application.platform == RuntimePlatform.PSP2 ? "on" : null);
            if (value != null) Set(value);
            string menu = Perf.Value(args, "-crow-menu-cull") ?? (Application.platform == RuntimePlatform.PSP2 ? "on" : null);
            if (menu != null) MenuCull.Set(menu == "on");
            string fog = Perf.Value(args, "-crow-fog-far");
            if (fog != null) FogFar(fog == "on");
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
            if (camera != fogCamera || (setFar > 0f && !Mathf.Approximately(camera.farClipPlane, setFar))) UndoFog();
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
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                nextScan = nextShown = nextFog = 0f;
                if (nextReport >= 0f) nextReport = Time.realtimeSinceStartup + 5f;
                rendered.Clear();
            };
            Application.onBeforeRender += Tick;
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
                Application.onBeforeRender += Tick;
                SceneManager.sceneLoaded += (scene, mode) => nextFind = 0f;
            }
            if (!on) Release();
            Debug.Log("CROW_CAMERA menu cull " + (on ? "on" : "off"));
        }

        static void Release()
        {
            if (culling && camera != null && camera.cullingMask == MenuLayers) camera.cullingMask = saved;
            culling = false;
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
}
