using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// The Enemy Puddle's random looks picked as the PS Vita build writes the scene (the user's call: the same
// puddle at every visit). Haunted Hilltop's Enemy Puddle is 3,613 transforms, 1,822 behaviours and 1,371
// renderers in the export: 68 copies of a blob part, each made of RandomVariant sets (252 scripts, 2 to 18 variants
// each) of which its Awake keeps one and switches the others off, so about 2,700 of those transforms are loaded at
// every visit only to stay off. Haunted Hilltop's load after Ocean Kingdom bottomed at 4-7 MB of free main memory.
// Here each RandomVariant of a puddle picks once, in hierarchy order (an outer pick first, so the variants it drops
// take their inner scripts with them; System.Random with a fixed seed, so every build picks alike): the picked
// variant is switched on, the others are destroyed and the script goes, so the static merge after this (callback
// order) sees plain parts. An unpicked variant stays, switched off as its Awake would leave it, if anything outside
// it references it or anything in it (a behaviour's serialized data, PlayMaker FSMs and their templates included; a
// joint's body; an LODGroup's renderer; a particle system's shape), or a string in a behaviour names it; the script
// then stays too, with its pick made (onAwake and onEnable off, its list the picked variant alone), and so does a
// script anything references. From CROW_PUDDLE_BAKE (common.BUILD_SWITCHES): on (Haunted Hilltop's puddles; the
// default with the PS Vita's scene data), all (every scene's), off.
class CrowPuddleBake : UnityEditor.Build.IProcessSceneWithReport
{
    const string Puddle = "Enemy Puddle";
    static readonly HashSet<string> Scenes = new HashSet<string> { "Haunted Hilltop" };

    public int callbackOrder
    {
        get { return -20; }  // before CrowProBuilderMeshes (-10) and CrowMergeScenes (0): the merge sees the picks
    }

    static Transform Of(UnityEngine.Object target)
    {
        var go = target as GameObject;
        if (go != null) return go.transform;
        var component = target as Component;
        return component != null ? component.transform : null;
    }

    static bool Inside(Transform t, Transform root)
    {
        for (; t != null; t = t.parent)
            if (t == root) return true;
        return false;
    }

