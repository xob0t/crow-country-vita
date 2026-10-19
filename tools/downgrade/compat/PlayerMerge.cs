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

// The player (Mara and what she carries) in fewer draws, as the enemy rebuild (EnemyRebuild.cs) does for the
// enemies: the PS Vita build (unity/PlayerMerge.cs, CROW_PLAYER_MERGE=on|ab|off) merges the player's scene
// (CrowMara, the prefab's instance) as it writes it, the parts below each anchor into one renderer with a submesh
// per material, then an atlas per material group (PlayerProcess below). The analysis is the merge's (Merge.cs)
// with the player's rules (KnownPlayer, ReadOnlyActions, the run-time random picks) and what every other scene
// does through globals and names (PlayerUses, written as the build writes each scene). Run time:
//   -crow-player-merge on|off|noatlas, pmerge=  (builds made with ab) off: the player's merged renderers off and
//                               their parts on; noatlas: merged, with their materials before the atlas; on: as built
//   -crow-player-merge-check     every second, each part of the player's merged renderers against the pose it was
//                               merged at, its activity, renderer and materials (PlayerMergeCheck below);
//                               -crow-merge-check (Merge.cs) checks them as it does the areas'
//   -crow-player-shots <s> <dir> [n]  close-ups of the player merged and not, in the same frame (ab builds;
//                               PlayerShots below; with -crow-player-shots-groups, every merged renderer
//                               alone against its parts, shown or not, the first time)
//   -crow-player-merge-report <s>  s seconds after each area load: every renderer of the player's
//                               (CharacterCameraPrefab's, but the menus' layer 5) that is on, with whether
//                               it was drawn, its materials, the anchor the analysis gives it (the player's
//                               FSMs and scripts read with the area's) and what keeps it out
//                               (CROW_PLAYER_MERGE_PART); the drawn ones by anchor (CROW_PLAYER_MERGE_ANCHOR)
//                               and an estimate of the draws a merge and an atlas would leave (CROW_PLAYER_MERGE)
//   -crow-player-merge-dry <dir> the build's analysis of the player's scene as it loads, with the uses in dir
//                               (work/player-uses), reporting what it would merge (CROW_PLAYER_MERGE dry)
//   pmergetest=hide|show        (-crow-fight-cycle / -crow-cycle / -crow-ab; a cost test, it changes the
//                               picture) of each group the player's own analysis would merge (worked out at the
//                               first use), all renderers but the first switched off; show puts them back
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static partial class Merge
    {
        static float playerReportAfter = -1f, playerReportAt = -1f;

        internal static void InstallPlayer(string[] args)
        {
            InstallPlayerSwitch(args);
            PlayerShots.Install(args);
            PlayerMergeCheck.Install(args);
            playerReportAfter = CrowFlags.Number(args, "-crow-player-merge-report", -1f);
            if (playerReportAfter < 0f) return;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (scene.name == "Logos" || scene.name == "Title" || scene.name == "CrowMara" || scene.name == "CrowGap") return;
                playerReportAt = Time.realtimeSinceStartup + playerReportAfter;
            };
            Application.onBeforeRender += () =>
            {
                if (playerReportAt < 0f || Time.realtimeSinceStartup < playerReportAt) return;
                playerReportAt = -1f;
                ReportPlayer();
            };
        }

        internal static void ReportPlayer()
        {
            try
            {
                foreach (string line in PlayerReport().Split('\n')) if (line.Length > 0) Debug.Log(line);
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_PLAYER_MERGE failed: " + exception);
            }
        }

        // The player's scripts the scene rules (Known) leave unknown, read from the game's code; only for the
        // player's analysis, so the areas' merge and split stay as they were checked.
        static bool playerRules;
        // Build keeps the parts' poses (MergedRenderer.poses) for the player's check.
        static bool recordPoses;

        static bool KnownPlayer(Component component, string name, Analysis analysis)
        {
            switch (name)
            {
                case "CharacterAndCamera":
                    // SetPosition moves character, camera and cameraRotateMe; Activate, Deactivate and Reset switch
                    // its own object; Reset runs the resetters (judged on their own) after DOTween.Clear.
                    analysis.Add(component.transform, Mark.Anchor, name);
                    foreach (string field in new[] { "character", "camera", "cameraRotateMe" }) analysis.Add(Field(component, field), Mark.Anchor, name);
                    return true;
                case "PMGlobalFloatToRotation":  // sets its target's local rotation from a global, every frame
                case "ActivateGameObject":  // com.sfbgames.general.utils': switches its target on
                    analysis.Add(Field(component, "target"), Mark.Anchor, name);
                    return true;
                case "PMGlobalVectorsToTransform":  // sets its gameObject's position and rotation from globals
                    analysis.Add(Field(component, "gameObject"), Mark.Anchor, name);
                    return true;
                case "PMFollow":  // moves itself towards a global's object
                    analysis.Add(component.transform, Mark.Anchor, name);
                    return true;
                case "PMGlobalBoolsToState":
                    AnchorAll(component, analysis, name, "statesTrue", "statesFalse");
                    return true;
                case "PMKeyOrCon":
                    AnchorAll(component, analysis, name, "keyboardObjects", "controllerObjects");
                    return true;
                case "PMGlobalIntToState":  // switches its states' objects by a global int
                {
                    var states = Field(component, "states") as IList;
                    if (states != null)
                        foreach (object state in states)
                        {
                            var objects = Field(state, "gameObjects") as IList;
                            if (objects != null) foreach (object item in objects) analysis.Add(item, Mark.Anchor, name);
                        }
                    return true;
                }
                case "ActiveResetter":  // at a reset, its points' objects back to their activity
                case "TransformResetter":  // at a reset, its points' transforms back to their local poses
                {
                    // A point already as it records (the prefab's state) only changes back what something else
                    // changed, which marks it itself; one that differs is switched or moved.
                    var points = Field(component, "resetPoints") as IList;
                    if (points == null) return true;
                    foreach (object point in points)
                    {
                        if (name == "ActiveResetter")
                        {
                            var target = Field(point, "gameObject") as GameObject;
                            if (target != null && target.activeSelf != (bool)(Field(point, "active") ?? !target.activeSelf)) analysis.Add(target, Mark.Anchor, name);
                            continue;
                        }
                        var t = Field(point, "transform") as Transform;
                        if (t == null) continue;
                        object position = Field(point, "localPosition"), rotation = Field(point, "localRotation"), scale = Field(point, "localScale");
                        if (!(position is Vector3) || !(rotation is Quaternion) || !(scale is Vector3) || t.localPosition != (Vector3)position ||
                            t.localRotation != (Quaternion)rotation || t.localScale != (Vector3)scale)
                            analysis.Add(t, Mark.Anchor, name);
                    }
                    return true;
                }
                case "RuntimeReparent":  // goes below newParent in its Awake
                    analysis.Add(component.transform, Mark.Anchor, name);
                    return true;
                case "RigidbodyResetter":  // velocities and isKinematic of its bodies (anchors already)
                case "CloneRestter":  // destroys the "(Clone)" objects below its targets: what the game spawned
                case "PMTransformToGlobalVectors":  // reads its gameObject's pose into globals
                case "SetPMGlobalFloat":
                case "SetPMGlobalBool":
                case "PlatformLightSettings":  // a light's settings per platform
                case "PlayRandomClipAtInterval":
                case "SetVideoClip":  // its VideoPlayer's clip (the player's screen: marked by VideoPlayer)
                case "HideMouse":
                case "ShowFramerate":
                case "CrowCountryKeyMapCheck":
                case "KeyboardOrControllerCheck":
                case "CrowCountryCamEffect":  // the camera's image effect
                case "SplitMirror":  // Perf.cs' PlayerSplit: switches the split menus with its own object
                    return true;
            }
            return false;
        }

        static List<List<MeshRenderer>> testGroups;
        static readonly List<Renderer> testHidden = new List<Renderer>();

        // A -crow-cycle / -crow-fight-cycle / -crow-ab change (from EnemyCost.Change); false when it isn't one.
        internal static bool ChangePlayer(string key, string value)
        {
            if (key == "pmerge")
            {
                Debug.Log(SetPlayer(value));
                return true;
            }
            if (key != "pmergetest") return false;
            foreach (Renderer renderer in testHidden)
                if (renderer != null) renderer.enabled = true;
            testHidden.Clear();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (value == "hide")
            {
                if (testGroups == null) testGroups = PlayerGroups();
                foreach (List<MeshRenderer> group in testGroups)
                    foreach (MeshRenderer renderer in group.Skip(1))
                        if (renderer != null && renderer.enabled)
                        {
                            renderer.enabled = false;
                            testHidden.Add(renderer);
                        }
            }
            Debug.Log("CROW_PLAYER_MERGE_TEST " + value + ": groups=" + (testGroups == null ? 0 : testGroups.Count) + " hidden=" + testHidden.Count + " ms=" + clock.ElapsedMilliseconds);
            return true;
        }

        // The groups of the player's enabled renderers that the merge would make one renderer (two or more),
        // by the player's own analysis.
        static List<List<MeshRenderer>> PlayerGroups()
        {
            Transform player = PlayerRoot();
            var result = new List<List<MeshRenderer>>();
            if (player == null) return result;
            playerRules = true;
            Analysis analysis;
            try
            {
                analysis = Analyze(new List<GameObject> { player.gameObject }, new Dictionary<string, int>(), false);
                RandomAnchors(new[] { player.gameObject }, analysis);
            }
            finally
            {
                playerRules = false;
            }
            var groups = new Dictionary<string, List<MeshRenderer>>();
            foreach (MeshRenderer renderer in player.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (!renderer.enabled || renderer.gameObject.layer == 5) continue;
                Transform anchor;
                if (Blocker(renderer, analysis, out anchor) != null) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter == null ? null : filter.sharedMesh;
                Material[] materials = renderer.sharedMaterials;
                if (mesh == null || mesh.vertexCount == 0 || materials.Length != mesh.subMeshCount || materials.Any(m => m == null || m.renderQueue > 2500)) continue;
                string key = (anchor == null ? 0 : anchor.GetInstanceID()) + "|" + renderer.gameObject.layer + "|" + (int)renderer.shadowCastingMode + "|" +
                             renderer.receiveShadows + "|" + (int)renderer.lightProbeUsage + "|" + (int)renderer.reflectionProbeUsage;
                List<MeshRenderer> list;
                if (!groups.TryGetValue(key, out list)) groups[key] = list = new List<MeshRenderer>();
                list.Add(renderer);
            }
            result.AddRange(groups.Values.Where(g => g.Count > 1));
            return result;
        }

        // Actions the merge's lists don't name that only read what they act on (its pose, name, tag, variables,
        // components' values) or change what the merge doesn't depend on (another FSM's variables, sound,
        // lights, particles, physics events): the player's analysis leaves their targets alone; any other
        // unknown action excludes its target (Merge.AnalyzeFsm), but SelfActions', which anchor it.
        static readonly HashSet<string> ReadOnlyActions = new HashSet<string>
        {
            "GetPosition", "GetRotation", "GetScale", "GetWorldDistance", "GetDistance", "GetName", "GetTag", "GetLayer", "GetParent", "GetOwner",
            "GameObjectCompare", "GameObjectCompareTag", "GameObjectIsNull", "GameObjectIsChildOf", "SetTag",
            "SetGameObject",  // what it copies is followed by the first pass (Merge.AnalyzeFsm)
            "GetFsmBool", "GetFsmInt", "GetFsmFloat", "GetFsmString", "GetFsmGameObject", "GetFsmVector3", "SetFsmBool", "SetFsmInt",
            "SetFsmFloat", "SetFsmString", "SetFsmGameObject", "SetFsmVector3",
            "AudioPlay", "AudioStop", "AudioPause", "AudioMute", "SetAudioPitch", "SetAudioVolume", "SetAudioClip", "SetAudioLoop", "PlaySound",
            "PlayRandomSound", "ParticleSystemEmit", "SetSpriteColorRGBA",
            "CreateObject", "CreateObjectAdvanced",  // clone their gameObject (a template: the clone has what it has) below their parent
            "TriggerEvent", "CollisionEvent", "Raycast", "Raycast2IgnoreTriggers", "RaycastAdvanced", "GetVelocity", "GetSpeed",
            "GetAngularVelocity", "DOTweenSpriteRendererColor",
        };

        // EnableBehaviour switches a script (not a renderer: Behaviour only) on the object it acts on; the scripts
        // are judged as if always on, so only the object itself is taken to move.
        static readonly HashSet<string> SelfActions = new HashSet<string> { "EnableBehaviour" };

        static Mark PlayerUnknownMark(string action)
        {
            return ReadOnlyActions.Contains(action) ? Mark.None : SelfActions.Contains(action) ? Mark.Anchor : Mark.Exclude;
        }

        // ---------------------------------------------------------------- the build (unity/PlayerMerge.cs)

        // What a scene does to objects it can only reach through globals or names (the player's, whose own scene
        // is processed last): one line per global use, "use<tab>global<tab>mark<tab>action" (the player's rules:
        // an action the lists don't know marks Exclude, as does a copy of a global into a variable), and per
        // name or tag a find action looks for, "find<tab>name" ("find<tab>*" for a name from a variable).
        public static string PlayerUses(Scene scene)
        {
            Analysis analysis;
            playerRules = true;
            try
            {
                analysis = Analyze(Roots(scene), new Dictionary<string, int>(), false);
            }
            finally
            {
                playerRules = false;
            }
            var lines = new SortedSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, KeyValuePair<Mark, string>> use in analysis.globalUses)
                lines.Add("use\t" + use.Key + "\t" + (int)use.Value.Key + "\t" + use.Value.Value);
            findVariables.Clear();
            foreach (string name in FindNames(Roots(scene))) lines.Add("find\t" + name);
            foreach (string where in findVariables) lines.Add("findvar\t" + where);
            return string.Join("\n", lines.ToArray());
        }

        // The names and tags the find actions of the FSMs below these roots look for (FindGameObject,
        // FindChild, FindGameObjectsWithTag...: their string parameters). One from a variable of the FSM's own
        // may be any string the FSM holds (its string variables, its actions' string parameters), so it takes
        // them all; one from a global, "*" (anything). findVariables names the FSMs that find by a variable.
        static readonly List<string> findVariables = new List<string>();

        static HashSet<string> FindNames(IEnumerable<GameObject> roots, bool sceneWide = false)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (GameObject root in roots)
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || behaviour.GetType().Name != "PlayMakerFSM") continue;
                    object fsm = Field(behaviour, "fsm");
                    var states = Field(fsm, "states") as Array;
                    if (states == null) continue;
                    var own = new HashSet<string>(StringComparer.Ordinal);
                    var held = new HashSet<string>(StringComparer.Ordinal);
                    var stringVariables = Field(Field(fsm, "variables"), "stringVariables") as Array;
                    if (stringVariables != null)
                        foreach (object variable in stringVariables)
                        {
                            var name = Field(variable, "name") as string;
                            if (name != null) own.Add(name);
                            var value = Field(variable, "value") as string;
                            if (!string.IsNullOrEmpty(value)) held.Add(value);
                        }
                    foreach (object state in states)
                    {
                        object data = Field(state, "actionData");
                        foreach (string list in new[] { "fsmStringParams", "stringParams" })
                        {
                            var items = Field(data, list) as IList;
                            if (items == null) continue;
                            foreach (object item in items)
                            {
                                var text = list == "stringParams" ? item as string : Field(item, "value") as string;
                                if (!string.IsNullOrEmpty(text)) held.Add(text);
                            }
                        }
                    }
                    foreach (object state in states)
                    {
                        object data = Field(state, "actionData");
                        var actions = Field(data, "actionNames") as IList;
                        var starts = Field(data, "actionStartIndex") as IList;
                        var types = Field(data, "paramDataType") as IList;
                        var positions = Field(data, "paramDataPos") as IList;
                        var strings = Field(data, "fsmStringParams") as IList;
                        if (actions == null || starts == null || types == null || positions == null) continue;
                        for (int a = 0; a < actions.Count; a++)
                        {
                            string action = ShortName(actions[a] as string);
                            if (!action.StartsWith("Find", StringComparison.Ordinal) && action.IndexOf("WithTag", StringComparison.Ordinal) < 0) continue;
                            if (sceneWide && action == "FindChild") continue;  // LevelAtlas.cs: below the FSM's own objects
                            int first = a < starts.Count ? Convert.ToInt32(starts[a]) : types.Count;
                            int last = a + 1 < starts.Count ? Convert.ToInt32(starts[a + 1]) : types.Count;
                            for (int p = first; p < last && p < types.Count; p++)
                            {
                                if (Convert.ToInt32(types[p]) != FsmStringParam || strings == null) continue;
                                int at = Convert.ToInt32(positions[p]);
                                if (at < 0 || at >= strings.Count) continue;
                                object value = strings[at];
                                bool variable = (bool)(Field(value, "useVariable") ?? false);
                                string text = Field(value, "value") as string;
                                if (variable)
                                {
                                    string name = Field(value, "name") as string ?? "";
                                    findVariables.Add(Perf.Path(behaviour.transform, 3) + ":" + action + ":" + name + (own.Contains(name) ? "" : "(global)"));
                                    if (own.Contains(name)) names.UnionWith(held);
                                    else names.Add("*");
                                }
                                else if (!string.IsNullOrEmpty(text)) names.Add(text);
                            }
                        }
                    }
                }
            return names;
        }

        const int FsmStringParam = 18;  // ParamDataType.FsmString

        // The player's scene (CrowMara: the prefab's inactive instance) as the build writes it: the parts below each
        // anchor become one renderer with a submesh per material, as the enemies' do (Process' enemy mode: whatever
        // their lights and cell; parts up to EnemyMaxPartVertices), then an atlas per material group
        // (EnemyRebuild.cs' Atlas). The analysis is the scene's own with the player's rules (KnownPlayer, unknown
        // actions exclude their targets), and what the other scenes do through globals (uses, from PlayerUses: a
        // move or switch anchors what the global holds, a material change keeps its renderer, anything else keeps
        // all below it as it is) or by name (finds: those objects and all below them stay). ab keeps the parts'
        // meshes and the atlas records, so -crow-player-merge off and noatlas work. Returns the report.
        public static string PlayerProcess(Scene scene, IEnumerable<string> uses, bool ab, Func<Mesh, bool> canRead)
        {
            return PlayerProcess(scene, uses, ab, canRead, true);
        }

        // apply false: the analysis and groups only (-crow-player-merge-dry), reading no mesh data.
        static string PlayerProcess(Scene scene, IEnumerable<string> uses, bool ab, Func<Mesh, bool> canRead, bool apply)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            List<GameObject> roots = Roots(scene);
            string sceneName = scene.name.Replace(' ', '_');
            Analysis analysis;
            playerRules = true;
            collectReferences = true;
            try
            {
                analysis = Analyze(roots, new Dictionary<string, int>(), false);
            }
            finally
            {
                playerRules = false;
                collectReferences = false;
            }
            var all = roots.SelectMany(r => r.GetComponentsInChildren<Transform>(true)).ToList();
            RandomAnchors(roots, analysis);
            int globalMarks = 0, foundMarks = 0;
            findVariables.Clear();
            var finds = FindNames(roots);
            foreach (string line in uses)
            {
                string[] fields = line.Split('\t');
                if (fields.Length >= 4 && fields[0] == "use")
                {
                    List<object> values;
                    if (!analysis.globalValues.TryGetValue(fields[1], out values)) continue;
                    var mark = (Mark)int.Parse(fields[2], CultureInfo.InvariantCulture);
                    Mark applied = mark == Mark.Anchor || mark == Mark.Own ? mark : Mark.Exclude;
                    foreach (object value in values.ToList())
                    {
                        analysis.Add(value, applied, "area:" + fields[3]);
                        globalMarks++;
                    }
                }
                else if (fields.Length >= 2 && fields[0] == "find") finds.Add(fields[1]);
            }
            if (finds.Contains("*"))
                return "CROW_PLAYER_MERGE scene=" + sceneName + " skipped: a find action looks for a name from a global: " +
                       string.Join(" ", uses.Where(u => u.StartsWith("findvar\t", StringComparison.Ordinal) && u.EndsWith("(global)", StringComparison.Ordinal)).Concat(findVariables.Where(f => f.EndsWith("(global)", StringComparison.Ordinal))).Distinct().ToArray());
            foreach (Transform t in all)
                if (finds.Contains(t.name) || (t.tag != "Untagged" && finds.Contains(t.tag)))
                {
                    analysis.Add(t, Mark.Exclude, "found by name");
                    foundMarks++;
                }
            var blockers = new Dictionary<string, int>();
            var excluders = new Dictionary<string, int>();
            var groups = new Dictionary<string, List<Slot>>();
            int renderers = 0, candidates = 0;
            foreach (GameObject root in roots)
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
                        else if (anchor == null) why = "no anchor";
                        else if (renderer.gameObject.layer == 5) why = "ui layer";  // the menus' 3D objects, drawn only under the menus
                        else if (renderer.isPartOfStaticBatch) why = "static batch";
                        else if (renderer.lightmapIndex >= 0 && renderer.lightmapIndex < 65534) why = "lightmap";
                        else if (materials.Length != mesh.subMeshCount) why = "materials != submeshes";
                        else if (materials.Any(m => m == null || m.renderQueue > 2500 || m.GetTag("DisableBatching", false) == "True")) why = "material";
                        else if (Enumerable.Range(0, mesh.subMeshCount).Any(s => mesh.GetTopology(s) != MeshTopology.Triangles)) why = "topology";
                        else if (canRead == null ? !mesh.isReadable : !canRead(mesh)) why = "unreadable";
                        else if (renderer.probeAnchor != null) why = "probe anchor";
                        else if (mesh.vertexCount > EnemyMaxPartVertices) why = "big";
                    }
                    if (why != null)
                    {
                        string key = why.Length > 60 ? why.Substring(0, 60) : why;
                        int count;
                        blockers.TryGetValue(key, out count);
                        blockers[key] = count + 1;
                        if (why.StartsWith("exclude:", StringComparison.Ordinal))
                            for (Transform t = renderer.transform; t != null; t = t.parent)
                            {
                                Mark mark;
                                if (!analysis.marks.TryGetValue(t, out mark) || (mark & Mark.Exclude) == 0) continue;
                                string at = Perf.Path(t, 4) + "[" + analysis.why[t].Replace(' ', '_') + "]";
                                excluders.TryGetValue(at, out count);
                                excluders[at] = count + 1;
                                break;
                            }
                        continue;
                    }
                    candidates++;
                    string common = "P|" + anchor.GetInstanceID() + "|" + renderer.gameObject.layer + "|" + (int)renderer.shadowCastingMode + "|" + renderer.receiveShadows + "|" +
                                    (int)renderer.lightProbeUsage + "|" + (int)renderer.reflectionProbeUsage + "|" + (apply ? Channels(mesh) : "?");
                    List<Slot> slots;
                    if (!groups.TryGetValue(common, out slots)) groups[common] = slots = new List<Slot>();
                    for (int s = 0; s < materials.Length; s++)
                        slots.Add(new Slot { renderer = renderer, mesh = mesh, subMesh = s, material = materials[s], anchor = anchor });
                }
            var merging = groups.Values.Where(g => g.Select(x => x.renderer).Distinct().Count() > 1).ToList();
            var merged = new HashSet<MeshRenderer>();
            var created = new List<GameObject>();
            long bytes = 0, vertices = 0;
            if (!apply)
                return "CROW_PLAYER_MERGE dry scene=" + sceneName + " renderers=" + renderers + " candidates=" + candidates + " groups=" + merging.Count +
                       " parts=" + merging.Sum(g => g.Select(x => x.renderer).Distinct().Count()) + " submeshes=" + merging.Sum(g => g.Select(x => x.material).Distinct().Count()) +
                       " globalMarks=" + globalMarks + " foundMarks=" + foundMarks + " finds=" + finds.Count + " ms=" + clock.ElapsedMilliseconds +
                       "\nCROW_PLAYER_MERGE " + sceneName + " left out: " + string.Join(" ", blockers.OrderByDescending(p => p.Value).Take(30).Select(p => p.Key.Replace(' ', '_') + "=" + p.Value).ToArray()) +
                       "\nCROW_PLAYER_MERGE " + sceneName + " excluded below: " + string.Join(" ", excluders.OrderByDescending(p => p.Value).Take(25).Select(p => p.Key + "=" + p.Value).ToArray()) +
                       "\nCROW_PLAYER_MERGE " + sceneName + " groups: " + string.Join(" ", merging.Select(g => Perf.Path(g[0].anchor, 3) + "=" + g.Select(x => x.renderer).Distinct().Count() + "/" +
                                                                                                                  g.Select(x => x.material).Distinct().Count()).ToArray());
            recordPoses = true;
            try
            {
                foreach (List<Slot> group in merging)
                {
                    long groupBytes;
                    int groupVertices;
                    created.AddRange(Build(scene, group, out groupVertices, out groupBytes));
                    bytes += groupBytes;
                    vertices += groupVertices;
                    foreach (Slot slot in group) merged.Add(slot.renderer);
                }
            }
            finally
            {
                recordPoses = false;
            }
            foreach (MeshRenderer renderer in merged) renderer.enabled = false;
            int dropped = 0;
            long droppedBytes = 0;
            if (!ab) DropMeshes(scene, merged, out dropped, out droppedBytes);
            string atlas;
            try
            {
                atlas = Atlas(sceneName, "Player", created, ab);
            }
            catch (Exception exception)
            {
                atlas = "CROW_ENEMY_ATLAS scene=" + sceneName + " type=Player failed: " + exception.ToString().Replace('\n', ' ');
            }
            int submeshes = created.Sum(go => go.GetComponent<MeshRenderer>().sharedMaterials.Length);
            return "CROW_PLAYER_MERGE scene=" + sceneName + " mode=" + (ab ? "ab" : "on") + " renderers=" + renderers + " candidates=" + candidates + " parts=" + merged.Count +
                   " groups=" + merging.Count + " merged=" + created.Count + " submeshes=" + submeshes + " vertices=" + vertices + " bytes=" + bytes +
                   " dropped=" + dropped + " droppedBytes=" + droppedBytes + " globalMarks=" + globalMarks + " foundMarks=" + foundMarks + " finds=" + finds.Count +
                   " uses=" + uses.Count() + " marked=" + analysis.marks.Count + " ms=" + clock.ElapsedMilliseconds +
                   "\nCROW_PLAYER_MERGE " + sceneName + " left out: " + string.Join(" ", blockers.OrderByDescending(p => p.Value).Take(30).Select(p => p.Key.Replace(' ', '_') + "=" + p.Value).ToArray()) +
                   "\nCROW_PLAYER_MERGE " + sceneName + " excluded below: " + string.Join(" ", excluders.OrderByDescending(p => p.Value).Take(25).Select(p => p.Key + "=" + p.Value).ToArray()) +
                   "\nCROW_PLAYER_MERGE " + sceneName + " groups: " + string.Join(" ", merging.Select(g => Perf.Path(g[0].anchor, 2) + "=" + g.Select(x => x.renderer).Distinct().Count() + "/" +
                                                                                                           g.Select(x => x.material).Distinct().Count()).ToArray()) +
                   "\n" + atlas;
        }

        // -crow-player-merge on|off|noatlas, pmerge= (builds made with CROW_PLAYER_MERGE=ab): the player's merged
        // renderers off and their parts on, or the merged renderers without their atlas (EnemyAtlasRecord's
        // originals), or as built.
        static string playerMode;

        internal static string PlayerMode
        {
            get { return playerMode; }
        }

        internal static string SetPlayer(string value)
        {
            Transform player = null;
            int groups = 0, parts = 0, records = 0;
            foreach (MergedRenderer group in Resources.FindObjectsOfTypeAll<MergedRenderer>())
            {
                if (group == null || !group.gameObject.scene.IsValid() || !group.transform.root.name.StartsWith("CharacterCameraPrefab", StringComparison.Ordinal)) continue;
                player = group.transform.root;
                bool merged = value != "off";
                var renderer = group.GetComponent<MeshRenderer>();
                if (renderer != null) renderer.enabled = merged;
                groups++;
                if (group.parts != null)
                    foreach (Renderer part in group.parts)
                        if (part != null)
                        {
                            part.enabled = !merged;
                            parts++;
                        }
                var record = group.GetComponent<EnemyAtlasRecord>();
                if (record != null && record.Set(value != "noatlas")) records++;
            }
            playerMode = value;
            return "CROW_PLAYER_MERGE " + value + ": " + groups + " merged renderers, " + parts + " parts, " + records + " atlas records" + (player == null ? " (no player)" : "");
        }

        static void InstallPlayerSwitch(string[] args)
        {
            string dry = CrowFlags.Value(args, "-crow-player-merge-dry");
            if (dry != null)
                SceneManager.sceneLoaded += (scene, mode) =>
                {
                    if (scene.name != "CrowMara") return;
                    try
                    {
                        var lines = new List<string>();
                        foreach (string file in System.IO.Directory.GetFiles(dry, "*.txt")) lines.AddRange(System.IO.File.ReadAllLines(file));
                        foreach (string line in PlayerProcess(scene, lines, true, m => true, false).Split('\n')) Debug.Log(line);
                    }
                    catch (Exception exception)
                    {
                        Debug.Log("CROW_PLAYER_MERGE dry failed: " + exception);
                    }
                };
            string value = CrowFlags.Value(args, "-crow-player-merge");
            if (value == null || value == "on") return;
            playerMode = value;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (scene.name == "CrowMara" || (scene.name != "Logos" && scene.name != "Title" && scene.name != "CrowGap")) Debug.Log(SetPlayer(playerMode));
            };
        }

        // The game's random picks the player's objects make at run time (in Awake or OnEnable: the blood stains'
        // turns, made again in every clone) move what they pick for; at build time Known leaves those of Awake to
        // the scenes' bake, which the player doesn't get.
        static void RandomAnchors(IEnumerable<GameObject> roots, Analysis analysis)
        {
            foreach (GameObject root in roots)
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null) continue;
                    string name = behaviour.GetType().Name;
                    if (name != "RandomRotation" && name != "RandomScale" && name != "RandomRotationState" && name != "RandomPosition" && name != "RandomFlip") continue;
                    if (!(bool)(Field(behaviour, "onAwake") ?? true) && !(bool)(Field(behaviour, "onEnable") ?? true)) continue;
                    var targets = new List<object> { Field(behaviour, "target") };
                    var many = Field(behaviour, "targets") as IList;
                    if (many != null) foreach (object item in many) targets.Add(item);
                    if (targets.All(t => t == null)) targets.Add(behaviour.transform);
                    foreach (object target in targets) analysis.Add(target, Mark.Anchor, name);
                }
        }

        static Transform PlayerRoot()
        {
            GameObject player = GameObject.Find("CharacterCameraPrefab(Clone)");
            return player == null ? null : player.transform.root;
        }

        // The materials the atlas would draw alike (EnemyRebuild.cs' Prepare key without the texture checks).
        static string LookKey(Material m)
        {
            if (m == null) return "null";
            string shader = m.shader == null ? "" : m.shader.name;
            Texture main = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
            var rest = m.shaderKeywords.Where(k => k != "_DETAIL_MULX2" && k != "_METALLICGLOSSMAP");
            bool cutout = m.IsKeywordEnabled("_ALPHATEST_ON");
            return shader + "|" + m.renderQueue + "|" + KeywordSet(rest) + "|" + (cutout && m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff").ToString("R", CultureInfo.InvariantCulture) : "-") +
                   "|" + Prop(m, "_SrcBlend") + "|" + Prop(m, "_DstBlend") + "|" + Prop(m, "_ZWrite") + "|" + (main != null ? main.filterMode.ToString() : "-");
        }

        // The marks on the way from a part up to the player's root.
        static string MarkChain(Transform t, Transform root, Analysis analysis)
        {
            var chain = new List<string>();
            for (; t != null; t = t.parent)
            {
                Mark mark;
                if (analysis.marks.TryGetValue(t, out mark)) chain.Add(t.name.Replace(' ', '_') + "[" + mark + ":" + why(analysis, t).Replace(' ', '_') + "]");
                else if (!t.gameObject.activeSelf) chain.Add(t.name.Replace(' ', '_') + "[off]");
                if (t == root) break;
            }
            return string.Join("<", chain.ToArray());
        }

        static string PlayerReport()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Transform player = PlayerRoot();
            if (player == null) return "CROW_PLAYER_MERGE no player";
            var roots = new List<GameObject>();
            player.gameObject.scene.GetRootGameObjects(roots);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene != player.gameObject.scene) roots.AddRange(scene.GetRootGameObjects());
            }
            var scripts = new Dictionary<string, int>();
            bool collected = collectReferences;
            collectReferences = true;
            playerRules = true;
            Analysis analysis;
            try
            {
                analysis = Analyze(roots, scripts, false);
            }
            finally
            {
                collectReferences = collected;
                playerRules = false;
            }
            var report = new StringBuilder();
            var anchors = new Dictionary<string, List<string>>();
            var groups = new Dictionary<string, List<Material>>();
            var keptBy = new Dictionary<string, int>();
            int on = 0, drawn = 0, drawnSubmeshes = 0, keptSubmeshes = 0, transparent = 0;
            foreach (Renderer renderer in player.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled || renderer.gameObject.layer == 5) continue;
                on++;
                bool visible = renderer.isVisible;
                Material[] materials = renderer.sharedMaterials;
                var filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : renderer is SkinnedMeshRenderer ? ((SkinnedMeshRenderer)renderer).sharedMesh : null;
                Transform anchor = null;
                string blocker = renderer is MeshRenderer ? Blocker((MeshRenderer)renderer, analysis, out anchor) : renderer.GetType().Name;
                if (blocker == null)
                {
                    if (mesh == null || mesh.vertexCount == 0) blocker = "no mesh";
                    else if (materials.Length != mesh.subMeshCount) blocker = "materials != submeshes";
                    else if (materials.Any(m => m == null || m.renderQueue > 2500)) blocker = "material";
                    else if (mesh.vertexCount > EnemyMaxPartVertices) blocker = "big";
                }
                if (analysis.names.Contains(renderer.name) && blocker == null) blocker = "named";
                string anchorPath = anchor == null ? "-" : Perf.Path(anchor, 3);
                report.Append("CROW_PLAYER_MERGE_PART ").Append(visible ? "drawn " : "unseen ").Append(Perf.Path(renderer.transform, 5))
                    .Append(" type=").Append(renderer.GetType().Name).Append(" verts=").Append(mesh == null ? 0 : mesh.vertexCount)
                    .Append(" layer=").Append(renderer.gameObject.layer).Append(" shadow=").Append((int)renderer.shadowCastingMode)
                    .Append(" materials=").Append(string.Join(",", materials.Select(m => m == null ? "null" : m.name.Replace(' ', '_') + "(" + (m.shader == null ? "?" : m.shader.name.Replace(' ', '_')) + ":" + m.renderQueue + ")").ToArray()))
                    .Append(" anchor=").Append(anchorPath).Append("[").Append(anchor == null ? "" : why(analysis, anchor).Replace(' ', '_')).Append("]")
                    .Append(" blocker=").Append(blocker == null ? "-" : blocker.Replace(' ', '_'))
                    .Append(" marks:").Append(MarkChain(renderer.transform, player, analysis)).Append('\n');
                if (!visible) continue;
                drawn++;
                drawnSubmeshes += materials.Length;
                if (materials.Any(m => m != null && m.renderQueue > 2500)) transparent++;
                List<string> list;
                if (!anchors.TryGetValue(anchorPath, out list)) anchors[anchorPath] = list = new List<string>();
                list.Add(renderer.name.Replace(' ', '_') + (blocker == null ? "" : "!" + blocker.Split(':')[0]));
                if (blocker != null)
                {
                    keptSubmeshes += materials.Length;
                    string key = blocker.Length > 50 ? blocker.Substring(0, 50) : blocker;
                    int count;
                    keptBy.TryGetValue(key, out count);
                    keptBy[key] = count + 1;
                    continue;
                }
                string common = (anchor == null ? 0 : anchor.GetInstanceID()) + "|" + renderer.gameObject.layer + "|" + (int)renderer.shadowCastingMode + "|" +
                                renderer.receiveShadows + "|" + (int)renderer.lightProbeUsage + "|" + (int)renderer.reflectionProbeUsage + "|" + Channels(mesh);
                List<Material> slots;
                if (!groups.TryGetValue(common, out slots)) groups[common] = slots = new List<Material>();
                slots.AddRange(materials);
            }
            // After the merge: a submesh per material in each group; after the atlas: a submesh per look.
            int merged = groups.Values.Sum(g => g.Distinct().Count()), atlased = groups.Values.Sum(g => g.Select(LookKey).Distinct().Count());
            foreach (KeyValuePair<string, List<string>> pair in anchors.OrderByDescending(p => p.Value.Count))
                report.Append("CROW_PLAYER_MERGE_ANCHOR ").Append(pair.Key).Append(" drawn=").Append(pair.Value.Count).Append(": ").Append(string.Join(" ", pair.Value.ToArray())).Append('\n');
            report.Append("CROW_PLAYER_MERGE scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_')).Append(" on=").Append(on).Append(" drawn=").Append(drawn)
                .Append(" drawnSubmeshes=").Append(drawnSubmeshes).Append(" transparent=").Append(transparent).Append(" anchors=").Append(anchors.Count)
                .Append(" mergeable groups=").Append(groups.Count).Append(" kept=").Append(keptSubmeshes)
                .Append(" estimate merged=").Append(keptSubmeshes + merged).Append(" atlased=").Append(keptSubmeshes + atlased)
                .Append(" lookKeys=").Append(groups.Values.SelectMany(g => g).Select(LookKey).Distinct().Count())
                .Append(" ms=").Append(clock.ElapsedMilliseconds)
                .Append(" kept by: ").Append(string.Join(" ", keptBy.OrderByDescending(p => p.Value).Select(p => p.Key.Replace(' ', '_') + "=" + p.Value).ToArray())).Append('\n');
            foreach (string key in groups.Values.SelectMany(g => g).Select(LookKey).Distinct())
                report.Append("CROW_PLAYER_MERGE_LOOK ").Append(key.Replace(' ', '_')).Append(": ")
                    .Append(string.Join(" ", groups.Values.SelectMany(g => g).Where(m => LookKey(m) == key).Distinct().Select(m => m.name.Replace(' ', '_')).ToArray())).Append('\n');
            // The actions the lists don't know that act on the player's objects.
            var unknown = new Dictionary<string, int>();
            foreach (KeyValuePair<Transform, string> pair in analysis.why)
            {
                if (pair.Key == null || pair.Key.root != player) continue;
                foreach (string reason in pair.Value.Split('+'))
                {
                    if (!reason.StartsWith("unknown:", StringComparison.Ordinal)) continue;
                    int count;
                    unknown.TryGetValue(reason, out count);
                    unknown[reason] = count + 1;
                }
            }
            report.Append("CROW_PLAYER_MERGE unknown actions (all acted-on targets): ").Append(string.Join(" ", analysis.unknownTargets.Where(p => p.Key.StartsWith("unknown:", StringComparison.Ordinal))
                .OrderByDescending(p => p.Value).Select(p => p.Key.Substring(8) + "=" + p.Value).ToArray())).Append('\n');
            report.Append("CROW_PLAYER_MERGE unknown actions: ").Append(string.Join(" ", unknown.OrderByDescending(p => p.Value).Select(p => p.Key.Substring(8) + "=" + p.Value).ToArray())).Append('\n');
            // The scripts the rules don't know, on the player and in the area.
            report.Append("CROW_PLAYER_MERGE scripts: ").Append(string.Join(" ", scripts.OrderByDescending(p => p.Value).Select(p => p.Key + "=" + p.Value).ToArray())).Append('\n');
            var playerScripts = new Dictionary<string, int>();
            foreach (MonoBehaviour behaviour in player.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                string name = behaviour.GetType().Name;
                int count;
                playerScripts.TryGetValue(name, out count);
                playerScripts[name] = count + 1;
            }
            report.Append("CROW_PLAYER_MERGE player scripts: ").Append(string.Join(" ", playerScripts.OrderByDescending(p => p.Value).Select(p => p.Key + "=" + p.Value).ToArray())).Append('\n');
            // Globals that hold the player's objects, and what the loaded scenes' FSMs do through them.
            foreach (KeyValuePair<string, List<object>> global in analysis.globalValues)
            {
                var objects = global.Value.Select(v => v is GameObject ? ((GameObject)v).transform : v is Component ? ((Component)v).transform : null)
                    .Where(t => t != null && t.root == player).Distinct().ToList();
                if (objects.Count == 0) continue;
                var uses = analysis.globalUses.Where(u => u.Key == global.Key).Select(u => u.Value.Value).Distinct().ToArray();
                report.Append("CROW_PLAYER_MERGE_GLOBAL ").Append(global.Key.Replace(' ', '_')).Append(" = ").Append(string.Join(",", objects.Select(t => Perf.Path(t, 3)).ToArray()))
                    .Append(" uses: ").Append(string.Join(",", uses)).Append('\n');
            }
            return report.ToString();
        }
    }

    // -crow-player-shots <s> <dir> [n]: every s seconds after each area load (n times, default 6), close-ups of the
    // player rendered by a camera of their own (the main camera's settings, no camera effect) from four sides,
    // with the merged renderers (as built) and with the parts (pmerge=off) in the same frame, 512x512 PNGs
    // (<area>-<k>-<side>-merged.png, -parts.png) and CROW_PLAYER_SHOT pixel counts (ab builds).
    static class PlayerShots
    {
        static float every = -1f, next = -1f;
        static int times = 6, taken;
        static bool groups;
        static string dir, area;
        static Camera lens;
        static RenderTexture target;

        internal static void Install(string[] args)
        {
            int at = Array.IndexOf(args, "-crow-player-shots");
            if (at < 0 || at + 2 >= args.Length) return;
            float.TryParse(args[at + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out every);
            dir = args[at + 2];
            groups = args.Contains("-crow-player-shots-groups");
            int count;
            if (at + 3 < args.Length && int.TryParse(args[at + 3], out count)) times = count;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (scene.name == "Logos" || scene.name == "Title" || scene.name == "CrowMara" || scene.name == "CrowGap") return;
                area = scene.name.Replace(' ', '_');
                taken = 0;
                next = Time.realtimeSinceStartup + every;
            };
            Canvas.willRenderCanvases += Tick;
        }

        static void Tick()
        {
            if (next < 0f || Time.realtimeSinceStartup < next) return;
            next = ++taken < times ? Time.realtimeSinceStartup + every : -1f;
            try
            {
                Shoot();
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_PLAYER_SHOT failed: " + exception);
                next = -1f;
            }
        }

        // Every merged renderer of the player's, shown or not (the costumes, the weapons not held, the templates),
        // drawn alone on layer 31 by renderers of their own: the merged mesh with its materials against its parts'
        // meshes with theirs at their objects' places, from the game camera's side and the back (<area>-g<i>-<side>-
        // merged/parts.png, CROW_PLAYER_SHOT group); nothing is switched on, so no script runs.
        static void Groups()
        {
            int index = 0;
            foreach (MergedRenderer group in Resources.FindObjectsOfTypeAll<MergedRenderer>())
            {
                if (group == null || !group.gameObject.scene.IsValid() || !group.transform.root.name.StartsWith("CharacterCameraPrefab", StringComparison.Ordinal)) continue;
                var filter = group.GetComponent<MeshFilter>();
                var renderer = group.GetComponent<MeshRenderer>();
                if (filter == null || renderer == null || filter.sharedMesh == null || group.parts == null) continue;
                Mesh mesh = filter.sharedMesh;
                Matrix4x4 matrix = group.transform.localToWorldMatrix;
                Bounds local = mesh.bounds;
                var bounds = new Bounds(matrix.MultiplyPoint3x4(local.center), Vector3.zero);
                for (int corner = 0; corner < 8; corner++)
                    bounds.Encapsulate(matrix.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1))));
                float radius = Mathf.Max(0.05f, bounds.extents.magnitude);
                Material[] materials = renderer.sharedMaterials;
                index++;
                foreach (KeyValuePair<string, float> side in new[] { new KeyValuePair<string, float>("front", 0f), new KeyValuePair<string, float>("back", 180f) })
                {
                    Camera game = Camera.main;
                    Vector3 toCamera = game != null ? game.transform.position - bounds.center : Vector3.back;
                    toCamera.y = 0f;
                    if (toCamera.sqrMagnitude < 1e-4f) toCamera = Vector3.back;
                    Vector3 direction = Quaternion.Euler(0f, side.Value, 0f) * toCamera.normalized;
                    lens.fieldOfView = 30f;
                    lens.transform.position = bounds.center + direction * (radius / Mathf.Tan(15f * Mathf.Deg2Rad) * 1.05f) + Vector3.up * radius * 0.5f;
                    lens.transform.LookAt(bounds.center);
                    int mask = lens.cullingMask;
                    lens.cullingMask = 1 << 31;
                    var temporary = new List<GameObject> { Stand(mesh, materials, group.transform) };
                    Texture2D merged = Render();
                    foreach (GameObject go in temporary) UnityEngine.Object.DestroyImmediate(go);
                    temporary.Clear();
                    int drawn = 0;
                    foreach (Renderer part in group.parts)
                    {
                        var partFilter = part == null ? null : part.GetComponent<MeshFilter>();
                        if (partFilter == null || partFilter.sharedMesh == null) continue;
                        temporary.Add(Stand(partFilter.sharedMesh, part.sharedMaterials, part.transform));
                        drawn++;
                    }
                    Texture2D parts = Render();
                    foreach (GameObject go in temporary) UnityEngine.Object.DestroyImmediate(go);
                    lens.cullingMask = mask;
                    string name = area + "-g" + index + "-" + side.Key;
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name + "-merged.png"), merged.EncodeToPNG());
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name + "-parts.png"), parts.EncodeToPNG());
                    Color32[] a = merged.GetPixels32(), b = parts.GetPixels32();
                    int differ = 0, big = 0, covered = 0;
                    Color32 background = b[0];
                    for (int i = 0; i < a.Length; i++)
                    {
                        int d = Math.Max(Math.Abs(a[i].r - b[i].r), Math.Max(Math.Abs(a[i].g - b[i].g), Math.Abs(a[i].b - b[i].b)));
                        if (d > 2) differ++;
                        if (d > 24) big++;
                        if (b[i].r != background.r || b[i].g != background.g || b[i].b != background.b) covered++;
                    }
                    Debug.Log("CROW_PLAYER_SHOT group " + name + " " + Perf.Path(group.transform.parent, 3) + " active=" + group.gameObject.activeInHierarchy + " parts=" + drawn +
                              " materials=" + string.Join(",", materials.Select(m => m == null ? "null" : m.name.Replace(' ', '_')).ToArray()) +
                              " covered=" + covered + " differ=" + differ + " big=" + big);
                    UnityEngine.Object.Destroy(merged);
                    UnityEngine.Object.Destroy(parts);
                }
            }
        }

        // A renderer of its own on layer 31 at a transform's world pose (no scripts: nothing runs).
        static GameObject Stand(Mesh mesh, Material[] materials, Transform at)
        {
            var go = new GameObject("CrowPlayerShotPart") { layer = 31 };
            go.transform.position = at.position;
            go.transform.rotation = at.rotation;
            go.transform.localScale = at.lossyScale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = materials;
            return go;
        }

        static Texture2D Render()
        {
            lens.Render();
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var picture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            picture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            picture.Apply();
            RenderTexture.active = previous;
            return picture;
        }

        static void Shoot()
        {
            GameObject player = GameObject.Find("CharacterCameraPrefab(Clone)");
            if (player == null) return;
            Transform body = player.transform.Find("character RIGIDBODY");
            if (body == null) body = player.transform;
            if (lens == null)
            {
                target = new RenderTexture(512, 512, 24);
                var go = new GameObject("CrowPlayerShotCamera");
                UnityEngine.Object.DontDestroyOnLoad(go);
                lens = go.AddComponent<Camera>();
                Camera main = Camera.main;
                if (main != null) lens.CopyFrom(main);
                lens.enabled = false;
                lens.targetTexture = target;
                lens.nearClipPlane = 0.05f;
                lens.farClipPlane = 40f;
                lens.clearFlags = CameraClearFlags.SolidColor;
                lens.backgroundColor = new Color(0.25f, 0.25f, 0.28f);
                lens.cullingMask &= ~(1 << 5);
            }
            // Her drawn renderers' bounds (not the menus', particles or sprites), seen from the game camera's side.
            var bounds = new Bounds(body.position, Vector3.zero);
            bool any = false;
            foreach (Renderer renderer in body.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled || renderer.gameObject.layer == 5 || !(renderer is MeshRenderer)) continue;
                if (any) bounds.Encapsulate(renderer.bounds);
                else bounds = renderer.bounds;
                any = true;
            }
            Vector3 centre = bounds.center;
            float radius = Mathf.Max(0.3f, bounds.extents.magnitude);
            lens.fieldOfView = 30f;
            float distance = radius / Mathf.Tan(15f * Mathf.Deg2Rad) * 1.05f;
            Camera game = Camera.main;
            Vector3 toCamera = game != null ? game.transform.position - centre : -body.forward;
            toCamera.y = 0f;
            if (toCamera.sqrMagnitude < 1e-4f) toCamera = Vector3.back;
            toCamera.Normalize();
            System.IO.Directory.CreateDirectory(dir);
            string mode = Merge.PlayerMode;
            foreach (KeyValuePair<string, float> side in new[] { new KeyValuePair<string, float>("front", 0f), new KeyValuePair<string, float>("left", 90f),
                                                                  new KeyValuePair<string, float>("back", 180f), new KeyValuePair<string, float>("right", -90f) })
            {
                Vector3 direction = Quaternion.Euler(0f, side.Value, 0f) * toCamera;
                lens.transform.position = centre + direction * distance + Vector3.up * radius * 0.5f;
                lens.transform.LookAt(centre);
                Merge.SetPlayer("on");
                Texture2D merged = Render();
                Merge.SetPlayer("off");
                Texture2D parts = Render();
                Merge.SetPlayer(mode ?? "on");
                string name = area + "-" + taken + "-" + side.Key;
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name + "-merged.png"), merged.EncodeToPNG());
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name + "-parts.png"), parts.EncodeToPNG());
                Color32[] a = merged.GetPixels32(), b = parts.GetPixels32();
                int differ = 0, big = 0;
                long sum = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    int d = Math.Max(Math.Abs(a[i].r - b[i].r), Math.Max(Math.Abs(a[i].g - b[i].g), Math.Abs(a[i].b - b[i].b)));
                    if (d > 2) differ++;
                    if (d > 24) big++;
                    sum += d;
                }
                Debug.Log("CROW_PLAYER_SHOT " + name + " differ=" + differ + " big=" + big + " of " + a.Length + " mean=" + (sum / (double)a.Length).ToString("F3", CultureInfo.InvariantCulture));
                UnityEngine.Object.Destroy(merged);
                UnityEngine.Object.Destroy(parts);
            }
            if (groups && taken == 1) Groups();
        }
    }

    // -crow-player-merge-check: every second, each of the player's merged renderers' parts against the build: its
    // pose relative to the merged object as merged (MergedRenderer.poses), active exactly when the merged object
    // is, its own renderer off (while merged), its materials those it had; shown or not (the costumes, the guns
    // not held). CROW_PLAYER_MERGE_CHECK names a part once per kind of difference, and counts every 10 s.
    static class PlayerMergeCheck
    {
        static float next, summary;
        static long checks, differences;
        static readonly HashSet<string> reported = new HashSet<string>();
        static readonly Dictionary<Renderer, Material[]> looks = new Dictionary<Renderer, Material[]>();

        internal static void Install(string[] args)
        {
            if (!args.Contains("-crow-player-merge-check")) return;
            Canvas.willRenderCanvases += Tick;
        }

        static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < next) return;
            next = now + 1f;
            int groups = 0;
            foreach (MergedRenderer group in Resources.FindObjectsOfTypeAll<MergedRenderer>())
            {
                if (group == null || !group.gameObject.scene.IsValid() || group.parts == null || group.poses == null) continue;
                if (!group.transform.root.name.StartsWith("CharacterCameraPrefab", StringComparison.Ordinal)) continue;
                groups++;
                var merged = group.GetComponent<MeshRenderer>();
                bool active = group.gameObject.activeInHierarchy, on = merged != null && merged.enabled;
                for (int i = 0; i < group.parts.Length; i++)
                {
                    Renderer part = group.parts[i];
                    checks++;
                    string why = null;
                    if (part == null) why = "destroyed";
                    else
                    {
                        Matrix4x4 relative = group.transform.worldToLocalMatrix * part.transform.localToWorldMatrix;
                        float moved = 0f;
                        for (int k = 0; k < 16; k++) moved = Math.Max(moved, Math.Abs(relative[k] - group.poses[i][k]));
                        Material[] first;
                        if (!looks.TryGetValue(part, out first)) looks[part] = first = part.sharedMaterials;
                        if (part.gameObject.activeInHierarchy != active) why = "active=" + part.gameObject.activeInHierarchy + " merged=" + active;
                        else if (moved > 0.001f) why = "moved";
                        else if (on && part.enabled) why = "its renderer is on";
                        else if (!part.sharedMaterials.SequenceEqual(first)) why = "material";
                        if (why == "moved" && reported.Add(Perf.Path(part.transform, 6) + ":" + why))
                        {
                            differences++;
                            Debug.Log("CROW_PLAYER_MERGE_CHECK " + Perf.Path(part.transform, 6) + ": moved " + moved.ToString("0.####", CultureInfo.InvariantCulture) + " (merged in " +
                                      Perf.Path(group.transform.parent, 3) + ", active=" + active + ")");
                            continue;
                        }
                    }
                    if (why == null) continue;
                    differences++;
                    string key = (part != null ? Perf.Path(part.transform, 6) : Perf.Path(group.transform, 4)) + ":" + why.Split(' ')[0];
                    if (reported.Add(key)) Debug.Log("CROW_PLAYER_MERGE_CHECK " + key + " " + why + " (merged in " + Perf.Path(group.transform.parent, 3) + ")");
                }
            }
            if (now < summary + 10f) return;
            summary = now;
            Debug.Log("CROW_PLAYER_MERGE_CHECK groups=" + groups + " checks=" + checks + " differences=" + differences + " reported=" + reported.Count);
        }
    }
}
