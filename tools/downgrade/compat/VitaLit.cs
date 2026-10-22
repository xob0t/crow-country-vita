using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Every light in one pass. Built-in forward rendering draws each object once more for every pixel
// light after the main directional one (ForwardAdd): Roadside's one spot light (the daylight pool,
// range 25 m) adds a pass to 260 of its 281 renderers. On the PS Vita each of those draws costs the
// main thread about as much as the object itself, and the GPU a second fill of its pixels. With vita,
// the game's Standard materials (and SFBStandard's: SFB's copy of Standard, with the same properties,
// on one material, Entrance's "tiles floor" path; left to it, that path lost the key lights, which
// light nothing else, and its decompiled shadow read is the PC's screen-space one, so it drew nearly
// black; and Legacy Shaders/Diffuse's, see LegacyDiffuse) draw with CrowVita/Lit (vita_shaders/CrowLit.shader: Standard's
// simple form, no ForwardAdd pass), which draws the main directional light, the harmonics and the 4
// point lights Unity gives each object per vertex, and up to 4 key lights set here as globals per
// pixel, Lambert only: the spot and point lights that reach the camera's view (spots first), on the
// layers most of those renderers are on, and one fill directional light. Key lights get culling mask
// 0 and no shadows, so Unity doesn't light anything with them again; the others stay Unity's, at 0
// pixel lights (directional lights are Important, so the brightest is in the base pass). The game's
// PlayMaker actions keep driving the lights: the globals follow each key light's intensity, colour,
// range, angle, position and enabled state every frame, and the key set is chosen again every
// 0.25 s and in the frame a spot light comes on (the guns' laser sights as aiming starts). On the
// PS Vita in Roadside: 61-62 -> 38 ms a frame, the main thread 34-35 -> 24 ms.
//   -crow-lit game|vita    game keeps Standard and the game's lights (the default on other players);
//                          vita as above (the PS Vita's default)
//   -crow-lit-keys <n>     at most n point and spot key lights (default 4, at most 4; 8 slots cost Mansion
//                          8 ms for nothing seen)
//   -crow-lit-soa on|off   on (the default): the shader works out the key lights side by side, a light
//                          in each lane of its vectors (CROW_KEYS_2/4); off: one after another, the
//                          4-slot form it had before (CROW_KEYS_LOOP), for comparisons
//   -crow-lit-min-range <m>  point lights with a shorter range stay Unity's, per vertex (default 0)
//   -crow-lit-log          log the key lights whenever they change (CROW_LIT)
//   -crow-lit-bundle <path>  load CrowVita/Lit from an asset bundle (for builds without it)
//   -crow-lit-debug <n>    a value for the shader's _CrowDebug (for a debugging build of it)
// -crow-ab and -crow-cycle take lit=game|vita, litkeys=<n>, litrange=<m> and litsoa=on|off.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    static class VitaLit
    {
        const int Keys = 4;
        static bool on, hooked, log;
        static Shader lit, standard;
        // The swapped materials' original shaders, by instance ID: a static that holds an asset can keep
        // it, and what it references, through Resources.UnloadUnusedAssets (IL2CPP's liveness pass starts
        // from the statics; CamEffect's list of camera effects kept their render textures that way). The
        // areas' materials were not seen to stay with the Material keys, but nothing here needs them held.
        static readonly Dictionary<int, Shader> swapped = new Dictionary<int, Shader>();
        // Legacy Diffuse materials' own colours, by instance ID (LegacyDiffuse changes them).
        static readonly Dictionary<int, Color> diffuseColors = new Dictionary<int, Color>();
        static readonly Dictionary<Light, KeyValuePair<int, LightShadows>> keyed = new Dictionary<Light, KeyValuePair<int, LightShadows>>();
        static readonly Dictionary<Light, LightRenderMode> forced = new Dictionary<Light, LightRenderMode>();
        static readonly List<Light> keys = new List<Light>();
        static readonly Vector4[] positions = new Vector4[Keys], directions = new Vector4[Keys], colors = new Vector4[Keys];
        static int pixelLights = -1;
        static float nextSelect, sceneTime;
        static int swapPasses;
        static string lastLog, bundle;
        static int maxKeys = 4;
        static bool soa = true;
        static float minRange = 0f;

        // -crow-cycle's litkeys= and litrange=.
        internal static void SetLimits(string key, float value)
        {
            if (key == "litkeys") maxKeys = Mathf.Clamp((int)value, 0, Keys);
            else if (key == "litsoa") soa = value != 0f;
            else minRange = value;
            nextSelect = 0f;
        }
        static UnityEngine.Object[] bundleAssets;

        public static void Install(string[] args)
        {
            log = Array.IndexOf(args, "-crow-lit-log") >= 0;
            bundle = CrowFlags.Value(args, "-crow-lit-bundle");
            maxKeys = Mathf.Clamp((int)CrowFlags.Number(args, "-crow-lit-keys", maxKeys), 0, Keys);
            minRange = CrowFlags.Number(args, "-crow-lit-min-range", minRange);
            soa = CrowFlags.Value(args, "-crow-lit-soa") != "off";
            Shader.SetGlobalFloat("_CrowDebug", CrowFlags.Number(args, "-crow-lit-debug", 0f));
            string value = CrowFlags.Value(args, "-crow-lit") ?? (Application.platform == RuntimePlatform.PSP2 ? "vita" : null);
            if (value != null) Set(value);
        }

        internal static void Set(string value)
        {
            bool want = value == "vita";
            if (want && lit == null)
            {
                if (bundle != null)
                {
                    // A build without the shader: the shader and its variants' materials from an asset bundle.
                    AssetBundle loaded = AssetBundle.LoadFromFile(bundle);
                    if (loaded != null) bundleAssets = loaded.LoadAllAssets();
                    Debug.Log("CROW_LIT bundle " + bundle + ": " + (loaded == null ? "failed" : bundleAssets.Length + " assets"));
                    lit = bundleAssets == null ? null : bundleAssets.OfType<Shader>().FirstOrDefault();
                }
                if (lit == null) lit = Shader.Find("CrowVita/Lit");
                standard = Shader.Find("Standard");
                if (lit == null || !lit.isSupported || !HasProperty(lit, "_CrowLitV3"))
                {
                    Debug.Log("CROW_LIT CrowVita/Lit " + (lit == null ? "is not in the build" : !lit.isSupported ? "is not supported" : "is an older version") +
                              "; lights stay the game's");
                    lit = null;
                    return;
                }
            }
            // game on a build whose materials were saved with CrowVita/Lit (CROW_LIT_BAKE): they go back
            // to their shaders, here and after each load.
            if (!want)
            {
                restoreBaked = true;
                if (lit == null) lit = Shader.Find("CrowVita/Lit");
                if (standard == null) standard = Shader.Find("Standard");
            }
            if (!hooked)
            {
                hooked = true;
                CrowEvents.Loaded("VitaLit.sceneLoaded", (scene, mode) =>
                {
                    sceneTime = Time.realtimeSinceStartup;
                    swapPasses = 0;
                    nextSelect = 0f;
                    nextList = 0f;
                    nextCamera = 0f;
                    sendAll = true;
                    if (!on && restoreBaked) Restore();
                });
                CrowEvents.BeforeRender("VitaLit.Tick", Tick);
            }
            if (want == on)
            {
                if (!want) Restore();
                return;
            }
            on = want;
            if (on)
            {
                pixelLights = QualitySettings.pixelLightCount;
                QualitySettings.pixelLightCount = 0;
                linear = QualitySettings.activeColorSpace == ColorSpace.Linear;  // the project's: fixed for the run
                sendAll = true;
                swapPasses = 0;
                nextSelect = 0f;
                sceneTime = Time.realtimeSinceStartup;
                Tick();
            }
            else
            {
                Restore();
                foreach (Light light in keyed.Keys.ToList()) Unkey(light);
                foreach (KeyValuePair<Light, LightRenderMode> pair in forced)
                    if (pair.Key != null) pair.Key.renderMode = pair.Value;
                forced.Clear();
                keys.Clear();
                keyTransforms.Clear();
                if (pixelLights >= 0) QualitySettings.pixelLightCount = pixelLights;
                foreach (string name in keywords) Shader.DisableKeyword(name);
                keyword = null;
            }
            Debug.Log("CROW_LIT " + (on ? "vita: one pass, key lights per pixel" : "game"));
        }

        static bool restoreBaked;
        static readonly Dictionary<string, Shader> originals = new Dictionary<string, Shader>();

        // Whether a shader has a property, through a throwaway material.
        static bool HasProperty(Shader shader, string name)
        {
            var probe = new Material(shader);
            bool has = probe.HasProperty(name);
            UnityEngine.Object.Destroy(probe);
            return has;
        }

        // The materials CrowVita/Lit draws back on their own shaders: those swapped here, and those the
        // build saved with CrowVita/Lit (their _CrowLitFrom says which shader they had; a Legacy
        // Diffuse one's colour is in _CrowLitColor). A build saved so has those shaders only if
        // something else uses them, so -crow-lit game wants a build made with CROW_LIT_BAKE=off.
        static void Restore()
        {
            bakedBuild = false;  // the materials put back are on their shaders: the next vita pass looks again
            if (lit == null) return;
            int restored = 0, missing = 0;
            int from = Shader.PropertyToID("_CrowLitFrom"), own = Shader.PropertyToID("_CrowLitColor");
            foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (material == null || material.shader != lit) continue;
                Shader shader;
                bool diffuse = diffuseColors.ContainsKey(material.GetInstanceID());
                if (!swapped.TryGetValue(material.GetInstanceID(), out shader))
                {
                    float kind = material.HasProperty(from) ? material.GetFloat(from) : 0f;
                    if (kind < 0.5f) continue;
                    string name = kind < 1.5f ? "Standard" : kind < 2.5f ? "SFBStandard" : "Legacy Shaders/Diffuse";
                    if (!originals.TryGetValue(name, out shader)) originals[name] = shader = Shader.Find(name);
                    if (shader == null || !shader.isSupported)
                    {
                        missing++;
                        continue;
                    }
                    if (kind > 2.5f) diffuseColors[material.GetInstanceID()] = material.GetColor(own);
                    diffuse = kind > 2.5f;
                }
                int queue = material.renderQueue;
                material.shader = shader;
                material.renderQueue = queue;
                Color color;
                if (diffuse && diffuseColors.TryGetValue(material.GetInstanceID(), out color))
                {
                    material.color = color;
                    material.DisableKeyword("_GLOSSYREFLECTIONS_OFF");
                    material.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
                }
                restored++;
            }
            swapped.Clear();
            diffuseColors.Clear();
            if (restored > 0 || missing > 0)
                Debug.Log("CROW_LIT game: " + restored + " materials back on their shaders" +
                          (missing > 0 ? ", " + missing + " left on CrowVita/Lit (their shader is not in this build: build with CROW_LIT_BAKE=off)" : ""));
        }

        // Whether the build saved the materials with CrowVita/Lit (the first pass after a load finds
        // them so): the later passes, for what the game creates or loads late, are then left out
        // (what it loads is saved so too).
        static bool baked;
        // Since 21 Oct, once a first pass has found that, the first passes after later loads leave the
        // material scan out too: Resources.FindObjectsOfTypeAll<Material> makes a managed object, held
        // by one of IL2CPP's pinned GC handles (Memory.cs, GcHandles), for every material in memory, and
        // in such a build it found none to swap (every logged pass on the PS Vita: "0 materials to
        // CrowVita/Lit", 139 to 419 saved so). Haunted Hilltop's first frame took handle 65,537 there,
        // which made the handle table grow, and crash, on 21 Oct. The renderer pass below then looks at
        // the first material of every renderer it lists (FindObjectsOfType: those on active
        // GameObjects), and one on a shader the scan swaps (Standard, SFBStandard, Legacy Diffuse; until
        // 22 Oct Standard only) brings the scan back at once; other slots, and materials no such renderer
        // has, aren't seen. Every slot (GetSharedMaterials) made the post-load pass 2.5 to 7.5 times as
        // long on the PS Vita (Ocean Kingdom 31 -> 235 ms, P1-verify 22 Oct): a managed Material per slot.
        // Restore (lit=game) clears it. (A cache of the layer mask
        // by the scenes loaded and the renderer counts, also 21 Oct, went on 22 Oct: on the PS Vita only
        // the first pass after a load runs, and the counts differed on most revisits, Haunted Hilltop's
        // on every one, so it saved next to nothing.)
        static bool bakedBuild;
        static readonly Material[] NoMaterials = new Material[0];
        // Per shader (instance ID): whether the material scan swaps it.
        static readonly Dictionary<int, bool> swappable = new Dictionary<int, bool>();

        static void Tick()
        {
            if (!on) return;
            float now = Time.realtimeSinceStartup;
            // Materials: at once, and again 1, 3 and 10 s after a load (what the game creates or loads late).
            float since = now - sceneTime;
            if (swapPasses == 0 || (!baked && ((swapPasses == 1 && since > 1f) || (swapPasses == 2 && since > 3f) || (swapPasses == 3 && since > 10f))))
            {
                swapPasses++;
                Swap();
            }
            if (now >= nextSelect || Woken())
            {
                if (now < nextSelect) wokenSelects++;
                selects++;
                nextSelect = now + 0.25f;
                Select();
            }
            Upload();
            if (log && now >= nextSelectsLog)
            {
                if (nextSelectsLog > 0f) Debug.Log("CROW_LIT selects=" + selects + " woken=" + wokenSelects + " frames=" + (Time.frameCount - selectsFrame));
                nextSelectsLog = now + 10f;
                selects = wokenSelects = 0;
                selectsFrame = Time.frameCount;
            }
        }

        // -crow-lit-log: key light selections every 10 s, those a watched spot light coming on started.
        static int selects, wokenSelects, selectsFrame;
        static float nextSelectsLog;

        // A watched spot light that came on since the last Select (this runs before Unity culls the frame's lights).
        static bool Woken()
        {
            for (int i = 0; i < watched.Count; i++)
            {
                Light light = watched[i];
                if (light != null && light.isActiveAndEnabled && light.intensity > 0f) return true;
            }
            return false;
        }

        static void Swap()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int count = 0, changedQueue = 0, transparent = 0, cutout = 0, saved = 0;
            int from = Shader.PropertyToID("_CrowLitFrom");
            bool first = swapPasses == 1, scanned = !bakedBuild;
            foreach (Material material in scanned ? Resources.FindObjectsOfTypeAll<Material>() : NoMaterials)
            {
                Shader shader = material.shader;
                if (shader == null) continue;
                if (shader == lit)
                {
                    if (first && material.GetFloat(from) > 0.5f) saved++;
                    continue;
                }
                bool diffuse = false;
                if (shader != standard)
                {
                    string name = shader.name;
                    diffuse = name == "Legacy Shaders/Diffuse";
                    if (!diffuse && name != "Standard" && name != "SFBStandard") continue;
                }
                swapped[material.GetInstanceID()] = shader;
                if (diffuse) LegacyDiffuse(material);
                // Setting a shader resets the material's render queue to the shader's (Geometry): the
                // game's Fade and Transparent materials (queue 3000, no depth writes) were then drawn
                // before the ground, which covered them (Roadside's car glass). Keep the queue.
                int queue = material.renderQueue;
                material.shader = lit;
                if (material.renderQueue != queue)
                {
                    material.renderQueue = queue;
                    changedQueue++;
                }
                if (queue > 2450 || material.IsKeywordEnabled("_ALPHABLEND_ON") || material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON")) transparent++;
                else if (material.IsKeywordEnabled("_ALPHATEST_ON")) cutout++;
                count++;
            }
            // The layers most CrowVita/Lit renderers are on (each with a tenth of them or more).
            var perLayer = new int[32];
            int litRenderers = 0, lightmapped = 0;
            bool onStandard = false;
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                Material material = renderer.sharedMaterial;
                if (material == null) continue;
                Shader shader = material.shader;
                if (shader != VitaLit.lit)
                {
                    if (!scanned && !onStandard && Swappable(shader)) onStandard = true;
                    continue;
                }
                perLayer[renderer.gameObject.layer]++;
                litRenderers++;
                if (renderer.lightmapIndex >= 0 && renderer.lightmapIndex < 0xFFFE) lightmapped++;
            }
            if (onStandard)
            {
                // Not every material saved so after all: this pass again with the material scan.
                bakedBuild = false;
                Debug.Log("CROW_LIT a renderer's material is on a shader the scan swaps: the material scan is back");
                Swap();
                return;
            }
            majorLayers = 0;
            for (int layer = 0; layer < 32; layer++)
                if (perLayer[layer] * 10 >= litRenderers && perLayer[layer] > 0) majorLayers |= 1 << layer;
            if (first)
            {
                baked = bakedBuild || saved > 0;
                bakedBuild = baked;
            }
            if (log)
                Debug.Log("CROW_LIT " + count + " materials to CrowVita/Lit (" + transparent + " transparent, " + cutout + " cutout, " + changedQueue +
                          " render queues kept), " + (scanned ? saved + " saved so by the build" : "material scan left out (saved so by the build)") + "; " +
                          litRenderers + " renderers, " + lightmapped + " lightmapped (" + LightmapSettings.lightmaps.Length + " lightmaps); layers " +
                          majorLayers.ToString("X") + "; pass " + swapPasses + " in " + clock.ElapsedMilliseconds + " ms");
        }

        // Whether the material scan swaps a material on this shader (Standard, SFBStandard, Legacy Diffuse), by the
        // shader's instance ID (a name read is a new string).
        static bool Swappable(Shader shader)
        {
            if (shader == null || shader == lit) return false;
            if (shader == standard) return true;
            bool swaps;
            int id = shader.GetInstanceID();
            if (swappable.TryGetValue(id, out swaps)) return swaps;
            string name = shader.name;
            return swappable[id] = name == "Standard" || name == "SFBStandard" || name == "Legacy Shaders/Diffuse";
        }

        // Legacy Shaders/Diffuse (texture times colour, Lambert; 48 untextured materials, white, red,
        // green and grey) as CrowVita/Lit sees it: no specular highlights or reflections, smoothness and
        // metallic 0 (a variant the build has), and the colour divided by Standard's dielectric
        // diffuse factor in gamma space (1 - 0.2209), so the main light and the ambient light it as
        // Diffuse did. The game's visible ones: Swan Boats' swans (merged), Arcade's basketball hoop,
        // Office Tolman's elevator map lights, Control Room's and Fairy Pool's combined parts; the rest
        // are collider meshes with their renderers off and the examine sets' marker spheres.
        static void LegacyDiffuse(Material material)
        {
            Color color = material.color;
            diffuseColors[material.GetInstanceID()] = color;
            const float dielectricDiffuse = 1f - 0.220916301f;
            material.color = new Color(color.r / dielectricDiffuse, color.g / dielectricDiffuse, color.b / dielectricDiffuse, color.a);
            material.SetFloat("_Glossiness", 0f);
            material.SetFloat("_Metallic", 0f);
            material.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
            material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
        }

        static float Luminance(Color c)
        {
            return c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
        }

        // A key light lights every CrowVita/Lit renderer in its reach: only lights whose culling mask has
        // the layers most of them are on qualify (the game keeps some lights to a few layers: Pool's
        // "only below" and "only Object" spots, Mansion's ghosts' lights, the car's lamps in Driving
        // Intro); the others stay Unity's, per vertex or in the harmonics.
        static int majorLayers = ~(1 << 5);

        // Select runs every 0.25 s in play, so it allocates nothing (strings only with -crow-lit-log): the
        // scene's lights are listed again every 2 s and after each load, the buffers are reused, and the
        // key lights are picked by hand. A 2 s listing that finds another periodic scan in its frame
        // (CrowEvents.Turn) waits for the next Select; until 22 Oct it came in the same frame as the
        // camera's 2 s Camera.main every time.
        // The list has the lights that are off too, and the spot lights among them that would be key
        // lights are watched every frame (Woken): a spot light that isn't a key light is Unity's, per
        // vertex, where Unity drops its cone and lights everything in its range as a point light. The
        // guns' laser sights (red, green or blue spots of 1.5 degrees, 20 m, intensity 10) switch on as
        // aiming starts; listed only while on, the handgun's lit the wall in front of Mara red for up to
        // 2 s each time she aimed. Now it is a key light the frame it comes on.
        static readonly List<Light> lights = new List<Light>(), watched = new List<Light>();
        static float nextList;
        static readonly Plane[] frustum = new Plane[6];
        static readonly List<Light> candidates = new List<Light>(), chosen = new List<Light>(), scratch = new List<Light>();
        static readonly List<float> scores = new List<float>();
        static readonly List<string> excluded = new List<string>();

        static void Select()
        {
            float now = Time.realtimeSinceStartup;
            if (now >= nextList && CrowEvents.Turn(nextList <= 0f))
            {
                nextList = now + 2f;
                // The scenes' lights, on or off (FindObjectsOfType lists only those on); not prefabs'.
                lights.Clear();
                foreach (Light light in Resources.FindObjectsOfTypeAll<Light>())
                    if (light != null && light.gameObject.scene.IsValid()) lights.Add(light);
            }
            Light main = null;
            foreach (Light light in lights)
            {
                if (light == null || light.type != LightType.Directional || !light.isActiveAndEnabled || keyed.ContainsKey(light)) continue;
                if (light.cullingMask == 0 || (light.cullingMask & ~(1 << 5)) == 0) continue;
                if (main == null || light.intensity * Luminance(light.color) > main.intensity * Luminance(main.color)) main = light;
            }
            int world = majorLayers;
            Camera view = MainCamera(now);
            Vector3 camera = view != null ? view.transform.position : Vector3.zero;
            if (view != null) GeometryUtility.CalculateFrustumPlanes(view, frustum);
            candidates.Clear();
            scores.Clear();
            excluded.Clear();
            watched.Clear();
            Light fill = null;
            foreach (Light light in lights)
            {
                if (light == null) continue;
                LightType type = light.type;
                if (type == LightType.Directional)
                {
                    // Every directional light stays Important: Unity puts the brightest in the base pass.
                    if (!forced.ContainsKey(light)) forced[light] = light.renderMode;
                    if (light.renderMode != LightRenderMode.ForcePixel) light.renderMode = LightRenderMode.ForcePixel;
                }
                if (type == LightType.Area || light == main) continue;
                KeyValuePair<int, LightShadows> saved;
                int mask = keyed.TryGetValue(light, out saved) ? saved.Key : light.cullingMask;
                if (!light.isActiveAndEnabled || light.intensity <= 0f)
                {
                    // Off: a spot light that could be a key light is picked the frame it comes on.
                    if (type == LightType.Spot && (mask & world) == world) watched.Add(light);
                    continue;
                }
                if ((mask & world) != world)
                {
                    if (log) excluded.Add(light.type + ":" + Perf.Path(light.transform, 2) + "@" + light.range.ToString("F1") + "/mask=" + mask.ToString("X"));
                    continue;
                }
                if (light.type == LightType.Directional)
                {
                    // One fill directional light, the brightest.
                    if (fill == null || light.intensity > fill.intensity) fill = light;
                    continue;
                }
                Vector3 position = light.transform.position;
                // Only lights that reach what the camera sees.
                if (view != null && !GeometryUtility.TestPlanesAABB(frustum, new Bounds(position, Vector3.one * (2f * light.range)))) continue;
                // Each key slot costs every pixel of the screen, so at most maxKeys (Mansion: 4 slots with
                // its candles 33.3 ms a frame, 8 slots 41.9, the game 66); -crow-lit-min-range leaves
                // short-range point lights to Unity (per vertex) instead.
                if (light.type == LightType.Point && light.range < minRange) continue;
                float distance = Mathf.Max(0f, Vector3.Distance(camera, position) - light.range);
                float score = light.range * Mathf.Sqrt(light.intensity * Luminance(light.color)) / (1f + distance);
                if (light.type == LightType.Spot) score += 1e6f;
                candidates.Add(light);
                scores.Add(score);
            }
            // The fill light and the maxKeys best point and spot lights, best first.
            chosen.Clear();
            if (fill != null) chosen.Add(fill);
            for (int n = 0; n < maxKeys; n++)
            {
                int best = -1;
                for (int i = 0; i < candidates.Count; i++)
                    if (candidates[i] != null && (best < 0 || scores[i] > scores[best])) best = i;
                if (best < 0) break;
                chosen.Add(candidates[best]);
                candidates[best] = null;
            }
            scratch.Clear();
            foreach (KeyValuePair<Light, KeyValuePair<int, LightShadows>> pair in keyed)
                if (pair.Key == null || !chosen.Contains(pair.Key)) scratch.Add(pair.Key);
            foreach (Light light in scratch)
                if (light == null) keyed.Remove(light);
                else Unkey(light);
            foreach (Light light in chosen)
                if (!keyed.ContainsKey(light))
                {
                    keyed[light] = new KeyValuePair<int, LightShadows>(light.cullingMask, light.shadows);
                    light.cullingMask = 0;
                    light.shadows = LightShadows.None;
                }
            keys.Clear();
            keys.AddRange(chosen);
            keyTransforms.Clear();
            foreach (Light light in chosen) keyTransforms.Add(light.transform);
            if (log)
            {
                var line = new System.Text.StringBuilder("CROW_LIT keys");
                foreach (Light light in keys) line.Append(' ').Append(light.type).Append(':').Append(Perf.Path(light.transform, 2)).Append('@').Append(light.range.ToString("F1"));
                line.Append(main == null ? " main=none" : " main=" + Perf.Path(main.transform, 2) + "/mask=" + main.cullingMask.ToString("X"));
                if (excluded.Count > 0) line.Append(" other-layers ").Append(string.Join(" ", excluded.ToArray()));
                string text = line.ToString();
                if (text != lastLog) Debug.Log(text);
                lastLog = text;
            }
        }

        // Camera.main as Select last found it, asked again after each load and when that camera is gone
        // or off. Unity 2018's Camera.main walks the scene's objects for the MainCamera tag
        // (FindMainCamera: 3.2% of the main thread in a Station Square fight, Windows player at the
        // PS Vita's speed), and Select runs in every frame a watched spot light comes on. Every 2 s the
        // enabled cameras are looked at (Camera.GetAllCameras, a copy of the engine's list): while the
        // camera found is the only one tagged MainCamera there, it is what Camera.main would give, and
        // only otherwise is Camera.main asked. Until 22 Oct it was asked every 2 s (0.41% of the same
        // profile, about 8 ms in one frame every 2 s).
        internal static bool CacheCamera = true;
        static Camera mainCamera;
        static float nextCamera;
        static Camera[] enabledCameras = new Camera[8];

        static Camera MainCamera(float now)
        {
            if (!CacheCamera) return Camera.main;
            if (mainCamera == null || nextCamera <= 0f || !mainCamera.isActiveAndEnabled)
            {
                mainCamera = Camera.main;
                nextCamera = now + 2f;
            }
            else if (now >= nextCamera)
            {
                nextCamera = now + 2f;
                if (!OnlyMainCamera(mainCamera)) mainCamera = Camera.main;
            }
            return mainCamera;
        }

        // Whether the camera is the one enabled camera tagged MainCamera (Camera.main: "the first enabled camera
        // tagged MainCamera"; the engine's list holds the enabled cameras of active GameObjects).
        static bool OnlyMainCamera(Camera camera)
        {
            int count = Camera.allCamerasCount;
            if (count > enabledCameras.Length) enabledCameras = new Camera[count * 2];
            count = Camera.GetAllCameras(enabledCameras);
            bool self = false, other = false;
            for (int i = 0; i < count; i++)
            {
                Camera listed = enabledCameras[i];
                if (listed == null || !listed.CompareTag("MainCamera")) continue;
                if (listed == camera) self = true;
                else other = true;
            }
            Array.Clear(enabledCameras, 0, count);  // no cameras held
            return self && !other;
        }

        static void Unkey(Light light)
        {
            KeyValuePair<int, LightShadows> saved = keyed[light];
            keyed.Remove(light);
            if (light == null) return;
            light.cullingMask = saved.Key;
            light.shadows = saved.Value;
        }

        static readonly string[] keywords = { "CROW_KEYS_2", "CROW_KEYS_4", "CROW_KEYS_LOOP" };
        static readonly int[] laneIds =
        {
            Shader.PropertyToID("_CrowKeyX"), Shader.PropertyToID("_CrowKeyY"), Shader.PropertyToID("_CrowKeyZ"), Shader.PropertyToID("_CrowKeyW"),
            Shader.PropertyToID("_CrowKeyDX"), Shader.PropertyToID("_CrowKeyDY"), Shader.PropertyToID("_CrowKeyDZ"), Shader.PropertyToID("_CrowKeyDW"),
        };
        static readonly Vector4[] lanes = new Vector4[8];
        static string keyword;
        static readonly int KeyPosId = Shader.PropertyToID("_CrowKeyPos"), KeyDirId = Shader.PropertyToID("_CrowKeyDir"),
            KeyColorId = Shader.PropertyToID("_CrowKeyColor"), FillId = Shader.PropertyToID("_CrowFill"), FillColorId = Shader.PropertyToID("_CrowFillColor");
        // The key lights' transforms (Select; a light's transform is an engine call), the colour space, and what the
        // globals were last given: a global is set again only when its value changed (exactly; NaN always), and all of
        // them when VitaLit comes on and after each load (sendAll). Until 22 Oct all 13 every frame, the 5 arrays and
        // vectors by name (a string to ID lookup each: a third of Upload's samples in the 18 Oct profile).
        static readonly List<Transform> keyTransforms = new List<Transform>();
        static bool linear, sendAll = true;
        static readonly Vector4[] sentPositions = new Vector4[Keys], sentDirections = new Vector4[Keys], sentColors = new Vector4[Keys];
        static Vector4 sentFill, sentFillColor;

        static bool Same(Vector4 a, Vector4 b)
        {
            return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
        }

        // Copies values into sent; false when they were the same already.
        static bool Changed(Vector4[] values, Vector4[] sent)
        {
            bool changed = false;
            for (int k = 0; k < values.Length; k++)
                if (!Same(values[k], sent[k]))
                {
                    sent[k] = values[k];
                    changed = true;
                }
            return changed;
        }

        // The key lights in the shader's slots, the ones that are on first; the rest far away and
        // black. CROW_KEYS_2/4: how many slots the shader reads, side by side (_CrowKeyX...: slot k in
        // lane k); CROW_KEYS_LOOP: 4 slots one after another (-crow-lit-soa off).
        static void Upload()
        {
            Vector4 fill = Vector4.zero, fillColor = Vector4.zero;
            int used = 0;
            for (int i = 0; i < keys.Count; i++)
            {
                Light light = keys[i];
                if (light == null || !light.isActiveAndEnabled || light.intensity <= 0f) continue;
                Transform transform = keyTransforms[i];
                Color color = (linear ? light.color.linear : light.color) * light.intensity;
                if (light.type == LightType.Directional)
                {
                    Vector3 to = -transform.forward;
                    fill = new Vector4(to.x, to.y, to.z, 0f);
                    fillColor = new Vector4(color.r, color.g, color.b, 1f);
                    continue;
                }
                if (used == Keys) continue;
                Vector3 position = transform.position;
                positions[used] = new Vector4(position.x, position.y, position.z, 1f / Mathf.Max(light.range * light.range, 0.0001f));
                colors[used] = new Vector4(color.r, color.g, color.b, 1f);
                if (light.type == LightType.Spot)
                {
                    // The cone as a ramp in its cosine, from the edge (0) to where the default cookie
                    // reaches full strength, about two thirds of the way out in the cone's tangent.
                    float half = Mathf.Clamp(light.spotAngle, 1f, 179f) * 0.5f * Mathf.Deg2Rad;
                    float outer = Mathf.Cos(half), inner = Mathf.Cos(Mathf.Atan(0.67f * Mathf.Tan(half)));
                    float scale = 1f / Mathf.Max(inner - outer, 0.0001f);
                    Vector3 forward = transform.forward * scale;
                    directions[used] = new Vector4(forward.x, forward.y, forward.z, -outer * scale);
                }
                else directions[used] = new Vector4(0f, 0f, 0f, 1f);
                used++;
            }
            for (int k = used; k < Keys; k++)
            {
                positions[k] = new Vector4(1e6f, 1e6f, 1e6f, 1f);
                directions[k] = Vector4.zero;
                colors[k] = Vector4.zero;
            }
            bool all = sendAll;
            sendAll = false;
            // Changed() copies the new values into sent*, so all three run (no short circuit).
            bool positionsChanged = Changed(positions, sentPositions), directionsChanged = Changed(directions, sentDirections);
            if (Changed(colors, sentColors) || all) Shader.SetGlobalVectorArray(KeyColorId, colors);
            if (positionsChanged || directionsChanged || all)
            {
                Shader.SetGlobalVectorArray(KeyPosId, positions);
                Shader.SetGlobalVectorArray(KeyDirId, directions);
                for (int k = 0; k < Keys; k++)
                {
                    lanes[0][k] = positions[k].x;
                    lanes[1][k] = positions[k].y;
                    lanes[2][k] = positions[k].z;
                    lanes[3][k] = positions[k].w;
                    lanes[4][k] = directions[k].x;
                    lanes[5][k] = directions[k].y;
                    lanes[6][k] = directions[k].z;
                    lanes[7][k] = directions[k].w;
                }
                for (int i = 0; i < lanes.Length; i++) Shader.SetGlobalVector(laneIds[i], lanes[i]);
            }
            if (!Same(fill, sentFill) || all) Shader.SetGlobalVector(FillId, sentFill = fill);
            if (!Same(fillColor, sentFillColor) || all) Shader.SetGlobalVector(FillColorId, sentFillColor = fillColor);
            string wanted = used == 0 ? null : !soa ? keywords[2] : used <= 2 ? keywords[0] : keywords[1];
            if (wanted == keyword) return;
            foreach (string name in keywords)
                if (name == wanted) Shader.EnableKeyword(name);
                else Shader.DisableKeyword(name);
            keyword = wanted;
        }
    }
}
