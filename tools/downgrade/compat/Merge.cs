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

// Static geometry merged per material: small renderers that never move, switch or change on their
// own become one renderer per material, lighting and place. On the PS Vita each renderer costs the
// main thread its base draw, a draw per pixel light on it and a shadow caster draw (switching off
// Roadside's 238 static-batching candidates took 14 ms off the main thread's 36), and the game marks
// nothing static. Most of the areas' static architecture is MeshCombineStudio's already; what is left
// is props, debris and the rigid parts of the game's DOTween-animated characters, crows and machines.
//
// What may merge is read from the scene itself (Analyze): PlayMaker's FSMs (each action's targets,
// through FSM variables where they are static), the game's scripts (rules per class, read from the
// game's code: Known) and the engine's components. Every object gets marks:
//   exclude  it and all below it stay as they are: an Animator or Animation, a joint, an LOD group,
//            a script the rules don't know and what it references, below a GetChild or FindChild whose
//            result an FSM then moves or switches
//   own      its own renderer stays: SetMaterial and the like, RandomMaterial switching at enable,
//            DeactivateRenderer, a VideoPlayer's screen, what a MeshCombiner's own search takes
//            (MeshCombiner.ValidObject: MeshCombineStudio combines it at run time)
//   anchor   it moves, turns, scales, switches on and off or is destroyed as a whole (ActivateGameObject,
//            SetPosition, DOTween..., ActivateChildren on its parent, a Rigidbody, RandomVariant,
//            animation scripts...): what is below it may merge into a renderer that is its child, so
//            that it moves and switches with it
//   nochild  something counts or searches its children (GetChildCount, GetChild...): no merged object
//            goes directly under it
// The game's random transforms, materials and flips that run once in Awake (com.sfbgames' Random*)
// run at build time instead and are switched off, so the merged mesh has the part's pick (the game
// meant this for slow platforms: AbstractRandomElement.ShouldRandomiseAtRuntimeOnSlowPlatforms),
// each with its own seed (PickSeed), so every build picks alike.
// A renderer merges when nothing on it or above it excludes, it has no own mark, its mesh (at most
// MaxPartVertices) renders triangles with opaque or cutout materials and no lightmap, and every
// object between it and its anchor (the nearest anchor above it, or the scene) is active. Parts group
// by anchor, layer, material, shadow and probe settings, vertex channels, the set of point and spot
// lights whose range reaches their bounds (so a merged renderer gets no more light passes than its
// parts had) and an 8 m cell of their bounds' centre. Groups of two or more, those that save most
// renderers per byte first up to MaxBytes per scene, become CrowMerged objects under the anchor (or at
// the scene's root) with one mesh per 65,000 vertices; the parts' renderers are switched off. Their
// objects and colliders stay, so scripts that reference them work as before; with Drop (the build's
// default) the parts' meshes nothing else in the scene uses leave their MeshFilters.
//
// Enemies (the "Enemy <Type>" roots: rigidbodies whose parts the game's FSMs animate) merge their own
// way when Enemies is on (the build's default; CROW_MERGE_ENEMIES=off keeps them as they are; at run time
// -crow-merge-enemies): the parts below each anchor of an enemy become one renderer with a submesh per
// material, whatever their materials, lights and cell (they move); the enemy's MeshCombineStudio combiner,
// which would otherwise combine them per material when the enemy first switches on, is switched off; and
// its random materials, rotations and scales are picked once at build time even where the game picks them
// again at each OnEnable (a fixed look, the same in every build, accepted for the PS Vita). Their see-through
// parts (materials past the opaque queue) merge too: each submesh still draws in its material's queue, and the
// opaque body drawn before hides the parts behind it, so what changes is only the order in which overlapping
// parts of one renderer blend (the Lumber's 150-odd skin blobs, the Blob's slime, the Puddle's). Their own
// budget is EnemyMaxBytes per scene, their part size EnemyMaxPartVertices. When the build drops the parts' meshes
// (CROW_MERGE=on) it also removes the enemies' merged parts that are bare leaves (Strip below; not with
// CROW_MERGE_ENEMY_STRIP=off).
//
// The PS Vita build merges while it writes each scene (DowngradeBuild's CrowMergeScenes calls
// Process); other players can merge at run time, from readable meshes only.
//   -crow-merge report|on|off|merged  report: Analyze each area once it has loaded and log what would
//                               merge (CROW_MERGE lines: candidates, parts and groups, what leaves
//                               renderers out); on: merge at run time as the build does; off: the
//                               build's merged renderers off and their parts on (a build made with
//                               CROW_MERGE=ab, which keeps the parts' meshes), with the enemies'
//                               MeshCombineStudio combiners back on; merged: back (not the combiners).
//                               -crow-cycle takes merge=<value> (now, in the loaded scenes).
//   -crow-merge-cell <m>, -crow-merge-max-vertices <n>, -crow-merge-max-kb <n>, -crow-merge-drop
//                               the run-time merge's cell, part size, memory budget, and Drop
//   -crow-merge-enemies         the enemy mode at run time (the build's default)
//   (The build with CROW_MERGE=on also strips the enemies: the merged enemy parts that are bare
//                               leaves (a Transform, MeshFilter and MeshRenderer, inert scripts and
//                               random picks made once only; no children, no tag but MeshCombineEnemy)
//                               and that nothing in the scene names (no FSM's object or string, no mark
//                               of the analysis) destroyed, so the enemies' hierarchies are smaller.)
//   -crow-merge-materials       the report lists the enemies' merged materials (CROW_MERGE_MATERIALS=1
//                               for the build)
//   -crow-merge-check           every second, each merged renderer's parts: still there, active with
//                               it, where they were relative to it, their own renderers off, their
//                               materials the same; the parts
//                               that differ are logged once each (CROW_MERGE_CHECK)
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    // The merged renderer's parts, so a run can switch back to them (-crow-merge off).
    public class MergedRenderer : MonoBehaviour
    {
        public Renderer[] parts;
        // Each part's pose relative to the merged object as it was merged (PlayerMerge.cs' check).
        public Matrix4x4[] poses;
    }

    // -crow-merge-check: every second, whether each merged renderer still stands for its parts: each
    // part still there, active exactly when the merged object is, where it was relative to it (from
    // the first look after its scene loaded), its own renderer still off and its materials the same.
    // CROW_MERGE_CHECK lines name the parts that differ (once each) and count the checks.
    static class MergeCheck
    {
        static readonly Dictionary<Renderer, Matrix4x4> relative = new Dictionary<Renderer, Matrix4x4>();
        static readonly Dictionary<Renderer, Material[]> materials = new Dictionary<Renderer, Material[]>();
        static readonly HashSet<Renderer> reported = new HashSet<Renderer>();
        static float next, lastSummary;
        static int checks, differences;

        internal static void Install()
        {
            CrowEvents.BeforeRender("MergeCheck.Tick", Tick);
            CrowEvents.Loaded("MergeCheck.sceneLoaded", (scene, mode) =>
            {
                relative.Clear();
                materials.Clear();
                next = Time.realtimeSinceStartup + 3f;
            });
        }

        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < next) return;
            next = now + 1f;
            int groups = 0;
            foreach (MergedRenderer group in Resources.FindObjectsOfTypeAll<MergedRenderer>())
            {
                if (group == null || !group.gameObject.scene.IsValid() || group.parts == null) continue;
                var merged = group.GetComponent<MeshRenderer>();
                if (merged == null || !merged.enabled) continue;  // -crow-merge off
                groups++;
                bool active = group.gameObject.activeInHierarchy;
                foreach (Renderer part in group.parts)
                {
                    checks++;
                    string why = null;
                    if (part == null) why = active ? "destroyed while the merged object is active" : null;
                    else
                    {
                        Matrix4x4 now2 = group.transform.worldToLocalMatrix * part.transform.localToWorldMatrix;
                        Matrix4x4 first;
                        if (!relative.TryGetValue(part, out first)) relative[part] = first = now2;
                        Material[] looks;
                        if (!materials.TryGetValue(part, out looks)) materials[part] = looks = part.sharedMaterials;
                        float moved = 0f;
                        for (int i = 0; i < 16; i++) moved = Math.Max(moved, Math.Abs(now2[i] - first[i]));
                        if (part.gameObject.activeInHierarchy != active) why = "active=" + part.gameObject.activeInHierarchy + " merged=" + active;
                        else if (moved > 0.001f) why = "moved " + moved.ToString("0.###", CultureInfo.InvariantCulture);
                        else if (part.enabled) why = "its renderer is on";
                        else if (!part.sharedMaterials.SequenceEqual(looks)) why = "material " + string.Join(",", part.sharedMaterials.Select(m => m == null ? "null" : m.name).ToArray());
                    }
                    if (why == null) continue;
                    differences++;
                    if (!reported.Add(part != null ? part : (Renderer)merged)) continue;  // once each (a destroyed part: once per group)
                    Debug.Log("CROW_MERGE_CHECK " + (part == null ? Hierarchy.Path(group.transform, 4) + " part" : Hierarchy.Path(part.transform, 5)) + ": " + why);
                }
            }
            if (now < lastSummary + 10f) return;
            lastSummary = now;
            Debug.Log("CROW_MERGE_CHECK groups=" + groups + " checks=" + checks + " differences=" + differences);
        }
    }

    public static partial class Merge
    {
        public static float Cell = 8f;
        // Parts with bigger meshes stay as they are: a big mesh is one draw already, and merged copies of
        // a shape many objects share add memory.
        public static int MaxPartVertices = 500;
        // The parts' own meshes dropped from their MeshFilters where nothing else in the scene uses them
        // (the build: their data stays out of it). -crow-merge off can't switch those parts back.
        public static bool Drop;
        // The merged meshes' memory in one scene, at most: the groups that save most renderers per byte
        // first (the big areas have little main memory to spare: Ocean Kingdom dips to 7-9 MB free
        // while it loads).
        public static long MaxBytes = 1536L << 10;
        const int MaxVertices = 65000;
        // The enemy mode (see above): in the editor (the build) on unless CROW_MERGE_ENEMIES=off.
        public static bool Enemies = Application.isEditor && Environment.GetEnvironmentVariable("CROW_MERGE_ENEMIES") != "off";
        public static long EnemyMaxBytes = 8L << 20;
        public static int EnemyMaxPartVertices = 4000;
        static string mode;

        internal static void Install(string[] args)
        {
            mode = CrowFlags.Value(args, "-crow-merge");
            Cell = CrowFlags.Number(args, "-crow-merge-cell", Cell);
            MaxPartVertices = CrowFlags.Int(args, "-crow-merge-max-vertices", MaxPartVertices);
            if (CrowFlags.Has(args, "-crow-merge-check")) MergeCheck.Install();
            if (CrowFlags.Has(args, "-crow-merge-drop")) Drop = true;
            if (CrowFlags.Has(args, "-crow-merge-enemies")) Enemies = true;
            int kilobytes = CrowFlags.Int(args, "-crow-merge-max-kb", -1);
            if (kilobytes >= 0) MaxBytes = kilobytes * 1024L;
            InstallPlayer(args);  // PlayerMerge.cs
            if (mode == null) return;
            CrowEvents.Loaded("Merge.sceneLoaded", (scene, loadMode) =>
            {
                if (!Scenes.IsArea(scene)) return;
                Set(mode, scene);
            });
        }

        // -crow-cycle merge=on|off|report: now, in every loaded scene.
        internal static void Set(string value)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.name != MaraScene.Scene && scene.name != Scenes.Kept) Set(value, scene);
            }
        }

        static void Set(string value, Scene scene)
        {
            try
            {
                if (value == "off" || value == "back") Switch(scene, false);
                else if (value == "merged") Switch(scene, true);
                else if (value == "report" || value == "on")
                {
                    string report = Process(scene, value == "on", null, false);
                    foreach (string line in report.Split('\n')) if (line.Length > 0) Debug.Log(line);
                }
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_MERGE failed: " + exception);
            }
        }

        // The build's merged renderers off and their parts on (or back).
        static void Switch(Scene scene, bool merged)
        {
            int groups = 0, parts = 0;
            foreach (MergedRenderer group in Resources.FindObjectsOfTypeAll<MergedRenderer>())
            {
                if (group == null || group.gameObject.scene != scene) continue;
                var renderer = group.GetComponent<MeshRenderer>();
                if (renderer != null) renderer.enabled = merged;
                groups++;
                if (group.parts == null) continue;
                foreach (Renderer part in group.parts)
                    if (part != null)
                    {
                        part.enabled = !merged;
                        parts++;
                    }
            }
            // Off: the enemies' MeshCombineStudio combiners the build switched off back on too (before
            // their Start they combine as the game's do; once on, merged again doesn't undo that).
            int combiners = 0;
            Type combinerType = GameTypes.MeshCombiner;
            if (!merged && combinerType != null)
            {
                var analysis = new Analysis();
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (Rigidbody body in root.GetComponentsInChildren<Rigidbody>(true))
                        if (EnemyNames.Named(body.name)) analysis.enemies.Add(body.transform);
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (Component combiner in root.GetComponentsInChildren(combinerType, true))
                    {
                        if (((Behaviour)combiner).enabled || !EnemyCombiner(combiner, SearchParents(combiner), analysis)) continue;
                        ((Behaviour)combiner).enabled = true;
                        combiners++;
                    }
            }
            Debug.Log("CROW_MERGE " + scene.name.Replace(' ', '_') + " " + (merged ? "merged" : "off") + ": " + groups + " merged renderers, " + parts + " parts" +
                      (merged ? "" : ", " + combiners + " enemy combiners"));
        }

        // ---------------------------------------------------------------- analysis

        [Flags]
        enum Mark
        {
            None = 0,
            Exclude = 1,
            Own = 2,
            Anchor = 4,
            NoChild = 8,  // something counts or searches its children: no merged object goes directly under it
        }

        sealed class Analysis
        {
            public readonly Dictionary<Transform, Mark> marks = new Dictionary<Transform, Mark>();
            public readonly Dictionary<Transform, string> why = new Dictionary<Transform, string>();
            public readonly Dictionary<string, int> unknownTargets = new Dictionary<string, int>();
            // Global GameObject variables: what the scene's FSMs (SetGameObject into a variable the FSM
            // doesn't own) and SetPMGlobalGameObject scripts put in each, and what the FSMs do through
            // each (resolved once every FSM is read: the FSM that moves ElevatorRoom isn't the one that
            // stores it). A global use left without a value is counted in unresolved.
            public readonly Dictionary<string, List<object>> globalValues = new Dictionary<string, List<object>>();
            public readonly List<KeyValuePair<string, KeyValuePair<Mark, string>>> globalUses = new List<KeyValuePair<string, KeyValuePair<Mark, string>>>();
            public readonly Dictionary<string, int> unresolved = new Dictionary<string, int>();

            public void GlobalValue(string name, IEnumerable<object> values)
            {
                List<object> list;
                if (!globalValues.TryGetValue(name, out list)) globalValues[name] = list = new List<object>();
                list.AddRange(values.Where(v => v != null));
            }

            public void ResolveGlobals()
            {
                foreach (KeyValuePair<string, KeyValuePair<Mark, string>> use in globalUses)
                {
                    List<object> values;
                    if (globalValues.TryGetValue(use.Key, out values) && values.Count > 0)
                        foreach (object value in values)
                        {
                            if (MoverDebug && use.Value.Key == Mark.Anchor)
                            {
                                var go = value as GameObject;
                                var c = value as Component;
                                Transform t = go != null ? go.transform : c != null ? c.transform : null;
                                if (t != null) Debug.Log("CROW_SPLIT_GLOBAL " + use.Key + " -> " + Hierarchy.Path(t, 4) + " (" + t.GetComponentsInChildren<Transform>(true).Length + ") " + use.Value.Value);
                            }
                            Add(value, use.Value.Key, use.Value.Value);
                        }
                    else if (use.Value.Key == Mark.Anchor)
                    {
                        int count;
                        string key = use.Key + ":" + use.Value.Value;
                        unresolved.TryGetValue(key, out count);
                        unresolved[key] = count + 1;
                    }
                }
            }
            public int fsms, actions, globals, baked, combined;
            // The enemy mode: the enemies' roots, and the MeshCombiners that search only enemies (Process
            // switches them off when it merges, before their Start combines).
            public readonly HashSet<Transform> enemies = new HashSet<Transform>();
            public readonly List<Behaviour> enemyCombiners = new List<Behaviour>();
            // Strip's: every object (as its transform) and string any FSM of the scene names.
            public readonly HashSet<Transform> referenced = new HashSet<Transform>();
            public readonly HashSet<string> names = new HashSet<string>();

            public bool InEnemy(Transform t)
            {
                return EnemyOf(t) != null;
            }

            // Whether an anchor's reason moves, turns or scales it (switching it, destroying it or a rigidbody's physics don't;
            // nor do the animation scripts, which Secondary judges before they are anchors).
            static bool Motion(string reason)
            {
                if (reason.StartsWith("DOTweenTransform", StringComparison.Ordinal) || reason.StartsWith("DOTweenRigidbody", StringComparison.Ordinal))
                    return true;
                if (reason.StartsWith("SetProperty:", StringComparison.Ordinal))
                {
                    string property = reason.ToLowerInvariant();
                    return property.Contains("position") || property.Contains("rotation") || property.Contains("scale") || property.Contains("euler");
                }
                return MotionReasons.Contains(reason);
            }

            static readonly HashSet<string> MotionReasons = new HashSet<string>
            {
                "SetPosition", "SetRotation", "SetScale", "Rotate", "LookAt", "SmoothLookAt", "SmoothLookAt2d", "SmoothLookAtDirection",
                "SmoothLookAtWithLimit", "MoveTowards", "SetRandomRotation", "SpinGameObject", "SetRotationEveryFrame", "RotateAroundAxis",
                "TransformLock",
            };

            // An enemy's or a body's (Secondary): merged the enemies' way.
            public bool EnemyLike(Transform t)
            {
                if (EnemyOf(t) != null) return true;
                if (bodies.Count == 0) return false;
                for (; t != null; t = t.parent)
                    if (bodies.Contains(t)) return true;
                return false;
            }

            // The enemy root t is in, or null.
            public Transform EnemyOf(Transform t)
            {
                if (enemies.Count == 0) return null;
                for (; t != null; t = t.parent)
                    if (enemies.Contains(t)) return t;
                return null;
            }
            // Animation scripts PlatformSpecificAnimation switches off for good on the PS Vita.
            public readonly HashSet<Component> stillAnimations = new HashSet<Component>();
            // The animation scripts (Rotation, Scale, Position) and their targets, judged once the analysis knows what
            // moves (Secondary); the scripts other scripts switch on; what moves for any other reason; those switched off.
            public readonly List<KeyValuePair<Component, Transform>> animations = new List<KeyValuePair<Component, Transform>>();
            public readonly HashSet<object> stateDriven = new HashSet<object>();
            public readonly HashSet<Transform> movers = new HashSet<Transform>();
            public readonly HashSet<Transform> bodies = new HashSet<Transform>();
            public int secondary;

            public void Add(Transform transform, Mark mark, string reason)
            {
                if (transform == null) return;
                if ((mark & Mark.Anchor) != 0 && Motion(reason)) movers.Add(transform);
                Mark known;
                marks.TryGetValue(transform, out known);
                if ((known & mark) == mark) return;
                marks[transform] = known | mark;
                string before;
                why[transform] = why.TryGetValue(transform, out before) ? before + "+" + reason : reason;
            }

            public void Add(object target, Mark mark, string reason)
            {
                var go = target as GameObject;
                var component = target as Component;
                Add(go != null ? go.transform : component != null ? component.transform : null, mark, reason);
            }

            public void Unknown(string what)
            {
                int count;
                unknownTargets.TryGetValue(what, out count);
                unknownTargets[what] = count + 1;
            }
        }

        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static object Field(object target, string name)
        {
            if (target == null) return null;
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(target);
            }
            return null;
        }

        // Scripts that change nothing the merge depends on (no transform, activity, renderer or
        // material of their object or what they reference), by class name.
        // Most are empty in the player (editor tools: PrimitivePlusMaterial, MaterialCombiner,
        // OptimiseSetup), do nothing (RuntimeStatic; RandomDeactivate's DoRandom hides the base's
        // instead of overriding it, so it never runs; ManualRandomiser's Awake and OnEnable are empty)
        // or are editor helpers (FindOddNegativeScaling) and bookkeeping (MeshCombinerData,
        // GarbageCollectMesh); PlayMaker's event proxies (PlayMaker* but PlayMakerFSM) only forward
        // events to the FSMs.
        static readonly HashSet<string> InertScripts = new HashSet<string>
        {
            "PrimitivePlusMaterial", "MaterialCombiner", "OptimiseSetup", "RuntimeStatic", "RandomDeactivate", "ManualRandomiser",
            "FindOddNegativeScaling", "MeshCombinerData", "GarbageCollectMesh", "LocalisePlayMakerFSM",
            "ProBuilderMesh", "Entity", "EntityBehaviour", "ColliderBehaviour", "TriggerBehaviour", "PolyShape", "BezierShape",
            "PlayMakerUnity2DProxy", "PlayMakerFixedUpdate", "PlayMakerOnGUI", "PlayMakerGUI", "PlayMakerGlobals",
            "MergedRenderer", "MeshCombiner",
            "EnemyAtlasRecord", "EnemyFlattenRecord", "EnemyDebrisHolder",  // EnemyRebuild.cs' records
        };

        // PlayMaker actions, by what they do to the objects they act on (their FsmOwnerDefault
        // parameters, and FsmGameObject parameters named gameObject).
        static readonly HashSet<string> AnchorActions = new HashSet<string>
        {
            "ActivateGameObject", "DestroyObject", "DestroySelf", "SetPosition", "SetRotation", "SetScale", "Rotate", "LookAt",
            "SmoothLookAt", "SmoothLookAt2d", "SmoothLookAtDirection", "SmoothLookAtWithLimit", "MoveTowards", "SetRandomRotation",
            "DOTweenRigidbodyMove", "AddForce", "AddExplosionForce", "SetVelocity", "SetAngularVelocity", "SetIsKinematic",
            "UseGravity", "SetParent", "PMActivateMara",
        };
        static readonly HashSet<string> OwnActions = new HashSet<string> { "SetMaterial", "SetRandomMaterial", "ActivateComponent", "DestroyComponent" };
        static readonly HashSet<string> ChildActions = new HashSet<string>
        {
            "ActivateChildren", "DestroyChildren", "GetChild", "GetChildCount", "FindChild", "GameObjectHasChildren",
        };
        // Actions that store a GameObject found at run time in a variable.
        static readonly HashSet<string> FindingActions = new HashSet<string>
        {
            "Raycast", "Raycast2IgnoreTriggers", "RaycastAdvanced", "TriggerEvent", "CollisionEvent", "GetProperty", "WorldToScreenPoint",
        };

        // HutongGames.PlayMaker.ParamDataType values.
        const int ObjectReference = 5, FsmBool = 17, FsmGameObject = 19, FsmOwnerDefault = 20, FsmObject = 24, GameObjectParam = 27, FsmProperty = 36,
            FsmArray = 41;

        static string ShortName(string full)
        {
            return full == null ? "" : full.Substring(full.LastIndexOf('.') + 1);
        }

        static List<GameObject> Roots(Scene scene)
        {
            var roots = new List<GameObject>();
            scene.GetRootGameObjects(roots);
            return roots;
        }

        static Analysis Analyze(Scene scene, Dictionary<string, int> scripts, bool bake)
        {
            return Analyze(Roots(scene), scripts, bake);
        }

        // The same over any set of roots (PlayerMerge.cs: the player's with the area's).
        static Analysis Analyze(List<GameObject> roots, Dictionary<string, int> scripts, bool bake)
        {
            var analysis = new Analysis();
            Type combinerType = GameTypes.MeshCombiner;
            if (Enemies)
                foreach (GameObject root in roots)
                    foreach (Rigidbody body in root.GetComponentsInChildren<Rigidbody>(true))
                        if (EnemyNames.Named(body.name)) analysis.enemies.Add(body.transform);
            // PlatformSpecificAnimation (PatchPlatformAnimation): the PS Vita takes the Switch's choice
            // (onNS), so the animation scripts it lists stay off, unless an AnimationState lists them.
            if (bake || Application.platform == RuntimePlatform.PSP2)
            {
                HashSet<object> stateDriven = analysis.stateDriven;
                foreach (GameObject root in roots)
                    foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (behaviour == null) continue;
                        string type = behaviour.GetType().Name;
                        var list = Field(behaviour, "animations") as IList;
                        if (type == "AnimationState" && list != null) foreach (object item in list) if (item != null) stateDriven.Add(item);
                        // Behaviours other scripts switch on.
                        if (type == "RandomDeactivateBehaviour" && Field(behaviour, "target") != null) stateDriven.Add(Field(behaviour, "target"));
                        if (type == "EnableBehaviourAfter" && Field(behaviour, "behaviour") != null) stateDriven.Add(Field(behaviour, "behaviour"));
                    }
                foreach (GameObject root in roots)
                    foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (behaviour == null || behaviour.GetType().Name != "PlatformSpecificAnimation" || (bool)(Field(behaviour, "onNS") ?? true)) continue;
                        var list = Field(behaviour, "animationScripts") as IList;
                        if (list == null) continue;
                        foreach (object item in list)
                        {
                            var script = item as Component;
                            if (script != null && !stateDriven.Contains(script)) analysis.stillAnimations.Add(script);
                        }
                    }
            }
            foreach (GameObject root in roots)
                foreach (Component component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component is Transform) continue;
                    string name = component.GetType().Name;
                    Transform transform = component.transform;
                    if (name == "Animator" || name == "Animation" || name == "LODGroup" || name == "Cloth" || name.EndsWith("Joint"))
                        analysis.Add(transform, Mark.Exclude, name);
                    else if (component is Rigidbody) analysis.Add(transform, Mark.Anchor, "Rigidbody");
                    else if (component is SkinnedMeshRenderer || component is ParticleSystem) analysis.Add(transform, Mark.Own, name);
                    if (!(component is MonoBehaviour)) continue;
                    if (name == "PlayMakerFSM")
                    {
                        AnalyzeFsm(component, analysis);
                        continue;
                    }
                    if (combinerType != null && combinerType.IsInstanceOfType(component))
                    {
                        Combiner(component, analysis);
                        continue;
                    }
                    if (InertScripts.Contains(name) || (name.StartsWith("PlayMaker") && name != "PlayMakerFSM")) continue;
                    // MeshCombineStudio's bookkeeping on its combined objects (a run-time merge of enemies it
                    // combined already).
                    if (name == "CachedComponents" && analysis.InEnemy(transform)) continue;
                    if (Known(component, name, analysis, bake)) continue;
                    if (playerRules && KnownPlayer(component, name, analysis)) continue;  // PlayerMerge.cs
                    // Any other script: it, and what it references, stay as they are.
                    analysis.Add(transform, Mark.Exclude, name);
                    foreach (UnityEngine.Object reference in References(component))
                        analysis.Add(reference, Mark.Exclude, "ref:" + name);
                    int count;
                    scripts.TryGetValue(name, out count);
                    scripts[name] = count + 1;
                }
            analysis.ResolveGlobals();
            Secondary(analysis, bake);
            return analysis;
        }

        // A random pick's seed, from where its script is: the scene's path, each object's name from the scene's root
        // down with its place among the siblings of that name, and the script's type with its place among the
        // object's scripts of that type (FNV-1a). Every pick the build makes (the game's DoRandom below, unity/
        // VariantBake.cs' variants) seeds UnityEngine.Random with its own, so two builds of the same project pick
        // alike, and a change elsewhere in the scene leaves the other picks as they were. Before, the picks drew on
        // the editor's unseeded Random, and each build picked differently (26 Oct, LUMBER).
        public static int PickSeed(Component component)
        {
            var key = new StringBuilder(component.gameObject.scene.path);
            var path = new List<string>();
            for (Transform t = component.transform; t != null; t = t.parent)
            {
                int same = 0;
                if (t.parent != null)
                {
                    for (int i = 0; i < t.GetSiblingIndex(); i++)
                        if (t.parent.GetChild(i).name == t.name) same++;
                }
                else
                {
                    foreach (GameObject root in t.gameObject.scene.GetRootGameObjects())
                    {
                        if (root.transform == t) break;
                        if (root.name == t.name) same++;
                    }
                }
                path.Add(t.name + "#" + same);
            }
            for (int i = path.Count - 1; i >= 0; i--) key.Append('/').Append(path[i]);
            Component[] kind = component.GetComponents(component.GetType());
            key.Append('|').Append(component.GetType().Name).Append('#').Append(Array.IndexOf(kind, component));
            uint hash = 2166136261;
            foreach (char c in key.ToString())
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)hash;
        }

        // The game's random pick (its DoRandom, which draws on UnityEngine.Random) made with the script's PickSeed;
        // the editor's own Random state is left as it was.
        static void SeededPick(Component component, MethodInfo random)
        {
            UnityEngine.Random.State state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(PickSeed(component));
            try
            {
                random.Invoke(component, null);
            }
            finally
            {
                UnityEngine.Random.state = state;
            }
        }

        // Secondary motion (30 Oct, BOSS): with bake (the PS Vita build), an animation script (Rotation, Scale, Position)
        // whose target is below a rigidbody (not the rigidbody itself) that isn't an enemy's or in one (their merge has
        // rules of its own), below something in the rigidbody (or the rigidbody) that moves, turns or scales it there (a
        // DOTween move, SetPosition, LookAt...; not a switch, a destroy, another animation script or a mover outside the
        // rigidbody), and that holds only small parts (meshes of at most MaxPartVertices), is switched off for good, as
        // PlatformSpecificAnimation does for the crows' skin blobs on the Switch, so its parts merge (the PS Vita draws each
        // renderer for 35-45 us of the main thread). Pool's boss: its hair, pustules, splat balls and fingers wobble on
        // hundreds of scripts, each its own renderer. The rigidbody then merges as an enemy does: per anchor across materials,
        // its see-through parts too, in the enemies' budget (the boss's parts, about 2 MB, lost to Pool's level in the
        // level's). The player's are left (PlayerMerge's rules). The other animation scripts are anchors.
        static void Secondary(Analysis analysis, bool bake)
        {
            var still = new List<KeyValuePair<Component, Transform>>();
            foreach (KeyValuePair<Component, Transform> animation in analysis.animations)
            {
                if (!bake || playerRules || analysis.stateDriven.Contains(animation.Key) || !Small(animation.Value)) continue;
                Transform body = animation.Value;
                while (body != null && body.GetComponent<Rigidbody>() == null) body = body.parent;
                if (body == null || body == animation.Value || EnemyNames.RootOf(body) != null || !Moved(animation.Value.parent, body, analysis)) continue;
                still.Add(animation);
                analysis.bodies.Add(body);
            }
            foreach (KeyValuePair<Component, Transform> animation in still) ((Behaviour)animation.Key).enabled = false;
            foreach (KeyValuePair<Component, Transform> animation in analysis.animations.Except(still))
                analysis.Add(animation.Value, Mark.Anchor, animation.Key.GetType().Name);
            analysis.secondary = still.Count;
        }

        // Whether something moves, turns or scales t or one of its parents up to the body (t is in the body).
        static bool Moved(Transform t, Transform body, Analysis analysis)
        {
            for (; t != null; t = t.parent)
            {
                if (analysis.movers.Contains(t)) return true;
                if (t == body) return false;
            }
            return false;
        }

        static bool Small(Transform target)
        {
            MeshFilter[] filters = target.GetComponentsInChildren<MeshFilter>(true);
            return filters.Length > 0 && filters.All(f => f.sharedMesh == null || f.sharedMesh.vertexCount <= MaxPartVertices);
        }

        // A random pick made at Awake made now, at build time, and the script's Awake pick off: false when the script
        // can't be run so (no DoRandom or onAwake).
        static bool BakeAwakePick(Component component, Analysis analysis)
        {
            MethodInfo random = component.GetType().GetMethod("DoRandom", Instance);
            FieldInfo awake = component.GetType().GetField("onAwake", Instance);
            if (random == null || awake == null) return false;
            SeededPick(component, random);
            awake.SetValue(component, false);
            analysis.baked++;
            return true;
        }

        // The enemy mode: a random pick on an enemy's part made once (at build time; at run time the game's
        // pick stays) and the script's picks at Awake and OnEnable off. False when not an enemy's or the
        // script can't be run.
        static bool EnemyPick(Component component, Transform transform, Analysis analysis, bool bake)
        {
            if (!analysis.InEnemy(transform)) return false;
            MethodInfo random = component.GetType().GetMethod("DoRandom", Instance);
            FieldInfo awake = component.GetType().GetField("onAwake", Instance), enable = component.GetType().GetField("onEnable", Instance);
            if (random == null || awake == null) return false;
            if (bake) SeededPick(component, random);
            awake.SetValue(component, false);
            if (enable != null) enable.SetValue(component, false);
            analysis.baked++;
            return true;
        }

        // A MeshCombiner's renderers: what its own search (MeshCombiner.ValidObject with its search
        // conditions: active, enabled, static, layer, tag, components, size and vertex limits) takes,
        // active or not, combining at run time or not, stays for it. When the search can't be asked,
        // all below its search parents stays.
        static MethodInfo validObject;
        static Type objectType;

        // A MeshCombiner's search parents (its searchOptions' parent and parentGOs).
        static List<GameObject> SearchParents(Component combiner)
        {
            object options = Field(combiner, "searchOptions");
            var parents = new List<GameObject>();
            var one = Field(options, "parent") as GameObject;
            if (one != null) parents.Add(one);
            var many = Field(options, "parentGOs") as GameObject[];
            if (many != null) parents.AddRange(many.Where(p => p != null));
            return parents;
        }

        // The enemy mode: a combiner on an enemy, or searching only enemies.
        static bool EnemyCombiner(Component combiner, List<GameObject> parents, Analysis analysis)
        {
            if (analysis.enemies.Count == 0) return false;
            if (analysis.InEnemy(combiner.transform)) return true;
            return parents.Count > 0 && parents.All(p => analysis.InEnemy(p.transform));
        }

        static void Combiner(Component combiner, Analysis analysis)
        {
            List<GameObject> parents = SearchParents(combiner);
            if (EnemyCombiner(combiner, parents, analysis))
            {
                analysis.enemyCombiners.Add((Behaviour)combiner);  // the merge's to replace
                return;
            }
            if (validObject == null)
            {
                validObject = combiner.GetType().GetMethod("ValidObject", Instance);
                objectType = combiner.GetType().GetNestedType("ObjectType", BindingFlags.Public | BindingFlags.NonPublic);
            }
            foreach (GameObject parent in parents.Distinct())
            {
                if (validObject == null || objectType == null)
                {
                    analysis.Add(parent.transform, Mark.Exclude, "MeshCombiner");
                    continue;
                }
                foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
                {
                    if (t.GetComponent<MeshRenderer>() == null) continue;
                    var arguments = new object[] { parent.transform, t, Enum.ToObject(objectType, 0), true, null };
                    int valid;
                    // It logs an error for each unreadable mesh it meets (and skips it, as it will at run
                    // time): not this search's business, and the build counts errors.
                    bool logging = Debug.unityLogger.logEnabled;
                    Debug.unityLogger.logEnabled = false;
                    try
                    {
                        valid = (int)validObject.Invoke(combiner, arguments);
                    }
                    catch (Exception)
                    {
                        valid = 1;
                    }
                    finally
                    {
                        Debug.unityLogger.logEnabled = logging;
                    }
                    if (valid == 1)
                    {
                        analysis.Add(t, Mark.Own, "MeshCombiner");
                        analysis.combined++;
                    }
                }
            }
        }

        // The game's scripts the rules know (com.sfbgames.general.transform and others, read from the
        // game's code). The random transforms run once in Awake (onAwake) or at every OnEnable: with
        // bake (the build), those that run in Awake only run now and are switched off, so that the
        // merged mesh has the random rotation or scale the part will have (the game meant to do this
        // on slow platforms: AbstractRandomElement.ShouldRandomiseAtRuntimeOnSlowPlatforms), and every
        // build makes the same pick (PickSeed); at run time they have run already. Returns whether it knew it.
        static bool Known(Component component, string name, Analysis analysis, bool bake)
        {
            Transform transform = component.transform;
            switch (name)
            {
                case "RandomRotation":
                case "RandomScale":
                case "RandomRotationState":
                {
                    var target = Field(component, "target") as Transform;
                    if (target == null) target = transform;
                    bool onAwake = (bool)(Field(component, "onAwake") ?? true), onEnable = (bool)(Field(component, "onEnable") ?? false);
                    if (EnemyPick(component, transform, analysis, bake)) return true;
                    if (onEnable) analysis.Add(target, Mark.Anchor, name);
                    else if (onAwake && bake && !BakeAwakePick(component, analysis)) analysis.Add(target, Mark.Anchor, name);
                    return true;
                }
                case "RandomVariant":
                    // Picks one of its variants in Awake (or OnEnable) and switches the others off; one whose pick
                    // the build made (unity/VariantBake.cs: both off) switches nothing.
                    if (!(bool)(Field(component, "onAwake") ?? true) && !(bool)(Field(component, "onEnable") ?? true)) return true;
                    var variants = Field(component, "variants") as IList;
                    if (variants != null) foreach (object variant in variants) analysis.Add(variant, Mark.Anchor, name);
                    return true;
                case "RandomDestroyObject":
                    analysis.Add(Field(component, "target"), Mark.Anchor, name);
                    return true;
                case "RandomForce":
                    analysis.Add(Field(component, "rigidbody"), Mark.Anchor, name);
                    analysis.Add(Field(component, "target"), Mark.Anchor, name);
                    return true;
                case "DeactivateSelf":  // SetActive(false) in Awake
                    analysis.Add(transform, Mark.Anchor, name);
                    return true;
                case "LightFlicker":  // switches its lightOff and lightOn objects
                    AnchorAll(component, analysis, name, "lightOff", "lightOn");
                    return true;
                case "RandomMaterial":
                case "RandomPosition":
                case "RandomFlip":
                {
                    // A material picked for its renderer(s), a position or a flip of its target, in Awake.
                    bool onAwake = (bool)(Field(component, "onAwake") ?? true), onEnable = (bool)(Field(component, "onEnable") ?? false);
                    var targets = new List<object> { Field(component, "target") };
                    var many = Field(component, "targets") as IList;
                    if (many != null) foreach (object item in many) targets.Add(item);
                    if (targets.All(t => t == null)) targets.Add(transform);
                    Mark mark = name == "RandomMaterial" ? Mark.Own : Mark.Anchor;
                    if (EnemyPick(component, transform, analysis, bake)) return true;
                    if (onEnable || (onAwake && !bake && !Application.isPlaying))
                        foreach (object target in targets) analysis.Add(target, mark, name);
                    else if (onAwake && bake && !BakeAwakePick(component, analysis))
                        foreach (object target in targets) analysis.Add(target, mark, name);
                    return true;
                }
                case "RandomNumberEnabled":  // switches a random number of its objects on, the rest off
                    AnchorAll(component, analysis, name, "gameObjects");
                    return true;
                case "PMGlobalBoolToState":  // switches its statesTrue or statesFalse objects on by a global bool
                    AnchorAll(component, analysis, name, "statesTrue", "statesFalse");
                    return true;
                case "PlaySourceThenDeactivate":
                    analysis.Add(Field(component, "deactivateTarget"), Mark.Anchor, name);
                    return true;
                case "BlobTrailSpawn":  // switches pooled pieces on, moves its wiggle
                    AnchorAll(component, analysis, name, "pool");
                    analysis.Add(Field(component, "wiggle"), Mark.Anchor, name);
                    return true;
                case "BlobTrailPiece":  // moves itself and scales its scaleTarget, then switches its parent off
                    analysis.Add(transform, Mark.Anchor, name);
                    analysis.Add(Field(component, "scaleTarget"), Mark.Anchor, name);
                    analysis.Add(Field(component, "parent"), Mark.Anchor, name);
                    return true;
                case "RandomExplosiveForce":
                case "RandomAngularVelocity":
                    analysis.Add(Field(component, "rigidbody"), Mark.Anchor, name);
                    return true;
                case "TransformLock":  // keeps its world position and rotation however its parents move
                case "SpinGameObject":
                case "SetRotationEveryFrame":
                case "SetOnGround":  // moves to the ground and under the level's parent in Start
                case "RotateAroundAxis":
                case "MCSDynamicObject":  // MeshCombineStudio's mark for things that move
                    analysis.Add(transform, Mark.Anchor, name);
                    return true;
                case "DeactivateRenderer":  // its renderer off in Awake
                    analysis.Add(Field(component, "renderer"), Mark.Own, name);
                    return true;
                case "VideoPlayer":  // CrowCompat's: frames into its renderer's material
                case "UnityEngine_Video_VideoPlayer":
                    analysis.Add(transform, Mark.Own, name);
                    foreach (UnityEngine.Object reference in References(component)) analysis.Add(reference, Mark.Own, name);
                    return true;
                case "RotationAnimationScript":
                case "ScaleAnimationScript":
                case "PositionAnimationScript":
                {
                    // Animates its target while enabled; PlatformSpecificAnimation switches some off on
                    // the PS Vita for good (unless an AnimationState switches them on).
                    if (analysis.stillAnimations.Contains(component)) return true;
                    bool self = (bool)(Field(component, "targetSelf") ?? true);
                    object target = self ? null : Field(component, "target");
                    Transform targetTransform = target is GameObject ? ((GameObject)target).transform : target as Transform;
                    analysis.animations.Add(new KeyValuePair<Component, Transform>(component, targetTransform ?? transform));
                    return true;
                }
                case "SetPMGlobalGameObject":
                    // Puts its object in a global variable. The game's FSMs only move what globals name
                    // (ElevatorRoom, MineCartRoom, the player, the camera: SetPosition, DOTween...).
                    analysis.Add(Field(component, "gameObject"), Mark.Anchor, name);
                    foreach (string field in new[] { "pmGlobalGameObject" })
                    {
                        var global = Field(component, field) as string;
                        if (!string.IsNullOrEmpty(global)) analysis.GlobalValue(global, new[] { Field(component, "gameObject") });
                    }
                    return true;
                case "RandomDeactivateBehaviour":  // switch another behaviour (judged on its own) on or off
                case "EnableBehaviourAfter":
                case "AnimationState":  // switches its animations on and off (they are anchors)
                case "PlatformSpecificAnimation":
                case "PlaySourceOnCollisionEnter":
                case "WaitThenPlaySource":
                case "TestFlipper":  // Flip is never called
                    return true;
            }
            return false;
        }

        static void AnchorAll(Component component, Analysis analysis, string name, params string[] lists)
        {
            foreach (string list in lists)
            {
                var items = Field(component, list) as IList;
                if (items != null) foreach (object item in items) analysis.Add(item, Mark.Anchor, name);
            }
        }

        // The scene objects a script's serialized fields reference (fields of object types, arrays and lists of them).
        static IEnumerable<UnityEngine.Object> References(Component component)
        {
            for (Type type = component.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                foreach (FieldInfo field in type.GetFields(Instance | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic || field.IsNotSerialized) continue;
                    if (!field.IsPublic && field.GetCustomAttributes(typeof(SerializeField), false).Length == 0) continue;
                    Type fieldType = field.FieldType;
                    bool single = typeof(GameObject).IsAssignableFrom(fieldType) || typeof(Component).IsAssignableFrom(fieldType);
                    bool many = typeof(IList).IsAssignableFrom(fieldType);
                    if (!single && !many) continue;
                    object value;
                    try
                    {
                        value = field.GetValue(component);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (single)
                    {
                        var target = value as UnityEngine.Object;
                        if (target != null) yield return target;
                        continue;
                    }
                    var list = value as IList;
                    if (list == null) continue;
                    foreach (object item in list)
                    {
                        var target = item as UnityEngine.Object;
                        if (target != null && (target is GameObject || target is Component)) yield return target;
                    }
                }
        }

        // An FSM's actions: what each acts on, by its parameters (through the FSM's GameObject
        // variables' values, and what other actions store in them).
        static void AnalyzeFsm(Component owner, Analysis analysis)
        {
            object fsm = Field(owner, "fsm");
            var states = Field(fsm, "states") as Array;
            if (states == null) return;
            analysis.fsms++;
            if (collectReferences) References(fsm, states, analysis);
            var locals = new Dictionary<string, List<object>>();
            var variables = Field(Field(fsm, "variables"), "gameObjectVariables") as Array;
            if (variables != null)
                foreach (object variable in variables)
                {
                    string name = Field(variable, "name") as string;
                    if (name != null) locals[name] = new List<object> { Field(variable, "value") };
                }
            var own = new HashSet<string>(locals.Keys);  // the rest are globals
            var arrays = Field(Field(fsm, "variables"), "arrayVariables") as Array;
            var arrayItems = new List<object>();
            if (arrays != null)
                foreach (object array in arrays)
                {
                    var references = Field(array, "objectReferences") as Array;
                    if (references != null) foreach (object item in references) arrayItems.Add(item);
                }
            // First pass: what actions store in variables. Unknown ones (raycast hits, triggers) are marked.
            var found = new HashSet<string>();
            var foundUnder = new Dictionary<string, List<object>>();
            var actionsList = new List<KeyValuePair<string, List<KeyValuePair<string, object>>>>();
            foreach (object state in states)
            {
                object data = Field(state, "actionData");
                var names = Field(data, "actionNames") as IList;
                if (names == null) continue;
                var enabled = Field(data, "actionEnabled") as IList;
                var starts = Field(data, "actionStartIndex") as IList;
                var types = Field(data, "paramDataType") as IList;
                var positions = Field(data, "paramDataPos") as IList;
                var paramNames = Field(data, "paramName") as IList;
                if (starts == null || types == null || positions == null) continue;
                for (int a = 0; a < names.Count; a++)
                {
                    if (enabled != null && a < enabled.Count && !(bool)enabled[a]) continue;
                    string action = ShortName(names[a] as string);
                    int first = a < starts.Count ? Convert.ToInt32(starts[a]) : types.Count;
                    int last = a + 1 < starts.Count ? Convert.ToInt32(starts[a + 1]) : types.Count;
                    var parameters = new List<KeyValuePair<string, object>>();
                    for (int p = first; p < last && p < types.Count; p++)
                    {
                        int kind = Convert.ToInt32(types[p]);
                        int position = Convert.ToInt32(positions[p]);
                        string list = kind == FsmGameObject ? "fsmGameObjectParams" : kind == FsmOwnerDefault ? "fsmOwnerDefaultParams"
                            : kind == ObjectReference || kind == GameObjectParam ? "unityObjectParams" : kind == FsmObject ? "fsmObjectParams"
                            : kind == FsmProperty ? "fsmPropertyParams" : kind == FsmArray ? "fsmArrayParams"
                            : kind == FsmBool && action == "ActivateGameObject" ? "fsmBoolParams" : null;
                        if (list == null) continue;
                        var items = Field(data, list) as IList;
                        if (items == null || position < 0 || position >= items.Count) continue;
                        string paramName = paramNames != null && p < paramNames.Count ? paramNames[p] as string : "";
                        parameters.Add(new KeyValuePair<string, object>(kind + ":" + paramName, items[position]));
                    }
                    actionsList.Add(new KeyValuePair<string, List<KeyValuePair<string, object>>>(action, parameters));
                    analysis.actions++;
                    // Variables this action may store a found object in.
                    bool finds = FindingActions.Contains(action), copies = action == "SetGameObject", fromArray = action == "ArrayGet";
                    if (!finds && !copies && !fromArray && !ChildActions.Contains(action) && action != "CreateObjectAdvanced") continue;
                    object source = null;
                    foreach (KeyValuePair<string, object> parameter in parameters)
                        if (parameter.Key == FsmGameObject + ":gameObject" && copies) source = parameter.Value;
                    foreach (KeyValuePair<string, object> parameter in parameters)
                    {
                        if (!parameter.Key.StartsWith(FsmGameObject + ":")) continue;
                        if (copies && parameter.Key == FsmGameObject + ":gameObject") continue;
                        object variable = parameter.Value;
                        if (!(bool)(Field(variable, "useVariable") ?? false)) continue;
                        string name = Field(variable, "name") as string;
                        if (string.IsNullOrEmpty(name)) continue;
                        List<object> values;
                        if (!locals.TryGetValue(name, out values)) locals[name] = values = new List<object>();
                        if (finds) found.Add(name);
                        else if (action == "GetChild" || action == "FindChild")
                        {
                            // A descendant of its target: what is done to it is done somewhere below.
                            List<object> parents;
                            if (!foundUnder.TryGetValue(name, out parents)) foundUnder[name] = parents = new List<object>();
                            foreach (KeyValuePair<string, object> target in parameters)
                                if (target.Key.StartsWith(FsmOwnerDefault + ":")) parents.AddRange(OwnerTargets(target.Value, owner, locals));
                        }
                        else if (copies)
                        {
                            // The player's analysis: a global's object copied into a variable may be acted on through
                            // it, so what the global holds keeps all as it is.
                            if (playerRules && (bool)(Field(source, "useVariable") ?? false) && !own.Contains(Field(source, "name") as string ?? ""))
                                analysis.globalUses.Add(new KeyValuePair<string, KeyValuePair<Mark, string>>((string)Field(source, "name"), new KeyValuePair<Mark, string>(Mark.Exclude, "SetGameObject copy")));
                            List<object> copied = Values(source, locals).ToList();
                            values.AddRange(copied);
                            if (!own.Contains(name)) analysis.GlobalValue(name, copied);
                        }
                        else if (fromArray) values.AddRange(arrayItems);
                        // Children found by GetChild and FindChild are below an object marked exclude;
                        // created objects are new.
                    }
                }
            }
            string ownerPath = Hierarchy.Path(owner.transform, 3);
            foreach (KeyValuePair<string, List<KeyValuePair<string, object>>> entry in actionsList)
            {
                string action = entry.Key;
                // ActivateChildren and DestroyChildren switch or destroy all its children (a merged object
                // placed directly under it too, with the rest); the others count or search them.
                Mark mark = AnchorActions.Contains(action) || action.StartsWith("DOTweenTransform") || action == "ActivateChildren" || action == "DestroyChildren"
                    ? Mark.Anchor : OwnActions.Contains(action) ? Mark.Own : ChildActions.Contains(action) ? Mark.NoChild : Mark.None;
                // A recursive ActivateGameObject switches every object below its target too (Split
                // stops there; the merge treats it as any switch).
                foreach (KeyValuePair<string, object> parameter in entry.Value)
                    if (parameter.Key == FsmBool + ":recursive" &&
                        ((bool)(Field(parameter.Value, "useVariable") ?? true) || (bool)(Field(parameter.Value, "value") ?? true)))
                        action = "ActivateGameObjectRecursive";
                if (action == "SetProperty")
                {
                    foreach (KeyValuePair<string, object> parameter in entry.Value)
                    {
                        if (!parameter.Key.StartsWith(FsmProperty + ":")) continue;
                        object target = Field(Field(parameter.Value, "TargetObject"), "value");
                        string property = Field(parameter.Value, "PropertyName") as string ?? "";
                        if (target is Renderer || target is MeshFilter) analysis.Add(target, Mark.Own, "SetProperty");
                        else if (target is Transform || target is GameObject) analysis.Add(target, Mark.Anchor, "SetProperty:" + property);
                    }
                    continue;
                }
                if (mark == Mark.None)
                {
                    // The player's analysis (PlayerMerge.cs): an action the lists don't know keeps what it acts on
                    // as it is, unless it only reads.
                    if (!playerRules) continue;
                    mark = PlayerUnknownMark(action);
                    if (mark == Mark.None) continue;
                    action = "unknown:" + action;
                    analysis.Unknown(action);
                }
                foreach (KeyValuePair<string, object> parameter in entry.Value)
                {
                    object value = parameter.Value;
                    if (parameter.Key.StartsWith(FsmOwnerDefault + ":"))
                    {
                        object option = Field(value, "ownerOption");
                        if (option != null && Convert.ToInt32(option) == 0)
                        {
                            analysis.Add(owner.transform, mark, action);
                            continue;
                        }
                        value = Field(value, "gameObject");
                    }
                    else if (parameter.Key != FsmGameObject + ":gameObject") continue;
                    if (value == null) continue;
                    bool variable = (bool)(Field(value, "useVariable") ?? false);
                    string name = variable ? Field(value, "name") as string : null;
                    if (variable && !string.IsNullOrEmpty(name))
                    {
                        if (!own.Contains(name))
                        {
                            // A global (or a variable only a found or copied object is put in here):
                            // what the scene's FSMs put in the global gets this too, once all are read.
                            analysis.globalUses.Add(new KeyValuePair<string, KeyValuePair<Mark, string>>(name, new KeyValuePair<Mark, string>(mark, action)));
                            if (!locals.ContainsKey(name))
                            {
                                analysis.globals++;
                                analysis.Unknown("global " + name + " " + action);
                            }
                        }
                        else if (found.Contains(name))
                        {
                            analysis.Unknown(ownerPath + " " + action + " " + name);
                            if (mark == Mark.Anchor)
                            {
                                // Moved or switched through what a raycast, trigger or property found: anything.
                                int count;
                                string key = "found " + name + ":" + action;
                                analysis.unresolved.TryGetValue(key, out count);
                                analysis.unresolved[key] = count + 1;
                            }
                        }
                        List<object> parents;
                        if (mark != Mark.NoChild && foundUnder.TryGetValue(name, out parents))
                            foreach (object parent in parents) analysis.Add(parent, Mark.Exclude, action + " found child");
                    }
                    foreach (object target in Values(value, locals)) analysis.Add(target, mark, action);
                }
            }
        }

        // Strip's: whatever an FSM names, by object or by string, in its variables and its actions' parameters
        // (ParamDataType's lists, whatever the action).
        static bool collectReferences;

        static void References(object fsm, Array states, Analysis analysis)
        {
            Action<object> reference = item =>
            {
                var gameObject = item as GameObject;
                var component = item as Component;
                if (gameObject != null) analysis.referenced.Add(gameObject.transform);
                else if (component != null) analysis.referenced.Add(component.transform);
            };
            Action<object> name = item =>
            {
                var text = item as string;
                if (!string.IsNullOrEmpty(text)) analysis.names.Add(text);
            };
            Action<object> array = item =>
            {
                var objects = Field(item, "objectReferences") as Array;
                if (objects != null) foreach (object o in objects) reference(o);
                var strings = Field(item, "stringValues") as Array;
                if (strings != null) foreach (object o in strings) name(o);
            };
            object variables = Field(fsm, "variables");
            foreach (string list in new[] { "gameObjectVariables", "objectVariables", "stringVariables", "arrayVariables" })
            {
                var items = Field(variables, list) as Array;
                if (items == null) continue;
                foreach (object item in items)
                    if (list == "arrayVariables") array(item);
                    else if (list == "stringVariables") name(Field(item, "value"));
                    else reference(Field(item, "value"));
            }
            foreach (object state in states)
            {
                object data = Field(state, "actionData");
                if (data == null) continue;
                foreach (string list in new[]
                         {
                             "fsmGameObjectParams", "fsmOwnerDefaultParams", "unityObjectParams", "fsmObjectParams", "fsmEventTargetParams", "fsmVarParams",
                             "fsmArrayParams", "fsmStringParams", "stringParams", "fsmPropertyParams",
                         })
                {
                    var items = Field(data, list) as IList;
                    if (items == null) continue;
                    foreach (object item in items)
                    {
                        if (item == null) continue;
                        switch (list)
                        {
                            case "fsmGameObjectParams":
                            case "fsmObjectParams":
                                reference(Field(item, "value"));
                                break;
                            case "fsmOwnerDefaultParams":
                                reference(Field(Field(item, "gameObject"), "value"));
                                break;
                            case "fsmEventTargetParams":
                                reference(Field(Field(Field(item, "gameObject"), "gameObject"), "value"));
                                break;
                            case "fsmVarParams":
                                reference(Field(item, "objectReference"));
                                name(Field(item, "stringValue"));
                                break;
                            case "fsmArrayParams":
                                array(item);
                                break;
                            case "fsmStringParams":
                                name(Field(item, "value"));
                                break;
                            case "fsmPropertyParams":
                                reference(Field(Field(item, "TargetObject"), "value"));
                                break;
                            case "stringParams":
                                name(item);
                                break;
                            default:
                                reference(item);
                                break;
                        }
                    }
                }
            }
        }

        // The build's, with the analysis Process made with references.
        static string Strip(Scene scene, Analysis analysis, IEnumerable<MergedRenderer> groups)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var kept = new Dictionary<string, int>();
            int stripped = 0, parts = 0, transforms = 0;
            foreach (Transform enemy in analysis.enemies) transforms += enemy.GetComponentsInChildren<Transform>(true).Length;
            foreach (MergedRenderer group in groups)
            {
                if (group == null || group.gameObject.scene != scene || group.parts == null || !analysis.InEnemy(group.transform)) continue;
                var left = new List<Renderer>();
                foreach (Renderer part in group.parts)
                {
                    if (part == null) continue;
                    parts++;
                    string why = StripBlocker(part, analysis);
                    if (why == null)
                    {
                        UnityEngine.Object.DestroyImmediate(part.gameObject);
                        stripped++;
                        continue;
                    }
                    left.Add(part);
                    int count;
                    kept.TryGetValue(why, out count);
                    kept[why] = count + 1;
                }
                group.parts = left.ToArray();
            }
            return "CROW_MERGE strip scene=" + scene.name.Replace(' ', '_') + " enemies=" + analysis.enemies.Count + " transforms=" + transforms + " parts=" + parts +
                   " stripped=" + stripped + " referenced=" + analysis.referenced.Count + " names=" + analysis.names.Count + " ms=" + clock.ElapsedMilliseconds +
                   " kept: " + string.Join(" ", kept.OrderByDescending(k => k.Value).Select(k => k.Key + "=" + k.Value).ToArray());
        }

        static string StripBlocker(Renderer part, Analysis analysis)
        {
            GameObject gameObject = part.gameObject;
            if (gameObject.transform.childCount > 0) return "children";
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (component is Transform || component is MeshFilter || component is MeshRenderer) continue;
                if (component == null) return "missing";
                string name = component.GetType().Name;
                if (InertScripts.Contains(name)) continue;
                // A random pick made once (EnemyPick): the script does nothing more.
                if (name.StartsWith("Random", StringComparison.Ordinal) && !(bool)(Field(component, "onAwake") ?? true) && !(bool)(Field(component, "onEnable") ?? true)) continue;
                return name;
            }
            // MeshCombineEnemy: the enemies' MeshCombineStudio search's (the merge's now; PlatformSpecificAnimation
            // only clears it).
            if (gameObject.tag != "Untagged" && gameObject.tag != "MeshCombineEnemy") return "tag:" + gameObject.tag;
            if (analysis.names.Contains(gameObject.name)) return "named";
            if (analysis.referenced.Contains(gameObject.transform)) return "referenced";
            if (analysis.marks.ContainsKey(gameObject.transform)) return "marked";
            // What counts or searches the children of a parent (GetChild...), or an unknown script's.
            for (Transform t = gameObject.transform.parent; t != null; t = t.parent)
            {
                Mark mark;
                if (analysis.marks.TryGetValue(t, out mark) && (mark & (Mark.NoChild | Mark.Exclude)) != 0) return "parent";
                if (analysis.enemies.Contains(t)) break;
            }
            return null;
        }

        static IEnumerable<object> OwnerTargets(object ownerDefault, Component owner, Dictionary<string, List<object>> locals)
        {
            object option = Field(ownerDefault, "ownerOption");
            if (option != null && Convert.ToInt32(option) == 0) return new object[] { owner.gameObject };
            return Values(Field(ownerDefault, "gameObject"), locals).ToList();
        }

        static IEnumerable<object> Values(object fsmGameObject, Dictionary<string, List<object>> locals)
        {
            if (fsmGameObject == null) yield break;
            bool variable = (bool)(Field(fsmGameObject, "useVariable") ?? false);
            string name = Field(fsmGameObject, "name") as string;
            List<object> values;
            if (variable && !string.IsNullOrEmpty(name) && locals.TryGetValue(name, out values))
            {
                foreach (object value in values.ToList()) if (value != null) yield return value;
                yield break;
            }
            object own = Field(fsmGameObject, "value");
            if (own != null) yield return own;
        }

        // ---------------------------------------------------------------- grouping and merging

        sealed class Slot
        {
            public MeshRenderer renderer;
            public Mesh mesh;
            public int subMesh;
            public Material material;
            public Transform anchor;
        }

        // What stops a renderer from merging, or null (with its anchor).
        static string Blocker(MeshRenderer renderer, Analysis analysis, out Transform anchor)
        {
            anchor = null;
            Mark mark;
            for (Transform t = renderer.transform; t != null; t = t.parent)
            {
                if (analysis.marks.TryGetValue(t, out mark))
                {
                    if ((mark & Mark.Exclude) != 0) return "exclude:" + analysis.why[t];
                    if (t == renderer.transform && (mark & Mark.Own) != 0) return "own:" + analysis.why[t];
                    if ((mark & Mark.Anchor) != 0 && anchor == null)
                    {
                        anchor = t;
                        if ((mark & Mark.NoChild) != 0) return "nochild:" + analysis.why[t];
                    }
                }
                if (anchor == null && !t.gameObject.activeSelf) return "inactive";
            }
            return null;
        }

        static string Channels(Mesh mesh)
        {
            return (mesh.normals.Length > 0 ? "n" : "") + (mesh.tangents.Length > 0 ? "t" : "") + (mesh.colors32.Length > 0 ? "c" : "") +
                   (mesh.uv.Length > 0 ? "0" : "") + (mesh.uv2.Length > 0 ? "1" : "") + (mesh.uv3.Length > 0 ? "2" : "") + (mesh.uv4.Length > 0 ? "3" : "");
        }

        // Analyzes a scene and, with apply, merges it. canRead says whether a mesh's data can be read
        // (null: Mesh.isReadable; the editor reads every mesh). Returns the report, CROW_MERGE lines.
        public static string Process(Scene scene, bool apply, Func<Mesh, bool> canRead, bool bake)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var scripts = new Dictionary<string, int>();
            // The build strips the enemies' bare merged parts when it drops the parts' meshes.
            bool strip = apply && bake && Drop && Enemies && Environment.GetEnvironmentVariable("CROW_MERGE_ENEMY_STRIP") != "off";
            // The enemies' rebuild (compat/EnemyRebuild.cs) after the build's enemy merge, with the same analysis.
            bool rebuild = apply && bake && Enemies;
            collectReferences = strip || rebuild;
            Analysis analysis;
            try
            {
                analysis = Analyze(scene, scripts, bake);
            }
            finally
            {
                collectReferences = false;
            }
            var lights = new List<Light>();
            foreach (GameObject root in Roots(scene))
                foreach (Light light in root.GetComponentsInChildren<Light>(true))
                    if ((light.type == LightType.Point || light.type == LightType.Spot) && light.renderMode != LightRenderMode.ForceVertex) lights.Add(light);
            var blockers = new Dictionary<string, int>();
            var seenBlockers = new Dictionary<string, int>();  // of the renderers drawn in the last frame (at run time)
            int seen = 0, seenCandidates = 0;
            var groups = new Dictionary<string, List<Slot>>();
            int renderers = 0, candidates = 0;
            foreach (GameObject root in Roots(scene))
                foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    renderers++;
                    if (!renderer.enabled) continue;
                    Transform anchor;
                    string why = Blocker(renderer, analysis, out anchor);
                    var filter = renderer.GetComponent<MeshFilter>();
                    Mesh mesh = filter == null ? null : filter.sharedMesh;
                    Material[] materials = renderer.sharedMaterials;
                    bool enemy = why == null && analysis.EnemyLike(renderer.transform);
                    if (why == null)
                    {
                        if (mesh == null || mesh.vertexCount == 0) why = "no mesh";
                        else if (renderer.isPartOfStaticBatch) why = "static batch";
                        else if (renderer.lightmapIndex >= 0 && renderer.lightmapIndex < 65534) why = "lightmap";
                        else if (materials.Length != mesh.subMeshCount) why = "materials != submeshes";
                        else if (materials.Any(m => m == null || (m.renderQueue > 2500 && !enemy) || m.GetTag("DisableBatching", false) == "True")) why = "material";
                        else if (Enumerable.Range(0, mesh.subMeshCount).Any(s => mesh.GetTopology(s) != MeshTopology.Triangles)) why = "topology";
                        else if (canRead == null ? !mesh.isReadable : !canRead(mesh)) why = "unreadable";
                        else if (renderer.probeAnchor != null) why = "probe anchor";
                        else if (mesh.vertexCount > (enemy ? EnemyMaxPartVertices : MaxPartVertices)) why = "big";
                    }
                    bool drawn = Application.isPlaying && renderer.isVisible;
                    if (drawn) seen++;
                    if (why != null)
                    {
                        string key = why.Length > 60 ? why.Substring(0, 60) : why;
                        int count;
                        blockers.TryGetValue(key, out count);
                        blockers[key] = count + 1;
                        if (drawn)
                        {
                            seenBlockers.TryGetValue(key, out count);
                            seenBlockers[key] = count + 1;
                        }
                        continue;
                    }
                    candidates++;
                    if (drawn) seenCandidates++;
                    Bounds bounds = renderer.bounds;
                    var lit = new StringBuilder();
                    foreach (Light light in lights)
                        if (bounds.SqrDistance(light.transform.position) <= light.range * light.range) lit.Append(light.GetInstanceID()).Append(',');
                    Vector3 centre = bounds.center;
                    string cell = Mathf.FloorToInt(centre.x / Cell) + "," + Mathf.FloorToInt(centre.y / Cell) + "," + Mathf.FloorToInt(centre.z / Cell);
                    string common = DrawKey(anchor, renderer) + "|" + Channels(mesh) + "|" + lit + "|" + cell;
                    if (enemy)  // one group per anchor, across materials (submeshes); lights and cells mean nothing for what moves
                        common = "E|" + DrawKey(anchor, renderer) + "|" + Channels(mesh);
                    for (int s = 0; s < materials.Length; s++)
                    {
                        string key = (enemy ? "" : materials[s].GetInstanceID() + "|") + common;
                        List<Slot> slots;
                        if (!groups.TryGetValue(key, out slots)) groups[key] = slots = new List<Slot>();
                        slots.Add(new Slot { renderer = renderer, mesh = mesh, subMesh = s, material = materials[s], anchor = anchor });
                    }
                }
            // Groups of one stay as they are; a renderer merges only if all its slots do.
            string spread = "";
            if (!apply)
            {
                // What splits the candidates: anchors with their slot counts, and the singletons' anchors.
                var all = groups.Values.SelectMany(g => g).ToList();
                spread = " anchors=" + all.Select(s => s.anchor).Distinct().Count() + " materials=" + all.Select(s => s.material).Distinct().Count() +
                         " singles=" + groups.Values.Count(g => g.Count == 1) + " top: " +
                         string.Join(" ", all.GroupBy(s => s.anchor).OrderByDescending(g => g.Count()).Take(12)
                             .Select(g => (g.Key == null ? "scene" : Hierarchy.Path(g.Key, 2)) + "=" + g.Count() + "/" + g.Select(s => s.material).Distinct().Count()).ToArray()) +
                         " singles by anchor: " + string.Join(" ", groups.Values.Where(g => g.Count == 1).GroupBy(g => g[0].anchor).OrderByDescending(g => g.Count()).Take(12)
                             .Select(g => (g.Key == null ? "scene" : Hierarchy.Path(g.Key, 2) + "(" + Reason(analysis, g.Key) + ")") + "=" + g.Count()).ToArray());
            }
            Func<List<Slot>, bool> single = g => g.Count < 2 || g.All(x => x.renderer == g[0].renderer);  // one renderer's slots (an enemy part's materials)
            var merging = groups.Values.Where(g => !single(g)).ToList();
            var slotsOf = new Dictionary<MeshRenderer, int>();
            foreach (List<Slot> group in groups.Values)
                foreach (Slot slot in group)
                {
                    int count;
                    slotsOf.TryGetValue(slot.renderer, out count);
                    slotsOf[slot.renderer] = count + 1;
                }
            var merged = new HashSet<MeshRenderer>();
            foreach (List<Slot> group in merging) foreach (Slot slot in group) merged.Add(slot.renderer);
            foreach (List<Slot> group in groups.Values.Where(single))
                merged.Remove(group[0].renderer);
            for (int i = 0; i < merging.Count; i++)
            {
                merging[i] = merging[i].Where(s => merged.Contains(s.renderer)).ToList();
                if (single(merging[i]))
                {
                    foreach (Slot slot in merging[i]) merged.Remove(slot.renderer);
                    merging.RemoveAt(i--);
                }
            }
            // A renderer left with some slots unmerged (its other slots' groups shrank) stays whole.
            foreach (List<Slot> group in merging) group.RemoveAll(s => !merged.Contains(s.renderer));
            merging.RemoveAll(g => single(g));
            // The memory budget: estimated from the parts' meshes (vertices of the slot's submesh range
            // at most the mesh's, 40 bytes each, and 2 per index).
            long budget = 0, enemyBudget = 0;
            int overBudget = 0;
            var kept = new List<List<Slot>>();
            foreach (List<Slot> group in merging.OrderByDescending(g => (g.Count - 1) / (double)Math.Max(1L, Estimate(g))))
            {
                long estimate = Estimate(group);
                bool enemyGroup = analysis.EnemyLike(group[0].renderer.transform);
                if ((enemyGroup ? enemyBudget : budget) + estimate > (enemyGroup ? EnemyMaxBytes : MaxBytes))
                {
                    overBudget += group.Count;
                    foreach (Slot slot in group) merged.Remove(slot.renderer);
                    continue;
                }
                if (enemyGroup) enemyBudget += estimate;
                else budget += estimate;
                kept.Add(group);
            }
            merging = kept;
            foreach (List<Slot> group in merging) group.RemoveAll(s => !merged.Contains(s.renderer));
            merging.RemoveAll(g => single(g));
            merged.Clear();
            foreach (List<Slot> group in merging) foreach (Slot slot in group) merged.Add(slot.renderer);
            if (overBudget > 0)
            {
                int count;
                blockers.TryGetValue("over budget", out count);
                blockers["over budget"] = count + overBudget;
            }

            var report = new StringBuilder();
            string sceneName = scene.name.Replace(' ', '_');
            int partCount = merged.Count;
            long vertices = 0, bytes = 0, enemyBytes = 0;
            int meshes = 0, dropped = 0;
            long droppedBytes = 0;
            var created = new List<GameObject>();
            if (apply)
            {
                foreach (Behaviour combiner in analysis.enemyCombiners) combiner.enabled = false;
                foreach (List<Slot> group in merging)
                {
                    long groupBytes;
                    int groupVertices;
                    created.AddRange(Build(scene, group, out groupVertices, out groupBytes));
                    vertices += groupVertices;
                    bytes += groupBytes;
                    if (analysis.InEnemy(group[0].renderer.transform)) enemyBytes += groupBytes;
                }
                foreach (MeshRenderer renderer in merged) renderer.enabled = false;
                meshes = created.Count;
                if (Drop) DropMeshes(scene, merged, out dropped, out droppedBytes);
            }
            else foreach (List<Slot> group in merging) foreach (Slot slot in group) vertices += Count(slot.mesh, slot.subMesh);
            report.Append("CROW_MERGE scene=").Append(sceneName).Append(apply ? " merged" : " report").Append(" renderers=").Append(renderers)
                .Append(" candidates=").Append(candidates).Append(" parts=").Append(partCount).Append(" groups=").Append(merging.Count)
                .Append(" meshes=").Append(meshes).Append(" vertices=").Append(vertices).Append(" bytes=").Append(bytes).Append(" dropped=").Append(dropped).Append(" droppedBytes=").Append(droppedBytes)
                .Append(" lights=").Append(lights.Count).Append(" cell=").Append(Cell.ToString(CultureInfo.InvariantCulture))
                .Append(" baked=").Append(analysis.baked).Append(" secondary=").Append(analysis.secondary).Append(" bodies=").Append(analysis.bodies.Count).Append(" enemies=").Append(analysis.enemies.Count)
                .Append(" enemyCombiners=").Append(analysis.enemyCombiners.Count).Append(" enemyParts=").Append(merged.Count(r => analysis.InEnemy(r.transform)))
                .Append(" enemyGroups=").Append(merging.Count(g => analysis.InEnemy(g[0].renderer.transform))).Append(" enemyBytes=").Append(enemyBytes)
                .Append(" enemySubmeshes=").Append(merging.Where(g => analysis.InEnemy(g[0].renderer.transform)).Sum(g => g.Select(x => x.material).Distinct().Count()))
                .Append(" enemyMaterials=").Append(merging.Where(g => analysis.InEnemy(g[0].renderer.transform)).SelectMany(g => g.Select(x => x.material)).Distinct().Count()).Append(" forMeshCombiner=").Append(analysis.combined).Append(" fsms=").Append(analysis.fsms).Append(" actions=").Append(analysis.actions).Append(" marked=").Append(analysis.marks.Count)
                .Append(" ms=").Append(clock.ElapsedMilliseconds).Append('\n');
            if (spread.Length > 0) report.Append("CROW_MERGE ").Append(sceneName).Append(spread).Append('\n');
            // -crow-merge-materials (or CROW_MERGE_MATERIALS=1 in the build): the enemies' merged materials.
            if (Environment.GetEnvironmentVariable("CROW_MERGE_MATERIALS") == "1" || CrowFlags.Has(ProbeFiles.Args, "-crow-merge-materials"))
                foreach (Material m in merging.Where(g => analysis.InEnemy(g[0].renderer.transform)).SelectMany(g => g.Select(x => x.material)).Distinct())
                    report.Append("CROW_MERGE ").Append(sceneName).Append(" enemy material ").Append(m.name).Append(" shader=").Append(m.shader.name)
                        .Append(" texture=").Append(m.mainTexture == null ? "-" : m.mainTexture.name + "(" + m.mainTexture.width + "x" + m.mainTexture.height + ")")
                        .Append(" color=").Append(m.HasProperty("_Color") ? m.color.ToString() : "-").Append(" keywords=").Append(string.Join(",", m.shaderKeywords))
                        .Append(" slots=").Append(merging.Where(g => analysis.InEnemy(g[0].renderer.transform)).Sum(g => g.Count(x => x.material == m))).Append('\n');
            if (analysis.bodies.Count > 0)
            {
                Func<Transform, bool> inBody = t =>
                {
                    for (; t != null; t = t.parent) if (analysis.bodies.Contains(t)) return true;
                    return false;
                };
                List<List<Slot>> bodyGroups = merging.Where(g => inBody(g[0].renderer.transform)).ToList();
                report.Append("CROW_MERGE ").Append(sceneName).Append(" bodies: parts=").Append(bodyGroups.Sum(g => g.Select(x => x.renderer).Distinct().Count()))
                    .Append(" groups=").Append(bodyGroups.Count).Append(' ')
                    .Append(string.Join(" ", analysis.bodies.Select(b => Hierarchy.Full(b).Replace(' ', '_')).ToArray())).Append('\n');
            }
            report.Append("CROW_MERGE ").Append(sceneName).Append(" left out: ")
                .Append(string.Join(" ", blockers.OrderByDescending(p => p.Value).Take(30).Select(p => p.Key.Replace(' ', '_') + "=" + p.Value).ToArray())).Append('\n');
            if (seen > 0)
                report.Append("CROW_MERGE ").Append(sceneName).Append(" drawn=").Append(seen).Append(" drawnCandidates=").Append(seenCandidates).Append(" drawn left out: ")
                    .Append(string.Join(" ", seenBlockers.OrderByDescending(p => p.Value).Take(30).Select(p => p.Key.Replace(' ', '_') + "=" + p.Value).ToArray())).Append('\n');
            report.Append("CROW_MERGE ").Append(sceneName).Append(" scripts: ")
                .Append(string.Join(" ", scripts.OrderByDescending(p => p.Value).Take(40).Select(p => p.Key + "=" + p.Value).ToArray())).Append('\n');
            report.Append("CROW_MERGE ").Append(sceneName).Append(" unknown targets: ")
                .Append(string.Join(" | ", analysis.unknownTargets.OrderByDescending(p => p.Value).Take(15).Select(p => p.Key.Replace(' ', '_') + "=" + p.Value).ToArray())).Append('\n');
            report.Append("CROW_MERGE ").Append(sceneName).Append(" biggest groups: ")
                .Append(string.Join(" ", merging.OrderByDescending(g => g.Count).Take(12).Select(g => g[0].material.name.Replace(' ', '_') + "=" + g.Count).ToArray())).Append('\n');
            // Last: the report above reads the parts this destroys.
            if (strip) report.Append(Strip(scene, analysis, created.Select(go => go.GetComponent<MergedRenderer>()))).Append('\n');
            if (rebuild) report.Append(Rebuild(scene, analysis, created)).Append('\n');
            return report.ToString();
        }

        // What renderers merged into one must share: the anchor they move with, the layer, shadows and probes
        // ("anchor|layer|casting|receiving|light probes|reflection probes"; the merge keys add to it).
        static string DrawKey(Transform anchor, Renderer renderer)
        {
            return (anchor == null ? 0 : anchor.GetInstanceID()) + "|" + renderer.gameObject.layer + "|" + (int)renderer.shadowCastingMode + "|" +
                   renderer.receiveShadows + "|" + (int)renderer.lightProbeUsage + "|" + (int)renderer.reflectionProbeUsage;
        }

        static string Reason(Analysis analysis, Transform t)
        {
            string reason;
            return analysis.why.TryGetValue(t, out reason) ? reason : "?";
        }

        // Meshes only merged parts' MeshFilters use in the scene (no other filter, collider or particle
        // system) leave the parts' filters.
        static void DropMeshes(Scene scene, HashSet<MeshRenderer> merged, out int dropped, out long bytes)
        {
            dropped = 0;
            bytes = 0;
            var users = new Dictionary<Mesh, List<MeshFilter>>();
            var kept = new HashSet<Mesh>();
            foreach (GameObject root in Roots(scene))
            {
                foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    Mesh mesh = filter.sharedMesh;
                    if (mesh == null) continue;
                    var renderer = filter.GetComponent<MeshRenderer>();
                    if (renderer == null || !merged.Contains(renderer))
                    {
                        kept.Add(mesh);
                        continue;
                    }
                    List<MeshFilter> list;
                    if (!users.TryGetValue(mesh, out list)) users[mesh] = list = new List<MeshFilter>();
                    list.Add(filter);
                }
                foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
                    if (collider.sharedMesh != null) kept.Add(collider.sharedMesh);
                foreach (ParticleSystemRenderer particles in root.GetComponentsInChildren<ParticleSystemRenderer>(true))
                    if (particles.mesh != null) kept.Add(particles.mesh);
            }
            foreach (KeyValuePair<Mesh, List<MeshFilter>> pair in users)
            {
                if (kept.Contains(pair.Key)) continue;
                foreach (MeshFilter filter in pair.Value) filter.sharedMesh = null;
                dropped++;
                bytes += pair.Key.vertexCount * 48L;
            }
        }

        static long Estimate(List<Slot> group)
        {
            long bytes = 0;
            foreach (Slot slot in group) bytes += Math.Min(slot.mesh.vertexCount, (long)slot.mesh.GetIndexCount(slot.subMesh)) * 40L + slot.mesh.GetIndexCount(slot.subMesh) * 2L;
            return bytes;
        }

        static int Count(Mesh mesh, int subMesh)
        {
            return (int)mesh.GetIndexCount(subMesh);
        }

        // The merged objects for one group: one mesh per MaxVertices vertices.
        static List<GameObject> Build(Scene scene, List<Slot> group, out int vertexTotal, out long byteTotal)
        {
            int vertexSum = 0;
            long byteSum = 0;
            var created = new List<GameObject>();
            Slot first = group[0];
            Transform anchor = first.anchor;
            Matrix4x4 toAnchor = anchor == null ? Matrix4x4.identity : anchor.worldToLocalMatrix;
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var colors = new List<Color32>();
            var uv0 = new List<Vector2>();
            var uv1 = new List<Vector2>();
            // A submesh per material, in the order the materials first come (one for a material group).
            var materialOrder = group.Select(s => s.material).Distinct().ToList();
            var subIndices = materialOrder.Select(m => new List<int>()).ToList();
            var parts = new List<Renderer>();
            var poses = new Dictionary<Renderer, Matrix4x4>();
            string channels = Channels(first.mesh);
            bool hasN = channels.Contains("n"), hasT = channels.Contains("t"), hasC = channels.Contains("c"), has0 = channels.Contains("0"), has1 = channels.Contains("1");
            Action flush = () =>
            {
                if (subIndices.All(l => l.Count == 0)) return;
                var used = Enumerable.Range(0, materialOrder.Count).Where(i => subIndices[i].Count > 0).ToList();
                var mesh = new Mesh { name = "CrowMerged " + first.material.name + (used.Count > 1 ? " +" + (used.Count - 1) : "") };
                mesh.SetVertices(positions);
                if (hasN) mesh.SetNormals(normals);
                if (hasT) mesh.SetTangents(tangents);
                if (hasC) mesh.SetColors(colors);
                if (has0) mesh.SetUVs(0, uv0);
                if (has1) mesh.SetUVs(1, uv1);
                mesh.subMeshCount = used.Count;
                for (int i = 0; i < used.Count; i++) mesh.SetTriangles(subIndices[used[i]], i);
                mesh.RecalculateBounds();
                var go = new GameObject("CrowMerged");
                if (anchor != null) go.transform.SetParent(anchor, false);
                else SceneManager.MoveGameObjectToScene(go, scene);
                go.layer = first.renderer.gameObject.layer;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = used.Select(i => materialOrder[i]).ToArray();
                renderer.shadowCastingMode = first.renderer.shadowCastingMode;
                renderer.receiveShadows = first.renderer.receiveShadows;
                renderer.lightProbeUsage = first.renderer.lightProbeUsage;
                renderer.reflectionProbeUsage = first.renderer.reflectionProbeUsage;
                var record = go.AddComponent<MergedRenderer>();
                record.parts = parts.Distinct().ToArray();
                if (recordPoses) record.poses = record.parts.Select(r => poses[r]).ToArray();
                created.Add(go);
                vertexSum += positions.Count;
                byteSum += positions.Count * (12L + (hasN ? 12 : 0) + (hasT ? 16 : 0) + (hasC ? 4 : 0) + (has0 ? 8 : 0) + (has1 ? 8 : 0)) + subIndices.Sum(l => l.Count) * 2L;
                positions.Clear();
                normals.Clear();
                tangents.Clear();
                colors.Clear();
                uv0.Clear();
                uv1.Clear();
                foreach (List<int> list in subIndices) list.Clear();
                parts.Clear();
            };
            var cache = new Dictionary<Mesh, MeshData>();
            foreach (Slot slot in group)
            {
                MeshData data;
                if (!cache.TryGetValue(slot.mesh, out data)) cache[slot.mesh] = data = new MeshData(slot.mesh);
                int[] source = slot.mesh.GetIndices(slot.subMesh);
                var map = new Dictionary<int, int>();
                foreach (int index in source) if (!map.ContainsKey(index)) map[index] = map.Count;
                if (positions.Count + map.Count > MaxVertices) flush();
                if (map.Count > MaxVertices) continue;  // can't happen with 16-bit source meshes
                Matrix4x4 matrix = toAnchor * slot.renderer.transform.localToWorldMatrix;
                Matrix4x4 normalMatrix = matrix.inverse.transpose;
                bool flip = matrix.determinant < 0f;
                int offset = positions.Count;
                List<int> indices = subIndices[materialOrder.IndexOf(slot.material)];
                foreach (KeyValuePair<int, int> pair in map.OrderBy(p => p.Value))
                {
                    int v = pair.Key;
                    positions.Add(matrix.MultiplyPoint3x4(data.positions[v]));
                    if (hasN) normals.Add(normalMatrix.MultiplyVector(data.normals[v]).normalized);
                    if (hasT)
                    {
                        Vector4 t = data.tangents[v];
                        Vector3 direction = matrix.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
                        tangents.Add(new Vector4(direction.x, direction.y, direction.z, flip ? -t.w : t.w));
                    }
                    if (hasC) colors.Add(data.colors[v]);
                    if (has0) uv0.Add(data.uv0[v]);
                    if (has1) uv1.Add(data.uv1[v]);
                }
                for (int i = 0; i + 2 < source.Length; i += 3)
                {
                    int a = map[source[i]] + offset, b = map[source[i + 1]] + offset, c = map[source[i + 2]] + offset;
                    indices.Add(a);
                    if (flip)
                    {
                        indices.Add(c);
                        indices.Add(b);
                    }
                    else
                    {
                        indices.Add(b);
                        indices.Add(c);
                    }
                }
                parts.Add(slot.renderer);
                poses[slot.renderer] = matrix;
            }
            flush();
            vertexTotal = vertexSum;
            byteTotal = byteSum;
            return created;
        }

        sealed class MeshData
        {
            public readonly Vector3[] positions, normals;
            public readonly Vector4[] tangents;
            public readonly Color32[] colors;
            public readonly Vector2[] uv0, uv1;

            public MeshData(Mesh mesh)
            {
                positions = mesh.vertices;
                normals = mesh.normals;
                tangents = mesh.tangents;
                colors = mesh.colors32;
                uv0 = mesh.uv;
                uv1 = mesh.uv2;
            }
        }
    }
}
