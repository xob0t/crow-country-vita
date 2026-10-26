using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Batch-mode entry point for the downgrade pipeline (pipeline.py build). Builds the scenes listed in
// the recovered EditorBuildSettings.
//   -buildOutput <path>           player executable (Windows) or folder (Vita)
//   -buildTarget Win64|PSP2       Unity's flag; PSP2 needs Unity's PS Vita build module and Sony's SDK
//   -scriptingBackend mono|il2cpp Windows only; the Vita target is IL2CPP only
//   -crowScenes <a;b;...>         a test build of only these scenes (file names or paths), with the first
//                                 scene of the build settings, which the player starts
//   -developmentBuild             Unity's development player
//   -codeStage <dir>              PS Vita: save the linker's and IL2CPP's inputs there for quickcode
//   -vitaTitleId <id>             PS Vita: the app's title ID (pipeline.VITA_TITLE_ID)
// The build switches (CROW_* variables, common.BUILD_SWITCHES; BuildUtil.Switch) choose what the scripts
// here do to the scenes the build writes.
public static class DowngradeBuild
{
    // Homebrew title: XXYYYY-<9 char title ID>_00-<16 char label>.
    const string VitaContentId = "IV0000-{0}_00-CROWCOUNTRYVITA0";

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
            string titleId = Argument(args, "-vitaTitleId");
            if (titleId == null || titleId.Length != 9) throw new ArgumentException("Missing -vitaTitleId <9 characters>");
            EditorUserBuildSettings.psp2BuildSubtarget = PSP2BuildSubtarget.PCHosted;
            PlayerSettings.PSVita.contentID = string.Format(VitaContentId, titleId);
            PlayerSettings.PSVita.shortTitle = "Crow Country";
            // The Vita module calls IL2CPP without null and array bounds checks, so a null
            // reference or bad index the PC game survives as a logged exception crashes the
            // player instead. Keep the exception behaviour of the source version (pipeline.IL2CPP_CHECKS,
            // which link runs IL2CPP with: keep the two in step).
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

        scenes = scenes.Concat(AddressablesScenes()).ToArray();

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

    // The assets the game loads through Addressables (asset_rules.ADDRESSABLE_ASSETS, listed by the pipeline as
    // "key<tab>path" lines in ProjectSettings/CrowAddressables.txt). The game's catalog has one, the player
    // prefab, which goes in a scene of its own (SaveMaraScene); CrowCompat's Addressables loads any other key
    // from Resources/Addressables.
    const string AddressablesList = "ProjectSettings/CrowAddressables.txt";
    // The preloaded table of Addressables assets an older build script made, which stays empty: taken out.
    const string OldAddressablesTable = "Assets/CrowAddressablesTable.asset";

    // Returns the scenes the build adds (CrowMara).
    static string[] AddressablesScenes()
    {
        if (!System.IO.File.Exists(AddressablesList))
            throw new InvalidOperationException("No " + AddressablesList + ": run pipeline.py project");
        var added = new System.Collections.Generic.List<string>();
        foreach (string line in System.IO.File.ReadAllLines(AddressablesList))
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 2) throw new InvalidOperationException(AddressablesList + " has a line that isn't key<tab>path: " + line);
            if (fields[0] != MaraKey) throw new InvalidOperationException("No rule for the Addressables key " + fields[0] + " (" + AddressablesList + ")");
            var prefab = AssetDatabase.LoadMainAssetAtPath(fields[1]) as GameObject;
            if (prefab == null) throw new InvalidOperationException("No prefab " + fields[1] + " for the Addressables key " + fields[0]);
            added.Add(SaveMaraScene(prefab));
        }
        UnityEngine.Object[] preloaded = PlayerSettings.GetPreloadedAssets();
        if (preloaded.Any(a => a != null && AssetDatabase.GetAssetPath(a) == OldAddressablesTable))
            PlayerSettings.SetPreloadedAssets(preloaded.Where(a => a != null && AssetDatabase.GetAssetPath(a) != OldAddressablesTable).ToArray());
        AssetDatabase.DeleteAsset(OldAddressablesTable);
        Debug.Log("DOWNGRADE_ADDRESSABLES scenes " + string.Join(", ", added.ToArray()));
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
