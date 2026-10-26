using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Scripts that do nothing in the player, left out of each scene the PS Vita build writes. Every
// MonoBehaviour costs an area load its managed object, its fields read through the managed
// serializer on the loading thread, its Awake and OnEnable calls on the main thread, and the managed
// heap its object takes, which the GC heap keeps for good; Station Square has 11,374 of them, and
// 8,500 do nothing:
//   - the game's random transforms, materials and flips (com.sfbgames' RandomRotation, RandomScale,
//     RandomPosition, RandomMaterial, RandomFlip, RandomRotationState) that the static merge ran at
//     build time (compat/Merge.cs: onAwake off) and that don't run at every OnEnable: their Awake and
//     OnEnable do nothing then, and DoRandom is called only by ManualRandomiser's editor menu;
//   - scripts empty in the player or editor helpers: PrimitivePlusMaterial, MaterialCombiner,
//     OptimiseSetup, RuntimeStatic (a LateUpdate that sets a private flag), RandomDeactivate (its
//     DoRandom hides the base's, so nothing runs), FindOddNegativeScaling, ManualRandomiser (empty
//     Awake and OnEnable; its DoRandom is private and no FSM or script calls it).
// No game script looks these up (GetComponent and the like), and no FSM names them in a string.
// A candidate stays if anything that stays references it (any serialized object reference of the
// scene's other behaviours, FSMs included, and of candidates that stay; strings don't count, as no FSM
// names these). From CROW_STRIP (common.BUILD_SWITCHES): on (the default with the PS Vita's scene data) or
// off. The project's scene files don't change.
class CrowStripScenes : UnityEditor.Build.IProcessSceneWithReport
{
    static readonly HashSet<string> Inert = new HashSet<string>
    {
        "PrimitivePlusMaterial", "MaterialCombiner", "OptimiseSetup", "RuntimeStatic", "RandomDeactivate", "FindOddNegativeScaling",
        "ManualRandomiser",
    };
    static readonly HashSet<string> Baked = new HashSet<string>
    {
        "RandomRotation", "RandomScale", "RandomPosition", "RandomMaterial", "RandomFlip", "RandomRotationState",
    };

    public int callbackOrder
    {
        get { return 20; }  // after the merge (which bakes the random scripts), the split and the MeshCombineStudio bake
    }

    static bool Candidate(MonoBehaviour behaviour)
    {
        string name = behaviour.GetType().Name;
        if (Inert.Contains(name)) return true;
        if (!Baked.Contains(name)) return false;
        return !(bool)(BuildUtil.Field(behaviour, "onAwake") ?? true) && !(bool)(BuildUtil.Field(behaviour, "onEnable") ?? true);
    }

    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (report == null || !BuildPipeline.isBuildingPlayer) return;  // entering play mode
        if (BuildUtil.Switch("CROW_STRIP") != "on" || BuildUtil.SkippedScenes.Contains(scene.name)) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var candidates = new HashSet<MonoBehaviour>();
        var others = new List<MonoBehaviour>();
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;  // a missing script
                if (Candidate(behaviour)) candidates.Add(behaviour);
                else others.Add(behaviour);
            }
        if (candidates.Count == 0) return;
        var referenced = new HashSet<UnityEngine.Object>();
        foreach (MonoBehaviour behaviour in others) BuildUtil.Walk(behaviour, referenced, null, false, null);
        // Candidates that stay keep what they reference.
        var kept = new HashSet<MonoBehaviour>();
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (MonoBehaviour candidate in candidates)
                if (!kept.Contains(candidate) && referenced.Contains(candidate))
                {
                    kept.Add(candidate);
                    BuildUtil.Walk(candidate, referenced, null, false, null);
                    grew = true;
                }
        }
        var removed = new Dictionary<string, int>();
        foreach (MonoBehaviour candidate in candidates)
        {
            if (kept.Contains(candidate)) continue;
            string name = candidate.GetType().Name;
            int count;
            removed.TryGetValue(name, out count);
            removed[name] = count + 1;
            UnityEngine.Object.DestroyImmediate(candidate, true);
        }
        Debug.Log("DOWNGRADE_STRIP " + scene.name + " removed=" + removed.Values.Sum() + " kept=" + kept.Count + " of " + candidates.Count +
                  " behaviours=" + (others.Count + candidates.Count) + " ms=" + clock.ElapsedMilliseconds + " (" +
                  string.Join(", ", removed.OrderByDescending(p => p.Value).Select(p => p.Key + " " + p.Value).ToArray()) + ")");
    }
}
