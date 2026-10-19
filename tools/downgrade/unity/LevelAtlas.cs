using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// The areas' static renderers that draw alike but for their textures, atlased and merged per carrier and cell as the
// build writes each scene (CrowCompat's compat/LevelAtlas.cs, Merge.LevelProcess), after the static merge and split
// (CrowMergeScenes) and MeshCombineStudio's baked combining (CrowMeshCombineBake), whose renderers it takes too.
// From CROW_LEVEL_ATLAS: on (the merge, the parts' meshes nothing else uses dropped), ab (the parts' meshes kept, so
// -crow-level-atlas off works), report (log only), off (the default). Only with the PS Vita's scene data
// (DowngradeBuild.VitaData). CROW_LEVEL_ATLAS_CELL (m), _MAX_TILE, _PAGE (texels), _MAX_LIGHTS, _MAX_PAGES, _MAX_KB (pages), _MAX_MESH_KB,
// CROW_LEVEL_ATLAS_LIGHTS (exact, union4, any), CROW_LEVEL_ATLAS_MCS_CELLS (1: MeshCombineStudio's renderers by its cells),
// and CROW_LEVEL_ATLAS_PARTS=1 (a line per renderer) tune and log it.
class CrowLevelAtlas : UnityEditor.Build.IProcessSceneWithReport
{
    static readonly string[] Skipped = { "Logos", "Title", "CrowMara" };
    static Type merge;
    static HashSet<string> playerFinds;

    public int callbackOrder
    {
        get { return 12; }  // after CrowMergeScenes (0) and CrowMeshCombineBake (10)
    }

    internal static string Mode()
    {
        if (!DowngradeBuild.VitaData) return "off";
        string mode = Environment.GetEnvironmentVariable("CROW_LEVEL_ATLAS");
        return string.IsNullOrEmpty(mode) ? "off" : mode;
    }

    static void Number(string variable, string field, Func<string, object> parse)
    {
        string value = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(value)) merge.GetField(field).SetValue(null, parse(value));
    }

    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (report == null || !BuildPipeline.isBuildingPlayer) return;  // entering play mode
        if (Skipped.Contains(scene.name)) return;
        string mode = Mode();
        if (mode == "off") return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (merge == null)
            {
                merge = Type.GetType("CrowCompat.Merge, CrowCompat");
                if (merge == null || merge.GetMethod("LevelProcess") == null) throw new InvalidOperationException("No CrowCompat.Merge.LevelProcess in the project's CrowCompat.dll");
                const System.Reflection.BindingFlags Static = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                if (merge.GetField("SourceImage").GetValue(null) == null)
                    merge.GetField("SourceImage").SetValue(null, Delegate.CreateDelegate(typeof(Func<Texture, Texture2D>), typeof(CrowMergeScenes).GetMethod("SourceImage", Static)));
                if (merge.GetField("CompressTexture").GetValue(null) == null)
                    merge.GetField("CompressTexture").SetValue(null, Delegate.CreateDelegate(typeof(Action<Texture2D, bool>), typeof(CrowMergeScenes).GetMethod("CompressTexture", Static)));
                if (merge.GetField("VariantSets").GetValue(null) == null)
                    merge.GetField("VariantSets").SetValue(null, typeof(CrowMergeScenes).GetMethod("VariantSets", Static).Invoke(null, null));
                merge.GetField("LevelVita").SetValue(null, EditorUserBuildSettings.activeBuildTarget == BuildTarget.PSP2);
                merge.GetField("LevelParts").SetValue(null, Environment.GetEnvironmentVariable("CROW_LEVEL_ATLAS_PARTS") == "1");
                Number("CROW_LEVEL_ATLAS_CELL", "LevelCell", v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture));
                Number("CROW_LEVEL_ATLAS_MAX_TILE", "LevelMaxTile", v => int.Parse(v));
                Number("CROW_LEVEL_ATLAS_PAGE", "LevelPage", v => int.Parse(v));
                Number("CROW_LEVEL_ATLAS_MAX_LIGHTS", "LevelMaxLights", v => int.Parse(v));
                Number("CROW_LEVEL_ATLAS_MAX_PAGES", "LevelMaxPages", v => int.Parse(v));
                Number("CROW_LEVEL_ATLAS_MAX_KB", "LevelMaxBytes", v => long.Parse(v) * 1024L);
                Number("CROW_LEVEL_ATLAS_MAX_MESH_KB", "LevelMaxMeshBytes", v => long.Parse(v) * 1024L);
                Number("CROW_LEVEL_ATLAS_LIGHTS", "LevelLights", v => v);
                Number("CROW_LEVEL_ATLAS_MCS_CELLS", "LevelMcsCells", v => v == "1" || v == "on");
                Number("CROW_LEVEL_ATLAS_SMOOTH", "LevelSmooth", v => v == "1" || v == "on");
                playerFinds = PlayerFinds();
            }
            Func<Mesh, bool> canRead = Readable;
            string lines = (string)merge.GetMethod("LevelProcess").Invoke(null, new object[] { scene, mode, canRead, playerFinds });
            // The merged meshes and the atlases go to the GPU only, as the parts' meshes do.
            var unreadable = (System.Collections.IList)merge.GetField("Unreadable").GetValue(null);
            int count = 0;
            foreach (UnityEngine.Object made in unreadable) if (MarkUnreadable(made)) count++;
            unreadable.Clear();
            foreach (string line in lines.Split('\n'))
                if (line.Length > 0) Debug.Log("DOWNGRADE_" + line);
            Debug.Log("DOWNGRADE_LEVEL_ATLAS " + scene.name + " mode=" + mode + " unreadable=" + count + " ms=" + clock.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            Debug.LogError("DOWNGRADE_LEVEL_ATLAS " + scene.name + " failed: " + exception);
        }
    }

    // What the player's FSMs find in the whole scene by name or tag (the player prefab, DowngradeBuild's MaraKey).
    static HashSet<string> PlayerFinds()
    {
        const string list = "ProjectSettings/CrowAddressables.txt";
        if (!System.IO.File.Exists(list)) return null;
        foreach (string line in System.IO.File.ReadAllLines(list))
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 2 || fields[0] != "CharacterCameraPrefab") continue;
            var prefab = AssetDatabase.LoadMainAssetAtPath(fields[1]) as GameObject;
            if (prefab == null) return null;
            var names = (HashSet<string>)merge.GetMethod("PlayerFindNames").Invoke(null, new object[] { prefab });
            Debug.Log("DOWNGRADE_LEVEL_ATLAS player finds: " + string.Join(", ", names.OrderBy(n => n, StringComparer.Ordinal).ToArray()));
            return names;
        }
        return null;
    }

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

    static readonly Dictionary<Mesh, bool> readable = new Dictionary<Mesh, bool>();

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
