using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Enemies' random looks picked as the PS Vita build writes the scene, a fixed look per build (the user's call: the
// same puddle at every visit). Their RandomVariant scripts each keep one of their variants in Awake and switch the
// others off.
// - Haunted Hilltop's Enemy Puddle is 3,613 transforms, 1,822 behaviours and 1,371 renderers in the export: 68 copies
//   of a blob part, each made of RandomVariant sets (252 scripts, 2 to 18 variants each), so about 2,700 of those
//   transforms are loaded at every visit only to stay off. Haunted Hilltop's load after Ocean Kingdom bottomed at 4-7
//   MB of free main memory.
// - The Lumber (Ocean Kingdom, Corridor A, Corridor B, Underground NW) has 72 (its teeth, prods and wounds). The
//   merge takes every variant for a part that switches on its own, so the parts stayed apart below each: 289
//   renderers drawn where 12 of its transforms move, about 11 ms of the main thread a frame (26 Oct, DIP). With
//   the picks made, 211 (Underground NW's): most of the rest is its translucent skin, which Merge.SeeThroughEnemies
//   lets merge.
// Here each RandomVariant of those enemies picks once, in hierarchy order (an outer pick first, so the variants it
// drops take their inner scripts with them), as its DoRandom would (UnityEngine.Random.Range) but with its own seed
// (CrowCompat's Merge.PickSeed: the scene, the script's place in it), so every build picks alike and a change
// elsewhere in the scene leaves the other picks as they were:
// the picked variant is switched on, the others are destroyed and the script goes, so the static merge after this
// (callback order) sees plain parts. An unpicked variant stays, switched off as its Awake would leave it, if anything
// outside it references it or anything in it (a behaviour's serialized data, PlayMaker FSMs and their templates
// included; a joint's body; an LODGroup's renderer; a particle system's shape), or a string in a behaviour names it;
// the script then stays too, with its pick made (onAwake and onEnable off, its list the picked variant alone, which
// the merge then leaves alone), and so does a script anything references. From CROW_VARIANT_BAKE
// (common.BUILD_SWITCHES): on (the enemies in Enemies' scenes; the default with the PS Vita's scene data), all (in
// every scene), off.
class CrowVariantBake : UnityEditor.Build.IProcessSceneWithReport
{
    // The enemies' root names (a prefix: "Enemy Lumber (off)" too) and the scenes where on bakes them (null: every
    // scene). The puddle only where the memory is short.
    static readonly Dictionary<string, string[]> Enemies = new Dictionary<string, string[]>
    {
        { "Enemy Puddle", new[] { "Haunted Hilltop" } },
        { "Enemy Lumber", null },
    };

    static Func<Component, int> pickSeed;

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

    // The enemy prefix a root's name starts with, if mode bakes it in this scene; null if none.
    static string Enemy(string name, string scene, string mode)
    {
        foreach (KeyValuePair<string, string[]> enemy in Enemies)
            if (name.StartsWith(enemy.Key, StringComparison.Ordinal) && (mode == "all" || enemy.Value == null || enemy.Value.Contains(scene)))
                return enemy.Key;
        return null;
    }

    // Who references what inside the enemies: a target's transform -> the referrers' transforms; and every
    // string of the scene's behaviours, into names.
    static Dictionary<Transform, List<Transform>> Referrers(UnityEngine.SceneManagement.Scene scene, List<Transform> enemies, HashSet<string> names)
    {
        var referrers = new Dictionary<Transform, List<Transform>>();
        var walked = new HashSet<UnityEngine.Object>();
        Action<Transform, UnityEngine.Object> note = (from, target) =>
        {
            Transform t = Of(target);
            if (t == null || !enemies.Any(p => Inside(t, p))) return;
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
                // A ManualRandomiser lists every random script below it (the puddle's 1,507), for its editor menu
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
        string mode = BuildUtil.Switch("CROW_VARIANT_BAKE");
        if (mode == "off") return;
        var enemies = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
            .Where(t => Enemy(t.name, scene.name, mode) != null).ToList();
        enemies.RemoveAll(p => enemies.Any(q => q != p && Inside(p.parent, q)));  // the outermost
        if (enemies.Count == 0) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int scripts = 0, variants = 0, dropped = 0, transforms = 0, keptVariants = 0, keptScripts = 0, failed = 0;
        var reasons = new List<string>();
        try
        {
            if (pickSeed == null)
            {
                Type merge = Type.GetType("CrowCompat.Merge, CrowCompat");
                System.Reflection.MethodInfo method = merge == null ? null : merge.GetMethod("PickSeed");
                if (method == null) throw new InvalidOperationException("No CrowCompat.Merge.PickSeed in the project's CrowCompat.dll");
                pickSeed = (Func<Component, int>)Delegate.CreateDelegate(typeof(Func<Component, int>), method);
            }
            var names = new HashSet<string>();
            Dictionary<Transform, List<Transform>> referrers = Referrers(scene, enemies, names);
            // Every script's seed before any pick drops a variant, so that a pick doesn't change another's place.
            var seeds = new Dictionary<MonoBehaviour, int>();
            foreach (Transform enemy in enemies)
                foreach (MonoBehaviour script in enemy.GetComponentsInChildren<MonoBehaviour>(true))
                    if (script != null && script.GetType().Name == "RandomVariant") seeds[script] = pickSeed(script);
            UnityEngine.Random.State state = UnityEngine.Random.state;
            foreach (Transform enemy in enemies)
            {
                foreach (MonoBehaviour script in enemy.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (script == null || script.GetType().Name != "RandomVariant") continue;  // gone with an outer pick
                    var list = BuildUtil.Field(script, "variants") as System.Collections.IList;
                    if (list == null) continue;
                    var options = list.Cast<object>().OfType<GameObject>().Where(g => g != null).ToList();
                    if (options.Count == 0) continue;
                    scripts++;
                    variants += options.Count;
                    UnityEngine.Random.InitState(seeds[script]);
                    GameObject picked = options[UnityEngine.Random.Range(0, options.Count)];
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
            UnityEngine.Random.state = state;
        }
        catch (Exception exception)
        {
            failed++;
            Debug.LogError("DOWNGRADE_VARIANT_BAKE " + scene.name + " failed: " + exception);
        }
        // enemies=Puddle:1 or Lumber:1 (the prefix without "Enemy ").
        string kinds = string.Join(",", enemies.GroupBy(e => Enemy(e.name, scene.name, mode).Substring(6)).Select(g => g.Key + ":" + g.Count()).ToArray());
        Debug.Log("DOWNGRADE_VARIANT_BAKE " + scene.name + " enemies=" + kinds + " scripts=" + scripts + " variants=" + variants +
                  " dropped=" + dropped + " transforms=" + transforms + " keptVariants=" + keptVariants + " keptScripts=" + keptScripts +
                  " failed=" + failed + " ms=" + clock.ElapsedMilliseconds +
                  (reasons.Count > 0 ? " (kept: " + string.Join("; ", reasons.ToArray()) + ")" : ""));
    }
}
