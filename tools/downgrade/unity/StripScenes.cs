using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
// scene's other behaviours, FSMs included, and of candidates that stay). From CROW_STRIP: on (the PS
// Vita's default) or off (other platforms' default). The project's scene files don't change.
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
    static readonly HashSet<string> Primitive = new HashSet<string>
    {
        "int", "unsigned int", "SInt8", "UInt8", "SInt16", "UInt16", "SInt64", "UInt64", "float", "double", "bool", "char", "string",
        "Vector2f", "Vector3f", "Vector4f", "Quaternionf", "ColorRGBA", "Rectf",
    };
    static readonly string[] Skipped = { "Logos", "Title", "CrowMara" };

    public int callbackOrder
    {
        get { return 20; }  // after the merge (which bakes the random scripts), the split and the MeshCombineStudio bake
    }

    static string Mode()
    {
        string mode = Environment.GetEnvironmentVariable("CROW_STRIP");
        if (!string.IsNullOrEmpty(mode)) return mode;
        return EditorUserBuildSettings.activeBuildTarget == BuildTarget.PSP2 ? "on" : "off";
    }

    static object Field(object target, string name)
    {
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(target);
        }
        return null;
    }

    static bool Candidate(MonoBehaviour behaviour)
    {
        string name = behaviour.GetType().Name;
        if (Inert.Contains(name)) return true;
        if (!Baked.Contains(name)) return false;
        return !(bool)(Field(behaviour, "onAwake") ?? true) && !(bool)(Field(behaviour, "onEnable") ?? true);
    }

    // Every object a behaviour's serialized data references (arrays of plain values skipped).
    static void References(UnityEngine.Object behaviour, HashSet<UnityEngine.Object> into)
    {
        var serialized = new SerializedObject(behaviour);
        SerializedProperty property = serialized.GetIterator();
        bool enter = true;
        while (property.Next(enter))
        {
            enter = true;
            if (property.propertyType == SerializedPropertyType.ObjectReference)
            {
                UnityEngine.Object target = property.objectReferenceValue;
                if (target != null) into.Add(target);
                enter = false;
            }
            else if (property.propertyType == SerializedPropertyType.String) enter = false;
            else if (property.isArray && Primitive.Contains(property.arrayElementType)) enter = false;
        }
    }

    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (report == null || !BuildPipeline.isBuildingPlayer) return;  // entering play mode
        if (Mode() != "on" || Skipped.Contains(scene.name)) return;
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
        foreach (MonoBehaviour behaviour in others) References(behaviour, referenced);
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
                    References(candidate, referenced);
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
