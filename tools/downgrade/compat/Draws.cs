using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Draw calls: what the forward renderer is given each frame, and what could take fewer draws or
// material passes without changing the picture. On the PS Vita the frame is CPU-bound in draw
// submission (Quality's -crow-loop-profile), and on the PC most of a draw's main-thread time is the
// material pass (ApplyMaterialPassWithCache: the shader's uniforms gathered again whenever the
// material changes between two draws), so materials count as much as draws.
//   -crow-draw-report <s>     every s seconds (CROW_DRAWS): the renderers drawn (isVisible: in a
//                             camera or a shadow map), those in a camera's view, their materials
//                             (distinct, run-time instances, same-looking duplicates), shadow
//                             casters, small meshes, static batches; after a scene load also the
//                             materials used most and the duplicate groups (CROW_DRAWMAT)
//   -crow-casters <rule>      turn shadow casting off where the rule says, at every scene load and
//                             every 10 s (renderers come and go): off (all), flat (flat horizontal
//                             renderers: floors, ceilings, roads), small:<m> (bounds diagonal under m
//                             metres), player (all but the player's own: Mara keeps her shadow; the
//                             PS Vita's default, see Quality.cs), player:<m> (the player's parts of m
//                             metres or more keep theirs), game (none). A shadows-only renderer
//                             the rule takes is switched off: with its casting off it would be drawn
//                             with its material (Entrance's tutorial paper got its hidden caster box,
//                             a cube with the colour atlas; Ocean Kingdom has 354 of them).
//                             -crow-ab takes casters=<rule> too.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    static class Draws
    {
        static float every, lastReport;
        static bool detail;
        static string casterRule;
        static float lastCasters = -10f;

        public static void Install(string[] args)
        {
            every = Quality.Number(args, "-crow-draw-report", -1f);
            int index = Array.IndexOf(args, "-crow-casters");
            if (index >= 0 && index + 1 < args.Length) Casters(args[index + 1]);
            else if (Application.platform == RuntimePlatform.PSP2) Casters("player");
            StaticBatch.Install(args);
            Merge.Install(args);
            Cameras.Install(args);  // Cameras.cs: -crow-rt-cameras, -crow-camera-report
            if (every <= 0f) return;
            SceneManager.sceneLoaded += (scene, mode) => detail = true;
            Application.onBeforeRender += Tick;
        }

        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastReport < every) return;
            lastReport = now;
            try
            {
                if (Report(detail) > 0) detail = false;
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_DRAWS failed: " + exception);
            }
        }

        // -crow-casters and -crow-ab casters=<rule>.
        static bool castersHooked;
        static readonly HashSet<Renderer> turnedOff = new HashSet<Renderer>();

        public static void Casters(string rule)
        {
            casterRule = rule;
            lastCasters = -10f;
            Debug.Log("CROW_DRAWS casters " + rule);
            if (!castersHooked)
            {
                castersHooked = true;
                SceneManager.sceneLoaded += (scene, mode) => lastCasters = -10f;
                Application.onBeforeRender += CasterTick;
            }
            CasterTick();
        }

        static void CasterTick()
        {
            float now = Time.realtimeSinceStartup;
            // Every 10 s (each pass allocates the renderer list: Station Square 12 KB) and after each load.
            if (casterRule == null || now - lastCasters < 10f) return;
            lastCasters = now;
            int changed = 0, casters = 0;
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                if (renderer.shadowCastingMode == ShadowCastingMode.Off) continue;
                bool shadowsOnly = renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly;
                if (shadowsOnly && !renderer.enabled) continue;
                casters++;
                if (!CasterOff(renderer, casterRule)) continue;
                // A shadows-only renderer is the game's invisible shadow caster: Off would draw it.
                if (shadowsOnly) renderer.enabled = false;
                else renderer.shadowCastingMode = ShadowCastingMode.Off;
                turnedOff.Add(renderer);
                changed++;
            }
            if (changed > 0) Debug.Log("CROW_DRAWS casters " + casterRule + ": " + changed + " of " + casters + " off");
        }

        // Whether a hierarchy root is the player prefab, by its name, read once per root (a name read
        // is a new string, and CasterTick asks for every caster every 2 s).
        static readonly Dictionary<Transform, bool> playerRoots = new Dictionary<Transform, bool>();

        static bool PlayerRoot(Transform root)
        {
            bool player;
            if (playerRoots.TryGetValue(root, out player)) return player;
            if (playerRoots.Count > 4096) playerRoots.Clear();  // roots of unloaded scenes
            return playerRoots[root] = root.name.StartsWith("CharacterCameraPrefab");
        }

        static bool CasterOff(Renderer renderer, string rule)
        {
            if (rule == "off") return true;
            // player: only the player's own parts (Mara, under CharacterCameraPrefab) keep their shadows.
            if (rule == "player") return !PlayerRoot(renderer.transform.root);
            Bounds bounds = renderer.bounds;
            Vector3 size = bounds.size;
            // player:<m>: the player's parts of m metres (bounds diagonal) or more (Mara has 39 casters:
            // 7 under 0.1 m, 7 under 0.2, 22 under 0.4, 3 larger).
            if (rule.StartsWith("player:"))
            {
                float least;
                return !PlayerRoot(renderer.transform.root) ||
                       (float.TryParse(rule.Substring(7), NumberStyles.Float, CultureInfo.InvariantCulture, out least) && size.magnitude < least);
            }
            if (rule == "flat") return Flat(size);
            if (rule.StartsWith("small:"))
            {
                float metres;
                return float.TryParse(rule.Substring(6), NumberStyles.Float, CultureInfo.InvariantCulture, out metres) && size.magnitude < metres;
            }
            return false;
        }

        // The vertex channels a readable mesh has (positions and the others it fills).
        static int Channels(Mesh mesh)
        {
            int channels = 1;
            if (mesh.normals.Length > 0) channels++;
            if (mesh.tangents.Length > 0) channels++;
            if (mesh.colors32.Length > 0) channels++;
            if (mesh.uv.Length > 0) channels++;
            if (mesh.uv2.Length > 0) channels++;
            if (mesh.uv3.Length > 0) channels++;
            if (mesh.uv4.Length > 0) channels++;
            return channels;
        }

        static bool Flat(Vector3 size)
        {
            float wide = Mathf.Max(size.x, size.z);
            return wide > 1f && size.y < 0.05f * wide;
        }

        // A material's look: its shader, keywords, queue, textures and their placement, and the
        // values of the game's shaders' float and colour properties. Two materials with the same
        // signature draw the same (as far as these properties go).
        static readonly string[] floats =
        {
            "_Cutoff", "_Glossiness", "_GlossMapScale", "_SmoothnessTextureChannel", "_Metallic", "_SpecularHighlights",
            "_GlossyReflections", "_BumpScale", "_Parallax", "_OcclusionStrength", "_DetailNormalMapScale", "_UVSec", "_Mode",
            "_SrcBlend", "_DstBlend", "_ZWrite", "_Cull", "_ColorMask", "_InvFade", "_Stencil", "_StencilComp",
        };
        static readonly string[] colors = { "_Color", "_EmissionColor", "_TintColor" };
        static readonly Dictionary<Material, string> signatures = new Dictionary<Material, string>();

        static string Signature(Material material)
        {
            string known;
            if (signatures.TryGetValue(material, out known)) return known;
            var text = new StringBuilder(material.shader == null ? "?" : material.shader.name);
            text.Append('|').Append(material.renderQueue).Append('|');
            string[] keywords = material.shaderKeywords;
            Array.Sort(keywords, StringComparer.Ordinal);
            text.Append(string.Join(" ", keywords));
            foreach (string name in material.GetTexturePropertyNames())
            {
                Texture texture = material.GetTexture(name);
                if (texture == null) continue;
                text.Append('|').Append(name).Append('=').Append(texture.GetInstanceID());
                text.Append(material.GetTextureScale(name)).Append(material.GetTextureOffset(name));
            }
            foreach (string name in floats)
                if (material.HasProperty(name)) text.Append('|').Append(name).Append('=').Append(material.GetFloat(name).ToString("R", CultureInfo.InvariantCulture));
            foreach (string name in colors)
                if (material.HasProperty(name)) text.Append('|').Append(name).Append('=').Append(material.GetColor(name).ToString("F4"));
            string signature = text.ToString();
            if (signatures.Count > 20000) signatures.Clear();
            signatures[material] = signature;
            return signature;
        }

        // Returns the renderers drawn, so the details wait for a frame with some (after a load).
        static int Report(bool perMaterial)
        {
            signatures.Clear();
            var cameras = Camera.allCameras.ToList();
            var planes = cameras.Select(c => new KeyValuePair<Camera, Plane[]>(c, GeometryUtility.CalculateFrustumPlanes(c))).ToList();
            Camera main = Camera.main != null ? Camera.main : cameras.FirstOrDefault();
            float shadowDistance = QualitySettings.shadowDistance;
            int drawn = 0, inView = 0, slots = 0, slotsInView = 0, skinned = 0, opaqueSlots = 0;
            int casters = 0, castersInRange = 0, flatCasters = 0, casterSlotsInRange = 0, shadowOnly = 0, receivers = 0;
            int instances = 0, staticBatched = 0, small = 0, combined = 0, probes = 0, others = 0;
            int readable = 0, fits = 0;  // dynamic batching: 900 vertex attributes at most, and (it seems) a readable mesh
            var materials = new Dictionary<Material, int>();
            var shaders = new Dictionary<string, int>();
            var variants = new HashSet<string>();
            var meshes = new HashSet<Mesh>();
            var casterSizes = new int[6];  // bounds diagonal under 0.5, 1, 2, 5, 10 m, and larger
            var casterNames = new Dictionary<string, int>();
            var big = new List<KeyValuePair<Renderer, Mesh>>();  // the largest meshes drawn, for the details
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled || !renderer.isVisible) continue;
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                {
                    others++;
                    continue;
                }
                drawn++;
                if (renderer is SkinnedMeshRenderer) skinned++;
                Bounds bounds = renderer.bounds;
                int layer = 1 << renderer.gameObject.layer;
                bool seen = planes.Any(p => (p.Key.cullingMask & layer) != 0 && GeometryUtility.TestPlanesAABB(p.Value, bounds));
                Material[] shared = renderer.sharedMaterials;
                Mesh mesh = null;
                var skin = renderer as SkinnedMeshRenderer;
                if (skin != null) mesh = skin.sharedMesh;
                else
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null) mesh = filter.sharedMesh;
                }
                int count = mesh == null ? shared.Length : Math.Min(shared.Length, Math.Max(1, mesh.subMeshCount));
                slots += count;
                if (seen)
                {
                    inView++;
                    slotsInView += count;
                }
                else shadowOnly++;
                bool caster = renderer.shadowCastingMode != ShadowCastingMode.Off;
                if (caster)
                {
                    casters++;
                    bool inRange = main == null || Mathf.Sqrt(bounds.SqrDistance(main.transform.position)) <= shadowDistance;
                    if (inRange)
                    {
                        castersInRange++;
                        casterSlotsInRange += count;
                        if (Flat(bounds.size)) flatCasters++;
                        float diagonal = bounds.size.magnitude;
                        casterSizes[diagonal < 0.5f ? 0 : diagonal < 1f ? 1 : diagonal < 2f ? 2 : diagonal < 5f ? 3 : diagonal < 10f ? 4 : 5]++;
                        string name = renderer.name;
                        int named;
                        casterNames.TryGetValue(name, out named);
                        casterNames[name] = named + 1;
                    }
                }
                if (renderer.receiveShadows) receivers++;
                if (renderer.isPartOfStaticBatch) staticBatched++;
                if (renderer.lightProbeUsage != LightProbeUsage.Off) probes++;
                if (mesh != null)
                {
                    meshes.Add(mesh);
                    if (perMaterial) big.Add(new KeyValuePair<Renderer, Mesh>(renderer, mesh));
                    if (mesh.vertexCount < 300 && count == 1 && skin == null) small++;
                    if (mesh.isReadable)
                    {
                        readable++;
                        if (mesh.vertexCount * Channels(mesh) <= 900 && count == 1 && skin == null) fits++;
                    }
                    if (mesh.name.IndexOf("combined", StringComparison.OrdinalIgnoreCase) >= 0) combined++;
                }
                for (int i = 0; i < count; i++)
                {
                    Material material = shared[i];
                    if (material == null) continue;
                    if (material.renderQueue <= 2500) opaqueSlots++;
                    int uses;
                    materials.TryGetValue(material, out uses);
                    materials[material] = uses + 1;
                    if (uses == 0)
                    {
                        if (material.name.EndsWith("(Instance)")) instances++;
                        string shader = material.shader == null ? "?" : material.shader.name;
                        int n;
                        shaders.TryGetValue(shader, out n);
                        shaders[shader] = n + 1;
                        string[] keywords = material.shaderKeywords;
                        Array.Sort(keywords, StringComparer.Ordinal);
                        variants.Add(shader + "|" + string.Join(" ", keywords));
                    }
                }
            }
            var groups = materials.Keys.GroupBy(Signature).ToList();
            var line = new StringBuilder("CROW_DRAWS scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            line.Append(" drawn=").Append(drawn).Append(" inView=").Append(inView).Append(" shadowOnly=").Append(shadowOnly);
            line.Append(" skinned=").Append(skinned).Append(" otherRenderers=").Append(others);
            line.Append(" slots=").Append(slots).Append(" slotsInView=").Append(slotsInView).Append(" opaqueSlots=").Append(opaqueSlots);
            line.Append(" materials=").Append(materials.Count).Append(" instances=").Append(instances);
            line.Append(" looks=").Append(groups.Count).Append(" variants=").Append(variants.Count).Append(" shaders=").Append(shaders.Count);
            line.Append(" meshes=").Append(meshes.Count).Append(" small=").Append(small).Append(" combined=").Append(combined);
            line.Append(" readable=").Append(readable).Append(" fits900=").Append(fits);
            line.Append(" staticBatched=").Append(staticBatched).Append(" probes=").Append(probes);
            line.Append(" casters=").Append(casters).Append(" castersInRange=").Append(castersInRange);
            line.Append(" casterSlotsInRange=").Append(casterSlotsInRange).Append(" flatCasters=").Append(flatCasters);
            line.Append(" receivers=").Append(receivers).Append(" turnedOff=").Append(turnedOff.Count(r => r != null));
            line.Append(" batches=").Append(StaticBatch.Batched);
            Debug.Log(line.ToString());
            if (!perMaterial || drawn == 0) return drawn;
            Debug.Log("CROW_DRAWMAT casters by size <0.5m=" + casterSizes[0] + " <1m=" + casterSizes[1] + " <2m=" + casterSizes[2] + " <5m=" + casterSizes[3] +
                      " <10m=" + casterSizes[4] + " more=" + casterSizes[5] + " by name " +
                      string.Join(" ", casterNames.OrderByDescending(p => p.Value).Take(20).Select(p => p.Key.Replace(' ', '_') + "=" + p.Value).ToArray()));
            foreach (KeyValuePair<Renderer, Mesh> pair in big.OrderByDescending(p => p.Value.vertexCount).Take(8))
                Debug.Log("CROW_DRAWMAT mesh " + pair.Value.name.Replace(' ', '_') + " vertices=" + pair.Value.vertexCount + " readable=" + (pair.Value.isReadable ? 1 : 0) +
                          " path=" + Perf.Path(pair.Key.transform, 5));
            Debug.Log("CROW_DRAWMAT shaders " + string.Join(" ", shaders.OrderByDescending(p => p.Value).Select(p => p.Key.Replace(' ', '_') + "=" + p.Value).ToArray()));
            foreach (KeyValuePair<Material, int> pair in materials.OrderByDescending(p => p.Value).Take(15))
                Debug.Log("CROW_DRAWMAT uses=" + pair.Value + " " + pair.Key.name.Replace(' ', '_') + " shader=" + (pair.Key.shader == null ? "?" : pair.Key.shader.name.Replace(' ', '_')));
            foreach (var group in groups.Where(g => g.Count() > 1).OrderByDescending(g => g.Count()).Take(15))
            {
                var names = group.Select(m => m.name.Replace(' ', '_')).ToList();
                Debug.Log("CROW_DRAWMAT same-look=" + names.Count + " uses=" + group.Sum(m => materials[m]) + " " +
                          string.Join(",", names.Distinct().Take(6).ToArray()) + " sig=" + group.Key.Substring(0, Math.Min(160, group.Key.Length)).Replace(' ', '_'));
            }
            return drawn;
        }
    }

    // Static batching at run time (StaticBatchingUtility.Combine): the game's builds have none, and
    // its areas draw many small unique meshes with a few shared materials (Roadside: 281 renderers,
    // 180 of them with three vertex-colour materials), each a draw in the base pass, in every pixel
    // light's additive pass and in the shadow map. A static batch draws neighbours with the same
    // material and lights as one. Its renderers draw from a world-space copy of their meshes, so they
    // must never move; enabling, disabling and material changes still work. Not a default: on the PS
    // Vita the main thread's cost is per renderer more than per draw call. Roadside with its meshes made
    // readable (a test, they aren't on the PS Vita) and all 218 candidates batched: camera draw 20.4 ->
    // 19.8 ms, the combine itself a 375 ms frame; and most of its small renderers are the crows' and
    // flies' parts, which move (safe batches 29). Dynamic batching on the same readable meshes saved
    // nothing measurable.
    //   -crow-static-batch off|all|safe  all: some seconds after each area loads (MeshCombineStudio
    //                               has combined by then), batch every active, enabled MeshRenderer with
    //                               a readable mesh, outside the player's prefab and without a Rigidbody,
    //                               Animator or Animation on it or above it. safe also leaves out what a
    //                               PlayMaker action that moves transforms (SetPosition, DOTween...,
    //                               SmoothLookAt...) names, or a game script that moves its object, and
    //                               what is below them. A batched renderer that moves after all gets its
    //                               own mesh back (CROW_BATCH moved). -crow-cycle takes batch=all|safe
    //                               (now, in the current area).
    static class StaticBatch
    {
        static string rule = "off";
        static float due = -1f, lastWatch;
        static bool hooked;
        static readonly List<Transform> watched = new List<Transform>();
        static readonly HashSet<Transform> moved = new HashSet<Transform>();
        // Each batched renderer's own mesh, given back to its MeshFilter when it moves after all.
        static readonly Dictionary<Transform, KeyValuePair<MeshFilter, Mesh>> originals = new Dictionary<Transform, KeyValuePair<MeshFilter, Mesh>>();
        internal static int Batched;

        public static void Install(string[] args)
        {
            int index = Array.IndexOf(args, "-crow-static-batch");
            if (index >= 0 && index + 1 < args.Length) Set(args[index + 1], false);
        }

        internal static void Set(string value, bool now)
        {
            if (value == "nudge")
            {
                Nudge();
                return;
            }
            rule = value;
            if (!hooked)
            {
                hooked = true;
                SceneManager.sceneLoaded += (scene, mode) =>
                {
                    if (scene.name == "Logos" || scene.name == "Title" || scene.name == "CrowMara") return;
                    due = Time.realtimeSinceStartup + 8f;
                };
                Application.onBeforeRender += Tick;
            }
            if (now) due = 0f;
        }

        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (due >= 0f && now >= due)
            {
                due = -1f;
                try
                {
                    Combine();
                }
                catch (Exception exception)
                {
                    Debug.Log("CROW_BATCH failed: " + exception);
                }
            }
            if (watched.Count == 0 || now - lastWatch < 0.5f) return;
            lastWatch = now;
            for (int i = watched.Count - 1; i >= 0; i--)
            {
                Transform transform = watched[i];
                if (transform == null)
                {
                    watched.RemoveAt(i);
                    continue;
                }
                if (!transform.hasChanged) continue;
                transform.hasChanged = false;
                watched.RemoveAt(i);
                Unbatch(transform);
            }
        }

        // A batched renderer that moved draws its own mesh again: the static batch's copy stays where
        // it was batched.
        static void Unbatch(Transform transform)
        {
            KeyValuePair<MeshFilter, Mesh> original;
            if (!originals.TryGetValue(transform, out original) || original.Key == null) return;
            originals.Remove(transform);
            original.Key.sharedMesh = original.Value;
            var renderer = transform.GetComponent<MeshRenderer>();
            if (moved.Add(transform))
                Debug.Log("CROW_BATCH moved " + Perf.Path(transform, 6) + " in " + transform.gameObject.scene.name.Replace(' ', '_') +
                          ": own mesh again, batched=" + (renderer != null && renderer.isPartOfStaticBatch ? 1 : 0));
        }

        // batch=nudge (a test of Unbatch): lift ten batched renderers by 0.3 m.
        static void Nudge()
        {
            int count = 0;
            foreach (Transform transform in watched.Where(t => t != null).Take(10).ToList())
            {
                transform.position += new Vector3(0f, 0.3f, 0f);
                count++;
            }
            Debug.Log("CROW_BATCH nudged " + count);
        }

        static bool Moves(Transform transform)
        {
            // Animator and Animation by name: CrowCompat doesn't reference the animation module.
            for (Transform t = transform; t != null; t = t.parent)
            {
                if (t.GetComponent<Rigidbody>() != null) return true;
                foreach (Behaviour behaviour in t.GetComponents<Behaviour>())
                {
                    string name = behaviour == null ? null : behaviour.GetType().Name;
                    if (name == "Animator" || name == "Animation") return true;
                }
            }
            return false;
        }

        static void Combine()
        {
            if (rule == "off") return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var scenes = new Dictionary<Scene, List<GameObject>>();
            int seen = 0, unreadable = 0, moving = 0, player = 0, already = 0, vertices = 0;
            string why = "";
            MoverWhy.Clear();
            covered.Clear();
            HashSet<Transform> movers = rule == "safe" ? Movers(out why) : null;
            foreach (MeshRenderer renderer in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                seen++;
                if (renderer.isPartOfStaticBatch)
                {
                    already++;
                    continue;
                }
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter == null ? null : filter.sharedMesh;
                if (mesh == null || mesh.vertexCount == 0) continue;
                if (!mesh.isReadable)
                {
                    unreadable++;
                    continue;
                }
                Scene scene = renderer.gameObject.scene;
                if (scene.name == "DontDestroyOnLoad" || scene.name == "CrowMara" || renderer.transform.root.name.StartsWith("CharacterCameraPrefab"))
                {
                    player++;
                    continue;
                }
                if (Moves(renderer.transform) || (movers != null && Below(renderer.transform, movers)))
                {
                    moving++;
                    continue;
                }
                List<GameObject> list;
                if (!scenes.TryGetValue(scene, out list)) scenes[scene] = list = new List<GameObject>();
                list.Add(renderer.gameObject);
                originals[renderer.transform] = new KeyValuePair<MeshFilter, Mesh>(filter, mesh);
                vertices += mesh.vertexCount;
            }
            int batched = 0;
            foreach (KeyValuePair<Scene, List<GameObject>> pair in scenes)
            {
                StaticBatchingUtility.Combine(pair.Value.ToArray(), null);
                foreach (GameObject go in pair.Value)
                {
                    var renderer = go.GetComponent<MeshRenderer>();
                    if (renderer == null || !renderer.isPartOfStaticBatch)
                    {
                        originals.Remove(go.transform);
                        continue;
                    }
                    batched++;
                    go.transform.hasChanged = false;
                    watched.Add(go.transform);
                }
            }
            Batched += batched;
            foreach (Transform gone in originals.Keys.Where(t => t == null).ToList()) originals.Remove(gone);
            Debug.Log("CROW_BATCH " + rule + ": " + batched + " of " + seen + " renderers batched (" + vertices + " vertices) in " +
                      clock.ElapsedMilliseconds + " ms; left out: unreadable " + unreadable + ", moving " + moving + ", player " + player +
                      ", batched before " + already + why);
            if (covered.Count > 0)
                Debug.Log("CROW_BATCH left out below " + string.Join(" ", covered.OrderByDescending(p => p.Value).Take(40)
                    .Select(p => Perf.Path(p.Key, 4) + "=" + p.Value + "(" + (MoverWhy.ContainsKey(p.Key) ? MoverWhy[p.Key] : "?").Replace(' ', '_') + ")").ToArray()));
        }

        // -crow-cycle hide=static|safe|none: a test of what each renderer costs the main thread. static
        // disables every renderer static batching would take (readable or not), safe only those
        // static batching's safe rule takes, none enables them again. The picture loses them.
        static readonly List<Renderer> hidden = new List<Renderer>();

        internal static void Hide(string value)
        {
            foreach (Renderer renderer in hidden)
                if (renderer != null) renderer.enabled = true;
            hidden.Clear();
            if (value == "none")
            {
                Debug.Log("CROW_BATCH hide none");
                return;
            }
            string why = "";
            MoverWhy.Clear();
            covered.Clear();
            HashSet<Transform> movers = value == "safe" ? Movers(out why) : null;
            int seen = 0, vertices = 0;
            foreach (MeshRenderer renderer in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                seen++;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter == null ? null : filter.sharedMesh;
                if (mesh == null) continue;
                Scene scene = renderer.gameObject.scene;
                if (scene.name == "DontDestroyOnLoad" || scene.name == "CrowMara" || renderer.transform.root.name.StartsWith("CharacterCameraPrefab")) continue;
                if (Moves(renderer.transform) || (movers != null && Below(renderer.transform, movers))) continue;
                renderer.enabled = false;
                hidden.Add(renderer);
                vertices += mesh.vertexCount;
            }
            Debug.Log("CROW_BATCH hide " + value + ": " + hidden.Count + " of " + seen + " renderers off (" + vertices + " vertices)" + why);
        }

        static bool Below(Transform transform, HashSet<Transform> roots)
        {
            for (Transform t = transform; t != null; t = t.parent)
                if (roots.Contains(t))
                {
                    int count;
                    covered.TryGetValue(t, out count);
                    covered[t] = count + 1;
                    return true;
                }
            return false;
        }

        // Why each moving root counts (the FSM and action, or the script), and how many candidates
        // each left out, for the log.
        static readonly Dictionary<Transform, string> MoverWhy = new Dictionary<Transform, string>();
        static readonly Dictionary<Transform, int> covered = new Dictionary<Transform, int>();

        // PlayMaker actions that move, rotate, scale or reparent a transform, or push a rigidbody (by
        // class name; DOTweenTransform* too). SetProperty counts when it sets a transform's property.
        static readonly HashSet<string> MoverActions = new HashSet<string>
        {
            "SetPosition", "SetRotation", "SetScale", "Rotate", "Translate", "LookAt", "SmoothLookAt", "SmoothLookAt2d",
            "SmoothLookAtDirection", "SmoothLookAtWithLimit", "MoveTowards", "SetRandomRotation", "SetParent", "DOTweenRigidbodyMove",
            "AddForce", "AddExplosionForce", "SetVelocity", "SetAngularVelocity", "SetIsKinematic", "UseGravity",
        };

        static bool IsMoverAction(string name)
        {
            return MoverActions.Contains(name) || name.StartsWith("DOTweenTransform") || name == "SetProperty";
        }

        // Game scripts that move their own object (or what is below it) after it starts.
        static readonly HashSet<string> MoverScripts = new HashSet<string>
        {
            "TransformResetter", "PMGlobalFloatToRotation", "DOTweenAnimation", "DOTweenPath", "SmoothFollow", "LookAtCamera",
        };

        const System.Reflection.BindingFlags Instance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        static object Field(object target, string name)
        {
            if (target == null) return null;
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                System.Reflection.FieldInfo field = type.GetField(name, Instance | System.Reflection.BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(target);
            }
            return null;
        }

        static bool TransformProperty(string property)
        {
            if (property == null) return false;
            foreach (string part in new[] { "position", "rotation", "scale", "euler", "parent" })
                if (property.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // The objects a loaded mover action moves: its FsmOwnerDefault fields (the owner, or another
        // object; PlayMaker's actions name what they act on so, and their FsmGameObject fields are
        // other parameters: what to look at, the new parent); for SetProperty, its target when the
        // property is a transform's. Returns whether the action moves anything.
        static bool ActionTargets(object action, Component owner, List<KeyValuePair<object, string>> targets)
        {
            string name = action.GetType().Name;
            if (!IsMoverAction(name)) return false;
            bool moves = name != "SetProperty";
            foreach (System.Reflection.FieldInfo field in action.GetType().GetFields(Instance))
            {
                object value = field.GetValue(action);
                if (value == null) continue;
                string kind = value.GetType().Name;
                if (name == "SetProperty")
                {
                    if (kind != "FsmProperty") continue;
                    object target = Field(Field(value, "TargetObject"), "value");
                    if ((!(target is Transform) && !(target is GameObject)) || !TransformProperty(Field(value, "PropertyName") as string)) continue;
                    moves = true;
                    targets.Add(new KeyValuePair<object, string>(target, name));
                }
                else if (kind == "FsmOwnerDefault")
                {
                    object option = Field(value, "ownerOption");
                    targets.Add(new KeyValuePair<object, string>(option != null && Convert.ToInt32(option) == 0 ? owner.transform : Field(Field(value, "gameObject"), "value"), name));
                }
            }
            return moves;
        }

        // A state whose actions aren't loaded (its FSM never started: an inactive object): when its
        // action data names a mover action, all the objects the data names, and the owner.
        static bool DataTargets(object state, Component owner, List<KeyValuePair<object, string>> targets)
        {
            object data = Field(state, "actionData");
            var names = Field(data, "actionNames") as System.Collections.IList;
            if (names == null) return false;
            bool any = false;
            foreach (object item in names)
            {
                string full = item as string ?? "";
                if (IsMoverAction(full.Substring(full.LastIndexOf('.') + 1))) any = true;
            }
            if (!any) return false;
            targets.Add(new KeyValuePair<object, string>(owner.transform, "data"));
            foreach (string list in new[] { "fsmOwnerDefaultParams", "fsmGameObjectParams", "fsmPropertyParams" })
            {
                var items = Field(data, list) as System.Collections.IList;
                if (items == null) continue;
                foreach (object item in items)
                {
                    if (list == "fsmOwnerDefaultParams") targets.Add(new KeyValuePair<object, string>(Field(Field(item, "gameObject"), "value"), "data"));
                    else if (list == "fsmGameObjectParams") targets.Add(new KeyValuePair<object, string>(Field(item, "value"), "data"));
                    else targets.Add(new KeyValuePair<object, string>(Field(Field(item, "TargetObject"), "value"), "data"));
                }
            }
            return true;
        }

        // What PlayMaker's FSMs and the game's scripts in the loaded scenes may move: the objects their
        // mover actions name, with their FSMs' GameObject variables (a target often is one), and the
        // objects of mover scripts.
        static HashSet<Transform> Movers(out string report)
        {
            var movers = new HashSet<Transform>();
            int fsms = 0, moverFsms = 0, unloaded = 0, unknown = 0, scripts = 0;
            Type fsmType = Type.GetType("PlayMakerFSM, PlayMaker");
            if (fsmType != null)
                foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(fsmType))
                {
                    var component = found as Component;
                    if (component == null || !component.gameObject.scene.IsValid()) continue;
                    fsms++;
                    object fsm = Field(component, "fsm");
                    var states = Field(fsm, "states") as Array;
                    if (states == null) continue;
                    bool mover = false;
                    var targets = new List<KeyValuePair<object, string>>();
                    foreach (object state in states)
                    {
                        if (state == null) continue;
                        var actions = Field(state, "actions") as Array;
                        if (actions == null)
                        {
                            if (DataTargets(state, component, targets))
                            {
                                mover = true;
                                unloaded++;
                            }
                            continue;
                        }
                        foreach (object action in actions)
                            if (action != null && ActionTargets(action, component, targets)) mover = true;
                    }
                    if (!mover) continue;
                    moverFsms++;
                    // States not loaded name their targets through the FSM's variables.
                    var variables = Field(Field(fsm, "variables"), "gameObjectVariables") as Array;
                    if (variables != null && targets.Any(t => t.Value == "data"))
                        foreach (object variable in variables) targets.Add(new KeyValuePair<object, string>(Field(variable, "value"), "data"));
                    foreach (KeyValuePair<object, string> target in targets)
                    {
                        var go = target.Key as GameObject;
                        var part = target.Key as Component;
                        Transform transform = go != null ? go.transform : part != null ? part.transform : null;
                        if (transform == null)
                        {
                            unknown++;
                            continue;
                        }
                        movers.Add(transform);
                        if (!MoverWhy.ContainsKey(transform)) MoverWhy[transform] = Perf.Path(component.transform, 3) + ":" + target.Value;
                    }
                }
            foreach (MonoBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (behaviour == null || !MoverScripts.Contains(behaviour.GetType().Name)) continue;
                movers.Add(behaviour.transform);
                MoverWhy[behaviour.transform] = behaviour.GetType().Name;
                scripts++;
            }
            report = "; safe: " + moverFsms + " of " + fsms + " FSMs move things (" + unloaded + " states not loaded), " + movers.Count +
                     " moving roots, " + unknown + " targets unknown, " + scripts + " mover scripts";
            return movers;
        }
    }
}
