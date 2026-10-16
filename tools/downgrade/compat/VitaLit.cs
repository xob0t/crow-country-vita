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
// black) draw with CrowVita/Lit (vita_shaders/CrowLit.shader: Standard's
// simple form, no ForwardAdd pass), which draws the main directional light, the harmonics and the 4
// point lights Unity gives each object per vertex, and up to 8 key lights set here as globals per
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
//   -crow-lit-keys <n>     at most n point and spot key lights (default 4, at most 8)
//   -crow-lit-min-range <m>  point lights with a shorter range stay Unity's, per vertex (default 0)
//   -crow-lit-log          log the key lights whenever they change (CROW_LIT)
//   -crow-lit-bundle <path>  load CrowVita/Lit from an asset bundle (for builds without it)
//   -crow-lit-debug <n>    a value for the shader's _CrowDebug (for a debugging build of it)
// -crow-ab and -crow-cycle take lit=game|vita, litkeys=<n> and litrange=<m>.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    static class VitaLit
    {
        const int Keys = 8;
        static bool on, hooked, log;
        static Shader lit, standard;
        // The swapped materials' original shaders, by instance ID: a static that holds an asset can keep
        // it, and what it references, through Resources.UnloadUnusedAssets (IL2CPP's liveness pass starts
        // from the statics; CamEffect's list of camera effects kept their render textures that way). The
        // areas' materials were not seen to stay with the Material keys, but nothing here needs them held.
        static readonly Dictionary<int, Shader> swapped = new Dictionary<int, Shader>();
        static readonly Dictionary<Light, KeyValuePair<int, LightShadows>> keyed = new Dictionary<Light, KeyValuePair<int, LightShadows>>();
        static readonly Dictionary<Light, LightRenderMode> forced = new Dictionary<Light, LightRenderMode>();
        static readonly List<Light> keys = new List<Light>();
        static readonly Vector4[] positions = new Vector4[Keys], directions = new Vector4[Keys], colors = new Vector4[Keys];
        static int pixelLights = -1;
        static float nextSelect, sceneTime;
        static int swapPasses;
        static string lastLog, bundle;
        static int maxKeys = 4;
        static float minRange = 0f;

        // -crow-cycle's litkeys= and litrange=.
        internal static void SetLimits(string key, float value)
        {
            if (key == "litkeys") maxKeys = Mathf.Clamp((int)value, 0, Keys);
            else minRange = value;
            nextSelect = 0f;
        }
        static UnityEngine.Object[] bundleAssets;

        public static void Install(string[] args)
        {
            log = Array.IndexOf(args, "-crow-lit-log") >= 0;
            bundle = Perf.Value(args, "-crow-lit-bundle");
            maxKeys = Mathf.Clamp((int)Quality.Number(args, "-crow-lit-keys", maxKeys), 0, Keys);
            minRange = Quality.Number(args, "-crow-lit-min-range", minRange);
            Shader.SetGlobalFloat("_CrowDebug", Quality.Number(args, "-crow-lit-debug", 0f));
            string value = Perf.Value(args, "-crow-lit") ?? (Application.platform == RuntimePlatform.PSP2 ? "vita" : null);
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
                if (lit == null || !lit.isSupported || !new Material(lit).HasProperty("_CrowLitV3"))
                {
                    Debug.Log("CROW_LIT CrowVita/Lit " + (lit == null ? "is not in the build" : !lit.isSupported ? "is not supported" : "is an older version") +
                              "; lights stay the game's");
                    lit = null;
                    return;
                }
            }
            if (!hooked)
            {
                hooked = true;
                SceneManager.sceneLoaded += (scene, mode) =>
                {
                    sceneTime = Time.realtimeSinceStartup;
                    swapPasses = 0;
                    nextSelect = 0f;
                    nextList = 0f;
                };
                Application.onBeforeRender += Tick;
            }
            if (want == on) return;
            on = want;
            if (on)
            {
                pixelLights = QualitySettings.pixelLightCount;
                QualitySettings.pixelLightCount = 0;
                swapPasses = 0;
                nextSelect = 0f;
                sceneTime = Time.realtimeSinceStartup;
                Tick();
            }
            else
            {
                foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
                {
                    Shader shader;
                    if (material.shader != lit || !swapped.TryGetValue(material.GetInstanceID(), out shader)) continue;
                    int queue = material.renderQueue;
                    material.shader = shader;
                    material.renderQueue = queue;
                }
                swapped.Clear();
                foreach (Light light in keyed.Keys.ToList()) Unkey(light);
                foreach (KeyValuePair<Light, LightRenderMode> pair in forced)
                    if (pair.Key != null) pair.Key.renderMode = pair.Value;
                forced.Clear();
                keys.Clear();
                if (pixelLights >= 0) QualitySettings.pixelLightCount = pixelLights;
                foreach (string name in keywords) Shader.DisableKeyword(name);
                keyword = null;
            }
            Debug.Log("CROW_LIT " + (on ? "vita: one pass, key lights per pixel" : "game"));
        }

        static void Tick()
        {
            if (!on) return;
            float now = Time.realtimeSinceStartup;
            // Materials: at once, and again 1, 3 and 10 s after a load (what the game creates or loads late).
            float since = now - sceneTime;
            if (swapPasses == 0 || (swapPasses == 1 && since > 1f) || (swapPasses == 2 && since > 3f) || (swapPasses == 3 && since > 10f))
            {
                swapPasses++;
                Swap();
            }
            if (now >= nextSelect || Woken())
            {
                nextSelect = now + 0.25f;
                Select();
            }
            Upload();
        }

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
            int count = 0, changedQueue = 0, transparent = 0, cutout = 0;
            foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
            {
                Shader shader = material.shader;
                if (shader == null || shader == lit || !(shader == standard || shader.name == "Standard" || shader.name == "SFBStandard")) continue;
                swapped[material.GetInstanceID()] = shader;
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
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                Material material = renderer.sharedMaterial;
                if (material == null || material.shader != VitaLit.lit) continue;
                perLayer[renderer.gameObject.layer]++;
                litRenderers++;
                if (renderer.lightmapIndex >= 0 && renderer.lightmapIndex < 0xFFFE) lightmapped++;
            }
            majorLayers = 0;
            for (int layer = 0; layer < 32; layer++)
                if (perLayer[layer] * 10 >= litRenderers && perLayer[layer] > 0) majorLayers |= 1 << layer;
            if (log)
                Debug.Log("CROW_LIT " + count + " materials to CrowVita/Lit (" + transparent + " transparent, " + cutout + " cutout, " + changedQueue +
                          " render queues kept); " + litRenderers + " renderers, " + lightmapped + " lightmapped (" +
                          LightmapSettings.lightmaps.Length + " lightmaps); layers " + majorLayers.ToString("X"));
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
        // key lights are picked by hand.
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
            if (now >= nextList)
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
            Camera view = Camera.main;
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

        static void Unkey(Light light)
        {
            KeyValuePair<int, LightShadows> saved = keyed[light];
            keyed.Remove(light);
            if (light == null) return;
            light.cullingMask = saved.Key;
            light.shadows = saved.Value;
        }

        static readonly string[] keywords = { "CROW_KEYS_2", "CROW_KEYS_4", "CROW_KEYS_8" };
        static string keyword;

        // The key lights in the shader's slots, the ones that are on first; the rest far away and
        // black. CROW_KEYS_2/4/8: how many slots the shader reads (unrolled).
        static void Upload()
        {
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Vector4 fill = Vector4.zero, fillColor = Vector4.zero;
            int used = 0;
            foreach (Light light in keys)
            {
                if (light == null || !light.isActiveAndEnabled || light.intensity <= 0f) continue;
                Transform transform = light.transform;
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
            Shader.SetGlobalVectorArray("_CrowKeyPos", positions);
            Shader.SetGlobalVectorArray("_CrowKeyDir", directions);
            Shader.SetGlobalVectorArray("_CrowKeyColor", colors);
            Shader.SetGlobalVector("_CrowFill", fill);
            Shader.SetGlobalVector("_CrowFillColor", fillColor);
            string wanted = used == 0 ? null : used <= 2 ? keywords[0] : used <= 4 ? keywords[1] : keywords[2];
            if (wanted == keyword) return;
            foreach (string name in keywords)
                if (name == wanted) Shader.EnableKeyword(name);
                else Shader.DisableKeyword(name);
            keyword = wanted;
        }
    }
}
