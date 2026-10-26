using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// The player (Mara and what she carries) in fewer draws: the parts below each anchor of the player's scene
// (CrowMara, the prefab's instance) merged into one renderer with a submesh per material and an atlas per
// material group, as the build writes the scene (CrowCompat's compat/PlayerMerge.cs, Merge.PlayerProcess).
// The player's objects are reached from the other scenes through globals and names, so every scene the
// build writes first leaves what it does through them in work/player-uses/<scene>.txt (Merge.PlayerUses);
// CrowMara, written last, reads those of every scene in the build settings, from this build or an earlier
// one (a test build of a few scenes uses the full build's), and is left as it is when one is missing
// (CROW_PLAYER_MERGE_PARTIAL=1 merges anyway, for test players of the scenes there are uses of).
// From CROW_PLAYER_MERGE (common.BUILD_SWITCHES): on (the default with the PS Vita's scene data), ab (the
// parts' meshes and the atlas records kept, so -crow-player-merge off and noatlas work), off.
class CrowPlayerMerge : UnityEditor.Build.IProcessSceneWithReport
{
    static Type merge;

    public int callbackOrder
    {
        get { return 5; }  // after CrowProBuilderMeshes (the player's meshes) and CrowMergeScenes
    }

    static string UsesFolder
    {
        get { return System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "..", "player-uses")); }
    }

    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (report == null || !BuildPipeline.isBuildingPlayer) return;  // entering play mode
        string mode = BuildUtil.Switch("CROW_PLAYER_MERGE");
        if (mode == "off") return;
        if (merge == null)
        {
            merge = Type.GetType("CrowCompat.Merge, CrowCompat");
            if (merge == null || merge.GetMethod("PlayerProcess") == null) throw new InvalidOperationException("No CrowCompat.Merge.PlayerProcess in the project's CrowCompat.dll");
        }
        try
        {
            if (scene.name != "CrowMara")
            {
                System.IO.Directory.CreateDirectory(UsesFolder);
                string uses = (string)merge.GetMethod("PlayerUses").Invoke(null, new object[] { scene });
                System.IO.File.WriteAllText(System.IO.Path.Combine(UsesFolder, scene.name + ".txt"), uses);
                return;
            }
            var lines = new List<string>();
            var missing = new List<string>();
            foreach (EditorBuildSettingsScene other in EditorBuildSettings.scenes.Where(s => s.enabled))
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(other.path);
                string file = System.IO.Path.Combine(UsesFolder, name + ".txt");
                if (System.IO.File.Exists(file)) lines.AddRange(System.IO.File.ReadAllLines(file));
                else missing.Add(name);
            }
            // CROW_PLAYER_MERGE_PARTIAL=1: merge with the uses there are (a test build's scenes before any full build).
            if (missing.Count > 0 && BuildUtil.Switch("CROW_PLAYER_MERGE_PARTIAL") == "1")
                Debug.Log("DOWNGRADE_PLAYER_MERGE partial: no uses of " + missing.Count + " scenes; the merge reads the other " + (EditorBuildSettings.scenes.Count(s => s.enabled) - missing.Count));
            else if (missing.Count > 0)
            {
                Debug.Log("DOWNGRADE_PLAYER_MERGE skipped: no uses of " + missing.Count + " scenes (" + string.Join(", ", missing.Take(5).ToArray()) + "...) in " + UsesFolder + "; a full build writes them");
                return;
            }
            SetUpAtlas();
            Func<Mesh, bool> canRead = BuildUtil.CanRead;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string result = (string)merge.GetMethod("PlayerProcess").Invoke(null, new object[] { scene, lines, mode == "ab", canRead });
            // The merged meshes and the atlases go to the GPU only, as the parts' meshes do.
            var unreadable = (System.Collections.IList)merge.GetField("Unreadable").GetValue(null);
            int count = 0;
            foreach (UnityEngine.Object made in unreadable) if (BuildUtil.MarkUnreadable(made)) count++;
            unreadable.Clear();
            Type part = Type.GetType("CrowCompat.MergedRenderer, CrowCompat");
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Component merged in root.GetComponentsInChildren(part, true))
                {
                    var filter = merged.GetComponent<MeshFilter>();
                    if (filter != null && BuildUtil.MarkUnreadable(filter.sharedMesh)) count++;
                }
            foreach (string line in result.Split('\n'))
                if (line.Length > 0) Debug.Log("DOWNGRADE_" + line);
            Debug.Log("DOWNGRADE_PLAYER_MERGE " + scene.name + " mode=" + mode + " uses=" + lines.Count + " unreadable=" + count + " ms=" + clock.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            Debug.LogError("DOWNGRADE_PLAYER_MERGE " + scene.name + " failed: " + exception);
        }
    }

    // The atlas' source images, compression and variants: CrowMergeScenes' (set there too when the areas merge).
    static void SetUpAtlas()
    {
        if (merge.GetField("SourceImage").GetValue(null) == null)
            merge.GetField("SourceImage").SetValue(null, (Func<Texture, Texture2D>)CrowMergeScenes.SourceImage);
        if (merge.GetField("CompressTexture").GetValue(null) == null)
            merge.GetField("CompressTexture").SetValue(null, (Action<Texture2D, bool>)CrowMergeScenes.CompressTexture);
        if (merge.GetField("VariantSets").GetValue(null) == null)
            merge.GetField("VariantSets").SetValue(null, CrowMergeScenes.VariantSets());
    }
}
