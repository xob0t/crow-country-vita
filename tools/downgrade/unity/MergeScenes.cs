using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Static renderers merged per material and place as the PS Vita build writes each scene (compat/
// Merge.cs: what may merge is read from the scene's FSMs and scripts), then the still parts of the
// area's big hierarchies taken out to the scene's root (Merge.Split, Perf.cs's SceneSplit). The scene
// the build writes changes; the project's scene files don't.
class CrowMergeScenes : UnityEditor.Build.IProcessSceneWithReport
{
    static readonly string[] Skipped = { "Logos", "Title", "CrowMara" };
    static System.Reflection.MethodInfo process, split;
    static Type merge;

    public int callbackOrder
    {
        get { return 0; }
    }

    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (report == null || !BuildPipeline.isBuildingPlayer) return;  // entering play mode
        if (Skipped.Contains(scene.name)) return;
        string mode = DowngradeBuild.MergeMode();
        if (mode != "off") MergeScene(scene, mode);
        if (DowngradeBuild.SplitMode() == "off") return;
        if (split == null)
        {
            Type type = Type.GetType("CrowCompat.Merge, CrowCompat");
            split = type == null ? null : type.GetMethod("Split");
            if (split == null) throw new InvalidOperationException("No CrowCompat.Merge.Split in the project's CrowCompat.dll");
        }
        try
        {
            // CROW_SPLIT=movers: the parts that move on their own leave too (Merge.SplitMovers).
            System.Reflection.FieldInfo movers = split.DeclaringType.GetField("SplitMovers");
            if (movers != null) movers.SetValue(null, DowngradeBuild.SplitMode() == "movers");
            Debug.Log("DOWNGRADE_" + (string)split.Invoke(null, new object[] { scene, true }));
        }
        catch (Exception exception)
        {
            Debug.LogError("DOWNGRADE_SPLIT " + scene.name + " failed: " + exception);
        }
    }

    static void MergeScene(UnityEngine.SceneManagement.Scene scene, string mode)
    {
        if (process == null)
        {
            merge = Type.GetType("CrowCompat.Merge, CrowCompat");
            process = merge == null ? null : merge.GetMethod("Process");
            if (process == null) throw new InvalidOperationException("No CrowCompat.Merge.Process in the project's CrowCompat.dll");
            merge.GetField("Drop").SetValue(null, mode == "on");
            string limit = Environment.GetEnvironmentVariable("CROW_MERGE_MAX_VERTICES");
            if (!string.IsNullOrEmpty(limit)) merge.GetField("MaxPartVertices").SetValue(null, int.Parse(limit));
            string budget = Environment.GetEnvironmentVariable("CROW_MERGE_MAX_KB");
            if (!string.IsNullOrEmpty(budget)) merge.GetField("MaxBytes").SetValue(null, long.Parse(budget) * 1024L);
            // The enemies' atlases (compat/EnemyRebuild.cs): source images, compression, the variants there are.
            Func<Texture, Texture2D> source = SourceImage;
            Action<Texture2D, bool> compress = CompressTexture;
            merge.GetField("SourceImage").SetValue(null, source);
            merge.GetField("CompressTexture").SetValue(null, compress);
            merge.GetField("VariantSets").SetValue(null, VariantSets());
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string lines;
        try
        {
            Func<Mesh, bool> canRead = Readable;
            lines = (string)process.Invoke(null, new object[] { scene, true, canRead, true });
        }
        catch (Exception exception)
        {
            Debug.LogError("DOWNGRADE_MERGE " + scene.name + " failed: " + exception);
            return;
        }
        // The merged meshes go to the GPU only, as the parts' unreadable meshes do; so do the enemies' atlases
        // and the ab builds' old enemy meshes (EnemyRebuild.cs).
        var unreadable = (System.Collections.IList)merge.GetField("Unreadable").GetValue(null);
        foreach (UnityEngine.Object made in unreadable) MarkUnreadable(made);
        unreadable.Clear();
        int meshes = 0;
        Type part = Type.GetType("CrowCompat.MergedRenderer, CrowCompat");
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Component merged in root.GetComponentsInChildren(part, true))
            {
                var filter = merged.GetComponent<MeshFilter>();
                if (filter != null && MarkUnreadable(filter.sharedMesh)) meshes++;
            }
        foreach (string line in lines.Split('\n'))
            if (line.Length > 0) Debug.Log("DOWNGRADE_" + line);
        Debug.Log("DOWNGRADE_MERGE " + scene.name + " mode=" + mode + " unreadable=" + meshes + " ms=" + clock.ElapsedMilliseconds);
    }

    // Whether the asset (a mesh or texture) had a readable flag, now off.
    static bool MarkUnreadable(UnityEngine.Object asset)
    {
        if (asset == null) return false;
        var serialized = new SerializedObject(asset);
        SerializedProperty readable = serialized.FindProperty("m_IsReadable");
        if (readable == null) return false;
        readable.boolValue = false;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        return true;
    }

    // A texture's source image, readable (null: not a PNG or JPG). The importer's copy isn't readable, and
    // -nographics has no GPU to read it back.
    static readonly System.Collections.Generic.Dictionary<Texture, Texture2D> images = new System.Collections.Generic.Dictionary<Texture, Texture2D>();

    static Texture2D SourceImage(Texture texture)
    {
        Texture2D image;
        if (texture == null) return null;
        if (images.TryGetValue(texture, out image)) return image;
        image = null;
        string path = AssetDatabase.GetAssetPath(texture);
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if ((extension == ".png" || extension == ".jpg" || extension == ".jpeg") && System.IO.File.Exists(path))
        {
            image = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!image.LoadImage(System.IO.File.ReadAllBytes(path))) image = null;
        }
        images[texture] = image;
        return image;
    }

    static void CompressTexture(Texture2D texture, bool alpha)
    {
        EditorUtility.CompressTexture(texture, alpha ? TextureFormat.DXT5 : TextureFormat.DXT1, TextureCompressionQuality.Best);
    }

    // The CrowVita/Lit keyword sets the build has variants of (asset_rules.vita_lit_overlay's keepers),
    // sorted and space-separated as EnemyRebuild.cs compares them.
    static readonly string[] LitKeywords =
        { "_ALPHATEST_ON", "_ALPHABLEND_ON", "_ALPHAPREMULTIPLY_ON", "_EMISSION", "_METALLICGLOSSMAP", "_DETAIL_MULX2", "_SPECULARHIGHLIGHTS_OFF", "_GLOSSYREFLECTIONS_OFF" };

    static System.Collections.Generic.HashSet<string> VariantSets()
    {
        var sets = new System.Collections.Generic.HashSet<string>();
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/Resources/CrowVita/Variants" }))
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (material == null) continue;
            sets.Add(string.Join(" ", material.shaderKeywords.Where(k => LitKeywords.Contains(k)).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray()));
        }
        Debug.Log("DOWNGRADE_ENEMY_ATLAS variant sets: " + sets.Count);
        return sets.Count > 0 ? sets : null;
    }

    // The editor reads every mesh's data, readable or not; one it can't read stays out.
    static readonly System.Collections.Generic.Dictionary<Mesh, bool> readable = new System.Collections.Generic.Dictionary<Mesh, bool>();

    static bool Readable(Mesh mesh)
    {
        bool known;
        if (readable.TryGetValue(mesh, out known)) return known;
        bool can;
        try
        {
            can = mesh.vertices.Length == mesh.vertexCount;
        }
        catch (Exception)
        {
            can = false;
        }
        readable[mesh] = can;
        return can;
    }
}
