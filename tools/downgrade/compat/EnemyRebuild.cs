using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// The enemies rebuilt as the PS Vita build writes each scene (after the static merge's enemy mode, from
// Merge.Process; the build's code is MergeEnemies.cs), so that a fight frame has fewer draws and smaller moving
// hierarchies:
//   atlas    CROW_ENEMY_ATLAS=on|ab|off (on by default). Each enemy type's merged renderers (one per
//            moving part, with a submesh per material) draw with one material per shader variant instead:
//            the type's materials that the CrowVita/Lit (or Standard) base pass draws alike are baked into
//            one atlas texture per type, each material a tile of the texels its vertices sample (its
//            _MainTex, times its _Color, times its _DETAIL_MULX2 detail map x2 when it has one, wrapped as
//            the textures wrap, texel for texel on the densest texture's grid, 2 texels of padding), and
//            where the merged materials' metallic and smoothness differ (or one has a metallic map), a
//            second atlas in the same layout at half size carries them (R metallic, A smoothness:
//            _METALLICGLOSSMAP; tiles fill their 4-aligned footprints, so a texel never spans two);
//            the merged meshes' UVs go to their tiles and the submeshes that now share a material are
//            one. Atlases are DXT1 (DXT5 with an alpha test, and the metallic one). A material stays as it
//            is when its tile would be over 512 texels a side, its shader isn't one of those, it blends,
//            has emission, a normal or occlusion map, a detail map on UV1, a texture the build can't read
//            (not a PNG or JPG) or one that mirrors, or the keyword set it needs has no variant in the
//            build (the CrowVita/Lit variant keepers). Only the merged renderers change: a part the game
//            gives another material (SetMaterial, RandomMaterial at enable, a hit flash) or switches
//            apart was never merged (Merge.cs' own marks), so it keeps its own material. ab keeps each
//            merged renderer's old mesh and materials beside the new ones (EnemyAtlasRecord), so
//            -crow-enemy-atlas off switches back at run time.
//   flatten  CROW_ENEMY_FLATTEN=on|ab|off (on). The enemies' pivots, bare transforms that only hold
//            their children (and the build-time random picks, which do nothing at run time, or merged
//            parts whose mesh the merge dropped), go: their children go to their parent with their world
//            pose (the pivot's pose baked into theirs), and bare leaves go too. A pivot stays when
//            anything names it (an FSM's object or string, a script's field), the analysis marks it
//            (moved, switched, searched, an unknown script's), it is off, tagged, or below anything that
//            counts, searches or switches its children (GetChild, GetChildCount, FindChild,
//            ActivateChildren, DestroyChildren, an unknown script), and a pivot that isn't at its
//            parent's origin (identity) stays when one of its children is moved by anything (a tween,
//            an FSM's transform action, an animation script, a body: their local moves would land
//            elsewhere below another parent). A child whose world pose can't be kept (skew) puts the
//            pivot back. ab records each pivot (EnemyFlattenRecord on the enemy), so -crow-enemy-flatten
//            off puts them back at run time.
//   debris   CROW_ENEMY_DEBRIS=out|off (out). Each enemy's topmost inactive subtrees (death debris, blood
//            pools, effects) go to one root per scene (CrowEnemyDebris, EnemyDebrisHolder), local poses
//            kept, recorded with their places. As the scene loads the holder hands them to EnemySplit,
//            and the first time the game's code switches one on, reparents it, or reads or sets the world
//            position or rotation of it or of anything inside it (the rewriter's RouteEnemySplit), it goes
//            back where it was, so a death finds its debris in place and it flies from where the enemy
//            is. The Blob trails' pools go too (their spawner switches a piece on through the same calls;
//            with -crow-blob-trails off, the PS Vita's default, never). Left in: what a script switches
//            (RandomVariant, RandomNumberEnabled, PMGlobalBoolToState, LightFlicker,
//            PlaySourceThenDeactivate: they may run in Awake) or an unknown script references, and
//            whatever is below anything that counts, searches or switches its children (recursive
//            activation too; ActivateChildren and DestroyChildren on the part's own parent). The enemies' RandomVariant picks (the Guest's hairstyles) stay the game's,
//            made at run time: the holder's Start (after every Awake) takes their unpicked variants out
//            the same way, and a later pick switches its variant on in place.
// Run time (every player; they act only on what such a build wrote):
//   -crow-enemy-atlas on|off       off: the atlased renderers' old meshes and materials (ab builds)
//   -crow-enemy-flatten on|off     off: the enemies' pivots back (ab builds)
//   -crow-enemy-debris out|in|check  in: the moved parts back in their places as each scene loads; check:
//                                  out, and each call that puts a subtree back from a part inside it is
//                                  logged (CROW_SPLIT_MISS)
//   -crow-enemy-rebuild-log        CROW_ENEMY_REBUILD after each load: holders, parts, records
// (-crow-enemy-flatten-check, which watched the flattened children for moves, found none and is gone: status-log.md,
// Enemy rebuild.)
// -crow-cycle / -crow-fight-cycle / -crow-ab keys: eatlas=on|off, eflatten=on|off, edebrisout=on|off.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    // A scene's enemies' moved parts (the build's debris), handed to EnemySplit as the scene loads.
    [DefaultExecutionOrder(-32000)]
    public class EnemyDebrisHolder : MonoBehaviour
    {
        public Transform[] parts;
        public Transform[] parents;
        public int[] siblings;
        // The enemies' RandomVariant scripts: their unpicked variants leave at Start.
        public MonoBehaviour[] variants;

        void Awake()
        {
            EnemyRebuild.Adopt(this);
        }

        void Start()
        {
            EnemyRebuild.TakeVariants(this);
        }
    }

    // ab builds: a merged renderer's mesh and materials before the atlas.
    public class EnemyAtlasRecord : MonoBehaviour
    {
        public Mesh originalMesh, atlasMesh;
        public Material[] originalMaterials, atlasMaterials;

        internal bool Set(bool atlas)
        {
            var filter = GetComponent<MeshFilter>();
            var renderer = GetComponent<MeshRenderer>();
            if (filter == null || renderer == null) return false;
            filter.sharedMesh = atlas ? atlasMesh : originalMesh;
            renderer.sharedMaterials = atlas ? atlasMaterials : originalMaterials;
            return true;
        }
    }

    // ab builds: an enemy's collapsed pivots, in the order they went (parents before their children),
    // with each one's children (a later pivot by its index in childPivot), their poses below it and after.
    public class EnemyFlattenRecord : MonoBehaviour
    {
        public string[] names;
        public int[] layers, siblings, childStart, childCount;
        public Transform[] parents;
        public Vector3[] positions, scales;
        public Quaternion[] rotations;
        public Transform[] children;
        public int[] childPivot;
        public Vector3[] upPositions, upScales, flatPositions, flatScales;
        public Quaternion[] upRotations, flatRotations;
        [NonSerialized] Transform[] made;

        internal int Unflatten()
        {
            if (made != null || names == null) return 0;
            made = new Transform[names.Length];
            int count = 0;
            for (int i = names.Length - 1; i >= 0; i--)
            {
                Transform parent = parents[i];
                if (parent == null) continue;
                var pivot = new GameObject(names[i]).transform;
                pivot.gameObject.layer = layers[i];
                pivot.SetParent(parent, false);
                pivot.localPosition = positions[i];
                pivot.localRotation = rotations[i];
                pivot.localScale = scales[i];
                pivot.SetSiblingIndex(Math.Min(siblings[i], parent.childCount - 1));
                made[i] = pivot;
                for (int k = childStart[i]; k < childStart[i] + childCount[i]; k++)
                {
                    Transform child = childPivot[k] >= 0 ? made[childPivot[k]] : children[k];
                    // World pose kept: the child's animated pose goes on below its pivot (upPositions... are
                    // its pose there at build time).
                    if (child != null) child.SetParent(pivot, true);
                }
                count++;
            }
            return count;
        }

        internal int Reflatten()
        {
            if (made == null) return 0;
            int count = 0;
            for (int i = 0; i < made.Length; i++)
            {
                Transform pivot = made[i];
                if (pivot == null) continue;
                Transform parent = pivot.parent;
                int index = pivot.GetSiblingIndex();
                for (int k = childStart[i]; k < childStart[i] + childCount[i]; k++)
                {
                    Transform child = childPivot[k] >= 0 ? made[childPivot[k]] : children[k];
                    if (child == null || child.parent != pivot) continue;
                    child.SetParent(parent, true);
                    child.SetSiblingIndex(++index);
                }
                pivot.SetParent(null, false);
                UnityEngine.Object.Destroy(pivot.gameObject);
                count++;
            }
            made = null;
            return count;
        }
    }

    // The run-time side: the flags and cycle keys above, and the holders' hand-over.
    public static class EnemyRebuild
    {
        static bool atlasOn = true, flattenOn = true, debrisOut = true, log;
        internal static bool CheckDebris;
        static int adoptedParts, adoptedTransforms, holdersSeen;  // since the last area's line

        // From EnemySplit.Install (Diag, every player).
        internal static void Install(string[] args)
        {
            atlasOn = CrowFlags.Value(args, "-crow-enemy-atlas") != "off";
            flattenOn = CrowFlags.Value(args, "-crow-enemy-flatten") != "off";
            string debris = CrowFlags.Value(args, "-crow-enemy-debris");
            debrisOut = debris != "in";
            CheckDebris = debris == "check";
            log = CrowFlags.Has(args, "-crow-enemy-rebuild-log");
            CrowEvents.Loaded("EnemyRebuild.sceneLoaded", (scene, mode) =>
            {
                if (!Scenes.IsArea(scene)) return;
                string atlas = atlasOn ? "" : SetAtlas(false, scene);
                string flatten = flattenOn ? "" : SetFlatten(false, scene);
                if (log || !atlasOn || !flattenOn)
                    Debug.Log("CROW_ENEMY_REBUILD " + scene.name.Replace(' ', '_') + ": holders=" + holdersSeen + " parts=" + adoptedParts + " transforms=" + adoptedTransforms +
                              " debris=" + (debrisOut ? "out" : "in") + " atlasRecords=" + Records<EnemyAtlasRecord>(scene).Count +
                              " flattenRecords=" + Records<EnemyFlattenRecord>(scene).Count + " " + atlas + " " + flatten);
                holdersSeen = adoptedParts = adoptedTransforms = 0;
            });
        }

        // EnemyDebrisHolder.Awake: the build's moved parts to EnemySplit (in their places with debris in).
        internal static void Adopt(EnemyDebrisHolder holder)
        {
            holdersSeen++;
            if (holder.parts == null) return;
            for (int i = 0; i < holder.parts.Length; i++)
            {
                if (holder.parts[i] == null || holder.parents[i] == null) continue;
                adoptedParts++;
                adoptedTransforms += EnemySplit.Adopt(holder.parts[i], holder.parents[i], holder.siblings[i], holder.transform, debrisOut);
            }
        }

        // EnemyDebrisHolder.Start, after every Awake of the load: the RandomVariant scripts' variants that are
        // off now (picked against, or not yet picked: the pick switches its variant on, which puts it back).
        internal static void TakeVariants(EnemyDebrisHolder holder)
        {
            if (holder.variants == null) return;
            int scripts = 0, onePick = 0, taken = 0, asleep = 0;
            foreach (MonoBehaviour script in holder.variants)
            {
                if (script == null) continue;
                FieldInfo field = script.GetType().GetField("variants");
                var list = field == null ? null : field.GetValue(script) as IList;
                if (list == null) continue;
                scripts++;
                int on = 0;
                foreach (object item in list)
                {
                    var variant = item as GameObject;
                    if (variant != null && variant.activeSelf) on++;
                }
                if (on == 1) onePick++;
                else if (!script.gameObject.activeInHierarchy) asleep++;  // not awake yet: it picks when it wakes
                foreach (object item in list)
                {
                    var variant = item as GameObject;
                    if (variant == null || variant.activeSelf || variant.transform.parent == null) continue;
                    taken++;
                    Transform part = variant.transform;
                    adoptedTransforms += EnemySplit.Adopt(part, part.parent, part.GetSiblingIndex(), holder.transform, debrisOut);
                }
            }
            if (log) Debug.Log("CROW_ENEMY_REBUILD variants: " + scripts + " RandomVariant scripts, " + onePick + " with one pick on, " + asleep + " not awake yet, " + taken + " unpicked variants out");
        }

        static List<T> Records<T>(Scene scene) where T : Component
        {
            var found = new List<T>();
            foreach (T record in Resources.FindObjectsOfTypeAll<T>())
                if (record != null && record.gameObject.scene == scene) found.Add(record);
            return found;
        }

        static IEnumerable<Scene> Loaded()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) yield return scene;
            }
        }

        static string SetAtlas(bool on, Scene scene)
        {
            int count = 0;
            foreach (EnemyAtlasRecord record in Records<EnemyAtlasRecord>(scene))
                if (record.Set(on)) count++;
            return "atlas=" + (on ? "on" : "off") + ":" + count;
        }

        static string SetFlatten(bool on, Scene scene)
        {
            int count = 0;
            List<EnemyFlattenRecord> records = Records<EnemyFlattenRecord>(scene);
            if (records.Count == 0) return "flatten=" + (on ? "on" : "off") + ":0";
            WithDebrisIn(() =>
            {
                foreach (EnemyFlattenRecord record in records) count += on ? record.Reflatten() : record.Unflatten();
            });
            return "flatten=" + (on ? "on" : "off") + ":" + count + " pivots";
        }

        // The pivots come and go with the moved debris in its places (a part out of its enemy has none of
        // its pivots above it), then out again from wherever it now is.
        internal static void WithDebrisIn(Action change)
        {
            if (debrisOut) EnemySplit.SetAdopted(false);
            change();
            if (debrisOut) EnemySplit.SetAdopted(true);
        }

        // A -crow-cycle / -crow-fight-cycle / -crow-ab change (Quality.Change); false when it isn't one.
        internal static bool Change(string key, string value)
        {
            bool on = value != "off" && value != "in" && value != "game";
            string done;
            switch (key)
            {
                case "eatlas":
                    atlasOn = on;
                    done = string.Join(" ", Loaded().Select(s => SetAtlas(on, s)).ToArray());
                    break;
                case "eflatten":
                    flattenOn = on;
                    done = string.Join(" ", Loaded().Select(s => SetFlatten(on, s)).ToArray());
                    break;
                case "edebrisout":
                    debrisOut = on;
                    done = EnemySplit.SetAdopted(on);
                    break;
                default:
                    return false;
            }
            Debug.Log("CROW_ENEMY_REBUILD " + key + "=" + value + ": " + done);
            return true;
        }
    }
}
