using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Batch-mode entry point for the downgrade pipeline. Builds the scenes listed in
// the recovered EditorBuildSettings.
//   -buildOutput <path>           player executable (Windows) or folder (Vita)
//   -buildTarget Win64|PSP2       Unity's flag; PSP2 needs Unity's PS Vita build module and Sony's SDK
//   -scriptingBackend mono|il2cpp Windows only; the Vita target is IL2CPP only
//   -crowScenes <a;b;...>         a test build of only these scenes (file names or paths), with the first
public static class DowngradeBuild
{
    // Homebrew title: XXYYYY-<9 char title ID>_00-<16 char label>.
    const string VitaContentId = "IV0000-CROW00001_00-CROWCOUNTRYVITA0";

    public static void Build()
    {
        string[] args = Environment.GetCommandLineArgs();
        string output = Argument(args, "-buildOutput");
        if (output == null) throw new ArgumentException("Missing -buildOutput <path>");
        string requested = Argument(args, "-buildTarget"); // Unity's own flag; it also selects the import target
        if (requested != "PSP2" && requested != "Win64") throw new ArgumentException("Unsupported -buildTarget " + requested);
        bool vita = requested == "PSP2";
        bool il2cpp = vita || Argument(args, "-scriptingBackend") == "il2cpp";
        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        // pipeline.py build --scenes: a test build of some scenes. The first stays: the player starts it.
        string only = Argument(args, "-crowScenes");
        if (only != null)
        {
            string[] names = only.Split(';').Select(n => n.Trim()).Where(n => n.Length > 0).ToArray();
            string[] unknown = names.Where(n => !scenes.Any(s => IsScene(s, n))).ToArray();
            if (unknown.Length > 0)
            {
                Debug.LogError("DOWNGRADE_BUILD Failed: no scene " + string.Join(", ", unknown) + " in the build settings");
                EditorApplication.Exit(2);
                return;
            }
            scenes = scenes.Where((s, i) => i == 0 || names.Any(n => IsScene(s, n))).ToArray();
            Debug.Log("DOWNGRADE_SCENES test build of " + string.Join(", ", scenes));
        }

        BuildTarget target = BuildTarget.StandaloneWindows64;
        BuildTargetGroup group = BuildTargetGroup.Standalone;
        if (vita)
        {
            target = BuildTarget.PSP2;
            group = BuildTargetGroup.PSP2;
            if (!BuildPipeline.IsBuildTargetSupported(group, target) ||
                !EditorUserBuildSettings.SwitchActiveBuildTarget(group, target))
            {
                Debug.LogError("DOWNGRADE_BUILD Failed: PS Vita build support is not installed for this editor");
                EditorApplication.Exit(2);
                return;
            }
            EditorUserBuildSettings.psp2BuildSubtarget = PSP2BuildSubtarget.PCHosted;
            PlayerSettings.PSVita.contentID = VitaContentId;
            PlayerSettings.PSVita.shortTitle = "Crow Country";
            // The Vita module calls IL2CPP without null and array bounds checks, so a null
            // reference or bad index the PC game survives as a logged exception crashes the
            // player instead. Keep the exception behaviour of the source version.
            PlayerSettings.SetAdditionalIl2CppArgs("--emit-null-checks --enable-array-bounds-check");
            // The largest application memory budget; the game's art is sized for PCs.
            PlayerSettings.PSVita.memoryExpansionMode = PlayerSettings.PSVita.PSVitaMemoryExpansionMode.ExpandBy109MB;
        }
        PlayerSettings.SetScriptingBackend(group, il2cpp ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x);
        // The source game shows no Unity splash screen, and the project keeps that setting, but
        // the editor turns the splash back on when it loads a project under a license that
        // cannot hide it. Set it again, and log what the editor kept.
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;
        Debug.Log("DOWNGRADE_SPLASH hasPro=" + UnityEditorInternal.InternalEditorUtility.HasPro() +
                  " show=" + PlayerSettings.SplashScreen.show + " showUnityLogo=" + PlayerSettings.SplashScreen.showUnityLogo);

        scenes = scenes.Concat(PreloadAddressables()).ToArray();

        // pipeline.py build --development: the development player, for the engine's profiler markers.
        BuildOptions options = args.Contains("-developmentBuild") ? BuildOptions.Development : BuildOptions.None;
        string codeStage = vita ? Argument(args, "-codeStage") : null;
        if (codeStage != null && System.IO.Directory.Exists(codeStage)) System.IO.Directory.Delete(codeStage, true);
        var watcher = codeStage == null ? null : new System.Threading.Thread(() => WatchStagedAssemblies(codeStage)) { IsBackground = true };
        if (watcher != null) watcher.Start();
        BuildReport report = BuildPipeline.BuildPlayer(scenes, output, target, options);
        stopWatching = true;
        if (watcher != null) watcher.Join();
        Debug.Log("DOWNGRADE_BUILD " + report.summary.result + " errors=" + report.summary.totalErrors +
                  " development=" + (options == BuildOptions.Development) +
                  " target=" + target + " backend=" + (il2cpp ? "il2cpp" : "mono") +
                  " scenes=" + scenes.Length + " size=" + report.summary.totalSize);
        // How long each step took (the scenes, the shared assets, IL2CPP), for pipeline.py's summary.
        foreach (BuildStep step in report.steps)
            Debug.Log("DOWNGRADE_STEP " + step.duration.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                      " " + step.depth + " " + step.name);
        // Build report messages do not all reach the editor log; keep them next to it.
        var messages = report.steps.SelectMany(s => s.messages.Select(m => m.type + "\t" + s.name + "\t" + m.content))
            .Where(m => !m.StartsWith("Log\t"));
        System.IO.File.WriteAllLines(output.TrimEnd('/', System.IO.Path.DirectorySeparatorChar) + ".messages.txt", messages.ToArray());
        if (codeStage != null && report.summary.result == BuildResult.Succeeded) SaveCodeStage(codeStage);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }

