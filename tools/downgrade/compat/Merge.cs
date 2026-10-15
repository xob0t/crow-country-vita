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
// meant this for slow platforms: AbstractRandomElement.ShouldRandomiseAtRuntimeOnSlowPlatforms).
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
// The PS Vita build merges while it writes each scene (DowngradeBuild's CrowMergeScenes calls
// Process); other players can merge at run time, from readable meshes only.
//   -crow-merge report|on|off|merged  report: Analyze each area once it has loaded and log what would
//                               merge (CROW_MERGE lines: candidates, parts and groups, what leaves
//                               renderers out); on: merge at run time as the build does; off: the
//                               build's merged renderers off and their parts on (a build made with
//                               CROW_MERGE=ab, which keeps the parts' meshes), merged: back.
//                               -crow-cycle takes merge=<value> (now, in the loaded scenes).
//   -crow-merge-cell <m>, -crow-merge-max-vertices <n>, -crow-merge-max-kb <n>, -crow-merge-drop
//                               the run-time merge's cell, part size, memory budget, and Drop
//   -crow-merge-check           every second, each merged renderer's parts: still there, active with
//                               it, where they were relative to it, their own renderers off; the parts
//                               that differ are logged once each (CROW_MERGE_CHECK)
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    // The merged renderer's parts, so a run can switch back to them (-crow-merge off).
    public class MergedRenderer : MonoBehaviour
    {
        public Renderer[] parts;
    }

    // -crow-merge-check: every second, whether each merged renderer still stands for its parts: each
    // part still there, active exactly when the merged object is, where it was relative to it (from
    // the first look after its scene loaded) and its own renderer still off. CROW_MERGE_CHECK lines
    // name the parts that differ (once each) and count the checks.
    static class Check
    {
        static readonly Dictionary<Renderer, Matrix4x4> relative = new Dictionary<Renderer, Matrix4x4>();
        static readonly HashSet<Renderer> reported = new HashSet<Renderer>();
        static float next, lastSummary;
        static int checks, differences;

        internal static void Install()
        {
            Application.onBeforeRender += Tick;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                relative.Clear();
                next = Time.realtimeSinceStartup + 3f;
            };
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
                        float moved = 0f;
                        for (int i = 0; i < 16; i++) moved = Math.Max(moved, Math.Abs(now2[i] - first[i]));
                        if (part.gameObject.activeInHierarchy != active) why = "active=" + part.gameObject.activeInHierarchy + " merged=" + active;
                        else if (moved > 0.001f) why = "moved " + moved.ToString("0.###", CultureInfo.InvariantCulture);
                        else if (part.enabled) why = "its renderer is on";
                    }
                    if (why == null || part == null && reported.Contains(group.transform.GetComponent<Renderer>())) continue;
                    differences++;
                    if (part != null && !reported.Add(part)) continue;
                    Debug.Log("CROW_MERGE_CHECK " + (part == null ? Perf.Path(group.transform, 4) + " part" : Perf.Path(part.transform, 5)) + ": " + why);
                }
            }
            if (now < lastSummary + 10f) return;
            lastSummary = now;
            Debug.Log("CROW_MERGE_CHECK groups=" + groups + " checks=" + checks + " differences=" + differences);
        }
    }

    public static class Merge
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
        static string mode;

        internal static void Install(string[] args)
        {
            int index = Array.IndexOf(args, "-crow-merge");
            mode = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            int cell = Array.IndexOf(args, "-crow-merge-cell");
            float metres;
            if (cell >= 0 && cell + 1 < args.Length && float.TryParse(args[cell + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out metres)) Cell = metres;
            int limit = Array.IndexOf(args, "-crow-merge-max-vertices");
            int vertices;
            if (limit >= 0 && limit + 1 < args.Length && int.TryParse(args[limit + 1], out vertices)) MaxPartVertices = vertices;
            if (args.Contains("-crow-merge-check")) Check.Install();
            if (args.Contains("-crow-merge-drop")) Drop = true;
            int budgetAt = Array.IndexOf(args, "-crow-merge-max-kb");
            int kilobytes;
            if (budgetAt >= 0 && budgetAt + 1 < args.Length && int.TryParse(args[budgetAt + 1], out kilobytes)) MaxBytes = kilobytes * 1024L;
            if (mode == null) return;
            SceneManager.sceneLoaded += (scene, loadMode) =>
            {
                if (scene.name == "Logos" || scene.name == "Title" || scene.name == "CrowMara" || scene.name == "CrowGap") return;
                Set(mode, scene);
            };
        }

        // -crow-cycle merge=on|off|report: now, in every loaded scene.
        internal static void Set(string value)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.name != "CrowMara" && scene.name != "DontDestroyOnLoad") Set(value, scene);
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
            Debug.Log("CROW_MERGE " + scene.name.Replace(' ', '_') + " " + (merged ? "merged" : "off") + ": " + groups + " merged renderers, " + parts + " parts");
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
            public int fsms, actions, globals, baked, combined;
            // Animation scripts PlatformSpecificAnimation switches off for good on the PS Vita.
            public readonly HashSet<Component> stillAnimations = new HashSet<Component>();

            public void Add(Transform transform, Mark mark, string reason)
            {
                if (transform == null) return;
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
        const int ObjectReference = 5, FsmGameObject = 19, FsmOwnerDefault = 20, FsmObject = 24, GameObjectParam = 27, FsmProperty = 36, FsmArray = 41;

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
            var analysis = new Analysis();
            Type combinerType = Type.GetType("MeshCombineStudio.MeshCombiner, MeshCombineStudio.Runtime");
            // PlatformSpecificAnimation (PatchPlatformAnimation): the PS Vita takes the Switch's choice
            // (onNS), so the animation scripts it lists stay off, unless an AnimationState lists them.
            if (bake || Application.platform == RuntimePlatform.PSP2)
            {
                var stateDriven = new HashSet<object>();
                foreach (GameObject root in Roots(scene))
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
                foreach (GameObject root in Roots(scene))
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
            foreach (GameObject root in Roots(scene))
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
                    if (Known(component, name, analysis, bake)) continue;
                    // Any other script: it, and what it references, stay as they are.
                    analysis.Add(transform, Mark.Exclude, name);
                    foreach (UnityEngine.Object reference in References(component))
                        analysis.Add(reference, Mark.Exclude, "ref:" + name);
                    int count;
                    scripts.TryGetValue(name, out count);
                    scripts[name] = count + 1;
                }
            return analysis;
        }

        // A MeshCombiner's renderers: what its own search (MeshCombiner.ValidObject with its search
        // conditions: active, enabled, static, layer, tag, components, size and vertex limits) takes,
        // active or not, combining at run time or not, stays for it. When the search can't be asked,
        // all below its search parents stays.
        static MethodInfo validObject;
        static Type objectType;

        static void Combiner(Component combiner, Analysis analysis)
        {
            object options = Field(combiner, "searchOptions");
            var parents = new List<GameObject>();
            var one = Field(options, "parent") as GameObject;
            if (one != null) parents.Add(one);
            var many = Field(options, "parentGOs") as GameObject[];
            if (many != null) parents.AddRange(many.Where(p => p != null));
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
        // on slow platforms: AbstractRandomElement.ShouldRandomiseAtRuntimeOnSlowPlatforms), and each
        // build keeps its own pick; at run time they have run already. Returns whether it knew it.
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
                    if (onEnable) analysis.Add(target, Mark.Anchor, name);
                    else if (onAwake && bake)
                    {
                        MethodInfo random = component.GetType().GetMethod("DoRandom", Instance);
                        FieldInfo awake = component.GetType().GetField("onAwake", Instance);
                        if (random == null || awake == null)
                        {
                            analysis.Add(target, Mark.Anchor, name);
                            return true;
                        }
                        random.Invoke(component, null);
                        awake.SetValue(component, false);
                        analysis.baked++;
                    }
                    return true;
                }
                case "RandomVariant":
                    // Picks one of its variants in Awake (or OnEnable) and switches the others off.
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
                    if (onEnable || (onAwake && !bake && !Application.isPlaying))
                        foreach (object target in targets) analysis.Add(target, mark, name);
                    else if (onAwake && bake)
                    {
                        MethodInfo random = component.GetType().GetMethod("DoRandom", Instance);
                        FieldInfo awake = component.GetType().GetField("onAwake", Instance);
                        if (random == null || awake == null)
                        {
                            foreach (object target in targets) analysis.Add(target, mark, name);
                            return true;
                        }
                        random.Invoke(component, null);
                        awake.SetValue(component, false);
                        analysis.baked++;
                    }
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
                    analysis.Add(target ?? transform, Mark.Anchor, name);
                    return true;
                }
                case "SetPMGlobalGameObject":
                    // Puts its object in a global variable. The game's FSMs only move what globals name
                    // (ElevatorRoom, MineCartRoom, the player, the camera: SetPosition, DOTween...).
                    analysis.Add(Field(component, "gameObject"), Mark.Anchor, name);
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
            var locals = new Dictionary<string, List<object>>();
            var variables = Field(Field(fsm, "variables"), "gameObjectVariables") as Array;
            if (variables != null)
                foreach (object variable in variables)
                {
                    string name = Field(variable, "name") as string;
                    if (name != null) locals[name] = new List<object> { Field(variable, "value") };
                }
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
                            : kind == FsmProperty ? "fsmPropertyParams" : kind == FsmArray ? "fsmArrayParams" : null;
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
                        else if (copies) values.AddRange(Values(source, locals));
                        else if (fromArray) values.AddRange(arrayItems);
                        // Children found by GetChild and FindChild are below an object marked exclude;
                        // created objects are new.
                    }
                }
            }
            string ownerPath = Perf.Path(owner.transform, 3);
            foreach (KeyValuePair<string, List<KeyValuePair<string, object>>> entry in actionsList)
            {
                string action = entry.Key;
                // ActivateChildren and DestroyChildren switch or destroy all its children (a merged object
                // placed directly under it too, with the rest); the others count or search them.
                Mark mark = AnchorActions.Contains(action) || action.StartsWith("DOTweenTransform") || action == "ActivateChildren" || action == "DestroyChildren"
                    ? Mark.Anchor : OwnActions.Contains(action) ? Mark.Own : ChildActions.Contains(action) ? Mark.NoChild : Mark.None;
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
                if (mark == Mark.None) continue;
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
                        if (!locals.ContainsKey(name))
                        {
                            analysis.globals++;
                            analysis.Unknown("global " + name + " " + action);
                        }
                        else if (found.Contains(name)) analysis.Unknown(ownerPath + " " + action + " " + name);
                        List<object> parents;
                        if (mark != Mark.NoChild && foundUnder.TryGetValue(name, out parents))
                            foreach (object parent in parents) analysis.Add(parent, Mark.Exclude, action + " found child");
                    }
                    foreach (object target in Values(value, locals)) analysis.Add(target, mark, action);
                }
            }
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
            Analysis analysis = Analyze(scene, scripts, bake);
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
                    if (why == null)
                    {
                        if (mesh == null || mesh.vertexCount == 0) why = "no mesh";
                        else if (renderer.isPartOfStaticBatch) why = "static batch";
                        else if (renderer.lightmapIndex >= 0 && renderer.lightmapIndex < 65534) why = "lightmap";
                        else if (materials.Length != mesh.subMeshCount) why = "materials != submeshes";
                        else if (materials.Any(m => m == null || m.renderQueue > 2500 || m.GetTag("DisableBatching", false) == "True")) why = "material";
                        else if (Enumerable.Range(0, mesh.subMeshCount).Any(s => mesh.GetTopology(s) != MeshTopology.Triangles)) why = "topology";
                        else if (canRead == null ? !mesh.isReadable : !canRead(mesh)) why = "unreadable";
                        else if (renderer.probeAnchor != null) why = "probe anchor";
                        else if (mesh.vertexCount > MaxPartVertices) why = "big";
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
                    string common = (anchor == null ? 0 : anchor.GetInstanceID()) + "|" + renderer.gameObject.layer + "|" + (int)renderer.shadowCastingMode + "|" +
                                    renderer.receiveShadows + "|" + (int)renderer.lightProbeUsage + "|" + (int)renderer.reflectionProbeUsage + "|" +
                                    Channels(mesh) + "|" + lit + "|" + cell;
                    for (int s = 0; s < materials.Length; s++)
                    {
                        string key = materials[s].GetInstanceID() + "|" + common;
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
                             .Select(g => (g.Key == null ? "scene" : Perf.Path(g.Key, 2)) + "=" + g.Count() + "/" + g.Select(s => s.material).Distinct().Count()).ToArray()) +
                         " singles by anchor: " + string.Join(" ", groups.Values.Where(g => g.Count == 1).GroupBy(g => g[0].anchor).OrderByDescending(g => g.Count()).Take(12)
                             .Select(g => (g.Key == null ? "scene" : Perf.Path(g.Key, 2) + "(" + why(analysis, g.Key) + ")") + "=" + g.Count()).ToArray());
            }
            var merging = groups.Values.Where(g => g.Count > 1).ToList();
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
            foreach (List<Slot> group in groups.Values.Where(g => g.Count == 1))
                merged.Remove(group[0].renderer);
            for (int i = 0; i < merging.Count; i++)
            {
                merging[i] = merging[i].Where(s => merged.Contains(s.renderer)).ToList();
                if (merging[i].Count < 2)
                {
                    foreach (Slot slot in merging[i]) merged.Remove(slot.renderer);
                    merging.RemoveAt(i--);
                }
            }
            // A renderer left with some slots unmerged (its other slots' groups shrank) stays whole.
            foreach (List<Slot> group in merging) group.RemoveAll(s => !merged.Contains(s.renderer));
            merging.RemoveAll(g => g.Count < 2);
            // The memory budget: estimated from the parts' meshes (vertices of the slot's submesh range
            // at most the mesh's, 40 bytes each, and 2 per index).
            long budget = 0;
            int overBudget = 0;
            var kept = new List<List<Slot>>();
            foreach (List<Slot> group in merging.OrderByDescending(g => (g.Count - 1) / (double)Math.Max(1L, Estimate(g))))
            {
                long estimate = Estimate(group);
                if (budget + estimate > MaxBytes)
                {
                    overBudget += group.Count;
                    foreach (Slot slot in group) merged.Remove(slot.renderer);
                    continue;
                }
                budget += estimate;
                kept.Add(group);
            }
            merging = kept;
            foreach (List<Slot> group in merging) group.RemoveAll(s => !merged.Contains(s.renderer));
            merging.RemoveAll(g => g.Count < 2);
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
            long vertices = 0, bytes = 0;
            int meshes = 0, dropped = 0;
            long droppedBytes = 0;
            var created = new List<GameObject>();
            if (apply)
            {
                foreach (List<Slot> group in merging)
                {
                    long groupBytes;
                    int groupVertices;
                    created.AddRange(Build(scene, group, out groupVertices, out groupBytes));
                    vertices += groupVertices;
                    bytes += groupBytes;
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
                .Append(" baked=").Append(analysis.baked).Append(" forMeshCombiner=").Append(analysis.combined).Append(" fsms=").Append(analysis.fsms).Append(" actions=").Append(analysis.actions).Append(" marked=").Append(analysis.marks.Count)
                .Append(" ms=").Append(clock.ElapsedMilliseconds).Append('\n');
            if (spread.Length > 0) report.Append("CROW_MERGE ").Append(sceneName).Append(spread).Append('\n');
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
            return report.ToString();
        }

        static string why(Analysis analysis, Transform t)
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
            var indices = new List<int>();
            var parts = new List<Renderer>();
            string channels = Channels(first.mesh);
            bool hasN = channels.Contains("n"), hasT = channels.Contains("t"), hasC = channels.Contains("c"), has0 = channels.Contains("0"), has1 = channels.Contains("1");
            Action flush = () =>
            {
                if (indices.Count == 0) return;
                var mesh = new Mesh { name = "CrowMerged " + first.material.name };
                mesh.SetVertices(positions);
                if (hasN) mesh.SetNormals(normals);
                if (hasT) mesh.SetTangents(tangents);
                if (hasC) mesh.SetColors(colors);
                if (has0) mesh.SetUVs(0, uv0);
                if (has1) mesh.SetUVs(1, uv1);
                mesh.SetTriangles(indices, 0);
                mesh.RecalculateBounds();
                var go = new GameObject("CrowMerged");
                if (anchor != null) go.transform.SetParent(anchor, false);
                else SceneManager.MoveGameObjectToScene(go, scene);
                go.layer = first.renderer.gameObject.layer;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = first.material;
                renderer.shadowCastingMode = first.renderer.shadowCastingMode;
                renderer.receiveShadows = first.renderer.receiveShadows;
                renderer.lightProbeUsage = first.renderer.lightProbeUsage;
                renderer.reflectionProbeUsage = first.renderer.reflectionProbeUsage;
                go.AddComponent<MergedRenderer>().parts = parts.Distinct().ToArray();
                created.Add(go);
                vertexSum += positions.Count;
                byteSum += positions.Count * (12L + (hasN ? 12 : 0) + (hasT ? 16 : 0) + (hasC ? 4 : 0) + (has0 ? 8 : 0) + (has1 ? 8 : 0)) + indices.Count * 2L;
                positions.Clear();
                normals.Clear();
                tangents.Clear();
                colors.Clear();
                uv0.Clear();
                uv1.Clear();
                indices.Clear();
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