    // Who references what inside the puddles: a target's transform -> the referrers' transforms; and every
    // string of the scene's behaviours, into names.
    static Dictionary<Transform, List<Transform>> Referrers(UnityEngine.SceneManagement.Scene scene, List<Transform> puddles, HashSet<string> names)
    {
        var referrers = new Dictionary<Transform, List<Transform>>();
        var walked = new HashSet<UnityEngine.Object>();
        Action<Transform, UnityEngine.Object> note = (from, target) =>
        {
            Transform t = Of(target);
            if (t == null || !puddles.Any(p => Inside(t, p))) return;
            List<Transform> list;
            if (!referrers.TryGetValue(t, out list)) referrers[t] = list = new List<Transform>();
            list.Add(from);
        };
        var targets = new List<UnityEngine.Object>();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;  // a missing script
                // The puddle's ManualRandomiser lists every random script in it (1,507), for its editor menu
                // only; nothing calls it in the player and CrowStripScenes leaves it out (it checks for null).
                if (behaviour.GetType().Name == "ManualRandomiser") continue;
                targets.Clear();
                if (behaviour.GetType().Name == "RandomVariant")
                {
                    // Its own list doesn't count; whatever else it holds does.
                    var self = new List<UnityEngine.Object>();
                    BuildUtil.Walk(behaviour, self, names, false, walked);
                    var own = BuildUtil.Field(behaviour, "variants") as System.Collections.IList;
                    targets.AddRange(self.Where(o => own == null || !own.Contains(o)));
                }
                else BuildUtil.Walk(behaviour, targets, names, false, walked);
                foreach (UnityEngine.Object target in targets) note(behaviour.transform, target);
            }
            foreach (Joint joint in root.GetComponentsInChildren<Joint>(true))
                if (joint.connectedBody != null) note(joint.transform, joint.connectedBody);
            foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>(true))
                foreach (LOD lod in group.GetLODs())
                    foreach (Renderer renderer in lod.renderers)
                        if (renderer != null) note(group.transform, renderer);
            foreach (ParticleSystem system in root.GetComponentsInChildren<ParticleSystem>(true))
                if (system.shape.meshRenderer != null) note(system.transform, system.shape.meshRenderer);
        }
        return referrers;
    }

    public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
    {
        if (report == null || !BuildPipeline.isBuildingPlayer) return;  // entering play mode
        string mode = BuildUtil.Switch("CROW_PUDDLE_BAKE");
        if (mode == "off" || (mode != "all" && !Scenes.Contains(scene.name))) return;
        var puddles = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
            .Where(t => t.name.StartsWith(Puddle, StringComparison.Ordinal)).ToList();
        puddles.RemoveAll(p => puddles.Any(q => q != p && Inside(p.parent, q)));  // the outermost
        if (puddles.Count == 0) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int scripts = 0, variants = 0, dropped = 0, transforms = 0, keptVariants = 0, keptScripts = 0, failed = 0;
        var reasons = new List<string>();
        try
        {
            var names = new HashSet<string>();
            Dictionary<Transform, List<Transform>> referrers = Referrers(scene, puddles, names);
            var random = new System.Random(20221022);
            foreach (Transform puddle in puddles)
            {
                foreach (MonoBehaviour script in puddle.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (script == null || script.GetType().Name != "RandomVariant") continue;  // gone with an outer pick
                    var list = BuildUtil.Field(script, "variants") as System.Collections.IList;
                    if (list == null) continue;
                    var options = list.Cast<object>().OfType<GameObject>().Where(g => g != null).ToList();
                    if (options.Count == 0) continue;
                    scripts++;
                    variants += options.Count;
                    GameObject picked = options[random.Next(options.Count)];
                    picked.SetActive(true);
                    var left = new List<GameObject> { picked };
                    foreach (GameObject other in options)
                    {
                        // Gone with another of the list (a variant inside a variant), or holding the picked one.
                        if (other == null || other == picked) continue;
                        other.SetActive(false);
                        Transform root = other.transform;
                        string why = Inside(picked.transform, root) ? "holds the pick" : names.Contains(other.name) ? "named" : null;
                        if (why == null)
                            foreach (KeyValuePair<Transform, List<Transform>> pair in referrers)
                            {
                                if (!Inside(pair.Key, root)) continue;
                                Transform from = pair.Value.FirstOrDefault(f => !Inside(f, root));
                                if (from == null) continue;
                                why = pair.Key.name + " <- " + from.name;
                                break;
                            }
                        if (why != null)
                        {
                            keptVariants++;
                            left.Add(other);
                            if (reasons.Count < 8) reasons.Add(other.name + ": " + why);
                            continue;
                        }
                        transforms += other.GetComponentsInChildren<Transform>(true).Length;
                        UnityEngine.Object.DestroyImmediate(other, true);
                        dropped++;
                    }
                    bool scriptUsed = referrers.ContainsKey(script.transform) && referrers[script.transform].Any(from => from != script.transform);
                    if (left.Count == 1 && !scriptUsed)
                    {
                        UnityEngine.Object.DestroyImmediate(script, true);
                        continue;
                    }
                    // It stays with its pick made: it does nothing at Awake or OnEnable.
                    keptScripts++;
                    list.Clear();
                    list.Add(picked);
                    BuildUtil.SetField(script, "onAwake", false);
                    BuildUtil.SetField(script, "onEnable", false);
                }
            }
        }
        catch (Exception exception)
        {
            failed++;
            Debug.LogError("DOWNGRADE_PUDDLE " + scene.name + " failed: " + exception);
        }
        Debug.Log("DOWNGRADE_PUDDLE " + scene.name + " puddles=" + puddles.Count + " scripts=" + scripts + " variants=" + variants +
                  " dropped=" + dropped + " transforms=" + transforms + " keptVariants=" + keptVariants + " keptScripts=" + keptScripts +
                  " failed=" + failed + " ms=" + clock.ElapsedMilliseconds +
                  (reasons.Count > 0 ? " (kept: " + string.Join("; ", reasons.ToArray()) + ")" : ""));
    }
}
