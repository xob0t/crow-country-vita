using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// The areas' static geometry in fewer draws: after the static merge (Merge.cs), the split and MeshCombineStudio's
// baked combining (unity/MeshCombineBake.cs) have left one renderer per material and cell, the level's renderers that
// draw alike but for their textures are drawn with one material: their materials' textures go into an atlas per
// scene (LevelProcess, called by unity/LevelAtlas.cs as the PS Vita build writes each scene; CROW_LEVEL_ATLAS=
// on|ab|report|off), and the renderers of each group become one per carrier and cell.
//   What may join: an enabled MeshRenderer that nothing excludes and that is not its own (Merge.Analyze, with the
//   level's rules: Merge.Split's SplitPart is data, a SplitHolder switches its part, MeshCombineStudio's
//   CachedComponents is data and a combiner that no longer combines at run time searches nothing; whatever a
//   global holds is taken to move, since another scene's FSMs (the player's) may move it; and whatever the player's
//   FSMs find by name or tag stays as it is), opaque or cutout, triangles, readable at build time, with normals,
//   not an enemy's, not shadows-only, not on the UI layer, reached by at most LevelMaxLights point and spot lights
//   (Unity lights an object with four per vertex: a merged renderer reached by more could get other ones; a
//   merged renderer whose bounds reach a light none of its parts' did is taken apart again), every object between
//   it and its carrier active.
//   Carrier: the nearest object above it that moves, switches or is destroyed as a whole (an anchor), or a split part
//   below a SplitHolder (which the holder switches); a split part without a holder stands where its old parent is
//   (SplitFollow), so the way up goes on from its old parent. The merged renderer is a child of the carrier (or at
//   the scene's root), so it moves and switches with it.
//   Materials: CrowVita/Lit (or Standard) with no keyword but _ALPHATEST_ON, _DETAIL_MULX2, _SPECULARHIGHLIGHTS_OFF and
//   _GLOSSYREFLECTIONS_OFF, no metallic map, colour at most 1, textures PNG or JPG at their imported size, without
//   mipmaps, DXT on the PS Vita (a page keeps the texture format: DXT1 opaque, DXT5 cutout; uncompressed sources on
//   other players stay uncompressed), not mirrored. Each is a tile of the texels its vertices sample, texel for texel
//   (EnemyRebuild.cs' Texel: _MainTex x _Color, x the detail map x2), with LevelPad texels of padding wrapped as the
//   texture wraps, at the source's 4x4 block phase so DXT blocks hold the same texels; a slot's UVs on a repeating
//   texture are first moved by whole repeats to start in the first one. Groups: carrier, layer, shadow and probe
//   settings, the lights reaching the part, page, the material's base pass (shader, queue, keywords, cutoff, blend,
//   filter) with its metallic and smoothness, and a LevelCell cell of the part's centre. A group of two renderers or
//   more (or one renderer's slots) becomes one renderer per 65,000 vertices (positions, normals, UV0: what
//   CrowVita/Lit reads), the parts' renderers switched off; on drops the parts' meshes nothing else uses (ab keeps
//   them, so -crow-level-atlas off can switch back). report only logs (CROW_LEVEL_ATLAS lines; CROW_LEVEL_ATLAS_PARTS=1
//   also one CROW_LEVEL_ATLAS_PART line per enabled renderer).
// Run time (every player; it acts on what such a build wrote):
//   -crow-level-atlas on|off      off: the atlas renderers off and their parts on (ab builds); latlas=on|off|report
//                                 for -crow-cycle, -crow-fight-cycle and -crow-ab
//   -crow-level-atlas-check       every second, each atlas renderer's parts (and a merged part's own parts): active
//                                 exactly when it is, where they were relative to it, their renderers still off
//                                 (CROW_LEVEL_ATLAS_CHECK)
//   -crow-level-atlas-report <s>  s seconds after each area load (and at latlas=report): the level's renderers drawn
//                                 in the last frame, each with its material, shader, keywords and texture
//                                 (CROW_LEVEL_ATLAS_DRAWN), and their counts (CROW_LEVEL_ATLAS_DRAWS)
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    // An atlas renderer's parts, for -crow-level-atlas off and the check.
    public class LevelAtlasRecord : MonoBehaviour
    {
        public Renderer[] parts;
    }

    public static partial class Merge
    {
        // ---------------------------------------------------------------- run time

        static float levelReportAfter = -1f, levelReportAt = -1f;
        static bool levelOff;

        internal static void InstallLevel(string[] args)
        {
            levelOff = CrowFlags.Value(args, "-crow-level-atlas") == "off";
            if (CrowFlags.Has(args, "-crow-level-atlas-check")) LevelCheck.Install();
            levelReportAfter = CrowFlags.Number(args, "-crow-level-atlas-report", -1f);
            if (!levelOff && levelReportAfter < 0f) return;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (scene.name == "Logos" || scene.name == "Title" || scene.name == "CrowMara" || scene.name == "CrowGap") return;
                if (levelOff) Debug.Log(SetLevel(false, scene));
                if (levelReportAfter >= 0f) levelReportAt = Time.realtimeSinceStartup + levelReportAfter;
            };
            if (levelReportAfter < 0f) return;
            Application.onBeforeRender += () =>
            {
                if (levelReportAt < 0f || Time.realtimeSinceStartup < levelReportAt) return;
                levelReportAt = -1f;
                LevelReport();
            };
        }

        // A -crow-cycle / -crow-fight-cycle / -crow-ab change (from EnemyCost.Change); false when it isn't one.
        internal static bool ChangeLevel(string key, string value)
        {
            if (key != "latlas") return false;
            if (value == "report")
            {
                LevelReport();
                return true;
            }
            levelOff = value == "off";
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) Debug.Log(SetLevel(!levelOff, scene));
            }
            return true;
        }

        static string SetLevel(bool on, Scene scene)
        {
            int records = 0, parts = 0, missing = 0;
            foreach (LevelAtlasRecord record in Resources.FindObjectsOfTypeAll<LevelAtlasRecord>())
            {
                if (record == null || record.gameObject.scene != scene) continue;
                var renderer = record.GetComponent<MeshRenderer>();
                if (renderer != null) renderer.enabled = on;
                records++;
                if (record.parts == null) continue;
                foreach (Renderer part in record.parts)
                {
                    if (part == null) continue;
                    var filter = part.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh == null) missing++;  // an on build dropped it
                    part.enabled = !on;
                    parts++;
                }
            }
            return "CROW_LEVEL_ATLAS " + scene.name.Replace(' ', '_') + " " + (on ? "on" : "off") + ": " + records + " atlas renderers, " + parts + " parts" +
                   (missing > 0 ? ", " + missing + " without their meshes (not an ab build)" : "");
        }

        // The level's renderers drawn in the last frame (not the player's, in DontDestroyOnLoad, nor the enemies').
        static void LevelReport()
        {
            try
            {
                int drawn = 0, opaque = 0, atlas = 0, mcs = 0, merged = 0, partsInView = 0;
                Camera view = Camera.main;
                Plane[] planes = view == null ? null : GeometryUtility.CalculateFrustumPlanes(view);
                var materials = new HashSet<Material>();
                var byShader = new Dictionary<string, int>();
                foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (!renderer.enabled || !renderer.isVisible || renderer.gameObject.scene.name == "DontDestroyOnLoad" || EnemyRoot(renderer.transform)) continue;
                    drawn++;
                    Material material = renderer.sharedMaterial;
                    if (material != null) materials.Add(material);
                    bool isOpaque = material != null && material.renderQueue <= 2500;
                    if (isOpaque) opaque++;
                    var record = renderer.GetComponent<LevelAtlasRecord>();
                    bool isAtlas = record != null;
                    if (isAtlas)
                    {
                        atlas++;
                        // Its parts that would be drawn in its place (on and in the view, by their meshes' bounds).
                        if (planes != null && record.parts != null)
                            foreach (Renderer part in record.parts)
                            {
                                var partFilter = part == null ? null : part.GetComponent<MeshFilter>();
                                if (partFilter == null || partFilter.sharedMesh == null || !part.gameObject.activeInHierarchy) continue;
                                Bounds local = partFilter.sharedMesh.bounds;
                                var world = new Bounds(part.transform.TransformPoint(local.center), Vector3.zero);
                                for (int c = 0; c < 8; c++)
                                    world.Encapsulate(part.transform.TransformPoint(local.center + Vector3.Scale(local.extents,
                                        new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1))));
                                if (GeometryUtility.TestPlanesAABB(planes, world)) partsInView += part.sharedMaterials.Length;
                            }
                    }
                    string origin = isAtlas ? "atlas" : renderer.GetComponent<MergedRenderer>() != null ? "merged" : InCombiner(renderer.transform) ? "mcs" : "plain";
                    if (origin == "mcs") mcs++;
                    if (origin == "merged") merged++;
                    string shader = material == null || material.shader == null ? "none" : material.shader.name;
                    int count;
                    byShader.TryGetValue(shader, out count);
                    byShader[shader] = count + 1;
                    Texture texture = material != null && material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
                    var t2 = texture as Texture2D;
                    var filter = renderer.GetComponent<MeshFilter>();
                    Debug.Log("CROW_LEVEL_ATLAS_DRAWN " + Perf.Path(renderer.transform, 99) + " kind=" + renderer.GetType().Name + " origin=" + origin +
                              " verts=" + (filter != null && filter.sharedMesh != null ? filter.sharedMesh.vertexCount : -1) + " subs=" + renderer.sharedMaterials.Length +
                              " mat=" + (material == null ? "none" : material.name.Replace(' ', '_') + "@" + material.renderQueue) + " shader=" + shader.Replace(' ', '_') +
                              " keys=" + (material == null ? "" : string.Join(",", material.shaderKeywords)) +
                              " tex=" + (texture == null ? "-" : texture.name.Replace(' ', '_') + ":" + texture.width + "x" + texture.height + ":" + (t2 != null ? t2.format + ":mips" + t2.mipmapCount : texture.GetType().Name) +
                                                               ":" + texture.wrapModeU + "/" + texture.wrapModeV + ":" + texture.filterMode));
                }
                Debug.Log("CROW_LEVEL_ATLAS_DRAWS drawn=" + drawn + " opaque=" + opaque + " atlas=" + atlas + " (their parts in view " + partsInView + ": " +
                          (drawn - atlas + partsInView) + " drawn without the atlas, ab builds)" + " mcs=" + mcs + " merged=" + merged + " materials=" + materials.Count +
                          " arrays=" + SystemInfo.supports2DArrayTextures + " device=" + SystemInfo.graphicsDeviceType +
                          " shaders: " + string.Join(" ", byShader.OrderByDescending(p => p.Value).Select(p => p.Key.Replace(' ', '_') + "=" + p.Value).ToArray()));
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_LEVEL_ATLAS report failed: " + exception);
            }
        }

        static bool EnemyRoot(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.name.StartsWith("Enemy ", StringComparison.Ordinal) && t.GetComponent<Rigidbody>() != null) return true;
            return false;
        }

        static Type combinerTypeCache;

        static bool InCombiner(Transform t)
        {
            if (combinerTypeCache == null) combinerTypeCache = Type.GetType("MeshCombineStudio.MeshCombiner, MeshCombineStudio.Runtime");
            if (combinerTypeCache == null) return false;
            for (; t != null; t = t.parent)
                if (t.GetComponent(combinerTypeCache) != null) return true;
            return false;
        }

        // -crow-level-atlas-check: every second, whether each atlas renderer still stands for its parts.
        static class LevelCheck
        {
            static readonly Dictionary<Renderer, Matrix4x4> relative = new Dictionary<Renderer, Matrix4x4>();
            static readonly Dictionary<LevelAtlasRecord, string[]> names = new Dictionary<LevelAtlasRecord, string[]>();
            static readonly HashSet<Renderer> reported = new HashSet<Renderer>();
            static readonly Dictionary<LevelAtlasRecord, int> lightsSeen = new Dictionary<LevelAtlasRecord, int>();
            static float next, lastSummary;
            static int checks, differences, visibleChecks, overLights;

            internal static void Install()
            {
                Application.onBeforeRender += Tick;
                SceneManager.sceneLoaded += (scene, mode) =>
                {
                    relative.Clear();
                    // The parts' names now: what is destroyed while the scene starts is still there in this frame.
                    foreach (LevelAtlasRecord record in Resources.FindObjectsOfTypeAll<LevelAtlasRecord>())
                        if (record != null && record.gameObject.scene == scene && record.parts != null)
                            names[record] = record.parts.Select(r => r == null ? "(gone at load)" : Perf.Path(r.transform, 6)).ToArray();
                    next = Time.realtimeSinceStartup + 3f;
                };
            }

            static void Tick()
            {
                float now = Time.realtimeSinceStartup;
                if (now < next) return;
                next = now + 1f;
                int groups = 0;
                // The lights Unity lights objects with now (VitaLit's key lights have culling mask 0): a drawn atlas
                // renderer reached by more than 4 gets 4 per vertex and the rest in its harmonics, chosen by its
                // centre, where its parts' choice could have been another.
                var unityLights = UnityEngine.Object.FindObjectsOfType<Light>()
                    .Where(l => l.isActiveAndEnabled && l.intensity > 0f && (l.type == LightType.Point || l.type == LightType.Spot)).ToList();
                foreach (LevelAtlasRecord record in Resources.FindObjectsOfTypeAll<LevelAtlasRecord>())
                {
                    if (record == null || !record.gameObject.scene.IsValid() || record.parts == null) continue;
                    var merged = record.GetComponent<MeshRenderer>();
                    if (merged == null || !merged.enabled) continue;  // latlas=off
                    groups++;
                    if (merged.isVisible)
                    {
                        visibleChecks++;
                        Bounds bounds = merged.bounds;
                        int layer = 1 << record.gameObject.layer;
                        int reach = unityLights.Count(l => (l.cullingMask & layer) != 0 && bounds.SqrDistance(l.transform.position) <= l.range * l.range);
                        if (reach > 4)
                        {
                            overLights++;
                            int known;
                            if (!lightsSeen.TryGetValue(record, out known) || reach > known)
                            {
                                lightsSeen[record] = reach;
                                Debug.Log("CROW_LEVEL_ATLAS_LIGHTS " + Perf.Path(record.transform, 4) + " drawn, lit by " + reach + " lights: " +
                                          string.Join(",", unityLights.Where(l => (l.cullingMask & layer) != 0 && bounds.SqrDistance(l.transform.position) <= l.range * l.range)
                                              .Select(l => l.name.Replace(' ', '_')).ToArray()));
                            }
                        }
                    }
                    bool active = record.gameObject.activeInHierarchy;
                    string[] partNames;
                    if (!names.TryGetValue(record, out partNames))
                        names[record] = partNames = record.parts.Select(r => r == null ? "(gone at load)" : Perf.Path(r.transform, 6)).ToArray();
                    var all = new List<Renderer>();
                    foreach (Renderer part in record.parts)
                    {
                        all.Add(part);
                        var inner = part == null ? null : part.GetComponent<MergedRenderer>();
                        if (inner != null && inner.parts != null) all.AddRange(inner.parts);
                    }
                    foreach (Renderer part in all)
                    {
                        checks++;
                        string why = null;
                        if (part == null) why = active ? "destroyed while the atlas renderer is active" : null;
                        else
                        {
                            Matrix4x4 pose = record.transform.worldToLocalMatrix * part.transform.localToWorldMatrix;
                            Matrix4x4 first;
                            if (!relative.TryGetValue(part, out first)) relative[part] = first = pose;
                            float moved = 0f;
                            for (int i = 0; i < 16; i++) moved = Math.Max(moved, Math.Abs(pose[i] - first[i]));
                            if (part.gameObject.activeInHierarchy != active) why = "active=" + part.gameObject.activeInHierarchy + " atlas=" + active;
                            else if (moved > 0.001f) why = "moved " + moved.ToString("0.###", CultureInfo.InvariantCulture);
                            else if (part.enabled) why = "its renderer is on";
                        }
                        if (why == null) continue;
                        differences++;
                        if (!reported.Add(part != null ? part : (Renderer)merged)) continue;
                        Debug.Log("CROW_LEVEL_ATLAS_CHECK " + (part == null ? Perf.Path(record.transform, 4) + " part " +
                                  string.Join(",", partNames.Where((n, i) => record.parts[i] == null).ToArray()) : Perf.Path(part.transform, 6)) + ": " + why);
                    }
                }
                if (now < lastSummary + 10f) return;
                lastSummary = now;
                Debug.Log("CROW_LEVEL_ATLAS_CHECK groups=" + groups + " checks=" + checks + " differences=" + differences + " drawnChecks=" + visibleChecks +
                          " over4lights=" + overLights);
            }
        }

        // ---------------------------------------------------------------- the build (unity/LevelAtlas.cs)

        public static float LevelCell = 8f;
        public static int LevelMaxTile = 1024, LevelPage = 2048, LevelMaxLights = 64, LevelMaxPages = 2;
        // The atlas pages' memory per scene, at most (the pages that save most draws first).
        public static long LevelMaxBytes = 4L << 20;
        // The merged meshes' memory per scene, at most, after the parts' meshes nothing else uses are dropped (on; ab
        // keeps them): the groups that save most draws per byte first. A part whose mesh other objects share (a
        // shape copied about the area) costs its whole copy.
        public static long LevelMaxMeshBytes = 2L << 20;
        // Set by the build: the PS Vita's (DXT sources only), and whether to log every renderer.
        public static bool LevelVita, LevelParts;
        // How the parts' lights split groups: exact (the same point and spot lights reach each part's bounds), union4 (parts
        // reached by at most 4 group so that at most 4 reach any group's parts, whatever the set: Unity then lights the merged
        // renderer per vertex with all of them, as each part with its own), any (lights split nothing).
        public static string LevelLights = "exact";
        // MeshCombineStudio's combined renderers grouped by their own cell (its Cell object) instead of LevelCell's.
        public static bool LevelMcsCells;
        // Metallic and smoothness from a quarter-size page (_METALLICGLOSSMAP) for materials without a metallic map, so
        // materials that differ only in those values share a group.
        public static bool LevelSmooth;
        const int LevelPad = 4;
        static bool levelRules;

        static bool KnownLevel(Component component, string name, Analysis analysis)
        {
            switch (name)
            {
                case "SplitPart":  // Merge.Split's record of a part's old place (SplitFollow places the part)
                case "CachedComponents":  // MeshCombineStudio's references for its combiner, no code
                case "LevelAtlasRecord":
                    return true;
                case "SplitHolder":  // switches its part with itself
                    analysis.Add(Field(component, "part"), Mark.Anchor, name);
                    return true;
            }
            return false;
        }

        // A MeshCombiner that combines at run time searches its parents then (its renderers are its own); one that
        // doesn't (the build combined it, or the game's editor did) searches nothing.
        static bool LevelSkipsCombiner(Component combiner)
        {
            return levelRules && !(bool)(Field(combiner, "combineInRuntime") ?? true);
        }

        // What stops a renderer from joining (or null), with its carrier.
        static string LevelBlocker(MeshRenderer renderer, Analysis analysis, out Transform carrier, out string state)
        {
            carrier = null;
            state = "static";
            for (Transform t = renderer.transform; t != null;)
            {
                if (t.tag == "EditorOnly") return "editor only";  // left out of the player
                Mark mark;
                if (analysis.marks.TryGetValue(t, out mark))
                {
                    if ((mark & Mark.Exclude) != 0) return "exclude:" + analysis.why[t];
                    if (t == renderer.transform && (mark & Mark.Own) != 0) return "own:" + analysis.why[t];
                    if ((mark & Mark.Anchor) != 0)
                    {
                        carrier = t;
                        state = "anchor:" + analysis.why[t];
                        return (mark & Mark.NoChild) != 0 ? "nochild:" + analysis.why[t] : null;
                    }
                }
                var part = t.GetComponent<SplitPart>();
                if (part != null && part.holder != null)
                {
                    carrier = t;
                    state = "holder";
                    return null;
                }
                if (!t.gameObject.activeSelf) return "inactive";
                t = part != null ? part.parent : t.parent;
            }
            return null;
        }

        sealed class LevelSlot
        {
            public MeshRenderer renderer;
            public Mesh mesh;
            public int subMesh;
            public Tile tile;
            public Transform carrier;
            public Vector2 shift;
            public string common, key;
            public int page = -1;
        }

        sealed class LevelPageInfo
        {
            public string Class;
            public bool Cutout, Compressed, Gloss, Detail, Smooth;
            public FilterMode Filter;
            public readonly List<Tile> Tiles = new List<Tile>();
            public int Width, Height, Weight;
            public Texture2D Texture, GlossTexture, DetailTexture, SmoothTexture;
        }

        static bool IsDxt(Texture texture)
        {
            var t = texture as Texture2D;
            return t != null && (t.format == TextureFormat.DXT1 || t.format == TextureFormat.DXT5);
        }

        // The level's rules on top of Prepare: what keeps a material from a tile (null: none).
        static string LevelPrepare(Tile tile)
        {
            string why = Prepare(tile);
            if (why != null) return why;
            Material m = tile.Material;
            if (tile.Color.r > 1f || tile.Color.g > 1f || tile.Color.b > 1f || tile.Color.a > 1f) return "bright colour";
            var properties = new List<string> { "_MainTex" };
            if (tile.Detailed) properties.Add("_DetailAlbedoMap");
            if (tile.HasGlossMap) properties.Add("_MetallicGlossMap");
            foreach (string property in properties)
            {
                Texture texture = m.HasProperty(property) ? m.GetTexture(property) : null;
                if (texture == null) continue;
                var t2 = texture as Texture2D;
                if (t2 == null) return "not a 2D texture";
                if (t2.mipmapCount > 1) return "mipmaps";
                Texture2D image = SourceImage != null ? SourceImage(texture) : null;
                if (image != null && (image.width != texture.width || image.height != texture.height)) return "resized";
                if (LevelVita && !IsDxt(texture)) return "format " + t2.format;
            }
            string set = KeywordSet(m.shaderKeywords);
            if (VariantSets != null && !VariantSets.Contains(set)) return "no variant " + set.Replace(' ', ',');
            return null;
        }

        static string LevelClass(Tile tile)
        {
            Texture main = tile.Material.HasProperty("_MainTex") ? tile.Material.GetTexture("_MainTex") : null;
            bool compressed = main == null ? LevelVita : IsDxt(main);
            // A metallic map's tiles have pages of their own, the only ones with a second (metallic) texture.
            return tile.Filter + "|" + (tile.Material.IsKeywordEnabled("_ALPHATEST_ON") ? "cutout" : "opaque") + "|" + (compressed ? "dxt" : "raw") +
                   (tile.HasGlossMap ? "|metallic" : "") + (tile.Detailed ? "|detail" : "");
        }

        static string Lit(Bounds bounds, List<Light> lights, out int count)
        {
            var ids = new List<int>();
            foreach (Light light in lights)
                if (light != null && bounds.SqrDistance(light.transform.position) <= light.range * light.range) ids.Add(light.GetInstanceID());
            ids.Sort();
            count = ids.Count;
            return string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray());
        }

        // Analyzes a scene and, with on or ab, atlases and merges its level. Returns CROW_LEVEL_ATLAS lines.
        public static string LevelProcess(Scene scene, string mode, Func<Mesh, bool> canRead, HashSet<string> playerFinds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool apply = mode == "on" || mode == "ab";
            string sceneName = scene.name.Replace(' ', '_');
            var report = new StringBuilder();
            Analysis analysis;
            levelRules = true;
            try
            {
                analysis = Analyze(scene, new Dictionary<string, int>(), false);
            }
            finally
            {
                levelRules = false;
            }
            // An FSM with DestroySelf destroys its own object (the action names no target, so the analysis doesn't
            // see it: Roadside's examine markers, destroyed as the area starts).
            int selfDestroyed = 0;
            foreach (GameObject root in Roots(scene))
                foreach (MonoBehaviour fsm in root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (fsm != null && fsm.GetType().Name == "PlayMakerFSM" && FsmActions(fsm).Contains("DestroySelf"))
                    {
                        analysis.Add(fsm.transform, Mark.Anchor, "DestroySelf");
                        selfDestroyed++;
                    }
            // Whatever a global holds may be moved or switched by another scene's FSMs (the player's): it moves.
            foreach (KeyValuePair<string, List<object>> global in analysis.globalValues)
                foreach (object value in global.Value) analysis.Add(value, Mark.Anchor, "global:" + global.Key);
            int found = 0;
            if (playerFinds != null && playerFinds.Count > 0)
                foreach (GameObject root in Roots(scene))
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                        if (playerFinds.Contains(t.name) || (t.tag != "Untagged" && playerFinds.Contains(t.tag)))
                        {
                            analysis.Add(t, Mark.Exclude, "player find");
                            found++;
                        }
            // The point and spot lights that are on or can come on (off below something switched, or a light something
            // acts on); one off for good lights nothing.
            var lights = new List<Light>();
            int lightsOff = 0;
            foreach (GameObject root in Roots(scene))
                foreach (Light light in root.GetComponentsInChildren<Light>(true))
                {
                    if (light.type != LightType.Point && light.type != LightType.Spot) continue;
                    bool can = light.enabled || analysis.marks.ContainsKey(light.transform);
                    for (Transform t = light.transform; t != null && can; t = t.parent)
                        if (!t.gameObject.activeSelf)
                        {
                            Mark mark;
                            can = false;
                            for (Transform s = t; s != null && !can; s = s.parent)
                                can = analysis.marks.TryGetValue(s, out mark) && (mark & Mark.Anchor) != 0;
                        }
                    if (can) lights.Add(light);
                    else lightsOff++;
                }

            // The slots: each candidate renderer's submeshes, with their materials' tiles.
            var blockers = new Dictionary<string, int>();
            var partLines = LevelParts ? new List<string>() : null;
            var tiles = new Dictionary<Material, Tile>();
            var slots = new List<LevelSlot>();
            var slotsOf = new Dictionary<MeshRenderer, List<LevelSlot>>();
            var states = new Dictionary<MeshRenderer, string>();
            var uvCache = new Dictionary<Mesh, Vector2[]>();
            var litOf = new Dictionary<MeshRenderer, string>();
            int renderers = 0;
            Action<MeshRenderer, string, string> note = (renderer, origin, reason) =>
            {
                string key = reason.Length > 50 ? reason.Substring(0, 50) : reason;
                int count;
                blockers.TryGetValue(origin + ":" + key, out count);
                blockers[origin + ":" + key] = count + 1;
            };
            var origins = new Dictionary<MeshRenderer, string>();
            foreach (GameObject root in Roots(scene))
                foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!renderer.enabled) continue;
                    renderers++;
                    string origin = renderer.GetComponent<MergedRenderer>() != null ? "merged" : InCombiner(renderer.transform) ? "mcs" : "plain";
                    origins[renderer] = origin;
                    Transform carrier;
                    string state;
                    string why = LevelBlocker(renderer, analysis, out carrier, out state);
                    states[renderer] = state;
                    var filter = renderer.GetComponent<MeshFilter>();
                    Mesh mesh = filter == null ? null : filter.sharedMesh;
                    Material[] materials = renderer.sharedMaterials;
                    int lightCount = 0;
                    string lit = null;
                    if (why == null)
                    {
                        if (analysis.InEnemy(renderer.transform)) why = "enemy";
                        else if (renderer.gameObject.layer == 5) why = "ui layer";
                        else if (mesh == null || mesh.vertexCount == 0) why = "no mesh";
                        else if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) why = "shadows only";
                        else if (renderer.isPartOfStaticBatch) why = "static batch";
                        else if (renderer.lightmapIndex >= 0 && renderer.lightmapIndex < 65534) why = "lightmap";
                        else if (materials.Length != mesh.subMeshCount) why = "materials != submeshes";
                        else if (materials.Any(m => m == null)) why = "no material";
                        else if (materials.Any(m => m.renderQueue > 2500)) why = "blended";
                        else if (Enumerable.Range(0, mesh.subMeshCount).Any(s => mesh.GetTopology(s) != MeshTopology.Triangles)) why = "topology";
                        else if (canRead == null ? !mesh.isReadable : !canRead(mesh)) why = "unreadable";
                        else if (renderer.probeAnchor != null) why = "probe anchor";
                        else if (mesh.normals.Length == 0) why = "no normals";
                        else
                        {
                            lit = Lit(renderer.bounds, lights, out lightCount);
                            if (lightCount > LevelMaxLights) why = "lights " + lightCount;
                        }
                    }
                    if (why != null)
                    {
                        note(renderer, origin, why);
                        if (partLines != null) partLines.Add(PartLine(sceneName, renderer, origin, state, mesh, materials, why));
                        continue;
                    }
                    Vector3 centre = renderer.bounds.center;
                    string cell = Mathf.FloorToInt(centre.x / LevelCell) + "," + Mathf.FloorToInt(centre.y / LevelCell) + "," + Mathf.FloorToInt(centre.z / LevelCell);
                    if (LevelMcsCells && origin == "mcs" && renderer.transform.parent != null) cell = "mcs" + renderer.transform.parent.GetInstanceID();
                    string litKey = LevelLights == "any" ? "any" : LevelLights == "union4" && lightCount <= 4 ? "few" : lit;
                    litOf[renderer] = lit;
                    string common = (carrier == null ? 0 : carrier.GetInstanceID()) + "|" + renderer.gameObject.layer + "|" + (int)renderer.shadowCastingMode + "|" +
                                    renderer.receiveShadows + "|" + (int)renderer.lightProbeUsage + "|" + (int)renderer.reflectionProbeUsage + "|" + litKey + "|" + cell;
                    var list = new List<LevelSlot>();
                    for (int s = 0; s < materials.Length; s++)
                    {
                        Tile tile;
                        if (!tiles.TryGetValue(materials[s], out tile))
                        {
                            tiles[materials[s]] = tile = new Tile { Material = materials[s] };
                            tile.Why = LevelPrepare(tile);
                        }
                        list.Add(new LevelSlot { renderer = renderer, mesh = mesh, subMesh = s, tile = tile, carrier = carrier, common = common });
                    }
                    slotsOf[renderer] = list;
                    slots.AddRange(list);
                }

            // Each slot's UV range, moved by whole repeats to start in the first one where its texture repeats; a slot
            // spanning more than LevelMaxTile texels keeps its renderer out.
            var spanBlocked = new HashSet<MeshRenderer>();
            foreach (LevelSlot slot in slots)
            {
                Tile tile = slot.tile;
                if (tile.Why != null) continue;
                Vector2[] uv;
                if (!uvCache.TryGetValue(slot.mesh, out uv)) uvCache[slot.mesh] = uv = slot.mesh.uv;
                Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
                foreach (int v in slot.mesh.GetIndices(slot.subMesh))
                {
                    Vector2 at = v < uv.Length ? uv[v] : Vector2.zero;
                    min = Vector2.Min(min, at);
                    max = Vector2.Max(max, at);
                }
                if (min.x > max.x) continue;
                Texture main = tile.Material.HasProperty("_MainTex") ? tile.Material.GetTexture("_MainTex") : null;
                if (tile.Main != null && main != null)
                {
                    Vector4 st = tile.MainST;
                    float s0 = Mathf.Min(min.x * st.x, max.x * st.x) + st.z, s1 = Mathf.Max(min.x * st.x, max.x * st.x) + st.z;
                    float t0 = Mathf.Min(min.y * st.y, max.y * st.y) + st.w, t1 = Mathf.Max(min.y * st.y, max.y * st.y) + st.w;
                    if ((s1 - s0) * tile.Main.Width > LevelMaxTile || (t1 - t0) * tile.Main.Height > LevelMaxTile)
                    {
                        spanBlocked.Add(slot.renderer);
                        continue;
                    }
                    if (!tile.Detailed && st.x != 0f && st.y != 0f)
                    {
                        float du = main.wrapModeU == TextureWrapMode.Repeat ? -Mathf.Floor(s0 + 1e-3f) : 0f;
                        float dv = main.wrapModeV == TextureWrapMode.Repeat ? -Mathf.Floor(t0 + 1e-3f) : 0f;
                        slot.shift = new Vector2(du / st.x, dv / st.y);
                    }
                }
                tile.Min = Vector2.Min(tile.Min, min + slot.shift);
                tile.Max = Vector2.Max(tile.Max, max + slot.shift);
            }
            foreach (Tile tile in tiles.Values)
            {
                if (tile.Why != null) continue;
                if (tile.Min.x > tile.Max.x) tile.Why = "no vertices";
                // Grid's own limit is the enemies' 512 texels; the level's is LevelMaxTile.
                else if (!Grid(tile) && (tile.Grid == null || tile.GridST.x == 0f || tile.GridST.y == 0f)) tile.Why = "no tiling";
                else if (tile.Width > LevelMaxTile || tile.Height > LevelMaxTile) tile.Why = "tile " + tile.Width + "x" + tile.Height;
                else if (Clamps(tile)) tile.Why = "clamped";
            }
            foreach (KeyValuePair<MeshRenderer, List<LevelSlot>> pair in slotsOf)
            {
                string why = spanBlocked.Contains(pair.Key) ? "uv span" : pair.Value.Select(s => s.tile.Why).FirstOrDefault(w => w != null);
                if (why != null) note(pair.Key, origins[pair.Key], "tile:" + why);
            }
            var eligible = slotsOf.Where(p => !spanBlocked.Contains(p.Key) && p.Value.All(s => s.tile.Why == null)).ToDictionary(p => p.Key, p => p.Value);

            // First groups without pages, for the tiles that would save draws; then the pages; then the groups again.
            // Metallic and smoothness from the material's values, or from a metallic map (the page's second texture).
            Func<LevelSlot, string> tileKey = s => s.tile.Key + "|" + (s.tile.HasGlossMap ? "map" : SmoothTile(s.tile) ? "smooth" : s.tile.Metallic.ToString("R", CultureInfo.InvariantCulture) + "|" +
                                                   s.tile.Smoothness.ToString("R", CultureInfo.InvariantCulture)) + "|" + LevelClass(s.tile);
            foreach (LevelSlot slot in eligible.Values.SelectMany(l => l)) slot.key = slot.common + "|" + tileKey(slot);
            LightBins(eligible, litOf);
            Dictionary<string, List<LevelSlot>> groups = LevelGroups(eligible);
            var weight = new Dictionary<Tile, int>();
            foreach (LevelSlot slot in groups.Values.SelectMany(g => g))
            {
                int w;
                weight.TryGetValue(slot.tile, out w);
                weight[slot.tile] = w + 1;
            }
            var pages = new List<LevelPageInfo>();
            var unplaced = new HashSet<Tile>();
            foreach (IGrouping<string, Tile> type in weight.Keys.GroupBy(LevelClass))
            {
                // The tiles that save most draws per texel first.
                var waiting = type.OrderByDescending(t => weight[t] / (double)((t.Width + 2 * LevelPad) * (t.Height + 2 * LevelPad))).ToList();
                for (int n = 0; n < LevelMaxPages && waiting.Count > 0; n++)
                {
                    Tile first = waiting[0];
                    var page = new LevelPageInfo
                    {
                        Class = type.Key, Cutout = first.Material.IsKeywordEnabled("_ALPHATEST_ON"), Filter = first.Filter,
                        Compressed = type.Key.Split('|')[2] == "dxt",
                    };
                    foreach (Tile tile in waiting.ToList())
                    {
                        page.Tiles.Add(tile);
                        int w, h;
                        if (!LevelPack(page.Tiles, out w, out h)) page.Tiles.Remove(tile);
                        else waiting.Remove(tile);
                    }
                    if (page.Tiles.Count == 0) break;
                    pages.Add(page);
                }
                unplaced.UnionWith(waiting);
            }
            // The memory cap: the pages that save most draws first.
            long pageBytes = 0;
            var kept = new List<LevelPageInfo>();
            foreach (LevelPageInfo page in pages.OrderByDescending(p => p.Tiles.Sum(t => weight[t])))
            {
                int w, h;
                LevelPack(page.Tiles, out w, out h);
                page.Gloss = page.Tiles.Any(t => t.HasGlossMap);
                page.Detail = page.Tiles.Any(t => t.Detailed);
                page.Smooth = page.Tiles.Any(SmoothTile);
                long bytes = PageBytes(page, w, h);
                if (pageBytes + bytes > LevelMaxBytes)
                {
                    unplaced.UnionWith(page.Tiles);
                    continue;
                }
                pageBytes += bytes;
                kept.Add(page);
            }
            pages = kept;
            var pageOf = new Dictionary<Tile, int>();
            for (int i = 0; i < pages.Count; i++) foreach (Tile tile in pages[i].Tiles) pageOf[tile] = i;
            foreach (KeyValuePair<MeshRenderer, List<LevelSlot>> pair in eligible.ToList())
                if (pair.Value.Any(s => !pageOf.ContainsKey(s.tile)))
                {
                    if (pair.Value.Any(s => unplaced.Contains(s.tile))) note(pair.Key, origins[pair.Key], "no room");
                    else note(pair.Key, origins[pair.Key], "alone");
                    eligible.Remove(pair.Key);
                }
            foreach (LevelSlot slot in eligible.Values.SelectMany(l => l))
            {
                slot.page = pageOf[slot.tile];
                slot.key = slot.common + "|" + slot.page + "|" + tileKey(slot);
            }
            LightBins(eligible, litOf);
            groups = LevelGroups(eligible);
            long meshNet = LevelBudget(scene, eligible, ref groups, note, origins);
            var joined = new HashSet<MeshRenderer>(groups.Values.SelectMany(g => g).Select(s => s.renderer));
            foreach (MeshRenderer renderer in eligible.Keys)
                if (!joined.Contains(renderer)) note(renderer, origins[renderer], "alone");
            // The pages again with the tiles still used, smaller where they can be.
            var used = new HashSet<Tile>(groups.Values.SelectMany(g => g).Select(s => s.tile));
            long textureBytes = 0, rawBytes = 0;
            for (int i = 0; i < pages.Count; i++)
            {
                LevelPageInfo page = pages[i];
                page.Tiles.RemoveAll(t => !used.Contains(t));
                if (page.Tiles.Count == 0) continue;
                int w, h;
                LevelPack(page.Tiles, out w, out h);
                page.Width = w;
                page.Height = h;
                page.Gloss = page.Tiles.Any(t => t.HasGlossMap);
                page.Detail = page.Tiles.Any(t => t.Detailed);
                page.Smooth = page.Tiles.Any(SmoothTile);
                textureBytes += PageBytes(page, w, h);
                rawBytes += PageBytes(page, w, h, true);
            }

            // Draws: renderers before (each candidate is one, a renderer's submeshes are draws) and after.
            int before = groups.Values.SelectMany(g => g).Select(s => s.renderer).Distinct().Sum(r => r.sharedMaterials.Length);
            int after = groups.Values.Sum(g => Math.Max(1, (int)Math.Ceiling(g.Sum(s => (long)s.mesh.vertexCount) / 65000.0)));
            int vertices = 0, made = 0, dropped = 0, lightUndone = 0;
            long meshBytes = 0, droppedBytes = 0;
            if (apply && groups.Count > 0)
            {
                var materials = new Dictionary<string, Material>();
                for (int i = 0; i < pages.Count; i++)
                    if (pages[i].Tiles.Count > 0) pages[i].Texture = PageTexture(pages[i], sceneName, i);
                var built = new List<KeyValuePair<GameObject, bool>>();
                foreach (List<LevelSlot> group in groups.Values)
                {
                    LevelSlot first = group[0];
                    LevelPageInfo page = pages[first.page];
                    string materialKey = first.page + "|" + tileKey(first);
                    Material material;
                    if (!materials.TryGetValue(materialKey, out material))
                    {
                        material = new Material(first.tile.Material) { name = "CrowLevelAtlas " + sceneName + " " + (materials.Count + 1) };
                        material.SetTexture("_MainTex", page.Texture);
                        material.SetTextureScale("_MainTex", Vector2.one);
                        material.SetTextureOffset("_MainTex", Vector2.zero);
                        material.SetColor("_Color", Color.white);
                        if (first.tile.Detailed)
                        {
                            material.SetTexture("_DetailAlbedoMap", page.DetailTexture);
                            material.SetTextureScale("_DetailAlbedoMap", Vector2.one);
                            material.SetTextureOffset("_DetailAlbedoMap", Vector2.zero);
                        }
                        else
                        {
                            material.DisableKeyword("_DETAIL_MULX2");
                            if (material.HasProperty("_DetailAlbedoMap")) material.SetTexture("_DetailAlbedoMap", null);
                        }
                        if (SmoothTile(first.tile))
                        {
                            material.EnableKeyword("_METALLICGLOSSMAP");
                            material.SetTexture("_MetallicGlossMap", page.SmoothTexture);
                            material.SetTextureScale("_MetallicGlossMap", Vector2.one);
                            material.SetTextureOffset("_MetallicGlossMap", Vector2.zero);
                            material.SetFloat("_GlossMapScale", 1f);
                        }
                        else if (first.tile.HasGlossMap)
                        {
                            material.EnableKeyword("_METALLICGLOSSMAP");
                            material.SetTexture("_MetallicGlossMap", page.GlossTexture);
                            material.SetTextureScale("_MetallicGlossMap", Vector2.one);
                            material.SetTextureOffset("_MetallicGlossMap", Vector2.zero);
                            material.SetFloat("_GlossMapScale", 1f);
                        }
                        else
                        {
                            material.DisableKeyword("_METALLICGLOSSMAP");
                            if (material.HasProperty("_MetallicGlossMap")) material.SetTexture("_MetallicGlossMap", null);
                        }
                        materials[materialKey] = material;
                    }
                    string lit = first.common.Split('|')[6];
                    foreach (GameObject go in LevelBuild(scene, group, page, material, ref vertices, ref meshBytes))
                    {
                        int count;
                        // Its bounds reaching a light none of its parts' did (exact), or more than 4 (union4), Unity could light
                        // it otherwise.
                        string reach = Lit(go.GetComponent<MeshRenderer>().bounds, lights, out count);
                        built.Add(new KeyValuePair<GameObject, bool>(go, lit == "any" || (lit == "few" ? count <= 4 : reach == lit)));
                    }
                }
                // Undone: those, and any other with a part of theirs (a renderer joins whole or not at all).
                var tainted = new HashSet<Renderer>(built.Where(b => !b.Value).SelectMany(b => b.Key.GetComponent<LevelAtlasRecord>().parts));
                for (bool more = true; more;)
                {
                    more = false;
                    foreach (KeyValuePair<GameObject, bool> b in built)
                    {
                        Renderer[] own = b.Key.GetComponent<LevelAtlasRecord>().parts;
                        if (own.Any(tainted.Contains) && !own.All(tainted.Contains))
                        {
                            tainted.UnionWith(own);
                            more = true;
                        }
                    }
                }
                var merged = new HashSet<MeshRenderer>();
                foreach (KeyValuePair<GameObject, bool> b in built)
                {
                    GameObject go = b.Key;
                    Renderer[] own = go.GetComponent<LevelAtlasRecord>().parts;
                    Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
                    if (own.Any(tainted.Contains))
                    {
                        lightUndone += own.Length;
                        vertices -= mesh.vertexCount;
                        meshBytes -= mesh.vertexCount * 32L + mesh.GetIndexCount(0) * 2L;
                        UnityEngine.Object.DestroyImmediate(mesh);
                        UnityEngine.Object.DestroyImmediate(go);
                        continue;
                    }
                    made++;
                    foreach (Renderer part in own) merged.Add((MeshRenderer)part);
                    Unreadable.Add(mesh);
                }
                foreach (MeshRenderer renderer in merged) renderer.enabled = false;
                if (mode == "on") DropMeshes(scene, merged, out dropped, out droppedBytes);
                after = made;
                before = merged.Sum(r => r.sharedMaterials.Length);
            }
            report.Append("CROW_LEVEL_ATLAS scene=").Append(sceneName).Append(' ').Append(mode).Append(" renderers=").Append(renderers)
                .Append(" candidates=").Append(slotsOf.Count).Append(" eligible=").Append(eligible.Count).Append(" groups=").Append(groups.Count)
                .Append(" draws=").Append(before).Append("->").Append(after).Append(" pages=").Append(pages.Count(p => p.Tiles.Count > 0))
                .Append(" (").Append(string.Join(" ", pages.Where(p => p.Tiles.Count > 0).Select(p => p.Width + "x" + p.Height + ":" + p.Class.Replace('|', '/') + ":" + p.Tiles.Count).ToArray())).Append(")")
                .Append(" textureBytes=").Append(textureBytes).Append(" (here ").Append(rawBytes).Append(")").Append(" vertices=").Append(vertices).Append(" meshBytes=").Append(meshBytes).Append(" meshNet=").Append(meshNet)
                .Append(" dropped=").Append(dropped).Append(" droppedBytes=").Append(droppedBytes).Append(" lightUndone=").Append(lightUndone)
                .Append(" destroySelf=").Append(selfDestroyed).Append(" lights=").Append(lights.Count).Append(" lightsOff=").Append(lightsOff).Append(" playerFinds=").Append(found).Append(" cell=").Append(LevelCell.ToString(CultureInfo.InvariantCulture))
                .Append(" ms=").Append(clock.ElapsedMilliseconds).Append('\n');
            report.Append("CROW_LEVEL_ATLAS ").Append(sceneName).Append(" left out: ")
                .Append(string.Join(" ", blockers.OrderByDescending(p => p.Value).Take(40).Select(p => p.Key.Replace(' ', '_') + "=" + p.Value).ToArray())).Append('\n');
            report.Append("CROW_LEVEL_ATLAS ").Append(sceneName).Append(" materials kept: ")
                .Append(string.Join(" ", tiles.Values.Where(t => t.Why != null).GroupBy(t => t.Why.Length > 30 ? t.Why.Substring(0, 30) : t.Why)
                    .OrderByDescending(g => g.Count()).Select(g => g.Key.Replace(' ', '_') + "=" + g.Count()).ToArray())).Append('\n');
            report.Append("CROW_LEVEL_ATLAS ").Append(sceneName).Append(" biggest groups: ")
                .Append(string.Join(" ", groups.Values.OrderByDescending(g => g.Count).Take(12).Select(g => g.Count + "/" + g.Select(s => s.tile).Distinct().Count() + "@" +
                    (g[0].carrier == null ? "root" : g[0].carrier.name.Replace(' ', '_'))).ToArray())).Append('\n');
            if (partLines != null)
            {
                var groupOf = new Dictionary<MeshRenderer, int>();
                int index = 0;
                foreach (List<LevelSlot> group in groups.Values)
                {
                    index++;
                    foreach (LevelSlot slot in group) groupOf[slot.renderer] = index;
                }
                foreach (KeyValuePair<MeshRenderer, List<LevelSlot>> pair in slotsOf)
                {
                    int g;
                    string why = groupOf.TryGetValue(pair.Key, out g) ? "group:" + g : spanBlocked.Contains(pair.Key) ? "uv span" :
                        pair.Value.Select(s => s.tile.Why).FirstOrDefault(w => w != null) ?? (eligible.ContainsKey(pair.Key) ? "alone" : "page");
                    Mesh mesh = pair.Value[0].mesh;
                    LevelSlot one = pair.Value[0];
                    string[] common = one.common.Split('|');
                    partLines.Add(PartLine(sceneName, pair.Key, origins[pair.Key], states[pair.Key], mesh, pair.Key.sharedMaterials, why) +
                                  " tiles=" + string.Join(";", pair.Value.Select(s => s.tile.Width + "x" + s.tile.Height).ToArray()) +
                                  " carrier=" + (one.carrier == null ? "root" : one.carrier.name.Replace(' ', '_') + "#" + one.carrier.GetInstanceID()) +
                                  " settings=" + string.Join(",", common.Skip(1).Take(5).ToArray()) + " lit=" + (common[6].Length == 0 ? 0 : common[6].Split(',').Length) + ":" +
                                  (common[6].GetHashCode() & 0xffff).ToString("x") + " cellkey=" + common[7] +
                                  (one.tile.Why == null ? " tkey=" + tileKey(one).Replace(' ', '_').Replace("|", ",") : ""));
                }
                foreach (string line in partLines) report.Append(line).Append('\n');
            }
            return report.ToString();
        }

        static string PartLine(string scene, MeshRenderer renderer, string origin, string state, Mesh mesh, Material[] materials, string why)
        {
            return "CROW_LEVEL_ATLAS_PART scene=" + scene + " path=" + Perf.Path(renderer.transform, 99) + " origin=" + origin + " state=" + state.Replace(' ', '_') +
                   " verts=" + (mesh == null ? -1 : mesh.vertexCount) + " mats=" + string.Join(";", materials.Select(m => m == null ? "none" : m.name.Replace(' ', '_')).ToArray()) +
                   " why=" + why.Replace(' ', '_');
        }

        // The groups that save draws: two renderers or more, or one renderer's slots; a renderer joins only if all its
        // slots do.
        static Dictionary<string, List<LevelSlot>> LevelGroups(Dictionary<MeshRenderer, List<LevelSlot>> eligible)
        {
            var out_ = new HashSet<MeshRenderer>();
            while (true)
            {
                var groups = new Dictionary<string, List<LevelSlot>>();
                foreach (KeyValuePair<MeshRenderer, List<LevelSlot>> pair in eligible)
                {
                    if (out_.Contains(pair.Key)) continue;
                    foreach (LevelSlot slot in pair.Value)
                    {
                        List<LevelSlot> list;
                        if (!groups.TryGetValue(slot.key, out list)) groups[slot.key] = list = new List<LevelSlot>();
                        list.Add(slot);
                    }
                }
                bool changed = false;
                foreach (List<LevelSlot> group in groups.Values)
                    if (group.Count < 2 && out_.Add(group[0].renderer)) changed = true;
                if (!changed) return groups;
            }
        }

        // The mesh budget (LevelMaxMeshBytes): groups dropped from the least draws saved per net byte, until the rest fit;
        // the renderers of a dropped group leave the others too. Returns the net bytes of the groups kept.
        static long LevelBudget(Scene scene, Dictionary<MeshRenderer, List<LevelSlot>> eligible, ref Dictionary<string, List<LevelSlot>> groups,
                                Action<MeshRenderer, string, string> note, Dictionary<MeshRenderer, string> origins)
        {
            // Every user of each mesh: filters (any), colliders, particle systems.
            var users = new Dictionary<Mesh, int>();
            var held = new HashSet<Mesh>();
            foreach (GameObject root in Roots(scene))
            {
                foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null)
                    {
                        int n;
                        users.TryGetValue(filter.sharedMesh, out n);
                        users[filter.sharedMesh] = n + 1;
                    }
                foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
                    if (collider.sharedMesh != null) held.Add(collider.sharedMesh);
                foreach (ParticleSystemRenderer particles in root.GetComponentsInChildren<ParticleSystemRenderer>(true))
                    if (particles.mesh != null) held.Add(particles.mesh);
            }
            for (int round = 0; round < 4; round++)
            {
                var joinedUsers = new Dictionary<Mesh, int>();
                foreach (MeshRenderer renderer in groups.Values.SelectMany(g => g).Select(x => x.renderer).Distinct())
                {
                    Mesh mesh = eligible[renderer][0].mesh;
                    int n;
                    joinedUsers.TryGetValue(mesh, out n);
                    joinedUsers[mesh] = n + 1;
                }
                var cost = new Dictionary<string, long>();
                long total = 0;
                foreach (KeyValuePair<string, List<LevelSlot>> group in groups)
                {
                    long bytes = 0;
                    foreach (LevelSlot slot in group.Value)
                    {
                        long indices = slot.mesh.GetIndexCount(slot.subMesh);
                        bytes += Math.Min(slot.mesh.vertexCount, indices) * 32L + indices * 2L;
                    }
                    // A mesh all of whose users join is freed: its share for each of its renderers here.
                    foreach (MeshRenderer renderer in group.Value.Select(x => x.renderer).Distinct())
                    {
                        Mesh mesh = eligible[renderer][0].mesh;
                        int all, joinedCount;
                        users.TryGetValue(mesh, out all);
                        joinedUsers.TryGetValue(mesh, out joinedCount);
                        if (held.Contains(mesh) || all == 0 || joinedCount < all) continue;
                        bytes -= mesh.vertexCount * 48L / all / Math.Max(1, eligible[renderer].Count);
                    }
                    cost[group.Key] = bytes;
                    total += bytes;
                }
                if (total <= LevelMaxMeshBytes) return total;
                var keep = new Dictionary<string, List<LevelSlot>>();
                long sum = 0;
                var outside = new HashSet<MeshRenderer>();
                foreach (KeyValuePair<string, List<LevelSlot>> group in groups.OrderByDescending(g => (g.Value.Count - 1) / (double)Math.Max(1024L, cost[g.Key])))
                {
                    if (sum + Math.Max(0L, cost[group.Key]) > LevelMaxMeshBytes)
                    {
                        foreach (LevelSlot slot in group.Value) outside.Add(slot.renderer);
                        continue;
                    }
                    sum += Math.Max(0L, cost[group.Key]);
                    keep[group.Key] = group.Value;
                }
                foreach (MeshRenderer renderer in outside)
                {
                    note(renderer, origins[renderer], "mesh budget");
                    eligible.Remove(renderer);
                }
                groups = LevelGroups(eligible);
            }
            return -1;
        }

        // EnemyRebuild.cs' Texel with the detail map apart: albedo _MainTex x _Color, detail the detail map at the same place
        // (grey where there is none), gloss metallic and smoothness.
        static void LevelTexel(Tile tile, int x, int y, out Color albedo, out Color detail, out Color gloss)
        {
            if (!tile.Detailed)
            {
                Texel(tile, x, y, out albedo, out gloss);
                detail = new Color(0.5f, 0.5f, 0.5f, 1f);
                return;
            }
            tile.Detailed = false;
            try
            {
                Texel(tile, x, y, out albedo, out gloss);
            }
            finally
            {
                tile.Detailed = true;
            }
            Vector2 uv;
            if (tile.Grid == null) uv = (tile.Min + tile.Max) * 0.5f;
            else
            {
                float s = (tile.BaseX + x + 0.5f) / tile.Grid.Width, t = (tile.BaseY + y + 0.5f) / tile.Grid.Height;
                uv = new Vector2((s - tile.GridST.z) / tile.GridST.x, (t - tile.GridST.w) / tile.GridST.y);
            }
            detail = tile.Detail == null ? new Color(0.5f, 0.5f, 0.5f, 1f)
                : tile.Detail.Sample(uv.x * tile.DetailST.x + tile.DetailST.z, uv.y * tile.DetailST.y + tile.DetailST.w);
        }

        // union4: the renderers of each group whose parts each 4 lights or fewer reach, split into bins that at most 4 lights
        // reach together (a bin number in the slots' keys).
        static void LightBins(Dictionary<MeshRenderer, List<LevelSlot>> eligible, Dictionary<MeshRenderer, string> litOf)
        {
            if (LevelLights != "union4") return;
            var byKey = new Dictionary<string, List<MeshRenderer>>();
            foreach (KeyValuePair<MeshRenderer, List<LevelSlot>> pair in eligible)
            {
                if (pair.Value[0].common.Split('|')[6] != "few") continue;
                List<MeshRenderer> list;
                string key = pair.Value[0].key;
                if (!byKey.TryGetValue(key, out list)) byKey[key] = list = new List<MeshRenderer>();
                list.Add(pair.Key);
            }
            foreach (List<MeshRenderer> renderers in byKey.Values)
            {
                var bins = new List<HashSet<string>>();
                foreach (MeshRenderer renderer in renderers.OrderBy(r => litOf[r], StringComparer.Ordinal))
                {
                    var own = new HashSet<string>(litOf[renderer].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                    int bin = bins.FindIndex(b => b.Union(own).Count() <= 4);
                    if (bin < 0)
                    {
                        bins.Add(new HashSet<string>());
                        bin = bins.Count - 1;
                    }
                    bins[bin].UnionWith(own);
                    foreach (LevelSlot slot in eligible[renderer]) slot.key += "|b" + bin;
                }
            }
        }

        // A material whose metallic and smoothness come from the page's quarter-size texture (LevelSmooth): no metallic map,
        // and the build has the variant with _METALLICGLOSSMAP.
        static bool SmoothTile(Tile tile)
        {
            if (!LevelSmooth || tile.HasGlossMap || tile.Material == null) return false;
            string set = KeywordSet(tile.Material.shaderKeywords.Concat(new[] { "_METALLICGLOSSMAP" }));
            return VariantSets == null || VariantSets.Contains(set);
        }

        // An FSM's enabled actions, by short name.
        static HashSet<string> FsmActions(Component owner)
        {
            var found = new HashSet<string>();
            var states = Field(Field(owner, "fsm"), "states") as Array;
            if (states == null) return found;
            foreach (object state in states)
            {
                object data = Field(state, "actionData");
                var names = Field(data, "actionNames") as System.Collections.IList;
                var enabled = Field(data, "actionEnabled") as System.Collections.IList;
                if (names == null) continue;
                for (int a = 0; a < names.Count; a++)
                    if (enabled == null || a >= enabled.Count || (bool)enabled[a]) found.Add(ShortName(names[a] as string));
            }
            return found;
        }

        // Whether a tile's texels go over 1 (a bright colour or a detail map x2), which the atlas would clamp.
        static bool Clamps(Tile tile)
        {
            for (int y = 0; y < tile.Height; y++)
                for (int x = 0; x < tile.Width; x++)
                {
                    Color albedo, detail, gloss;
                    LevelTexel(tile, x, y, out albedo, out detail, out gloss);
                    if (albedo.r > 1.004f || albedo.g > 1.004f || albedo.b > 1.004f) return true;
                }
            return false;
        }

        // A page's memory as the PS Vita has it (DXT1, DXT5 with an alpha test, the metallic page DXT5), so the cap means the same
        // on test players, whose uncompressed pages take 4 bytes a texel (raw: those).
        static long PageBytes(LevelPageInfo page, int width, int height, bool raw = false)
        {
            long texels = (long)width * height;
            if (raw && !page.Compressed) return texels * 4 * (1 + (page.Gloss ? 1 : 0) + (page.Detail ? 1 : 0)) + (page.Smooth ? texels / 4 : 0);
            return (page.Cutout ? texels : texels / 2) + (page.Gloss ? texels : 0) + (page.Detail ? texels / 2 : 0) + (page.Smooth ? texels / 4 : 0);
        }

        // Shelf packing as EnemyRebuild.cs' Pack, with LevelPad texels of padding and each tile at its source's
        // 4x4 block phase (tile.X - LevelPad and tile.BaseX alike modulo 4), so a DXT block holds the source's texels.
        // A tile's footprint side: to a multiple of 8, so that a DXT block and a texel of the quarter-size metallic page hold
        // one tile each.
        static int Foot(int n)
        {
            return (n + 7) & ~7;
        }

        static bool LevelPack(List<Tile> tiles, out int width, out int height)
        {
            width = height = 0;
            var order = tiles.OrderByDescending(t => t.Height).ThenByDescending(t => t.Width).ToList();
            for (int side = 64; side <= LevelPage; side *= 2)
            {
                int x = 0, y = 0, row = 0;
                bool fits = true;
                foreach (Tile tile in order)
                {
                    int phaseX = ((tile.BaseX % 4) + 4) % 4, phaseY = ((tile.BaseY % 4) + 4) % 4;
                    int w = Foot(phaseX + tile.Width + 2 * LevelPad), h = Foot(phaseY + tile.Height + 2 * LevelPad);
                    if (w > side)
                    {
                        fits = false;
                        break;
                    }
                    if (x + w > side)
                    {
                        x = 0;
                        y += row;
                        row = 0;
                    }
                    tile.X = x + LevelPad + phaseX;
                    tile.Y = y + LevelPad + phaseY;
                    x += w;
                    row = Math.Max(row, h);
                }
                int usedHeight = y + row;
                if (!fits || usedHeight > side) continue;
                width = side;
                height = 4;
                while (height < usedHeight) height *= 2;
                return true;
            }
            return false;
        }

        static Texture2D PageTexture(LevelPageInfo page, string sceneName, int index)
        {
            int width = page.Width, height = page.Height;
            var pixels = new Color32[width * height];
            var glossPixels = page.Gloss ? new Color32[width * height] : null;
            var detailPixels = page.Detail ? new Color32[width * height] : null;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(0, 0, 0, 255);
            foreach (Tile tile in page.Tiles)
            {
                int phaseX = ((tile.BaseX % 4) + 4) % 4, phaseY = ((tile.BaseY % 4) + 4) % 4;
                // The whole footprint, padding included (to the next multiple of 4), from the tile's wrapped texels.
                int x0 = -LevelPad - phaseX, y0 = -LevelPad - phaseY;
                int x1 = x0 + Foot(phaseX + tile.Width + 2 * LevelPad), y1 = y0 + Foot(phaseY + tile.Height + 2 * LevelPad);
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        Color albedo, gloss, detail;
                        LevelTexel(tile, x, y, out albedo, out detail, out gloss);
                        pixels[(tile.Y + y) * width + tile.X + x] = albedo;
                        if (glossPixels != null) glossPixels[(tile.Y + y) * width + tile.X + x] = gloss;
                        if (detailPixels != null) detailPixels[(tile.Y + y) * width + tile.X + x] = detail;
                    }
            }
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "CrowLevelAtlas " + sceneName + " " + index, wrapMode = TextureWrapMode.Clamp, filterMode = page.Filter,
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            if (page.Compressed && CompressTexture != null) CompressTexture(texture, page.Cutout);
            Unreadable.Add(texture);
            if (glossPixels != null)
            {
                // Full size, texel for texel: R metallic, A smoothness (the map's times its scale).
                page.GlossTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    name = "CrowLevelAtlas " + sceneName + " " + index + " metallic", wrapMode = TextureWrapMode.Clamp, filterMode = page.Filter,
                };
                page.GlossTexture.SetPixels32(glossPixels);
                page.GlossTexture.Apply(false, false);
                if (page.Compressed && CompressTexture != null) CompressTexture(page.GlossTexture, true);
                Unreadable.Add(page.GlossTexture);
            }
            if (page.Smooth)
            {
                // Quarter size, each tile's metallic (R) and smoothness (A) over its footprint (8-aligned, so a texel holds one
                // tile), uncompressed: 8 bits each.
                int hw = Math.Max(1, width / 4), hh = Math.Max(1, height / 4);
                var smooth = new Color32[hw * hh];
                foreach (Tile tile in page.Tiles)
                {
                    int phaseX = ((tile.BaseX % 4) + 4) % 4, phaseY = ((tile.BaseY % 4) + 4) % 4;
                    int x0 = tile.X - LevelPad - phaseX, y0 = tile.Y - LevelPad - phaseY;
                    int x1 = x0 + Foot(phaseX + tile.Width + 2 * LevelPad), y1 = y0 + Foot(phaseY + tile.Height + 2 * LevelPad);
                    var value = new Color32((byte)Mathf.RoundToInt(Mathf.Clamp01(tile.Metallic) * 255f), 0, 0, (byte)Mathf.RoundToInt(Mathf.Clamp01(tile.Smoothness) * 255f));
                    for (int y = y0 / 4; y < y1 / 4; y++)
                        for (int x = x0 / 4; x < x1 / 4; x++) smooth[y * hw + x] = value;
                }
                page.SmoothTexture = new Texture2D(hw, hh, TextureFormat.RGBA32, false)
                {
                    name = "CrowLevelAtlas " + sceneName + " " + index + " smoothness", wrapMode = TextureWrapMode.Clamp, filterMode = page.Filter,
                };
                page.SmoothTexture.SetPixels32(smooth);
                page.SmoothTexture.Apply(false, false);
                Unreadable.Add(page.SmoothTexture);
            }
            if (detailPixels != null)
            {
                // The detail map on the tiles' grid (the material multiplies it in, x2, as before).
                page.DetailTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    name = "CrowLevelAtlas " + sceneName + " " + index + " detail", wrapMode = TextureWrapMode.Clamp, filterMode = page.Filter,
                };
                page.DetailTexture.SetPixels32(detailPixels);
                page.DetailTexture.Apply(false, false);
                if (page.Compressed && CompressTexture != null) CompressTexture(page.DetailTexture, false);
                Unreadable.Add(page.DetailTexture);
            }
            return texture;
        }

        // A group's renderers (one per 65,000 vertices) below its carrier: positions and normals in its space, UV0
        // in the page's tiles.
        static List<GameObject> LevelBuild(Scene scene, List<LevelSlot> group, LevelPageInfo page, Material material, ref int vertexTotal, ref long byteTotal)
        {
            var created = new List<GameObject>();
            LevelSlot first = group[0];
            Transform carrier = first.carrier;
            Matrix4x4 toCarrier = carrier == null ? Matrix4x4.identity : carrier.worldToLocalMatrix;
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();
            var parts = new List<Renderer>();
            int vertexSum = 0;
            long byteSum = 0;
            Action flush = () =>
            {
                if (indices.Count == 0) return;
                var mesh = new Mesh { name = "CrowLevelAtlas " + first.tile.Material.name + " +" + (parts.Distinct().Count() - 1) };
                mesh.SetVertices(positions);
                mesh.SetNormals(normals);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(indices, 0);
                mesh.RecalculateBounds();
                var go = new GameObject("CrowLevelAtlas");
                if (carrier != null) go.transform.SetParent(carrier, false);
                else SceneManager.MoveGameObjectToScene(go, scene);
                go.layer = first.renderer.gameObject.layer;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = first.renderer.shadowCastingMode;
                renderer.receiveShadows = first.renderer.receiveShadows;
                renderer.lightProbeUsage = first.renderer.lightProbeUsage;
                renderer.reflectionProbeUsage = first.renderer.reflectionProbeUsage;
                go.AddComponent<LevelAtlasRecord>().parts = parts.Distinct().ToArray();
                created.Add(go);
                vertexSum += positions.Count;
                byteSum += positions.Count * 32L + indices.Count * 2L;
                positions.Clear();
                normals.Clear();
                uvs.Clear();
                indices.Clear();
                parts.Clear();
            };
            var cache = new Dictionary<Mesh, MeshData>();
            foreach (LevelSlot slot in group.OrderBy(s => s.renderer.GetInstanceID()).ThenBy(s => s.subMesh))
            {
                MeshData data;
                if (!cache.TryGetValue(slot.mesh, out data)) cache[slot.mesh] = data = new MeshData(slot.mesh);
                int[] source = slot.mesh.GetIndices(slot.subMesh);
                var map = new Dictionary<int, int>();
                foreach (int index in source) if (!map.ContainsKey(index)) map[index] = map.Count;
                // A renderer's slots stay in one mesh.
                int rendererVertices = map.Count;
                if (positions.Count + rendererVertices > MaxVertices && !parts.Contains(slot.renderer)) flush();
                Matrix4x4 matrix = toCarrier * slot.renderer.transform.localToWorldMatrix;
                Matrix4x4 normalMatrix = matrix.inverse.transpose;
                bool flip = matrix.determinant < 0f;
                int offset = positions.Count;
                foreach (KeyValuePair<int, int> pair in map.OrderBy(p => p.Value))
                {
                    int v = pair.Key;
                    positions.Add(matrix.MultiplyPoint3x4(data.positions[v]));
                    normals.Add(normalMatrix.MultiplyVector(data.normals[v]).normalized);
                    Vector2 uv = (v < data.uv0.Length ? data.uv0[v] : Vector2.zero) + slot.shift;
                    uvs.Add(AtlasUV(slot.tile, uv, page.Width, page.Height));
                }
                for (int i = 0; i + 2 < source.Length; i += 3)
                {
                    int a = map[source[i]] + offset, b = map[source[i + 1]] + offset, c = map[source[i + 2]] + offset;
                    indices.Add(a);
                    indices.Add(flip ? c : b);
                    indices.Add(flip ? b : c);
                }
                parts.Add(slot.renderer);
            }
            flush();
            vertexTotal += vertexSum;
            byteTotal += byteSum;
            return created;
        }

        // The build's: the names and tags the player's FSMs find in the whole scene (FindGameObject and the like, not
        // FindChild); "*" (a name from a global) is left out.
        public static HashSet<string> PlayerFindNames(GameObject player)
        {
            HashSet<string> names = FindNames(new[] { player }, true);
            names.Remove("*");
            return names;
        }
    }
}
