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
// Which lights are keys: Unity picks each object's pixel lights by the object, but the key lights are one
// set for the whole frame, so a light that drops out of it goes from per pixel to per vertex (on the big
// combined walls next to nothing) at once: its pool of light on a wall or a pillar vanishes. Scored from the
// camera, 15 m away from everything it shows, range and intensity decide, the spot lights first wherever
// they are: in Corridor B's north walkway the window spot 17 m away in the south-west wing (behind walls)
// took a slot, and the wall lamps a few metres from Mara swapped in and out as the camera turned through a
// few degrees. So each light is scored by what it gives the ground the camera shows: the player's body (else
// a point 15 m along the camera's view), lights whose range doesn't reach within -crow-lit-view metres (4)
// of it out, the others by intensity x range, attenuated as Unity does over their distance past that radius;
// spots only first when the player is inside their range (the laser sights, daylight pools), the narrowest
// first (one without a slot is drawn per vertex, without its cone); the last choice counts 1.5 times, each
// light is scored on its highest intensity and range since the load (the game's flicker doesn't reorder them),
// a key the game switches off keeps its slot for up to 2 s (a candle going
// out and back for a moment), and keys change by a fade (a light Select drops for half a second keeps its
// slot, then its slot fades out over -crow-lit-fade seconds, 0.35, and the new one fades in; a spot light
// coming on, a laser sight, takes its slot at once).
//   -crow-lit game|vita    game keeps Standard and the game's lights (the default on other players);
//                          vita as above (the PS Vita's default)
//   -crow-lit-keys <n>     at most n point and spot key lights (default 4, at most 4; 8 slots cost Mansion
//                          8 ms for nothing seen)
//   -crow-lit-log          log the key lights whenever they change (CROW_LIT)
//   -crow-lit-fade <s>     the fade when keys change (default 0.35; 0: at once)
//   -crow-lit-view <m>     the radius around the player the view scoring counts (default 4)
//   -crow-lit-bundle <path>  load CrowVita/Lit from an asset bundle (for builds without it)
//   -crow-lit-debug <n>    the shader's _CrowDebug (every build reads it: 1 switches the depth layers off);
//                          2 lets the key lights light every layer, as before 27 Oct (their culling masks off)
// -crow-ab and -crow-cycle take lit=game|vita, litkeys=<n>, litfade=<s> and litview=<m>.
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
        static float fadeTime = 0.35f, viewRadius = 4f;

        // -crow-ab's, -crow-cycle's and -crow-fight-cycle's lit=, litkeys=, litfade=, litview=;
        // false for other keys (Quality.Change asks the next module).
        internal static bool Change(string key, string value)
        {
            if (key == "lit")
            {
                Set(value);
                return true;
            }
            float number;
            float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number);
            switch (key)
            {
                case "litkeys": maxKeys = Mathf.Clamp((int)number, 0, Keys); break;
                case "litfade": fadeTime = Mathf.Max(0f, number); break;
                case "litview": viewRadius = Mathf.Max(0f, number); break;
                default: return false;
            }
            nextSelect = 0f;
            return true;
        }
        static UnityEngine.Object[] bundleAssets;

        public static void Install(string[] args)
        {
            log = CrowFlags.Has(args, "-crow-lit-log");
            bundle = CrowFlags.Value(args, "-crow-lit-bundle");
            maxKeys = Mathf.Clamp((int)CrowFlags.Number(args, "-crow-lit-keys", maxKeys), 0, Keys);
            fadeTime = Mathf.Max(0f, CrowFlags.Number(args, "-crow-lit-fade", fadeTime));
            viewRadius = Mathf.Max(0f, CrowFlags.Number(args, "-crow-lit-view", viewRadius));
            Shader.SetGlobalFloat("_CrowDebug", CrowFlags.Number(args, "-crow-lit-debug", 0f));
            allLayers = CrowFlags.Number(args, "-crow-lit-debug", 0f) == 2f;
            string value = CrowFlags.Value(args, "-crow-lit", "vita");
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
                if (lit == null || !lit.isSupported || !HasProperty(lit, "_CrowLitV4"))
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
                    peaks.Clear();
                    offSince.Clear();
                    dropped.Clear();
                    Destroyed.Forget(forced);  // an unloaded area's directional lights
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
                weights.Clear();
                desired.Clear();
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
        // Once a first pass has found that, the first passes after later loads leave the material scan out
        // too: Resources.FindObjectsOfTypeAll<Material> makes a managed object, held by one of IL2CPP's
        // pinned GC handles (Memory.cs, GcHandles), for every material in memory, and in such a build it
        // finds none to swap (every logged pass on the PS Vita: "0 materials to CrowVita/Lit", 139 to 419
        // saved so); Haunted Hilltop's first frame took handle 65,537 there, which made the handle table
        // grow, and crash. The renderer pass below then looks at the first material of every renderer it
        // lists (FindObjectsOfType: those on active GameObjects), and one on a shader the scan swaps
        // (Standard, SFBStandard, Legacy Diffuse) brings the scan back at once; other slots, and materials
        // no such renderer has, aren't seen: every slot (GetSharedMaterials) made the post-load pass 2.5 to
        // 7.5 times as long on the PS Vita (Ocean Kingdom 31 -> 235 ms), a managed Material per slot.
        // Restore (lit=game) clears it.
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
            // Woken every frame: a sight that comes on when the periodic pass is due is urgent too.
            bool woken = Woken();
            if (now >= nextSelect || woken)
            {
                if (woken) urgent = true;
                if (now < nextSelect) wokenSelects++;
                selects++;
                nextSelect = now + 0.25f;
                Select();
            }
            Transition();
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
            if (scanned) MaterialLayers();
            majorLayers = 0;
            for (int layer = 0; layer < 32; layer++)
                if (perLayer[layer] * 10 >= litRenderers && perLayer[layer] > 0) majorLayers |= 1 << layer;
            topLayer = 0;
            for (int layer = 0, most = 0; layer < 32; layer++)
                if (perLayer[layer] > most)
                {
                    most = perLayer[layer];
                    topLayer = 1 << layer;
                }
            Array.Copy(perLayer, layerRenderers, 32);
            litRendererCount = litRenderers;
            if (first)
            {
                baked = bakedBuild || saved > 0;
                bakedBuild = baked;
            }
            if (log)
                Debug.Log("CROW_LIT " + count + " materials to CrowVita/Lit (" + transparent + " transparent, " + cutout + " cutout, " + changedQueue +
                          " render queues kept), " + (scanned ? saved + " saved so by the build" : "material scan left out (saved so by the build)") + "; " +
                          litRenderers + " renderers, " + lightmapped + " lightmapped (" + LightmapSettings.lightmaps.Length + " lightmaps); layers " +
                          majorLayers.ToString("X") + " (" + LayerCounts() + "); pass " + swapPasses + " in " + clock.ElapsedMilliseconds + " ms");
        }

        // The key lights' culling masks (27 Oct, ARCADE): a key light lit every CrowVita/Lit renderer in its reach,
        // where the game's lights leave out the layers their masks don't have. The game keeps its screens and
        // displays to their own lights that way: each Arcade cabinet has a point light (intensity 5, 0.8 m) 8 cm in
        // front of its screen that lights the cabinet around it and leaves out the screen's layer (28), and as a key
        // light it washed the middle of the screen out to white. The shader drops a key from a material whose
        // renderers are all on one layer (_CrowLayerLo/Hi) when the key's mask (_CrowKeyMaskLo/Hi, lane by
        // lane) leaves that layer out. The PS Vita build saves the layers (asset_rules.material_layers, from every
        // scene and prefab, those switched off at the load as well); a build without saved materials gets them here, from
        // every renderer in the loaded scenes, after each material scan. -crow-lit-debug 2 switches it off.
        static bool allLayers;
        static readonly int LayerLoId = Shader.PropertyToID("_CrowLayerLo"), LayerHiId = Shader.PropertyToID("_CrowLayerHi");
        static readonly Dictionary<Material, int> materialLayers = new Dictionary<Material, int>();
        static readonly List<Material> rendererMaterials = new List<Material>();

        static void MaterialLayers()
        {
            materialLayers.Clear();
            foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (renderer == null || !renderer.gameObject.scene.IsValid()) continue;
                int layer = renderer.gameObject.layer;
                renderer.GetSharedMaterials(rendererMaterials);
                foreach (Material material in rendererMaterials)
                {
                    if (material == null || material.shader != lit) continue;
                    int seen;
                    materialLayers[material] = materialLayers.TryGetValue(material, out seen) && seen != layer ? -1 : layer;
                }
            }
            int layered = 0;
            foreach (KeyValuePair<Material, int> pair in materialLayers)
            {
                int layer = pair.Value < 0 ? 0 : pair.Value;
                pair.Key.SetFloat(LayerLoId, layer < 16 ? Mathf.Pow(2f, -layer) : 0f);
                pair.Key.SetFloat(LayerHiId, layer >= 16 ? Mathf.Pow(2f, 16 - layer) : 0f);
                if (layer != 0) layered++;
            }
            rendererMaterials.Clear();
            materialLayers.Clear();
            if (log) Debug.Log("CROW_LIT material layers: " + layered + " materials on one layer other than Default");
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
        // Intro); the others stay Unity's, per vertex or in the harmonics. A narrow spot (10 degrees or
        // less) also qualifies when it leaves out such a layer but lights Default, the layer with the most
        // renderers or half of them (26 Oct, LASER): Unity drops a spot's cone per vertex and lights all it
        // reaches, so the guns' laser sights, which leave out NotPlayer (Corridor A: 113 of 559 renderers,
        // Default 412; Windows data) and Roots (Pool: 468 of 2217), lit whole walls red there as Mara aimed.
        // Default counts on its own because the PS Vita build merges most Default renderers: there Roots is
        // Pool's top layer (469 of 775, Default 230) and the sight covers 0.31, so it stayed per vertex and
        // lit Pool's floor orange (27 Oct, VERIFY). As a key the sight lights the materials drawn on one layer
        // only if its mask has it (_CrowLayerLo/Hi, ARCADE); those on several get its dot, which the game's
        // doesn't draw. Wider spots stay out: Pool's path spotlight also leaves out Roots, and keyed it lit
        // Pool's floor brighter than the game does. The lights the game keeps to a few layers light none of
        // them (the survey of 26 Oct, every area: 6% of the renderers at most, never Default).
        static int majorLayers = ~(1 << MaraScene.UiLayer), topLayer;
        const float KeyCoverage = 0.5f, NarrowSpot = 10f;

        static bool Qualifies(Light light, int mask)
        {
            if ((mask & majorLayers) == majorLayers) return true;
            return light.type == LightType.Spot && light.spotAngle <= NarrowSpot &&
                   ((mask & (1 | topLayer)) != 0 || Coverage(mask) >= KeyCoverage);
        }

        // The CrowVita/Lit renderers on each layer at the last pass, and all of them.
        static readonly int[] layerRenderers = new int[32];
        static int litRendererCount;

        // The share of the CrowVita/Lit renderers on the layers of a culling mask (1 before a pass has counted them).
        static float Coverage(int mask)
        {
            if (litRendererCount <= 0) return 1f;
            int covered = 0;
            for (int layer = 0; layer < 32; layer++)
                if ((mask & (1 << layer)) != 0) covered += layerRenderers[layer];
            return covered / (float)litRendererCount;
        }

        // -crow-lit-log: the renderers per layer ("0:412 31:120").
        static string LayerCounts()
        {
            var text = new System.Text.StringBuilder();
            for (int layer = 0; layer < 32; layer++)
                if (layerRenderers[layer] > 0) text.Append(text.Length > 0 ? " " : "").Append(layer).Append(':').Append(layerRenderers[layer]);
            return text.ToString();
        }

        // Select runs every 0.25 s in play, so it allocates nothing (strings only with -crow-lit-log): the
        // scene's lights are listed again every 2 s and after each load, the buffers are reused, and the
        // key lights are picked by hand. A 2 s listing that finds another periodic scan in its frame
        // (CrowEvents.Turn) waits for the next Select.
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
                if (light.cullingMask == 0 || (light.cullingMask & ~(1 << MaraScene.UiLayer)) == 0) continue;
                if (main == null || light.intensity * Luminance(light.color) > main.intensity * Luminance(main.color)) main = light;
            }
            Camera view = MainCamera(now);
            if (view != null) GeometryUtility.CalculateFrustumPlanes(view, frustum);
            Vector3 focus = Focus(view, now);
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
                bool off = !light.isActiveAndEnabled || light.intensity <= 0f;
                if (off && keyed.ContainsKey(light))
                {
                    // A key the game switched off for a moment (Mansion's candles flicker by going out
                    // and back): its slot is kept for up to 2 s (it draws nothing meanwhile), so the next
                    // candidate doesn't take it and give it back as it relights.
                    float since;
                    if (!offSince.TryGetValue(light, out since)) offSince[light] = since = now;
                    if (now - since < 2f) off = false;
                }
                else offSince.Remove(light);
                if (off)
                {
                    // Off: a spot light that could be a key light is picked the frame it comes on.
                    if (type == LightType.Spot && Qualifies(light, mask)) watched.Add(light);
                    continue;
                }
                if (!Qualifies(light, mask))
                {
                    if (log) excluded.Add(light.type + ":" + Hierarchy.Path(light.transform, 2) + "@" + light.range.ToString("F1") + "/mask=" + mask.ToString("X") + "/cover=" + Coverage(mask).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
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
                // its candles 33.3 ms a frame, 8 slots 41.9, the game 66).
                // What it gives the ground around the player: out if its range ends short of the
                // radius, else intensity x range, attenuated as Unity's lights are over the distance
                // past the radius; a spot first only when the player is inside its range (below).
                // Its peak intensity and range since the load: the game's flicker (candles, fires,
                // a fireplace's glow, LightFlicker and the "light flicker" FSMs) doesn't reorder them.
                Vector2 peak;
                peaks.TryGetValue(light, out peak);
                peak = new Vector2(Mathf.Max(peak.x, light.intensity), Mathf.Max(peak.y, light.range));
                peaks[light] = peak;
                float reach = peak.y, away = Vector3.Distance(focus, position), beyond = Mathf.Max(0f, away - viewRadius);
                if (beyond >= reach) continue;
                float x = beyond / Mathf.Max(reach, 0.0001f);
                float score = peak.x * Luminance(light.color) * reach / (1f + 25f * x * x);
                // The narrowest spot first: one left without a slot is Unity's, per vertex, a point light over all
                // its range, so the narrower its cone the more it lights that the game's doesn't. With the same lead
                // for every spot (and the last choice's 1.5 on it), Dig Site's three ceiling spots and Mara's
                // flashlight kept the 4 slots, and her laser sight (1.5 degrees, 20 m) lit the room red (DIGLASER).
                if (light.type == LightType.Spot && away < reach) score += 1e8f / Mathf.Max(light.spotAngle, 1f);
                // The last choice keeps its slot against a near tie (not every keyed light: one fading out
                // is keyed too, and three equal candles all so marked swapped places at every pass).
                if (desired.Contains(light)) score *= 1.5f;
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
            // The slots follow in Transition (a fade, or at once).
            desired.Clear();
            desired.AddRange(chosen);
            if (log)
            {
                var line = new System.Text.StringBuilder("CROW_LIT keys");
                foreach (Light light in desired)
                {
                    Vector3 at = light.transform.position;
                    line.Append(' ').Append(light.type).Append(':').Append(Hierarchy.Path(light.transform, 2)).Append('@').Append(light.range.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))
                        .Append('(').Append(at.x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(';').Append(at.z.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(')');
                }
                line.Append(main == null ? " main=none" : " main=" + Hierarchy.Path(main.transform, 2) + "/mask=" + main.cullingMask.ToString("X"));
                if (excluded.Count > 0) line.Append(" other-layers ").Append(string.Join(" ", excluded.ToArray()));
                string text = line.ToString();
                if (text != lastLog) Debug.Log(text);
                lastLog = text;
            }
        }

        // The key slots: keys (with keyTransforms and weights, 0 to 1) are what the shader is given;
        // desired is Select's choice. A key Select no longer wants fades out and is given back to
        // Unity at 0; a wanted light takes a free slot and fades in; a spot light coming on (urgent:
        // a laser sight) takes the slot of a leaving key at once, at full strength. With no fade
        // (-crow-lit-fade 0): every change at once.
        static readonly List<Light> desired = new List<Light>();
        static readonly List<float> weights = new List<float>();
        // Each light's highest intensity and range seen since the load (the view scoring's).
        static readonly Dictionary<Light, Vector2> peaks = new Dictionary<Light, Vector2>();
        // When each key light the game switched off went off (the view scoring keeps its slot 2 s).
        static readonly Dictionary<Light, float> offSince = new Dictionary<Light, float>();
        // When Select dropped each key still in a slot (its fade starts half a second later).
        static readonly Dictionary<Light, float> dropped = new Dictionary<Light, float>();
        static bool urgent;

        static void Transition()
        {
            float step = fadeTime > 0f ? Time.unscaledDeltaTime / fadeTime : 1f;
            float now = Time.realtimeSinceStartup;
            bool changed = false;
            for (int i = keys.Count - 1; i >= 0; i--)
            {
                Light light = keys[i];
                bool want = light != null && desired.Contains(light);
                if (!want && light != null && light.type != LightType.Directional && fadeTime > 0f)
                {
                    // A key Select dropped starts to fade only after half a second: a light that wins
                    // one pass and loses the next (Station Square's blinking lamps) never gets a slot.
                    float since;
                    if (!dropped.TryGetValue(light, out since)) dropped[light] = since = now;
                    if (now - since < 0.5f) continue;
                }
                else if (light != null) dropped.Remove(light);
                if (want && light.type == LightType.Directional) weights[i] = 1f;
                else if (want) weights[i] = Mathf.Min(1f, weights[i] + step);
                else weights[i] = light == null || light.type == LightType.Directional ? 0f : Mathf.Max(0f, weights[i] - step);
                if (want || weights[i] > 0f) continue;
                if (light == null) keyed.Remove(light);
                else if (keyed.ContainsKey(light)) Unkey(light);
                if (light != null) dropped.Remove(light);
                keys.RemoveAt(i);
                weights.RemoveAt(i);
                changed = true;
            }
            int slots = 0;
            foreach (Light light in keys)
                if (light != null && light.type != LightType.Directional) slots++;
            foreach (Light light in desired)
            {
                if (light == null || keys.Contains(light)) continue;
                bool directional = light.type == LightType.Directional;
                if (!directional && slots >= maxKeys)
                {
                    if (!urgent) continue;
                    // A spot light that just came on: the weakest leaving key gives its slot at once.
                    int weakest = -1;
                    for (int i = 0; i < keys.Count; i++)
                        if (keys[i] != null && keys[i].type != LightType.Directional && !desired.Contains(keys[i]) &&
                            (weakest < 0 || weights[i] < weights[weakest])) weakest = i;
                    if (weakest < 0) continue;
                    dropped.Remove(keys[weakest]);
                    Unkey(keys[weakest]);
                    keys.RemoveAt(weakest);
                    weights.RemoveAt(weakest);
                    slots--;
                }
                if (!keyed.ContainsKey(light))
                {
                    keyed[light] = new KeyValuePair<int, LightShadows>(light.cullingMask, light.shadows);
                    light.cullingMask = 0;
                    light.shadows = LightShadows.None;
                }
                keys.Add(light);
                weights.Add(directional || urgent || fadeTime <= 0f ? 1f : 0f);
                if (!directional) slots++;
                changed = true;
            }
            urgent = false;
            if (!changed) return;
            if (log)
            {
                // What the shader is given from now (CROW_LIT keys is Select's choice; with the fades the slots follow it).
                var line = new System.Text.StringBuilder("CROW_LIT slots");
                for (int i = 0; i < keys.Count; i++)
                {
                    Light light = keys[i];
                    if (light == null || light.type == LightType.Directional) continue;
                    line.Append(' ').Append(Hierarchy.Path(light.transform, 2)).Append('@')
                        .Append(weights[i].ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                }
                Debug.Log(line.ToString());
            }
            keyTransforms.Clear();
            foreach (Light light in keys) keyTransforms.Add(light != null ? light.transform : null);
        }

        // The view scoring's centre: the player's body (character RIGIDBODY, looked up again at most
        // every 2 s while missing or off), else a point 15 m along the camera's view (the gameplay
        // camera is about 15 m from the player).
        static Transform body;
        static float nextBody;

        static Vector3 Focus(Camera view, float now)
        {
            if ((body == null || !body.gameObject.activeInHierarchy) && now >= nextBody)
            {
                nextBody = now + 2f;
                GameObject found = MaraScene.FindBody();
                body = found != null ? found.transform : null;
            }
            if (body != null && body.gameObject.activeInHierarchy) return body.position + Vector3.up * 0.3f;
            return view != null ? view.transform.position + view.transform.forward * 15f : Vector3.zero;
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

        static readonly string[] keywords = { "CROW_KEYS_2", "CROW_KEYS_4" };
        static readonly int[] laneIds =
        {
            Shader.PropertyToID("_CrowKeyX"), Shader.PropertyToID("_CrowKeyY"), Shader.PropertyToID("_CrowKeyZ"), Shader.PropertyToID("_CrowKeyW"),
            Shader.PropertyToID("_CrowKeyDX"), Shader.PropertyToID("_CrowKeyDY"), Shader.PropertyToID("_CrowKeyDZ"), Shader.PropertyToID("_CrowKeyDW"),
        };
        static readonly Vector4[] lanes = new Vector4[8];
        static string keyword;
        static readonly int KeyColorId = Shader.PropertyToID("_CrowKeyColor"), FillId = Shader.PropertyToID("_CrowFill"), FillColorId = Shader.PropertyToID("_CrowFillColor");
        // Each key's culling mask in its lane, the low and high 16 bits (floats hold them exactly); unused lanes: all layers.
        static readonly int MaskLoId = Shader.PropertyToID("_CrowKeyMaskLo"), MaskHiId = Shader.PropertyToID("_CrowKeyMaskHi");
        static Vector4 maskLo, maskHi, sentMaskLo, sentMaskHi;
        // The key lights' transforms (Select; a light's transform is an engine call), the colour space, and what the
        // globals were last given: a global is set again only when its value changed (exactly; NaN always), and all of
        // them when VitaLit comes on and after each load (sendAll). Until 22 Oct all of them every frame, the arrays and
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
        // lane k; the 4-slot loop it had before took 129 cycles a pixel against 82, status-log.md).
        static void Upload()
        {
            Vector4 fill = Vector4.zero, fillColor = Vector4.zero;
            int used = 0;
            for (int i = 0; i < keys.Count; i++)
            {
                Light light = keys[i];
                if (light == null || !light.isActiveAndEnabled || light.intensity <= 0f) continue;
                Transform transform = keyTransforms[i];
                Color color = (linear ? light.color.linear : light.color) * (light.intensity * weights[i]);
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
                KeyValuePair<int, LightShadows> saved;
                int mask = allLayers ? -1 : keyed.TryGetValue(light, out saved) ? saved.Key : light.cullingMask;
                maskLo[used] = mask & 0xFFFF;
                maskHi[used] = (mask >> 16) & 0xFFFF;
                colors[used] = new Vector4(color.r, color.g, color.b, 1f);
                if (light.type == LightType.Spot)
                {
                    // The cone as a ramp in its cosine, from the edge (0) to where the default cookie
                    // reaches full strength, about two thirds of the way out in the cone's tangent (a laser
                    // sight's 1.5 degrees: 4.7e-5 of the cosine wide; until 26 Oct at least 1e-4, so its dot
                    // reached 0.86 of its strength at the centre).
                    float half = Mathf.Clamp(light.spotAngle, 1f, 179f) * 0.5f * Mathf.Deg2Rad;
                    float outer = Mathf.Cos(half), inner = Mathf.Cos(Mathf.Atan(0.67f * Mathf.Tan(half)));
                    float scale = 1f / Mathf.Max(inner - outer, 1e-6f);
                    Vector3 forward = transform.forward * scale;
                    directions[used] = new Vector4(forward.x, forward.y, forward.z, -outer * scale);
                }
                else directions[used] = new Vector4(0f, 0f, 0f, 1f);
                used++;
            }
            for (int k = used; k < Keys; k++)
            {
                maskLo[k] = maskHi[k] = 0xFFFF;
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
            if (!Same(maskLo, sentMaskLo) || all) Shader.SetGlobalVector(MaskLoId, sentMaskLo = maskLo);
            if (!Same(maskHi, sentMaskHi) || all) Shader.SetGlobalVector(MaskHiId, sentMaskHi = maskHi);
            if (!Same(fillColor, sentFillColor) || all) Shader.SetGlobalVector(FillColorId, sentFillColor = fillColor);
            string wanted = used == 0 ? null : used <= 2 ? keywords[0] : keywords[1];
            if (wanted == keyword) return;
            foreach (string name in keywords)
                if (name == wanted) Shader.EnableKeyword(name);
                else Shader.DisableKeyword(name);
            keyword = wanted;
        }
    }
}
