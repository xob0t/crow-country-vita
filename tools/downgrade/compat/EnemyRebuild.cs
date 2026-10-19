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
// Merge.Process; compat/Merge.cs), so that a fight frame has fewer draws and smaller moving hierarchies:
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
//   -crow-enemy-shots <s> <dir>, -crow-enemy-death-shots <dir>   close-ups for look checks (EnemyShots below)
//   -crow-enemy-flatten-check      what moves a child the build moved up from a pivot (FlattenCheck below)
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
            log = args.Contains("-crow-enemy-rebuild-log");
            EnemyShots.Install(args);
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (scene.name == "Logos" || scene.name == "Title" || scene.name == "CrowMara" || scene.name == "CrowGap") return;
                string atlas = atlasOn ? "" : SetAtlas(false, scene);
                string flatten = flattenOn ? "" : SetFlatten(false, scene);
                if (log || !atlasOn || !flattenOn)
                    Debug.Log("CROW_ENEMY_REBUILD " + scene.name.Replace(' ', '_') + ": holders=" + holdersSeen + " parts=" + adoptedParts + " transforms=" + adoptedTransforms +
                              " debris=" + (debrisOut ? "out" : "in") + " atlasRecords=" + Records<EnemyAtlasRecord>(scene).Count +
                              " flattenRecords=" + Records<EnemyFlattenRecord>(scene).Count + " " + atlas + " " + flatten);
                holdersSeen = adoptedParts = adoptedTransforms = 0;
            };
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
            FlattenCheck.Reset();  // the children's local poses change with their parents
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

        // A -crow-cycle / -crow-fight-cycle / -crow-ab change (from EnemyCost.Change); false when it isn't one.
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

    // -crow-enemy-shots <s> <dir>, -crow-enemy-death-shots <dir>: close-ups of the enemies for look checks,
    // plain renders by a camera of their own (the main camera's settings, no camera effect), 512x512 PNGs.
    // s seconds after the fight starts, one awake enemy of each type from three sides, with the atlases
    // on and off (ab builds) in the same frame (<type>-<side>-atlas.png, -orig.png; CROW_ENEMY_SHOT counts
    // the pixels that differ); 0, 0.4, 1.2 and 3 s after each of the fight's first 4 kills, its body's place, zoomed from the game camera
    // (death<n>-<type>-<t>.png).
    public class EnemyShots : MonoBehaviour
    {
        static float after = -1f;
        static string dir, deathDir;
        static readonly float[] DeathTimes = { 0f, 0.4f, 1.2f, 3f };
        bool done;
        GameObject lastKill;
        float killAt;
        Vector3 killPlace, killFrom;
        string killType;
        int kills, deathShot = int.MaxValue;
        Camera shotCamera;
        RenderTexture target;

        internal static void Install(string[] args)
        {
            int at = Array.IndexOf(args, "-crow-enemy-shots");
            if (at >= 0 && at + 2 < args.Length)
            {
                float.TryParse(args[at + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out after);
                dir = args[at + 2];
            }
            deathDir = CrowFlags.Value(args, "-crow-enemy-death-shots");
            FlattenCheck.On = args.Contains("-crow-enemy-flatten-check");
            if (dir == null && deathDir == null && !FlattenCheck.On) return;
            var holder = new GameObject("CrowEnemyShots");
            DontDestroyOnLoad(holder);
            holder.AddComponent<EnemyShots>();
            Debug.Log("CROW_ENEMY_SHOT on: after " + after + " s into " + dir + ", deaths into " + deathDir);
        }

        void LateUpdate()
        {
            if (FlattenCheck.On) FlattenCheck.Tick();
            float start = Fight.StartedAt;
            if (start < 0f) return;
            float now = Time.realtimeSinceStartup;
            try
            {
                if (!done && dir != null && now >= start + after)
                {
                    done = true;
                    Types();
                }
                if (deathDir != null) Deaths(now);
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_ENEMY_SHOT failed: " + exception);
                done = true;
                deathDir = null;
            }
        }

        Camera Lens()
        {
            if (shotCamera != null) return shotCamera;
            target = new RenderTexture(512, 512, 24);
            var go = new GameObject("CrowEnemyShotCamera");
            DontDestroyOnLoad(go);
            shotCamera = go.AddComponent<Camera>();
            Camera main = Camera.main;
            if (main != null) shotCamera.CopyFrom(main);
            shotCamera.enabled = false;
            shotCamera.targetTexture = target;
            shotCamera.fieldOfView = 30f;
            shotCamera.nearClipPlane = 0.05f;
            shotCamera.farClipPlane = 80f;
            shotCamera.clearFlags = CameraClearFlags.SolidColor;
            shotCamera.backgroundColor = new Color(0.25f, 0.25f, 0.28f);
            return shotCamera;
        }

        // The player's renderers (CharacterCameraPrefab) off while a shot renders: she stands in front.
        static List<Renderer> HidePlayer()
        {
            var hidden = new List<Renderer>();
            var roots = new List<Transform>();
            GameObject player = GameObject.Find("CharacterCameraPrefab(Clone)");  // in DontDestroyOnLoad's scene
            if (player != null) roots.Add(player.transform);
            if (Fight.PlayerBody != null) roots.Add(Fight.PlayerBody.transform.root);
            foreach (Transform root in roots.Distinct())
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(false))
                    if (renderer.enabled)
                    {
                        renderer.enabled = false;
                        hidden.Add(renderer);
                    }
            return hidden;
        }

        Texture2D Render()
        {
            Camera lens = Lens();
            List<Renderer> hidden = HidePlayer();
            lens.Render();
            foreach (Renderer renderer in hidden) renderer.enabled = true;
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var picture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            picture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            picture.Apply();
            RenderTexture.active = previous;
            return picture;
        }

        static void Save(Texture2D picture, string folder, string name)
        {
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, name), picture.EncodeToPNG());
        }

        static Bounds Extent(GameObject root)
        {
            var bounds = new Bounds(root.transform.position, Vector3.zero);
            bool any = false;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                if (any) bounds.Encapsulate(renderer.bounds);
                else bounds = renderer.bounds;
                any = true;
            }
            return bounds;
        }

        // From the game camera's own place (its view is clear), zoomed to the radius.
        void Zoom(Vector3 centre, float radius, Vector3 from)
        {
            Camera lens = Lens();
            float distance = Mathf.Max(0.5f, (centre - from).magnitude);
            lens.fieldOfView = Mathf.Clamp(2f * Mathf.Atan(radius * 1.15f / distance) * Mathf.Rad2Deg, 3f, 60f);
            lens.transform.position = from;
            lens.transform.LookAt(centre);
        }

        void Aim(Vector3 centre, float radius, Vector3 from, float turn)
        {
            Camera lens = Lens();
            lens.fieldOfView = 30f;
            Vector3 direction = centre - from;
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-4f) direction = Vector3.forward;
            direction = Quaternion.Euler(0f, turn, 0f) * direction.normalized;
            float distance = Mathf.Max(0.8f, radius / Mathf.Tan(lens.fieldOfView * 0.5f * Mathf.Deg2Rad) * 0.8f);
            lens.transform.position = centre - direction * distance + Vector3.up * radius * 0.35f;
            lens.transform.LookAt(centre);
        }

        void Types()
        {
            Camera main = Camera.main;
            Vector3 from = main != null ? main.transform.position : Vector3.zero;
            foreach (KeyValuePair<string, GameObject> type in Fight.AwakeByType())
            {
                Bounds bounds = Extent(type.Value);
                EnemyAtlasRecord[] records = type.Value.GetComponentsInChildren<EnemyAtlasRecord>(true);
                string name = type.Key.Replace(' ', '_');
                // As the game camera sees it (Mara hidden), and from beyond the enemy on both sides.
                foreach (float turn in new[] { 0f, 120f, -120f })
                {
                    if (turn == 0f) Zoom(bounds.center, Mathf.Max(0.3f, bounds.extents.magnitude), from);
                    else Aim(bounds.center, Mathf.Max(0.3f, bounds.extents.magnitude), from, turn);
                    string side = turn == 0f ? "front" : turn > 0f ? "left" : "right";
                    Texture2D atlas = Render();
                    Save(atlas, dir, name + "-" + side + "-atlas.png");
                    if (records.Length == 0)
                    {
                        UnityEngine.Object.Destroy(atlas);  // each render is a new texture (768 KB at 512x512)
                        continue;
                    }
                    foreach (EnemyAtlasRecord record in records) record.Set(false);
                    Texture2D original = Render();
                    foreach (EnemyAtlasRecord record in records) record.Set(true);
                    Save(original, dir, name + "-" + side + "-orig.png");
                    Color32[] a = atlas.GetPixels32(), b = original.GetPixels32();
                    int differ = 0, big = 0;
                    long sum = 0;
                    for (int i = 0; i < a.Length; i++)
                    {
                        int d = Math.Max(Math.Abs(a[i].r - b[i].r), Math.Max(Math.Abs(a[i].g - b[i].g), Math.Abs(a[i].b - b[i].b)));
                        if (d > 2) differ++;
                        if (d > 24) big++;
                        sum += d;
                    }
                    Debug.Log("CROW_ENEMY_SHOT " + name + " " + side + " records=" + records.Length + " differ=" + differ + " big=" + big + " of " + a.Length +
                              " mean=" + (sum / (double)a.Length).ToString("F2", CultureInfo.InvariantCulture));
                    UnityEngine.Object.Destroy(atlas);
                    UnityEngine.Object.Destroy(original);
                }
                // The flattening, the same way: the pivots back for one render (ab builds).
                EnemyFlattenRecord flat = type.Value.GetComponent<EnemyFlattenRecord>();
                if (flat == null) continue;
                Zoom(bounds.center, Mathf.Max(0.3f, bounds.extents.magnitude), from);
                Texture2D flattened = Render();
                int pivots = 0;
                Texture2D pivoted = null;
                EnemyRebuild.WithDebrisIn(() =>
                {
                    pivots = flat.Unflatten();
                    pivoted = Render();
                    flat.Reflatten();
                });
                Save(flattened, dir, name + "-flat.png");
                Save(pivoted, dir, name + "-pivots.png");
                Color32[] f = flattened.GetPixels32(), g = pivoted.GetPixels32();
                int moved = 0;
                for (int i = 0; i < f.Length; i++)
                    if (Math.Max(Math.Abs(f[i].r - g[i].r), Math.Max(Math.Abs(f[i].g - g[i].g), Math.Abs(f[i].b - g[i].b))) > 2) moved++;
                Debug.Log("CROW_ENEMY_SHOT " + name + " flatten pivots=" + pivots + " differ=" + moved + " of " + f.Length);
                UnityEngine.Object.Destroy(flattened);
                UnityEngine.Object.Destroy(pivoted);
            }
        }

        void Deaths(float now)
        {
            GameObject killed = Fight.LastKilled;
            if (killed != null && killed != lastKill && kills < 4)
            {
                lastKill = killed;
                killAt = Fight.LastKilledAt;
                Bounds bounds = Extent(killed);
                killPlace = killed.transform.position + Vector3.up * 0.5f;  // the body, not its parts' bounds
                Camera main = Camera.main;
                killFrom = main != null ? main.transform.position : killPlace - Vector3.forward * 5f;
                killType = killed.name.Replace(' ', '_');
                kills++;
                deathShot = 0;
            }
            if (deathShot >= DeathTimes.Length || now < killAt + DeathTimes[deathShot]) return;
            Zoom(killPlace, 1.3f, killFrom);
            Texture2D shot = Render();
            Save(shot, deathDir, "death" + kills + "-" + killType + "-" + DeathTimes[deathShot].ToString("F1", CultureInfo.InvariantCulture) + ".png");
            UnityEngine.Object.Destroy(shot);
            Debug.Log("CROW_ENEMY_SHOT death " + kills + " " + killType + " at " + killPlace + " t=" + DeathTimes[deathShot]);
            deathShot++;
        }
    }

    // -crow-enemy-flatten-check (ab builds): every half second, the children the build moved up from a pivot
    // that wasn't at its parent's origin (their local pose has the pivot's baked in), but those with a body
    // of their own: any whose local pose changes is moved by something in its parent's space, which now
    // lands elsewhere (CROW_FLATTEN_MOVED, once each; CROW_FLATTEN_CHECK every 10 s).
    static class FlattenCheck
    {
        internal static bool On;
        static float next, report;
        static int checks, watched;
        static readonly Dictionary<Transform, Matrix4x4> poses = new Dictionary<Transform, Matrix4x4>();
        static readonly HashSet<Transform> logged = new HashSet<Transform>();

        internal static void Reset()
        {
            poses.Clear();
        }

        static Matrix4x4 Local(Transform t)
        {
            return Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale);
        }

        internal static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < next) return;
            next = now + 0.5f;
            foreach (EnemyFlattenRecord record in Resources.FindObjectsOfTypeAll<EnemyFlattenRecord>())
            {
                if (record == null || !record.gameObject.scene.IsValid() || record.names == null) continue;
                for (int i = 0; i < record.names.Length; i++)
                {
                    bool identity = record.positions[i].sqrMagnitude < 1e-10f && Mathf.Abs(Mathf.Abs(record.rotations[i].w) - 1f) < 1e-6f &&
                                    (record.scales[i] - Vector3.one).sqrMagnitude < 1e-10f;
                    if (identity) continue;
                    for (int k = record.childStart[i]; k < record.childStart[i] + record.childCount[i]; k++)
                    {
                        Transform child = record.children[k];
                        if (child == null || child.GetComponent<Rigidbody>() != null) continue;
                        Matrix4x4 pose = Local(child), known;
                        if (!poses.TryGetValue(child, out known))
                        {
                            poses[child] = pose;
                            watched++;
                            continue;
                        }
                        checks++;
                        bool same = true;
                        for (int e = 0; e < 16 && same; e++) same = Mathf.Abs(pose[e] - known[e]) < 1e-4f;
                        if (same || logged.Contains(child)) continue;
                        logged.Add(child);
                        Debug.Log("CROW_FLATTEN_MOVED " + Perf.Path(child, 5) + " (was below " + record.names[i].Replace(' ', '_') + ") active=" +
                                  child.gameObject.activeSelf + " parent=" + (child.parent == null ? "-" : child.parent.name.Replace(' ', '_')) +
                                  " pos " + (Vector3)known.GetColumn(3) + " -> " + child.localPosition + " scale " + known.lossyScale + " -> " + child.localScale);
                    }
                }
            }
            if (now >= report)
            {
                report = now + 10f;
                Debug.Log("CROW_FLATTEN_CHECK watched=" + watched + " checks=" + checks + " moved=" + logged.Count);
            }
        }
    }

    // The build's side (Process calls Rebuild after the enemy merge, with its analysis).
    public static partial class Merge
    {
        // Set by the build (unity/DowngradeBuild.cs): a readable RGBA copy of a texture's source image (null:
        // not readable), a texture compressed in place (true: with alpha, DXT5; false DXT1), and the
        // keyword sets the build has variants of (sorted, space-separated; null: any).
        public static Func<Texture, Texture2D> SourceImage;
        public static Action<Texture2D, bool> CompressTexture;
        public static HashSet<string> VariantSets;
        // What the build writes unreadable (the atlases, the ab builds' old meshes); it empties the list.
        public static readonly List<UnityEngine.Object> Unreadable = new List<UnityEngine.Object>();

        static readonly string[] LitKeywords =
            { "_ALPHATEST_ON", "_ALPHABLEND_ON", "_ALPHAPREMULTIPLY_ON", "_EMISSION", "_METALLICGLOSSMAP", "_DETAIL_MULX2", "_SPECULARHIGHLIGHTS_OFF", "_GLOSSYREFLECTIONS_OFF" };
        static readonly HashSet<string> AtlasKeywords = new HashSet<string> { "_ALPHATEST_ON", "_METALLICGLOSSMAP", "_DETAIL_MULX2", "_SPECULARHIGHLIGHTS_OFF", "_GLOSSYREFLECTIONS_OFF" };
        const int TilePad = 2, MaxTile = 512, MaxAtlas = 2048;

        static string Env(string name, string fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        static string EnemyType(Transform enemy)
        {
            string name = enemy.name.Substring(6);
            int paren = name.IndexOf(" (", StringComparison.Ordinal);
            return (paren > 0 ? name.Substring(0, paren) : name).Replace(' ', '_');
        }

        // The rebuild of a scene's enemies; returns its CROW_ENEMY_REBUILD lines.
        static string Rebuild(Scene scene, Analysis analysis, List<GameObject> created)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string atlasMode = Env("CROW_ENEMY_ATLAS", "on"), flattenMode = Env("CROW_ENEMY_FLATTEN", "on"), debrisMode = Env("CROW_ENEMY_DEBRIS", "out");
            var report = new StringBuilder();
            string sceneName = scene.name.Replace(' ', '_');
            if (analysis.enemies.Count == 0) return "CROW_ENEMY_REBUILD scene=" + sceneName + " enemies=0";
            var byType = new Dictionary<string, List<Transform>>();
            foreach (Transform enemy in analysis.enemies)
            {
                if (enemy == null) continue;
                string type = EnemyType(enemy);
                List<Transform> list;
                if (!byType.TryGetValue(type, out list)) byType[type] = list = new List<Transform>();
                list.Add(enemy);
            }
            int before = analysis.enemies.Where(e => e != null).Sum(e => e.GetComponentsInChildren<Transform>(true).Length);
            if (atlasMode != "off")
                foreach (KeyValuePair<string, List<Transform>> type in byType)
                {
                    var merged = created.Where(go => go != null && type.Value.Any(e => go.transform.IsChildOf(e))).ToList();
                    try
                    {
                        report.Append(Atlas(sceneName, type.Key, merged, atlasMode == "ab")).Append('\n');
                    }
                    catch (Exception exception)
                    {
                        report.Append("CROW_ENEMY_ATLAS scene=").Append(sceneName).Append(" type=").Append(type.Key).Append(" failed: ").Append(exception.ToString().Replace('\n', ' ')).Append('\n');
                    }
                }
            // What names objects: the scene's FSMs (the analysis, made with references) and the fields of its
            // scripts that do something at run time.
            var referenced = new HashSet<Transform>(analysis.referenced);
            foreach (GameObject root in Roots(scene))
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || Idle(behaviour)) continue;
                    string name = behaviour.GetType().Name;
                    if (name == "PlayMakerFSM" || name == "MergedRenderer") continue;
                    foreach (UnityEngine.Object target in References(behaviour))
                    {
                        var go = target as GameObject;
                        var component = target as Component;
                        Transform t = go != null ? go.transform : component != null ? component.transform : null;
                        if (t != null) referenced.Add(t);
                    }
                }
            var mergedParts = new HashSet<Renderer>();
            foreach (GameObject go in created)
            {
                var record = go == null ? null : go.GetComponent<MergedRenderer>();
                if (record != null && record.parts != null) foreach (Renderer part in record.parts) if (part != null) mergedParts.Add(part);
            }
            var flattenKept = new Dictionary<string, int>();
            int pivots = 0, leaves = 0;
            if (flattenMode != "off")
                foreach (Transform enemy in analysis.enemies)
                    if (enemy != null) Flatten(enemy, analysis, referenced, mergedParts, flattenMode == "ab", flattenKept, ref pivots, ref leaves);
            int flattened = analysis.enemies.Where(e => e != null).Sum(e => e.GetComponentsInChildren<Transform>(true).Length);
            var debrisKept = new Dictionary<string, int>();
            int subtrees = 0, debrisTransforms = 0, variantScripts = 0;
            if (debrisMode != "off") Debris(scene, analysis, debrisKept, ref subtrees, ref debrisTransforms, ref variantScripts);
            int after = analysis.enemies.Where(e => e != null).Sum(e => e.GetComponentsInChildren<Transform>(true).Length);
            report.Append("CROW_ENEMY_REBUILD scene=").Append(sceneName).Append(" enemies=").Append(analysis.enemies.Count)
                .Append(" types=").Append(string.Join(",", byType.Select(p => p.Key + ":" + p.Value.Count).ToArray()))
                .Append(" atlas=").Append(atlasMode).Append(" flatten=").Append(flattenMode).Append(" debris=").Append(debrisMode)
                .Append(" transforms=").Append(before).Append("->").Append(flattened).Append("->").Append(after)
                .Append(" pivots=").Append(pivots).Append(" leaves=").Append(leaves)
                .Append(" debrisParts=").Append(subtrees).Append(" debrisTransforms=").Append(debrisTransforms).Append(" variantScripts=").Append(variantScripts)
                .Append(" ms=").Append(clock.ElapsedMilliseconds)
                .Append(" flattenKept: ").Append(string.Join(" ", flattenKept.OrderByDescending(p => p.Value).Select(p => p.Key + "=" + p.Value).ToArray()))
                .Append(" debrisKept: ").Append(string.Join(" ", debrisKept.OrderByDescending(p => p.Value).Select(p => p.Key + "=" + p.Value).ToArray()));
            foreach (KeyValuePair<string, List<Transform>> type in byType)
            {
                Transform one = type.Value.FirstOrDefault(e => e != null);
                if (one == null) continue;
                Transform[] all = one.GetComponentsInChildren<Transform>(true);
                report.Append('\n').Append("CROW_ENEMY_REBUILD scene=").Append(sceneName).Append(" type=").Append(type.Key).Append(" n=").Append(type.Value.Count)
                    .Append(" transforms=").Append(all.Length).Append(" active=").Append(all.Count(t => t.gameObject.activeInHierarchy || ActiveBelow(t, one)));
            }
            return report.ToString();
        }

        // Active up to the enemy's root (whatever the root's own parents).
        static bool ActiveBelow(Transform t, Transform enemy)
        {
            for (; t != null && t != enemy; t = t.parent)
                if (!t.gameObject.activeSelf) return false;
            return t == enemy;
        }

        // A script that does nothing at run time: the inert ones and the random picks made at build time.
        static bool Idle(Component component)
        {
            string name = component.GetType().Name;
            if (InertScripts.Contains(name) && name != "MeshCombiner") return true;
            if (name == "RandomRotation" || name == "RandomScale" || name == "RandomPosition" || name == "RandomMaterial" || name == "RandomFlip" || name == "RandomRotationState")
                return !(bool)(Field(component, "onAwake") ?? true) && !(bool)(Field(component, "onEnable") ?? true);
            return false;
        }

        static string[] Reasons(Analysis analysis, Transform t)
        {
            string why;
            return analysis.why.TryGetValue(t, out why) ? why.Split('+') : new string[0];
        }

        // ---------------------------------------------------------------- flatten

        // What doesn't move what it marks.
        static readonly HashSet<string> StillReasons = new HashSet<string>
        {
            "ActivateGameObject", "ActivateGameObjectRecursive", "DestroyObject", "DestroySelf", "RandomVariant", "RandomNumberEnabled", "PMGlobalBoolToState",
            "LightFlicker", "DeactivateSelf", "PlaySourceThenDeactivate", "ActivateComponent", "DestroyComponent", "SetMaterial", "SetRandomMaterial",
            "RandomMaterial", "DeactivateRenderer", "SetProperty", "MeshCombiner", "ParticleSystem", "SkinnedMeshRenderer", "VideoPlayer",
            "UnityEngine_Video_VideoPlayer", "PlaySourceOnCollisionEnter",
        };

        static bool Driven(Analysis analysis, Transform t)
        {
            Mark mark;
            if (!analysis.marks.TryGetValue(t, out mark)) return false;
            if ((mark & Mark.Exclude) != 0) return true;
            return Reasons(analysis, t).Any(r => !StillReasons.Contains(r));
        }

        static bool Identity(Transform t)
        {
            Vector3 p = t.localPosition, s = t.localScale;
            Quaternion r = t.localRotation;
            return p.sqrMagnitude < 1e-10f && Mathf.Abs(Mathf.Abs(r.w) - 1f) < 1e-6f && (s - Vector3.one).sqrMagnitude < 1e-10f;
        }

        // Whether a transform's components do nothing at run time: none, the idle scripts, a merged part
        // whose mesh the merge dropped.
        static bool Bare(Transform t, HashSet<Renderer> mergedParts)
        {
            foreach (Component component in t.GetComponents<Component>())
            {
                if (component == null) return false;
                if (component is Transform) continue;
                var renderer = component as MeshRenderer;
                if (renderer != null)
                {
                    if (!Drop || renderer.enabled || !mergedParts.Contains(renderer)) return false;
                    continue;
                }
                var filter = component as MeshFilter;
                if (filter != null)
                {
                    var own = t.GetComponent<MeshRenderer>();
                    if (!Drop || filter.sharedMesh != null || own == null || !mergedParts.Contains(own)) return false;
                    continue;
                }
                if (!(component is MonoBehaviour) || !Idle(component)) return false;
            }
            return true;
        }

        sealed class PivotRecord
        {
            public string Name;
            public int Layer, Sibling;
            public Transform Parent, Self;
            public Vector3 Position, Scale;
            public Quaternion Rotation;
            public readonly List<Transform> Children = new List<Transform>();
            public readonly List<Vector3> UpPositions = new List<Vector3>(), UpScales = new List<Vector3>(), FlatPositions = new List<Vector3>(), FlatScales = new List<Vector3>();
            public readonly List<Quaternion> UpRotations = new List<Quaternion>(), FlatRotations = new List<Quaternion>();
        }

        static void Count(Dictionary<string, int> counts, string key)
        {
            int count;
            counts.TryGetValue(key, out count);
            counts[key] = count + 1;
        }

        static void Flatten(Transform enemy, Analysis analysis, HashSet<Transform> referenced, HashSet<Renderer> mergedParts, bool record,
            Dictionary<string, int> kept, ref int pivots, ref int leaves)
        {
            Transform[] all = enemy.GetComponentsInChildren<Transform>(true);
            // Below something that counts, searches or may look at its children (NoChild, Exclude): nothing
            // below it leaves its place.
            var guarded = new HashSet<Transform>();
            foreach (Transform t in all)
            {
                Mark mark;
                bool own = analysis.marks.TryGetValue(t, out mark) && (mark & (Mark.NoChild | Mark.Exclude)) != 0;
                if (own || (t != enemy && guarded.Contains(t.parent))) guarded.Add(t);
            }
            var records = new List<PivotRecord>();
            foreach (Transform t in all)
            {
                if (t == null || t == enemy) continue;
                Transform parent = t.parent;
                string why = null;
                if (!Bare(t, mergedParts)) continue;
                if (analysis.marks.ContainsKey(t)) why = "marked";
                else if (referenced.Contains(t)) why = "referenced";
                else if (analysis.names.Contains(t.name)) why = "named";
                else if (t.gameObject.tag != "Untagged" && t.gameObject.tag != "MeshCombineEnemy") why = "tagged";
                else if (guarded.Contains(parent)) why = "guarded";
                else if (Reasons(analysis, parent).Any(r => r == "ActivateChildren" || r == "DestroyChildren")) why = "parent switches children";
                else if (!t.gameObject.activeSelf) why = "off";
                else if (t.childCount > 0 && !Identity(t))
                    foreach (Transform child in t)
                        if (Driven(analysis, child))
                        {
                            why = "moved child";
                            break;
                        }
                if (why != null)
                {
                    Count(kept, why);
                    continue;
                }
                var pivot = new PivotRecord
                {
                    Name = t.name, Layer = t.gameObject.layer, Sibling = t.GetSiblingIndex(), Parent = parent, Self = t,
                    Position = t.localPosition, Rotation = t.localRotation, Scale = t.localScale,
                };
                var children = new List<Transform>();
                foreach (Transform child in t) children.Add(child);
                var worlds = children.Select(c => c.localToWorldMatrix).ToList();
                int index = t.GetSiblingIndex();
                bool skewed = false;
                for (int i = 0; i < children.Count; i++)
                {
                    Transform child = children[i];
                    pivot.Children.Add(child);
                    pivot.UpPositions.Add(child.localPosition);
                    pivot.UpRotations.Add(child.localRotation);
                    pivot.UpScales.Add(child.localScale);
                    child.SetParent(parent, true);
                    child.SetSiblingIndex(++index);
                    if (!Same(child.localToWorldMatrix, worlds[i])) skewed = true;
                }
                if (skewed)
                {
                    for (int i = 0; i < children.Count; i++)
                    {
                        children[i].SetParent(t, false);
                        children[i].localPosition = pivot.UpPositions[i];
                        children[i].localRotation = pivot.UpRotations[i];
                        children[i].localScale = pivot.UpScales[i];
                    }
                    Count(kept, "skew");
                    continue;
                }
                foreach (Transform child in children)
                {
                    pivot.FlatPositions.Add(child.localPosition);
                    pivot.FlatRotations.Add(child.localRotation);
                    pivot.FlatScales.Add(child.localScale);
                }
                if (children.Count == 0) leaves++;
                else pivots++;
                records.Add(pivot);
                UnityEngine.Object.DestroyImmediate(t.gameObject);
            }
            if (!record || records.Count == 0) return;
            // The record: a child that was a later pivot is named by its index (by reference: the pivots are
            // destroyed by now, and Unity's == would take any two of them for the same).
            var flat = enemy.gameObject.AddComponent<EnemyFlattenRecord>();
            int total = records.Sum(r => r.Children.Count), k = 0;
            flat.names = records.Select(r => r.Name).ToArray();
            flat.layers = records.Select(r => r.Layer).ToArray();
            flat.siblings = records.Select(r => r.Sibling).ToArray();
            flat.parents = records.Select(r => r.Parent).ToArray();
            flat.positions = records.Select(r => r.Position).ToArray();
            flat.rotations = records.Select(r => r.Rotation).ToArray();
            flat.scales = records.Select(r => r.Scale).ToArray();
            flat.childStart = new int[records.Count];
            flat.childCount = new int[records.Count];
            flat.children = new Transform[total];
            flat.childPivot = new int[total];
            flat.upPositions = new Vector3[total];
            flat.upRotations = new Quaternion[total];
            flat.upScales = new Vector3[total];
            flat.flatPositions = new Vector3[total];
            flat.flatRotations = new Quaternion[total];
            flat.flatScales = new Vector3[total];
            for (int i = 0; i < records.Count; i++)
            {
                PivotRecord r = records[i];
                flat.childStart[i] = k;
                flat.childCount[i] = r.Children.Count;
                for (int c = 0; c < r.Children.Count; c++, k++)
                {
                    Transform child = r.Children[c];
                    int later = records.FindIndex(x => ReferenceEquals(x.Self, child));
                    flat.childPivot[k] = later;
                    flat.children[k] = later >= 0 ? null : r.Children[c];
                    flat.upPositions[k] = r.UpPositions[c];
                    flat.upRotations[k] = r.UpRotations[c];
                    flat.upScales[k] = r.UpScales[c];
                    flat.flatPositions[k] = r.FlatPositions[c];
                    flat.flatRotations[k] = r.FlatRotations[c];
                    flat.flatScales[k] = r.FlatScales[c];
                }
            }
        }

        // ---------------------------------------------------------------- debris

        // Scripts that switch objects on, maybe in their Awake (before a holder hands its parts over).
        static readonly HashSet<string> ScriptSwitches = new HashSet<string>
        {
            "RandomVariant", "RandomNumberEnabled", "PMGlobalBoolToState", "LightFlicker", "PlaySourceThenDeactivate",
        };

        static void Debris(Scene scene, Analysis analysis, Dictionary<string, int> kept, ref int subtrees, ref int transforms, ref int variantScripts)
        {
            // The enemies' RandomVariant scripts and their variants (left for the holder's Start).
            var variantScriptList = new List<MonoBehaviour>();
            var variants = new HashSet<Transform>();
            foreach (Transform enemy in analysis.enemies)
            {
                if (enemy == null) continue;
                foreach (MonoBehaviour behaviour in enemy.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || behaviour.GetType().Name != "RandomVariant") continue;
                    variantScriptList.Add(behaviour);
                    var list = Field(behaviour, "variants") as IList;
                    if (list != null) foreach (object item in list) { var go = item as GameObject; if (go != null) variants.Add(go.transform); }
                }
            }
            var parts = new List<Transform>();
            var parents = new List<Transform>();
            var siblings = new List<int>();
            foreach (Transform enemy in analysis.enemies)
            {
                if (enemy == null) continue;
                var tops = new List<Transform>();
                Collect(enemy, tops);
                foreach (Transform top in tops)
                {
                    string why = null;
                    Transform[] below = top.GetComponentsInChildren<Transform>(true);
                    for (Transform up = top.parent; up != null && why == null; up = up.parent)
                    {
                        Mark mark;
                        if (analysis.marks.TryGetValue(up, out mark) && (mark & (Mark.NoChild | Mark.Exclude)) != 0) why = "guarded";
                        else
                        {
                            // ActivateChildren and DestroyChildren act on their target's own children; a recursive
                            // activation on everything below it.
                            bool parent = up == top.parent;
                            string switching = Reasons(analysis, up).FirstOrDefault(r => r == "ActivateGameObjectRecursive" ||
                                                                                     (parent && (r == "ActivateChildren" || r == "DestroyChildren")));
                            if (switching != null) why = switching + "@" + up.name.Replace(' ', '_');
                        }
                        if (up == enemy) break;
                    }
                    if (why == null && below.Length < 2) why = "single";
                    if (why == null)
                        foreach (Transform t in below)
                        {
                            Mark mark;
                            if (variants.Contains(t)) why = "variant";
                            else if (analysis.marks.TryGetValue(t, out mark) && (mark & Mark.Exclude) != 0) why = "excluded";
                            else if (Reasons(analysis, t).Any(ScriptSwitches.Contains)) why = "script";
                            else if (t.GetComponents<MonoBehaviour>().Any(b => b != null && ScriptSwitches.Contains(b.GetType().Name))) why = "script";
                            if (why != null) break;
                        }
                    if (why != null)
                    {
                        Count(kept, why);
                        continue;
                    }
                    parts.Add(top);
                    parents.Add(top.parent);
                    siblings.Add(top.GetSiblingIndex());
                    transforms += below.Length;
                    subtrees++;
                }
            }
            if (parts.Count == 0 && variantScriptList.Count == 0) return;
            var holderObject = new GameObject("CrowEnemyDebris");
            SceneManager.MoveGameObjectToScene(holderObject, scene);
            foreach (Transform part in parts) part.SetParent(holderObject.transform, false);
            var holder = holderObject.AddComponent<EnemyDebrisHolder>();
            holder.parts = parts.ToArray();
            holder.parents = parents.ToArray();
            holder.siblings = siblings.ToArray();
            holder.variants = variantScriptList.ToArray();
            variantScripts = variantScriptList.Count;
        }

        // The topmost inactive transforms below an enemy (its root's own activity doesn't count).
        static void Collect(Transform t, List<Transform> tops)
        {
            foreach (Transform child in t)
            {
                if (!child.gameObject.activeSelf) tops.Add(child);
                else Collect(child, tops);
            }
        }

        // ---------------------------------------------------------------- atlas

        // A texture as the atlas samples it: its source image, wrapped as it wraps, bilinear.
        sealed class Source
        {
            public int Width, Height;
            public Color32[] Pixels;
            public bool ClampU, ClampV, Point;

            public Color Sample(float u, float v)
            {
                if (Point)
                {
                    int px = Wrap(Mathf.FloorToInt(u * Width), Width, ClampU), py = Wrap(Mathf.FloorToInt(v * Height), Height, ClampV);
                    return Pixels[py * Width + px];
                }
                float x = u * Width - 0.5f, y = v * Height - 0.5f;
                int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
                float fx = x - x0, fy = y - y0;
                int xa = Wrap(x0, Width, ClampU), xb = Wrap(x0 + 1, Width, ClampU), ya = Wrap(y0, Height, ClampV), yb = Wrap(y0 + 1, Height, ClampV);
                Color a = Pixels[ya * Width + xa], b = Pixels[ya * Width + xb], c = Pixels[yb * Width + xa], d = Pixels[yb * Width + xb];
                return Color.Lerp(Color.Lerp(a, b, fx), Color.Lerp(c, d, fx), fy);
            }

            static int Wrap(int i, int n, bool clamp)
            {
                if (clamp) return i < 0 ? 0 : i >= n ? n - 1 : i;
                i %= n;
                return i < 0 ? i + n : i;
            }
        }

        static readonly Dictionary<Texture, Source> sources = new Dictionary<Texture, Source>();

        static Source SourceOf(Texture texture, out string why)
        {
            why = null;
            if (texture == null) return null;
            Source known;
            if (sources.TryGetValue(texture, out known))
            {
                if (known == null) why = "unreadable texture " + texture.name;
                return known;
            }
            Texture2D image = null;
            if (texture.wrapModeU != TextureWrapMode.Mirror && texture.wrapModeV != TextureWrapMode.Mirror && texture.wrapModeU != TextureWrapMode.MirrorOnce &&
                texture.wrapModeV != TextureWrapMode.MirrorOnce)
            {
                if (SourceImage != null) image = SourceImage(texture);
                else image = texture as Texture2D;  // at run time: readable or not, GetPixels32 says
            }
            Color32[] pixels = null;
            try
            {
                if (image != null) pixels = image.GetPixels32();
            }
            catch (Exception)
            {
                pixels = null;
            }
            Source source = pixels == null ? null : new Source
            {
                Width = image.width, Height = image.height, Pixels = pixels, ClampU = texture.wrapModeU == TextureWrapMode.Clamp,
                ClampV = texture.wrapModeV == TextureWrapMode.Clamp, Point = texture.filterMode == FilterMode.Point,
            };
            sources[texture] = source;
            if (source == null) why = "unreadable texture " + texture.name;
            return source;
        }

        // A material of a type's merged renderers, and its tile.
        sealed class Tile
        {
            public Material Material;
            public string Key, Why;
            public Source Main, Detail, Gloss, Grid;
            public Vector4 MainST, DetailST, GridST;
            public Color Color;
            public float Metallic, Smoothness, GlossScale;
            public bool HasGlossMap, Detailed;
            public Vector2 Min = new Vector2(float.MaxValue, float.MaxValue), Max = new Vector2(float.MinValue, float.MinValue);
            public int BaseX, BaseY, Width = 1, Height = 1, X, Y;
            public Material Atlas;
            public FilterMode Filter;
        }

        static Vector4 ST(Material material, string property)
        {
            Vector2 scale = material.GetTextureScale(property), offset = material.GetTextureOffset(property);
            return new Vector4(scale.x, scale.y, offset.x, offset.y);
        }

        static string KeywordSet(IEnumerable<string> keywords)
        {
            return string.Join(" ", keywords.Where(k => LitKeywords.Contains(k)).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }

        static string Describe(Material m)
        {
            return m.name.Replace(' ', '_');
        }

        // Whether a material can be a tile, with what it needs; null: yes.
        static string Prepare(Tile tile)
        {
            Material m = tile.Material;
            if (m == null) return "no material";
            string shader = m.shader == null ? "" : m.shader.name;
            if (shader != "CrowVita/Lit" && shader != "Standard") return "shader " + shader;
            if (m.renderQueue > 2500) return "queue";
            var keywords = m.shaderKeywords;
            string other = keywords.FirstOrDefault(k => !AtlasKeywords.Contains(k));
            if (other != null) return "keyword " + other;
            if (m.HasProperty("_Mode") && m.GetFloat("_Mode") > 1.5f) return "blend mode";
            tile.Detailed = keywords.Contains("_DETAIL_MULX2");
            tile.HasGlossMap = keywords.Contains("_METALLICGLOSSMAP");
            if (tile.Detailed && m.HasProperty("_UVSec") && m.GetFloat("_UVSec") > 0.5f) return "detail on uv1";
            if (tile.Detailed && m.HasProperty("_DetailMask") && m.GetTexture("_DetailMask") != null) return "detail mask";
            string why;
            tile.Main = SourceOf(m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null, out why);
            if (why != null) return why;
            tile.MainST = ST(m, "_MainTex");
            if (tile.Detailed)
            {
                tile.Detail = SourceOf(m.GetTexture("_DetailAlbedoMap"), out why);
                if (why != null) return why;
                tile.DetailST = ST(m, "_DetailAlbedoMap");
            }
            if (tile.HasGlossMap)
            {
                tile.Gloss = SourceOf(m.GetTexture("_MetallicGlossMap"), out why);
                if (why != null) return why;
            }
            tile.Color = m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
            tile.Metallic = m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f;
            tile.Smoothness = m.HasProperty("_Glossiness") ? m.GetFloat("_Glossiness") : 0.5f;
            tile.GlossScale = m.HasProperty("_GlossMapScale") ? m.GetFloat("_GlossMapScale") : 1f;
            Texture main = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
            tile.Filter = main != null ? main.filterMode : FilterMode.Bilinear;
            var rest = keywords.Where(k => k != "_DETAIL_MULX2" && k != "_METALLICGLOSSMAP");
            bool cutout = keywords.Contains("_ALPHATEST_ON");
            tile.Key = shader + "|" + m.renderQueue + "|" + KeywordSet(rest) + "|" + (cutout ? m.GetFloat("_Cutoff").ToString("R", CultureInfo.InvariantCulture) : "-") +
                       "|" + Prop(m, "_SrcBlend") + "|" + Prop(m, "_DstBlend") + "|" + Prop(m, "_ZWrite") + "|" + tile.Filter;
            return null;
        }

        static string Prop(Material m, string name)
        {
            return m.HasProperty(name) ? m.GetFloat(name).ToString("R", CultureInfo.InvariantCulture) : "-";
        }

        // The tile's texel grid: the densest of its textures', aligned to that texture's texels, over the
        // range of the tile's UVs; false when it would be too big.
        static bool Grid(Tile tile)
        {
            Source grid = null;
            Vector4 st = Vector4.zero;
            float best = -1f;
            foreach (KeyValuePair<Source, Vector4> option in new[]
                     {
                         new KeyValuePair<Source, Vector4>(tile.Main, tile.MainST), new KeyValuePair<Source, Vector4>(tile.Detail, tile.DetailST),
                         new KeyValuePair<Source, Vector4>(tile.Gloss, tile.MainST),
                     })
            {
                if (option.Key == null) continue;
                float density = Mathf.Abs(option.Value.x * option.Key.Width * option.Value.y * option.Key.Height);
                if (density > best)
                {
                    best = density;
                    grid = option.Key;
                    st = option.Value;
                }
            }
            tile.Grid = grid;
            tile.GridST = st;
            if (grid == null)
            {
                tile.Width = tile.Height = 1;
                return true;
            }
            if (st.x == 0f || st.y == 0f) return false;
            float s0 = Mathf.Min(tile.Min.x * st.x, tile.Max.x * st.x) + st.z, s1 = Mathf.Max(tile.Min.x * st.x, tile.Max.x * st.x) + st.z;
            float t0 = Mathf.Min(tile.Min.y * st.y, tile.Max.y * st.y) + st.w, t1 = Mathf.Max(tile.Min.y * st.y, tile.Max.y * st.y) + st.w;
            tile.BaseX = Mathf.FloorToInt(s0 * grid.Width + 1e-4f);
            tile.BaseY = Mathf.FloorToInt(t0 * grid.Height + 1e-4f);
            tile.Width = Math.Max(1, Mathf.CeilToInt(s1 * grid.Width - 1e-4f) - tile.BaseX);
            tile.Height = Math.Max(1, Mathf.CeilToInt(t1 * grid.Height - 1e-4f) - tile.BaseY);
            return tile.Width <= MaxTile && tile.Height <= MaxTile;
        }

        // The tile's albedo and metallic-smoothness at one of its texels (x, y: from its first texel, the
        // padding outside 0..Width-1).
        static void Texel(Tile tile, int x, int y, out Color albedo, out Color gloss)
        {
            Vector2 uv;
            if (tile.Grid == null) uv = tile.Min.x <= tile.Max.x ? (tile.Min + tile.Max) * 0.5f : Vector2.zero;
            else
            {
                float s = (tile.BaseX + x + 0.5f) / tile.Grid.Width, t = (tile.BaseY + y + 0.5f) / tile.Grid.Height;
                uv = new Vector2((s - tile.GridST.z) / tile.GridST.x, (t - tile.GridST.w) / tile.GridST.y);
            }
            Color main = tile.Main == null ? Color.white : tile.Main.Sample(uv.x * tile.MainST.x + tile.MainST.z, uv.y * tile.MainST.y + tile.MainST.w);
            albedo = new Color(main.r * tile.Color.r, main.g * tile.Color.g, main.b * tile.Color.b, main.a * tile.Color.a);
            if (tile.Detailed)
            {
                // Standard's _DETAIL_MULX2 in gamma space: albedo x detail x 2 (no detail map: grey, x1).
                Color detail = tile.Detail == null ? new Color(0.5f, 0.5f, 0.5f, 1f)
                    : tile.Detail.Sample(uv.x * tile.DetailST.x + tile.DetailST.z, uv.y * tile.DetailST.y + tile.DetailST.w);
                albedo.r *= detail.r * 2f;
                albedo.g *= detail.g * 2f;
                albedo.b *= detail.b * 2f;
            }
            if (tile.HasGlossMap)
            {
                Color map = tile.Gloss == null ? Color.white : tile.Gloss.Sample(uv.x * tile.MainST.x + tile.MainST.z, uv.y * tile.MainST.y + tile.MainST.w);
                gloss = new Color(map.r, 0f, 0f, map.a * tile.GlossScale);
            }
            else gloss = new Color(tile.Metallic, 0f, 0f, tile.Smoothness);
        }

        // An atlas position's UV for a vertex of the tile.
        static Vector2 AtlasUV(Tile tile, Vector2 uv, int width, int height)
        {
            if (tile.Grid == null) return new Vector2((tile.X + 0.5f) / width, (tile.Y + 0.5f) / height);
            float s = uv.x * tile.GridST.x + tile.GridST.z, t = uv.y * tile.GridST.y + tile.GridST.w;
            return new Vector2((tile.X + s * tile.Grid.Width - tile.BaseX) / width, (tile.Y + t * tile.Grid.Height - tile.BaseY) / height);
        }

        // Shelf packing in a power-of-two square-ish atlas, tiles at multiples of 4 (DXT blocks).
        static bool Pack(List<Tile> tiles, out int width, out int height)
        {
            width = height = 0;
            var order = tiles.OrderByDescending(t => t.Height).ThenByDescending(t => t.Width).ToList();
            for (int side = 64; side <= MaxAtlas; side *= 2)
            {
                int x = 0, y = 0, row = 0;
                bool fits = true;
                foreach (Tile tile in order)
                {
                    int w = Up4(tile.Width + 2 * TilePad), h = Up4(tile.Height + 2 * TilePad);
                    if (w > side)
                    {
                        fits = false;
                        break;
                    }
                    if (x + w > side)
                    {
                        x = 0;
                        y += row;
                        row = 0;
                    }
                    tile.X = x + TilePad;
                    tile.Y = y + TilePad;
                    x += w;
                    row = Math.Max(row, h);
                }
                int used = y + row;
                if (!fits || used > side) continue;
                width = side;
                height = 4;
                while (height < used) height *= 2;
                return true;
            }
            return false;
        }

        static int Up4(int n)
        {
            return (n + 3) & ~3;
        }

        static string Atlas(string sceneName, string type, List<GameObject> merged, bool ab)
        {
            var renderers = new List<KeyValuePair<MeshFilter, MeshRenderer>>();
            foreach (GameObject go in merged)
            {
                var filter = go.GetComponent<MeshFilter>();
                var renderer = go.GetComponent<MeshRenderer>();
                if (filter == null || renderer == null || filter.sharedMesh == null) continue;
                if (renderer.sharedMaterials.Length != filter.sharedMesh.subMeshCount) continue;
                renderers.Add(new KeyValuePair<MeshFilter, MeshRenderer>(filter, renderer));
            }
            string head = "CROW_ENEMY_ATLAS scene=" + sceneName + " type=" + type + " renderers=" + renderers.Count;
            if (renderers.Count == 0) return head;
            // The tiles: each material's UV range over its submeshes.
            var tiles = new Dictionary<Material, Tile>();
            int submeshesBefore = 0;
            foreach (KeyValuePair<MeshFilter, MeshRenderer> pair in renderers)
            {
                Mesh mesh = pair.Key.sharedMesh;
                Material[] materials = pair.Value.sharedMaterials;
                submeshesBefore += materials.Length;
                Vector2[] uv = mesh.uv;
                for (int s = 0; s < materials.Length; s++)
                {
                    if (materials[s] == null) continue;
                    Tile tile;
                    if (!tiles.TryGetValue(materials[s], out tile)) tiles[materials[s]] = tile = new Tile { Material = materials[s] };
                    foreach (int v in mesh.GetIndices(s))
                    {
                        Vector2 at = uv.Length > v ? uv[v] : Vector2.zero;
                        tile.Min = Vector2.Min(tile.Min, at);
                        tile.Max = Vector2.Max(tile.Max, at);
                    }
                }
            }
            foreach (Tile tile in tiles.Values)
            {
                tile.Why = tile.Min.x > tile.Max.x ? "no vertices" : Prepare(tile);
                if (tile.Why == null && !Grid(tile)) tile.Why = "tile " + tile.Width + "x" + tile.Height;
            }
            // One filter mode per atlas: the most common one's.
            var candidates = tiles.Values.Where(t => t.Why == null).ToList();
            if (candidates.Count > 0)
            {
                FilterMode common = candidates.GroupBy(t => t.Filter).OrderByDescending(g => g.Count()).First().Key;
                foreach (Tile tile in candidates) if (tile.Filter != common) tile.Why = "filter " + tile.Filter;
            }
            // The groups: materials the base pass draws alike, one atlas material each; metallic and
            // smoothness from the second atlas where they differ (or a map gives them).
            var groups = tiles.Values.Where(t => t.Why == null).GroupBy(t => t.Key).ToList();
            var atlasTiles = new List<Tile>();
            var groupGloss = new Dictionary<string, bool>();
            foreach (IGrouping<string, Tile> group in groups)
            {
                bool gloss = group.Any(t => t.HasGlossMap) || group.Select(t => t.Metallic.ToString("R", CultureInfo.InvariantCulture) + "/" + t.Smoothness.ToString("R", CultureInfo.InvariantCulture)).Distinct().Count() > 1;
                string set = KeywordSet(group.First().Material.shaderKeywords.Where(k => k != "_DETAIL_MULX2" && k != "_METALLICGLOSSMAP").Concat(gloss ? new[] { "_METALLICGLOSSMAP" } : new string[0]));
                if (VariantSets != null && !VariantSets.Contains(set))
                {
                    foreach (Tile tile in group) tile.Why = "no variant " + set.Replace(' ', ',');
                    continue;
                }
                if (group.Count() < 2 && !renderers.Any(p => p.Value.sharedMaterials.Count(m => m != null && tiles.ContainsKey(m) && tiles[m].Key == group.Key) > 1))
                {
                    // A lone material saves no draw.
                    foreach (Tile tile in group) tile.Why = "alone";
                    continue;
                }
                groupGloss[group.Key] = gloss;
                atlasTiles.AddRange(group);
            }
            if (atlasTiles.Count == 0)
                return head + " atlased=0 kept: " + string.Join(" ", tiles.Values.Select(t => Describe(t.Material) + "(" + (t.Why ?? "?").Replace(' ', '_') + ")").ToArray());
            int width, height;
            if (!Pack(atlasTiles, out width, out height)) return head + " atlased=0 (tiles don't fit " + MaxAtlas + ")";
            bool alpha = atlasTiles.Any(t => t.Material.IsKeywordEnabled("_ALPHATEST_ON"));
            bool anyGloss = groupGloss.Values.Any(g => g);
            var albedoPixels = new Color32[width * height];
            var glossPixels = anyGloss ? new Color32[width * height] : null;
            for (int i = 0; i < albedoPixels.Length; i++) albedoPixels[i] = new Color32(0, 0, 0, 255);
            int clamped = 0;
            // Each tile's whole footprint (its padding up to the next multiple of 4 too), so that DXT blocks and
            // the half-size metallic atlas's texels hold one tile each.
            foreach (Tile tile in atlasTiles)
                for (int y = -TilePad; y < Up4(tile.Height + 2 * TilePad) - TilePad; y++)
                    for (int x = -TilePad; x < Up4(tile.Width + 2 * TilePad) - TilePad; x++)
                    {
                        Color albedo, gloss;
                        Texel(tile, x, y, out albedo, out gloss);
                        if (albedo.r > 1.02f || albedo.g > 1.02f || albedo.b > 1.02f) clamped++;
                        int at = (tile.Y + y) * width + tile.X + x;
                        albedoPixels[at] = albedo;
                        if (glossPixels != null) glossPixels[at] = gloss;
                    }
            FilterMode filterMode = atlasTiles[0].Filter;
            var atlas = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "CrowEnemyAtlas " + type, wrapMode = TextureWrapMode.Clamp, filterMode = filterMode,
            };
            atlas.SetPixels32(albedoPixels);
            atlas.Apply(false, false);
            if (CompressTexture != null) CompressTexture(atlas, alpha);
            Unreadable.Add(atlas);
            Texture2D glossAtlas = null;
            if (glossPixels != null)
            {
                // At half size: a tile's metallic and smoothness are mostly one value (a map's at half its
                // resolution), and a texel never spans two tiles (footprints are 4-aligned).
                var half = new Color32[width * height / 4];
                for (int y = 0; y < height / 2; y++)
                    for (int x = 0; x < width / 2; x++)
                    {
                        int r = 0, a = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                Color32 c = glossPixels[(2 * y + dy) * width + 2 * x + dx];
                                r += c.r;
                                a += c.a;
                            }
                        half[y * (width / 2) + x] = new Color32((byte)((r + 2) / 4), 0, 0, (byte)((a + 2) / 4));
                    }
                glossAtlas = new Texture2D(width / 2, height / 2, TextureFormat.RGBA32, false)
                {
                    name = "CrowEnemyAtlas " + type + " metallic", wrapMode = TextureWrapMode.Clamp, filterMode = filterMode,
                };
                glossAtlas.SetPixels32(half);
                glossAtlas.Apply(false, false);
                if (CompressTexture != null) CompressTexture(glossAtlas, true);
                Unreadable.Add(glossAtlas);
            }
            // The atlas materials.
            int made = 0;
            foreach (IGrouping<string, Tile> group in groups)
            {
                bool gloss;
                if (!groupGloss.TryGetValue(group.Key, out gloss)) continue;
                Tile first = group.First();
                var material = new Material(first.Material) { name = "CrowEnemyAtlas " + type + " " + (++made) };
                material.SetTexture("_MainTex", atlas);
                material.SetTextureScale("_MainTex", Vector2.one);
                material.SetTextureOffset("_MainTex", Vector2.zero);
                material.SetColor("_Color", Color.white);
                material.DisableKeyword("_DETAIL_MULX2");
                if (material.HasProperty("_DetailAlbedoMap")) material.SetTexture("_DetailAlbedoMap", null);
                if (gloss)
                {
                    material.EnableKeyword("_METALLICGLOSSMAP");
                    material.SetTexture("_MetallicGlossMap", glossAtlas);
                    material.SetTextureScale("_MetallicGlossMap", Vector2.one);
                    material.SetTextureOffset("_MetallicGlossMap", Vector2.zero);
                    material.SetFloat("_GlossMapScale", 1f);
                }
                else
                {
                    material.DisableKeyword("_METALLICGLOSSMAP");
                    if (material.HasProperty("_MetallicGlossMap")) material.SetTexture("_MetallicGlossMap", null);
                    material.SetFloat("_Metallic", first.Metallic);
                    material.SetFloat("_Glossiness", first.Smoothness);
                }
                foreach (Tile tile in group) tile.Atlas = material;
            }
            // The meshes: UVs to the tiles, the submeshes that share an atlas material as one.
            int changed = 0, submeshesAfter = 0, conflicts = 0;
            long meshBytes = 0;
            foreach (KeyValuePair<MeshFilter, MeshRenderer> pair in renderers)
            {
                Mesh mesh = pair.Key.sharedMesh;
                Material[] materials = pair.Value.sharedMaterials;
                Vector2[] uv = mesh.uv;
                if (uv.Length != mesh.vertexCount) uv = new Vector2[mesh.vertexCount];
                var remapped = (Vector2[])uv.Clone();
                var owner = new Tile[mesh.vertexCount];
                var newMaterials = new List<Material>();
                var newIndices = new List<List<int>>();
                bool conflict = false;
                for (int s = 0; s < materials.Length; s++)
                {
                    int[] indices = mesh.GetIndices(s);
                    Tile tile = materials[s] != null && tiles.ContainsKey(materials[s]) ? tiles[materials[s]] : null;
                    bool atlased = tile != null && tile.Atlas != null;
                    Material target = atlased ? tile.Atlas : materials[s];
                    if (atlased)
                        foreach (int v in indices)
                        {
                            if (owner[v] == null)
                            {
                                owner[v] = tile;
                                remapped[v] = AtlasUV(tile, uv[v], width, height);
                            }
                            else if (owner[v] != tile) conflict = true;
                        }
                    int at = atlased ? newMaterials.IndexOf(target) : -1;
                    if (at < 0)
                    {
                        newMaterials.Add(target);
                        newIndices.Add(new List<int>());
                        at = newMaterials.Count - 1;
                    }
                    newIndices[at].AddRange(indices);
                }
                if (conflict)
                {
                    conflicts++;
                    submeshesAfter += materials.Length;
                    continue;
                }
                if (newMaterials.Count == materials.Length)
                {
                    submeshesAfter += materials.Length;
                    continue;  // no draw saved: the renderer stays as it is
                }
                var rebuilt = new Mesh { name = mesh.name + " atlas" };
                rebuilt.vertices = mesh.vertices;
                if (mesh.normals.Length > 0) rebuilt.normals = mesh.normals;
                if (mesh.tangents.Length > 0) rebuilt.tangents = mesh.tangents;
                if (mesh.colors32.Length > 0) rebuilt.colors32 = mesh.colors32;
                rebuilt.uv = remapped;
                if (mesh.uv2.Length > 0) rebuilt.uv2 = mesh.uv2;
                rebuilt.subMeshCount = newIndices.Count;
                for (int s = 0; s < newIndices.Count; s++) rebuilt.SetTriangles(newIndices[s], s, false);
                rebuilt.bounds = mesh.bounds;
                if (ab)
                {
                    var record = pair.Key.gameObject.AddComponent<EnemyAtlasRecord>();
                    record.originalMesh = mesh;
                    record.originalMaterials = materials;
                    record.atlasMesh = rebuilt;
                    record.atlasMaterials = newMaterials.ToArray();
                    Unreadable.Add(mesh);
                }
                pair.Key.sharedMesh = rebuilt;
                pair.Value.sharedMaterials = newMaterials.ToArray();
                meshBytes += rebuilt.vertexCount * 48L;
                changed++;
                submeshesAfter += newMaterials.Count;
            }
            long textureBytes = (long)width * height / (alpha ? 1 : 2) + (glossAtlas != null ? (long)width * height / 4 : 0);
            return head + " changed=" + changed + " submeshes=" + submeshesBefore + "->" + submeshesAfter + " conflicts=" + conflicts +
                   " materials=" + tiles.Count + " atlased=" + atlasTiles.Count + " groups=" + groupGloss.Count + " (metallic atlas " + groupGloss.Count(g => g.Value) + ")" +
                   " atlas=" + width + "x" + height + (alpha ? " DXT5" : " DXT1") + (glossAtlas != null ? "+DXT5" : "") + " textureBytes=" + textureBytes + " meshBytes=" + meshBytes +
                   " clamped=" + clamped +
                   " tiles: " + string.Join(" ", atlasTiles.Select(t => Describe(t.Material) + "=" + t.Width + "x" + t.Height).ToArray()) +
                   " kept: " + string.Join(" ", tiles.Values.Where(t => t.Atlas == null).Select(t => Describe(t.Material) + "(" + (t.Why ?? "?").Replace(' ', '_') + ")").ToArray());
        }
    }
}
