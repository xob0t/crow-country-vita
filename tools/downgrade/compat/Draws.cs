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

        // From Quality.Install: Install, then Merge's and Cameras', then InstallReport (Install installed those two
        // in between until 21 Oct; the order is the same).
        public static void Install(string[] args)
        {
            string casters = CrowFlags.Value(args, "-crow-casters");
            if (casters != null) Casters(casters);
            else if (Application.platform == RuntimePlatform.PSP2) Casters("player");
        }

        // -crow-draw-report.
        public static void InstallReport(string[] args)
        {
            every = CrowFlags.Number(args, "-crow-draw-report", -1f);
            if (every <= 0f) return;
            CrowEvents.Loaded("Draws.sceneLoaded", (scene, mode) => detail = true);
            CrowEvents.BeforeRender("Draws.Tick", Tick);
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
        // What the rule turned off, with the casting mode it had (ShadowsOnly: switched off instead).
        static readonly Dictionary<Renderer, ShadowCastingMode> turnedOff = new Dictionary<Renderer, ShadowCastingMode>();

        public static void Casters(string rule)
        {
            casterRule = rule;
            lastCasters = -10f;
            Debug.Log("CROW_DRAWS casters " + rule);
            if (castersHooked) Restore();  // a later rule (-crow-ab casters=game) starts from the game's casters
            else
            {
                castersHooked = true;
                CrowEvents.Loaded("Draws.sceneLoaded 2", (scene, mode) => lastCasters = -10f);
                CrowEvents.Unloaded("Draws.sceneUnloaded", scene => Forget());
                CrowEvents.BeforeRender("Draws.CasterTick", CasterTick);
            }
            CasterTick();
        }

        static void Restore()
        {
            foreach (KeyValuePair<Renderer, ShadowCastingMode> pair in turnedOff)
            {
                if (pair.Key == null) continue;
                if (pair.Value == ShadowCastingMode.ShadowsOnly) pair.Key.enabled = true;
                else pair.Key.shadowCastingMode = pair.Value;
            }
            turnedOff.Clear();
        }

        // The destroyed renderers' entries, at each unload (a session's worth otherwise).
        static void Forget()
        {
            var dead = new List<Renderer>();
            foreach (Renderer renderer in turnedOff.Keys) if (renderer == null) dead.Add(renderer);
            foreach (Renderer renderer in dead) turnedOff.Remove(renderer);
        }

        static void CasterTick()
        {
            float now = Time.realtimeSinceStartup;
            // Every 10 s (each pass allocates the renderer list: Station Square 12 KB) and after each load.
            if (casterRule == null || now - lastCasters < 10f) return;
            lastCasters = now;
            int changed = 0, casters = 0;
            if (casterRule == "player" || casterRule.StartsWith("player:")) PlayerRenderers();
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                if (renderer.shadowCastingMode == ShadowCastingMode.Off) continue;
                bool shadowsOnly = renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly;
                if (shadowsOnly && !renderer.enabled) continue;
                casters++;
                if (!CasterOff(renderer, casterRule)) continue;
                // A shadows-only renderer is the game's invisible shadow caster: Off would draw it.
                turnedOff[renderer] = renderer.shadowCastingMode;
                if (shadowsOnly) renderer.enabled = false;
                else renderer.shadowCastingMode = ShadowCastingMode.Off;
                changed++;
            }
            playerRenderers.Clear();  // not held between passes
            if (changed > 0) Debug.Log("CROW_DRAWS casters " + casterRule + ": " + changed + " of " + casters + " off");
        }

        // Whether a hierarchy root is the player prefab, by its name, read once per root (a name read
        // is a new string, and CasterTick looks at every root every 10 s).
        static readonly Dictionary<GameObject, bool> playerRoots = new Dictionary<GameObject, bool>();

        static bool PlayerRoot(GameObject root)
        {
            bool player;
            if (playerRoots.TryGetValue(root, out player)) return player;
            if (playerRoots.Count > 4096) playerRoots.Clear();  // roots of unloaded scenes
            return playerRoots[root] = root.name.StartsWith("CharacterCameraPrefab");
        }

        // The player rules' renderers: those under a root named so (PlayerRoot), in a loaded scene or in
        // DontDestroyOnLoad (the player's), on active GameObjects (FindObjectsOfType lists no others), made again
        // at each pass, so they follow the player's hierarchy as it is then: PlayerSplit's UI Canvases and RigPark's
        // CrowRigPark are roots of other names, as before. Until 21 Oct each caster's transform.root was asked,
        // which made a managed Transform, held by one of IL2CPP's pinned GC handles until the transform is
        // destroyed (Memory.cs, GcHandles), for every caster (Haunted Hilltop 5,067; with VitaLit's layer change
        // 700-800 fewer handles there in the Windows IL2CPP player, so most of them had one anyway); the areas
        // have one or two roots, and the renderers found here are casters' too, which FindObjectsOfType makes anyway.
        static readonly HashSet<Renderer> playerRenderers = new HashSet<Renderer>();
        static readonly List<GameObject> roots = new List<GameObject>();
        static readonly List<Renderer> found = new List<Renderer>();
        static Scene kept;  // DontDestroyOnLoad's scene, which SceneManager doesn't list

        static void PlayerRenderers()
        {
            playerRenderers.Clear();
            if (!kept.IsValid())
            {
                var probe = new GameObject("CrowDrawsScene");
                UnityEngine.Object.DontDestroyOnLoad(probe);
                kept = probe.scene;
                UnityEngine.Object.DestroyImmediate(probe);
            }
            for (int i = -1; i < SceneManager.sceneCount; i++)
            {
                Scene scene = i < 0 ? kept : SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || (i >= 0 && !scene.isLoaded)) continue;  // a scene still loading has no player roots
                scene.GetRootGameObjects(roots);
                foreach (GameObject root in roots)
                {
                    if (!PlayerRoot(root)) continue;
                    root.GetComponentsInChildren(false, found);
                    foreach (Renderer renderer in found) playerRenderers.Add(renderer);
                }
            }
            roots.Clear();
            found.Clear();
        }

        static bool CasterOff(Renderer renderer, string rule)
        {
            if (rule == "off") return true;
            // player: only the player's own parts (Mara, under CharacterCameraPrefab) keep their shadows. The
            // scenes without the player prefab in play (no MaraLoader: Ladder, Driving Intro, Ending Sequence,
            // Results, CrowQuest Unlock; their Mara is a model of the scene's own) get every shadow off
            // (Ladder: "casters player: 43 of 43 off"). Left so: at the PS Vita's 12 m shadow distance most of
            // them would draw none anyway (Ladder's camera is 15.3 m from the climber; the Driving Intro's and
            // the Ending's start 18.7 and 12.3 m from their scene's origin).
            if (rule == "player") return !playerRenderers.Contains(renderer);
            Bounds bounds = renderer.bounds;
            Vector3 size = bounds.size;
            // player:<m>: the player's parts of m metres (bounds diagonal) or more (Mara has 39 casters:
            // 7 under 0.1 m, 7 under 0.2, 22 under 0.4, 3 larger).
            if (rule.StartsWith("player:"))
            {
                float least;
                return !playerRenderers.Contains(renderer) ||
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
            line.Append(" receivers=").Append(receivers).Append(" turnedOff=").Append(turnedOff.Keys.Count(r => r != null));
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
}
