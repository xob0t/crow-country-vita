using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// The scene split as the PS Vita build writes each area (DowngradeBuild's CrowMergeScenes; CROW_SPLIT): the parts
// nothing acts on taken out to the scene's root, each with a SplitPart; SceneSplit.cs has the run-time side
// (SplitPart, SplitHolder, SplitMirror, SplitFollow) and -crow-scene-split.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static partial class Merge
    {
        // The still parts of an area's big hierarchies made hierarchies of their own (each engine system
        // that follows transform changes walks a whole hierarchy whenever anything in it moves). From
        // each root of 1,000 transforms or more down, a child subtree of 32 transforms or more is taken
        // out to the scene's root when:
        //   - nothing on the way down to it moves, is destroyed or reparented, searches or counts its
        //     children (GetChild, GetChildCount...), switches what is below it recursively (a recursive
        //     ActivateGameObject), or is a script the rules don't know, an Animator, Animation, LOD group,
        //     joint, particle system or UI; objects that are only switched on and off (ActivateGameObject,
        //     ActivateChildren and the game's switching scripts) may be on the way;
        //   - its own root is not marked at all (nothing names it to switch, move or search it);
        //   - nothing inside it is excluded (an unknown script might look up through its parents) or
        //     taken by a MeshCombineStudio combiner's search (which would no longer find it).
        // What moves inside a part keeps its parents. Below switched objects a SplitHolder takes the
        // part's place (its local pose, sibling index and activity) and switches the part with it; the
        // part is built switched off. Elsewhere a part whose ancestors are off for good is switched off
        // itself. A part whose world transform can't be kept (a skewed parent) stays. Each part gets a
        // SplitPart with its old parent and place. apply false only reports.
        // SplitMovers (-crow-scene-split apply-movers; CROW_SPLIT=movers for the build): also the parts that
        // move on their own, whatever their size: a subtree whose own root only the movers in
        // MoverReasons move (DOTween transform tweens, SetPosition/SetRotation/Rotate/LookAt actions, the
        // game's animation and spin scripts; it may also be switched or destroyed itself), with nothing
        // excluded inside, goes out below a new root of its own that keeps its old parent's world pose
        // (its "split parent"), so a move in its parent's space lands where it did; the split parent is
        // the SplitPart (in the old place it would stand at the parent's origin). And the way down may
        // go below objects the game destroys (DestroyObject, DestroySelf), with a SplitHolder in the
        // part's place: destroying the holder destroys its part. A moving part in a big hierarchy made
        // the engine walk the whole hierarchy every frame (Pool: a breathing creature and a few
        // animation scripts in a root of 16,000 transforms). A moving object of BigMover transforms or
        // more (Station Square's and the underground areas' whole levels, which the elevator moves as
        // the player rides it) isn't taken whole: the way goes on below it as below a switched object,
        // and the parts taken out below it follow it at run time (SceneSplit.cs' SplitFollow puts each part
        // back at its place below its old parent in any frame that parent moved), so they stay put as
        // they would have and the level isn't walked while it stands still.
        public static bool SplitMovers;
        const int BigMover = 1000;
        // The roots split: a hierarchy of fewer transforms costs the engine's walks little (an area's is 20,000 or more).
        const int MinSplitRoot = 1000;

        public static string Split(Scene scene, bool apply)
        {
            bool movers = SplitMovers;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Analysis analysis = Analyze(scene, new Dictionary<string, int>(), false);
            long analysed = clock.ElapsedMilliseconds;
            int before = 0, moved = 0, parts = 0, held = 0, skewed = 0, off = 0, fsms = 0, moving = 0;
            var stops = new Dictionary<string, int>();
            foreach (GameObject root in Roots(scene))
            {
                if (root.transform.hierarchyCount < MinSplitRoot || root.GetComponent<EnemyDebrisHolder>() != null) continue;  // the enemies' debris (EnemyRebuild.cs)
                before += root.transform.hierarchyCount;
                Transform[] all = root.GetComponentsInChildren<Transform>(true);
                var index = new Dictionary<Transform, int>(all.Length);
                for (int i = 0; i < all.Length; i++) index[all[i]] = i;
                var size = new int[all.Length];
                var inside = new bool[all.Length];  // something below or on it may not leave its parents
                // The same for the movers' split, where MeshCombineStudio's CachedComponents (references
                // for its combiner, no code) doesn't count.
                var insideMover = new bool[all.Length];
                for (int i = all.Length - 1; i >= 0; i--)
                {
                    size[i]++;
                    Mark mark;
                    string why;
                    if (analysis.marks.TryGetValue(all[i], out mark) &&
                        ((mark & Mark.Exclude) != 0 || ((mark & Mark.Own) != 0 && analysis.why.TryGetValue(all[i], out why) && why.Contains("MeshCombiner"))))
                    {
                        inside[i] = true;
                        insideMover[i] = analysis.why[all[i]] != "CachedComponents";
                    }
                    int parent;
                    if (i == 0 || !index.TryGetValue(all[i].parent, out parent)) continue;
                    size[parent] += size[i];
                    inside[parent] |= inside[i];
                    insideMover[parent] |= insideMover[i];
                }
                // Each node on the way, and whether something on the way is switched.
                var cuts = new List<KeyValuePair<Transform, bool>>();
                var queue = new Queue<KeyValuePair<Transform, bool>>();
                var moverCuts = new HashSet<Transform>();
                bool rootSwitched;
                if (Way(root.transform, analysis, stops, all.Length, out rootSwitched, movers)) queue.Enqueue(new KeyValuePair<Transform, bool>(root.transform, rootSwitched));
                while (queue.Count > 0)
                {
                    KeyValuePair<Transform, bool> node = queue.Dequeue();
                    foreach (Transform child in node.Key)
                    {
                        int i = index[child];
                        bool switched;
                        if (movers && MoverDebug && analysis.why.ContainsKey(child) && analysis.why[child].Contains("DOTween"))
                            Debug.Log("CROW_SPLIT_MOVER " + Hierarchy.Path(child, 4) + " inside=" + insideMover[i] + " mover=" + Mover(child, analysis) + " why=" + analysis.why[child] +
                                      " excluded: " + string.Join(" ", child.GetComponentsInChildren<Transform>(true).Where(d => analysis.marks.ContainsKey(d) &&
                                          ((analysis.marks[d] & Mark.Exclude) != 0 || analysis.why[d].Contains("MeshCombiner"))).Take(4)
                                          .Select(d => d.name.Replace(' ', '_') + "=" + analysis.why[d]).ToArray()));
                        if (movers && !insideMover[i] && size[i] < BigMover && Mover(child, analysis))
                        {
                            cuts.Add(new KeyValuePair<Transform, bool>(child, node.Value));
                            moverCuts.Add(child);
                            continue;
                        }
                        // With movers the way goes into small subtrees too (a moving part of any size counts), but
                        // only subtrees of MinSplit transforms or more leave still.
                        bool small = size[i] < MinSplit;
                        if ((small && !movers) || !Way(child, analysis, small ? new Dictionary<string, int>() : stops, size[i], out switched, movers)) continue;
                        if (!small && !inside[i] && !analysis.marks.ContainsKey(child)) cuts.Add(new KeyValuePair<Transform, bool>(child, node.Value));
                        else queue.Enqueue(new KeyValuePair<Transform, bool>(child, node.Value || switched));
                    }
                }
                foreach (KeyValuePair<Transform, bool> cut in cuts)
                {
                    if (MoverDebug)
                    {
                        var way = new System.Text.StringBuilder("CROW_SPLIT_WAY ").Append(Hierarchy.Path(cut.Key, 8)).Append(':');
                        for (Transform t = cut.Key.parent; t != null; t = t.parent)
                            way.Append(' ').Append(t.name.Replace(' ', '_')).Append('=').Append(analysis.why.ContainsKey(t) ? analysis.why[t] : "-");
                        Debug.Log(way.ToString());
                    }
                    Transform part = cut.Key, parent = part.parent;
                    bool mover = moverCuts.Contains(part);
                    if (!apply)
                    {
                        parts++;
                        if (cut.Value) held++;
                        if (mover) moving++;
                        moved += size[index[part]];
                        continue;
                    }
                    int moverSize = size[index[part]];
                    if (mover)
                    {
                        // The split parent: in the part's place at the old parent's origin, the part below it
                        // with its local pose as it was; from here on it stands for the part.
                        var split = new GameObject(part.name + " (split parent)");
                        split.layer = part.gameObject.layer;
                        split.transform.SetParent(parent, false);
                        split.transform.SetSiblingIndex(part.GetSiblingIndex());
                        part.SetParent(split.transform, false);
                        moving++;
                        part = split.transform;
                    }
                    Matrix4x4 world = part.localToWorldMatrix;
                    int sibling = part.GetSiblingIndex();
                    Vector3 position = part.localPosition, scale = part.localScale;
                    Quaternion rotation = part.localRotation;
                    // Switched off first where it must be (below a holder, or below parents off for
                    // good), so that leaving its parents doesn't wake it up.
                    bool active = part.gameObject.activeSelf, switchOff = active && (cut.Value || !parent.gameObject.activeInHierarchy);
                    if (switchOff) part.gameObject.SetActive(false);
                    part.SetParent(null, true);
                    if (!Same(part.localToWorldMatrix, world))
                    {
                        part.SetParent(parent, false);
                        part.SetSiblingIndex(sibling);
                        part.localPosition = position;
                        part.localRotation = rotation;
                        part.localScale = scale;
                        part.gameObject.SetActive(active);
                        if (mover)
                        {
                            // The moving part back in its place, the split parent gone.
                            Transform own = part.GetChild(0);
                            own.SetParent(parent, false);
                            own.SetSiblingIndex(sibling);
                            UnityEngine.Object.DestroyImmediate(part.gameObject);
                            moving--;
                        }
                        skewed++;
                        continue;
                    }
                    SplitHolder holder = null;
                    if (cut.Value)
                    {
                        var place = new GameObject(part.name + " (split)");
                        place.SetActive(false);
                        place.transform.SetParent(parent, false);
                        place.transform.localPosition = position;
                        place.transform.localRotation = rotation;
                        place.transform.localScale = scale;
                        place.transform.SetSiblingIndex(sibling);
                        holder = place.AddComponent<SplitHolder>();
                        holder.part = part.gameObject;
                        place.SetActive(active);  // last: playing (-crow-scene-split apply), it switches the part on
                        held++;
                    }
                    else if (switchOff) off++;
                    var record = part.gameObject.AddComponent<SplitPart>();
                    record.parent = parent;
                    record.sibling = sibling;
                    record.position = position;
                    record.rotation = rotation;
                    record.scale = scale;
                    record.active = active;
                    record.holder = holder;
                    fsms += part.GetComponentsInChildren<Component>(true).Count(c => c != null && c.GetType().Name == "PlayMakerFSM");
                    parts++;
                    moved += mover ? moverSize : size[index[part]];
                }
            }
            return "SPLIT " + scene.name.Replace(' ', '_') + (apply ? "" : " (report)") + (movers ? " (movers: " + moving + " moving parts)" : "") + ": " + parts + " parts of " + moved + " transforms out of big roots of " +
                   before + " (" + (before - moved) + " left), " + held + " with a holder below switched objects (" + fsms + " FSMs in parts), " + skewed +
                   " kept for a skewed parent, " + off + " switched off with their parents; analysis " + analysed + " ms, all " + clock.ElapsedMilliseconds +
                   " ms; stopped at: " + string.Join(" ", stops.OrderByDescending(p => p.Value).Take(12).Select(p => p.Key.Replace(' ', '_') + "=" + p.Value).ToArray()) +
                   "; moves through globals with no value here or found objects: " + (analysis.unresolved.Count == 0 ? "none" :
                       string.Join(" ", analysis.unresolved.OrderByDescending(p => p.Value).Take(12).Select(p => p.Key.Replace(' ', '_') + "=" + p.Value).ToArray()));
        }

        const int MinSplit = 32;
        // What only switches its target on and off (non-recursively): the split may go below it.
        static readonly HashSet<string> Switches = new HashSet<string>
        {
            "ActivateGameObject", "ActivateChildren", "PMGlobalBoolToState", "RandomNumberEnabled", "LightFlicker", "DeactivateSelf",
            "RandomVariant", "PlaySourceThenDeactivate",
        };

        // Whether the split may go below this object (switched: only because it is switched, or with
        // movers destroyed); counts the transforms it stops at by reason.
        static bool Way(Transform t, Analysis analysis, Dictionary<string, int> stops, int size, out bool switched, bool movers = false)
        {
            switched = false;
            Mark mark;
            string why = null;
            if (analysis.marks.TryGetValue(t, out mark) && (mark & (Mark.Anchor | Mark.Exclude | Mark.NoChild)) != 0)
            {
                string[] reasons = analysis.why[t].Split('+');
                switched = (mark & (Mark.Exclude | Mark.NoChild)) == 0 &&
                           reasons.All(r => Switches.Contains(r) || !AnchorReason(r) ||
                                            (movers && (r == "DestroyObject" || r == "DestroySelf" || (size >= BigMover && MoverReason(r)))));
                if (!switched) why = reasons.First(r => !Switches.Contains(r) && AnchorReason(r) || (mark & (Mark.Exclude | Mark.NoChild)) != 0);
            }
            else if (t is RectTransform) why = "UI";
            else if (t.GetComponent<ParticleSystem>() != null) why = "ParticleSystem";
            if (why == null) return true;
            int count;
            stops.TryGetValue(why, out count);
            stops[why] = count + size;
            return false;
        }

        // What moves only the object it acts on, in its parent's space or the world's (a split parent keeps
        // both): the movers' split takes such an object out with its subtree.
        static readonly HashSet<string> MoverReasons = new HashSet<string>
        {
            "SetPosition", "SetRotation", "SetScale", "Rotate", "LookAt", "SmoothLookAt", "SmoothLookAt2d", "SmoothLookAtDirection",
            "SmoothLookAtWithLimit", "MoveTowards", "SetRandomRotation", "SpinGameObject", "SetRotationEveryFrame", "RotateAroundAxis",
            "RotationAnimationScript", "ScaleAnimationScript", "PositionAnimationScript", "TransformLock",
            "SetProperty:position", "SetProperty:localPosition", "SetProperty:rotation", "SetProperty:localRotation", "SetProperty:eulerAngles",
            "SetProperty:localEulerAngles", "SetProperty:localScale",
        };

        static bool MoverReason(string reason)
        {
            return MoverReasons.Contains(reason) || reason.StartsWith("DOTweenTransform", StringComparison.Ordinal);
        }

        // A subtree the movers' split takes out whole: its root is moved only by MoverReasons (and maybe
        // switched or destroyed itself, which it still is out there), and nothing marks it excluded.
        internal static bool MoverDebug;

        static bool Mover(Transform t, Analysis analysis)
        {
            Mark mark;
            if (!analysis.marks.TryGetValue(t, out mark) || (mark & Mark.Exclude) != 0) return false;
            bool moves = false;
            foreach (string reason in analysis.why[t].Split('+'))
            {
                if (MoverReasons.Contains(reason) || reason.StartsWith("DOTweenTransform", StringComparison.Ordinal)) moves = true;
                else if (!Switches.Contains(reason) && AnchorReason(reason) && reason != "DestroyObject" && reason != "DestroySelf") return false;
            }
            return moves && !(t is RectTransform) && t.GetComponent<ParticleSystem>() == null;
        }

        // Reasons that only mark a renderer (own) don't stop the split.
        static bool AnchorReason(string reason)
        {
            return !OwnActions.Contains(reason) && reason != "MeshCombiner" && reason != "SetMaterial" && reason != "DeactivateRenderer" &&
                   reason != "VideoPlayer" && reason != "UnityEngine_Video_VideoPlayer" && reason != "RandomMaterial" && reason != "SkinnedMeshRenderer" &&
                   reason != "ParticleSystem" && reason != "SetProperty";
        }

        static bool Same(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(a[i] - b[i]) > 1e-4f * Mathf.Max(1f, Mathf.Abs(b[i]))) return false;
            return true;
        }
    }
}