    // The assets the game loads through Addressables (asset_rules.ADDRESSABLE_ASSETS, listed by the
    // pipeline as "key<tab>path" lines in ProjectSettings/CrowAddressables.txt) go in a CrowCompat AddressablesTable
    // among the player's preloaded assets: the player loads it before the first scene, with the
    // dependencies the build lists, and CrowCompat's Addressables serves the keys from it.
    const string AddressablesList = "ProjectSettings/CrowAddressables.txt", AddressablesTablePath = "Assets/CrowAddressablesTable.asset";

    // Returns the scenes the build adds (CrowMara).
    static string[] PreloadAddressables()
    {
        if (!System.IO.File.Exists(AddressablesList))
            throw new InvalidOperationException("No " + AddressablesList + ": run pipeline.py project");
        Type type = Type.GetType("CrowCompat.AddressablesTable, CrowCompat");
        if (type == null) throw new InvalidOperationException("No CrowCompat.AddressablesTable in the project's CrowCompat.dll");
        var keys = new System.Collections.Generic.List<string>();
        var assets = new System.Collections.Generic.List<UnityEngine.Object>();
        var added = new System.Collections.Generic.List<string>();
        foreach (string line in System.IO.File.ReadAllLines(AddressablesList))
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 2) continue;
            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(fields[1]);
            if (asset == null) throw new InvalidOperationException("No asset " + fields[1] + " for the Addressables key " + fields[0]);
            if (fields[0] == MaraKey)
            {
                added.Add(SaveMaraScene((GameObject)asset));
                continue;
            }
            keys.Add(fields[0]);
            assets.Add(asset);
        }
        ScriptableObject table = ScriptableObject.CreateInstance(type);
        type.GetField("keys").SetValue(table, keys.ToArray());
        type.GetField("assets").SetValue(table, assets.ToArray());
        AssetDatabase.DeleteAsset(AddressablesTablePath);
        AssetDatabase.CreateAsset(table, AddressablesTablePath);
        AssetDatabase.SaveAssets();
        var preloaded = PlayerSettings.GetPreloadedAssets()
            .Where(a => a != null && AssetDatabase.GetAssetPath(a) != AddressablesTablePath).ToList();
        preloaded.Add(AssetDatabase.LoadMainAssetAtPath(AddressablesTablePath));
        PlayerSettings.SetPreloadedAssets(preloaded.ToArray());
        Debug.Log("DOWNGRADE_ADDRESSABLES preloaded " + string.Join(", ", keys.ToArray()) + "; scenes " + string.Join(", ", added.ToArray()));
        return added.ToArray();
    }

    // The player prefab (CharacterCameraPrefab: the player, the camera and every menu, 10,000
    // GameObjects) goes in a scene of its own instead, as an inactive instance named as
    // Object.Instantiate names a clone. CrowCompat (compat/Loading.cs, MaraScene) loads it in the
    // background once the title has loaded and gives the instance to MaraLoader, where the game
    // cloned the prefab in its first area (a third of that load at the PS Vita's speed), and the
    // prefab no longer stays in memory for good among the preloaded assets.
    const string MaraKey = "CharacterCameraPrefab", MaraScenePath = "Assets/CrowMara.unity";

    static string SaveMaraScene(GameObject prefab)
    {
        UnityEngine.SceneManagement.Scene scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        instance.name = prefab.name + "(Clone)";
        instance.SetActive(false);
        if (!UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, MaraScenePath))
            throw new InvalidOperationException("Could not save " + MaraScenePath);
        Debug.Log("DOWNGRADE_MARA saved " + prefab.name + " in " + MaraScenePath);
        return MaraScenePath;
    }

    // pipeline.py quickcode replays the build's UnityLinker and IL2CPP steps (their command lines are
    // in the editor log) on newly rewritten assemblies. Their inputs are in Temp, which the editor
    // deletes when it exits, under their paths relative to Temp in the code stage: the staged
    // assemblies, the link XMLs and the compiled ApplicationInfo.o.
    static volatile bool stopWatching;

    // The staged assemblies exist only inside the platform's post-process, between the editor
    // staging them and the linker replacing them with its output (from tempStrip); no build callback
    // runs there. So a thread copies Temp/StagingArea/Data/Managed whenever it changes, until the
    // linker's tempStrip appears.
    static void WatchStagedAssemblies(string stage)
    {
        string managed = System.IO.Path.GetFullPath(System.IO.Path.Combine("Temp", "StagingArea", "Data", "Managed"));
        string target = System.IO.Path.Combine(stage, "StagingArea", "Data", "Managed");
        string copied = null;
        while (!stopWatching)
        {
            System.Threading.Thread.Sleep(250);
            try
            {
                if (!System.IO.Directory.Exists(managed)) continue;
                // The linker has started: what was copied last is its input. (After it, the editor puts
                // the stripped assemblies in Managed, which are no linker input.)
                if (System.IO.Directory.Exists(System.IO.Path.Combine(managed, "tempStrip"))) return;
                string[] files = System.IO.Directory.GetFiles(managed, "*", System.IO.SearchOption.AllDirectories);
                string state = string.Join("|", files.Select(f => f + ":" + new System.IO.FileInfo(f).Length + ":" +
                                                                   System.IO.File.GetLastWriteTimeUtc(f).Ticks).ToArray());
                if (state == copied) continue;
                if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, true);
                foreach (string file in files)
                {
                    string destination = System.IO.Path.Combine(target, file.Substring(managed.Length + 1));
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination));
                    System.IO.File.Copy(file, destination, true);
                }
                copied = state;
            }
            catch (Exception)
            {
                copied = null;  // a file being written or moved; copy again on the next look
            }
        }
    }

    static void SaveCodeStage(string stage)
    {
        string temp = System.IO.Path.GetFullPath("Temp");
        int count = 0;
        foreach (string file in System.IO.Directory.GetFiles(temp, "*", System.IO.SearchOption.AllDirectories))
        {
            string relative = file.Substring(temp.Length + 1);
            string[] parts = relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            bool linkXml = parts.Length == 3 && parts[0] == "StagingArea" && parts[1] == "Data" && relative.EndsWith(".xml");
            if (!linkXml && !relative.EndsWith(".o")) continue;
            string target = System.IO.Path.Combine(stage, relative);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
            System.IO.File.Copy(file, target, true);
            count++;
        }
        string managed = System.IO.Path.Combine(stage, "StagingArea", "Data", "Managed");
        int assemblies = System.IO.Directory.Exists(managed) ? System.IO.Directory.GetFiles(managed, "*.dll", System.IO.SearchOption.AllDirectories).Length : 0;
        Debug.Log("DOWNGRADE_CODE_STAGE saved " + count + " files and " + assemblies + " staged assemblies to " + stage);
    }

    // The PS Vita build's static merge (CrowCompat's compat/Merge.cs, CrowMergeScenes below), from the
    // CROW_MERGE environment variable: on (merge, and drop the parts' meshes nothing else in the scene
    // uses; the default), ab (merge and keep the parts' meshes, so -crow-merge off can switch back for
    // comparisons), off. Other platforms don't merge, unless CROW_VITA_DATA=1 (a Windows player with
    // the PS Vita's scene data, for measurements: merge, split and MeshCombineStudio bake as on the PS Vita).
    internal static string MergeMode()
    {
        if (!VitaData) return "off";
        string mode = Environment.GetEnvironmentVariable("CROW_MERGE");
        return string.IsNullOrEmpty(mode) ? "on" : mode;
    }

    // The PS Vita build's scene split (CrowCompat's Merge.Split, after the merge in CrowMergeScenes),
    // from the CROW_SPLIT environment variable: movers (the default: the still parts and the parts that
    // move on their own), on (the still parts only) or off. Other platforms don't split (CROW_VITA_DATA=1 does).
    internal static string SplitMode()
    {
        if (!VitaData) return "off";
        string mode = Environment.GetEnvironmentVariable("CROW_SPLIT");
        return string.IsNullOrEmpty(mode) ? "movers" : mode;
    }

    // The scene data as the PS Vita build writes it: the PS Vita target, or CROW_VITA_DATA=1 on another.
    internal static bool VitaData
    {
        get
        {
            return EditorUserBuildSettings.activeBuildTarget == BuildTarget.PSP2 ||
                   Environment.GetEnvironmentVariable("CROW_VITA_DATA") == "1";
        }
    }

    // Whether a scene path is the scene named (its file name, or its path), in any case.
    static bool IsScene(string path, string name)
    {
        return string.Equals(path, name, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase);
    }

    static string Argument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

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
                if (filter == null || filter.sharedMesh == null) continue;
                var serialized = new SerializedObject(filter.sharedMesh);
                SerializedProperty readable = serialized.FindProperty("m_IsReadable");
                if (readable == null) continue;
                readable.boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                meshes++;
            }
        foreach (string line in lines.Split('\n'))
            if (line.Length > 0) Debug.Log("DOWNGRADE_" + line);
        Debug.Log("DOWNGRADE_MERGE " + scene.name + " mode=" + mode + " unreadable=" + meshes + " ms=" + clock.ElapsedMilliseconds);
    }

    static void MarkUnreadable(UnityEngine.Object asset)
    {
        if (asset == null) return;
        var serialized = new SerializedObject(asset);
        SerializedProperty readable = serialized.FindProperty("m_IsReadable");
        if (readable == null) return;
        readable.boolValue = false;
        serialized.ApplyModifiedPropertiesWithoutUndo();
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
