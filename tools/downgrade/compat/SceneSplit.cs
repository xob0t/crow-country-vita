using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// The PS Vita build's split areas at run time: the parts Merge.Split took out (SplitPart, SplitHolder,
// SplitMirror), following their old places (SplitFollow) and the -crow-scene-split switches (SceneSplit;
// Perf.cs installs it and takes its -crow-cycle keys).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    // An area is one transform hierarchy of 20,000 transforms or more under its root (Station Square
    // 21,600, Pool 22,400), and a few of its parts move every frame (bunting, crows, flies, an NPC's
    // breathing, the pool's swirl). Each engine system that follows transform changes then walks the
    // whole hierarchy every frame: the renderers' update (PostLateUpdate.UpdateAllRenderers), physics'
    // transform sync (FixedUpdate.PhysicsFixedUpdate) and the particle systems. The PS Vita build takes
    // the parts nothing acts on out to the scene's root as it writes each scene (Merge.Split, called by
    // DowngradeBuild's CrowMergeScenes; CROW_SPLIT=movers, the default, on or off), each with a SplitPart
    // naming its old parent and place; below objects the game switches on and off, a SplitHolder in
    // the part's place switches it. A first run-time test that took out every subtree that didn't move
    // in 60 frames: main thread Station Square 30.6 -> 26.8 ms, Pool 31.0 -> 24.4 ms.
    //   -crow-scene-split off|on|check|report|apply|apply-check|report-movers|apply-movers|apply-movers-check
    //       off: after each area load, every part goes back where it was (its old parent, place,
    //       sibling index and activity), for A/B runs on one build; on: out again (-crow-cycle takes
    //       scenesplit=off|on, now); check: every second and at each switch of a holder, whether each
    //       part is active exactly when it would be in its old place and where it would be there
    //       (CROW_SPLIT_CHECK, once per part that differs, and a count every 30 s); report: log what
    //       Merge.Split would take out of each area (for builds made without it); apply, apply-check:
    //       split each area as it loads, as the build would (and check); report-movers,
    //       apply-movers, apply-movers-check: the same with Merge.Split's movers (the parts that move on
    //       their own, below split parents, and the way below destroyed objects)
    public class SplitPart : MonoBehaviour
    {
        public Transform parent;
        public int sibling;
        public Vector3 position, scale;
        public Quaternion rotation;
        public bool active;  // its own activity before the split
        public SplitHolder holder;  // its place below a switched object, or none
    }

    // A split part's place below objects the game switches on and off: it stands for the part there
    // (its activeSelf is the part's own, so ActivateChildren on its parent switches it as it would the
    // part), and the part, built switched off, is on exactly while this is. While this is off because
    // of its parents, own keeps the part's own activity; switched off itself, the part is on again
    // when it is.
    public class SplitHolder : MonoBehaviour
    {
        public GameObject part;
        public bool own = true;

        void OnEnable()
        {
            if (part == null) return;
            part.SetActive(own);
            SceneSplit.Switched(this);
        }

        // Destroyed with what is above it (the game's DestroyObject, DestroySelf), the part goes too, as
        // it would have in its old place (the movers' split goes below destroyed objects).
        void OnDestroy()
        {
            if (part != null && gameObject.scene.isLoaded) Destroy(part);
        }

        void OnDisable()
        {
            if (part == null) return;
            // Torn down (its scene unloading, the game quitting) rather than switched off: then nothing
            // here or above is off itself (scene.isLoaded can still be true), and nothing is done.
            bool switchedOff = !gameObject.activeSelf;
            for (Transform t = transform.parent; t != null && !switchedOff; t = t.parent) switchedOff = !t.gameObject.activeSelf;
            if (!switchedOff) return;
            own = !gameObject.activeSelf || part.activeSelf;
            if (part.activeSelf) part.SetActive(false);
            SceneSplit.Switched(this);
        }
    }

    // The split parts' old places followed: in any frame an old parent's world pose changed (an elevator
    // moving the whole level, anything the split's analysis couldn't see), each part taken out from below
    // it is put back where it would be there (its old local pose below the old parent's world pose), so
    // it moves as it did before the split. Checked just before rendering, once per old parent: a matrix
    // read and a comparison. -crow-split-follow off (or -crow-cycle splitfollow=off) leaves the parts where
    // they are, for A/B; CROW_SPLIT_FOLLOW logs each old parent the first time it moves, and a count every
    // 30 s while any did.
    static class SplitFollow
    {
        sealed class Place
        {
            public Transform Old;
            public Matrix4x4 Last;
            public readonly List<SplitPart> Parts = new List<SplitPart>();
            public bool Logged;
            public int Moves;
        }

        static bool on = true, hooked, dirty = true;
        static readonly List<Place> places = new List<Place>();
        static int moves, followed;
        static float summaryAt;

        internal static void Install(string[] args)
        {
            if (CrowFlags.Value(args, "-crow-split-follow") == "off") on = false;
            if (hooked) return;
            hooked = true;
            // Collected in the frame after a load: the split (at run time) and the holders' switching
            // happen while the scene loads.
            CrowEvents.Loaded("SplitFollow.sceneLoaded", (scene, mode) => Dirty());
            CrowEvents.Unloaded("SplitFollow.sceneUnloaded", scene => Dirty());
            CrowEvents.BeforeRender("SplitFollow.Tick", Tick);
        }

        static int collectFrame;

        // Whether the parts are where following keeps them (not between a load and the next frame's step).
        internal static bool Ready
        {
            get { return !on || !dirty; }
        }

        static void Dirty()
        {
            dirty = true;
            collectFrame = Time.frameCount + 1;
        }

        internal static void Set(string value)
        {
            on = value != "off";
            Dirty();
            Debug.Log("CROW_SPLIT follow " + (on ? "on" : "off"));
        }

        static void Collect()
        {
            dirty = false;
            places.Clear();
            var byParent = new Dictionary<Transform, Place>();
            foreach (SplitPart part in Resources.FindObjectsOfTypeAll<SplitPart>())
            {
                if (part == null || part.parent == null || !part.gameObject.scene.IsValid() || part.transform.parent == part.parent) continue;
                Place place;
                if (!byParent.TryGetValue(part.parent, out place))
                {
                    // Where the old parent was when the part left it (from the part and its old local
                    // pose): it may have moved since, as the elevator rooms do while the area starts.
                    Matrix4x4 then = part.transform.localToWorldMatrix * Matrix4x4.TRS(part.position, part.rotation, part.scale).inverse;
                    byParent[part.parent] = place = new Place { Old = part.parent, Last = then };
                    places.Add(place);
                }
                place.Parts.Add(part);
            }
            if (places.Count > 0) Debug.Log("CROW_SPLIT_FOLLOW watching " + places.Count + " old parents of " + places.Sum(p => p.Parts.Count) + " parts");
        }

        internal static void Tick()
        {
            if (!on) return;
            try
            {
                if (dirty)
                {
                    if (Time.frameCount < collectFrame) return;
                    Collect();
                }
                for (int i = 0; i < places.Count; i++)
                {
                    Place place = places[i];
                    if (place.Old == null) continue;
                    Matrix4x4 now = place.Old.localToWorldMatrix;
                    if (now == place.Last) continue;
                    place.Last = now;
                    moves++;
                    place.Moves++;
                    // Each part where it would be below the old parent: its old local pose through the
                    // parent's matrix, read back as the root pose it has. Its scale stays (the rooms move
                    // and turn, they don't scale), so its rotation is that matrix's axes over its scale.
                    // Below a mirroring parent the parent's rotation times the part's old local rotation
                    // is not it: the split left such a part a negative scale and the rotation that goes
                    // with it (Underground NW's and SE's offset has x scale -1, and its decor/pipes, turned
                    // 90 degrees, followed the elevator room turned 180 degrees about their pivot: a pipe
                    // loop's collider stood in the elevator and Mara couldn't leave it, 28 Oct).
                    int count = 0;
                    foreach (SplitPart part in place.Parts)
                    {
                        if (part == null) continue;
                        Transform t = part.transform;
                        if (t.parent == part.parent) continue;  // put back (-crow-scene-split off)
                        Matrix4x4 there = now * Matrix4x4.TRS(part.position, part.rotation, part.scale);
                        Vector3 scale = t.localScale;
                        t.SetPositionAndRotation(there.GetColumn(3), Quaternion.LookRotation(there.GetColumn(2) * Mathf.Sign(scale.z), there.GetColumn(1) * Mathf.Sign(scale.y)));
                        count++;
                    }
                    followed += count;
                    if (!place.Logged && count > 0)
                    {
                        place.Logged = true;
                        Debug.Log("CROW_SPLIT_FOLLOW " + Hierarchy.Path(place.Old, 4) + " moved: " + count + " parts taken out from below it follow it");
                    }
                }
                float time = Time.realtimeSinceStartup;
                if (moves > 0 && time >= summaryAt)
                {
                    summaryAt = time + 30f;
                    Debug.Log("CROW_SPLIT_FOLLOW " + places.Count + " old parents watched, " + moves + " moves, " + followed + " parts placed; most: " +
                              string.Join(" ", places.Where(p => p.Moves > 0 && p.Old != null).OrderByDescending(p => p.Moves).Take(4)
                                  .Select(p => Hierarchy.Path(p.Old, 3) + "=" + p.Moves).ToArray()));
                    moves = followed = 0;
                    foreach (Place p in places) p.Moves = 0;
                }
            }
            catch (Exception exception)
            {
                // Collected again next frame; a failure that keeps coming back is logged now and then.
                if (failures++ < 5 || failures % 1000 == 0) Debug.Log("CROW_SPLIT_FOLLOW failed (" + failures + "): " + exception);
                Dirty();
            }
        }

        static int failures;
    }

    static class SceneSplit
    {
        static string mode;
        static bool hooked, checking, checkHooked;
        static float nextCheck, lastSummary;
        static int checks, differences, switches;
        static readonly HashSet<SplitPart> reported = new HashSet<SplitPart>();
        static readonly List<SplitPart> found = new List<SplitPart>();

        internal static void Install(string[] args)
        {
            string value = CrowFlags.Value(args, "-crow-scene-split");
            Merge.MoverDebug = CrowFlags.Has(args, "-crow-split-mover-log");
            SplitFollow.Install(args);  // first: its frame step runs before the check's
            MoverFinder.Install(args);
            if (value != null) Set(value);
        }

        internal static void Set(string value)
        {
            mode = value;
            Apply();
            // A later -crow-cycle scenesplit= can turn the check on or off.
            checking = mode == "check" || mode.EndsWith("-check");
            if (checking && !checkHooked)
            {
                checkHooked = true;
                CrowEvents.BeforeRender("SceneSplit.Check", Check);
            }
            if (hooked) return;
            hooked = true;
            CrowEvents.Loaded("SceneSplit.sceneLoaded", (scene, loadMode) =>
            {
                bool area = Scenes.IsArea(scene);
                if (area && (mode.StartsWith("report") || mode.StartsWith("apply")))
                {
                    Merge.SplitMovers = mode.Contains("movers");
                    Debug.Log("CROW_" + Merge.Split(scene, !mode.StartsWith("report")));
                }
                else Apply();
            });
        }

        static void Apply()
        {
            if (mode != "off" && mode != "on") return;
            int count = 0;
            foreach (SplitPart part in Resources.FindObjectsOfTypeAll<SplitPart>())
            {
                if (part == null || part.parent == null || !part.gameObject.scene.IsValid()) continue;
                Transform transform = part.transform;
                SplitHolder holder = part.holder;
                if (mode == "off" && transform.parent != part.parent)
                {
                    // Its own activity: the holder's, and below it the part's (or what the holder kept).
                    bool own = holder == null ? part.gameObject.activeSelf || (part.active && !part.parent.gameObject.activeInHierarchy)
                        : holder.gameObject.activeSelf && (holder.gameObject.activeInHierarchy ? part.gameObject.activeSelf : holder.own);
                    if (holder != null)
                    {
                        holder.part = null;
                        holder.gameObject.SetActive(false);
                        holder.transform.SetAsLastSibling();
                    }
                    transform.SetParent(part.parent, false);
                    transform.SetSiblingIndex(part.sibling);
                    transform.localPosition = part.position;
                    transform.localRotation = part.rotation;
                    transform.localScale = part.scale;
                    part.gameObject.SetActive(own);
                    count++;
                }
                else if (mode == "on" && transform.parent == part.parent)
                {
                    bool own = part.gameObject.activeSelf;
                    if (holder != null || (own && !part.parent.gameObject.activeInHierarchy)) part.gameObject.SetActive(false);
                    transform.SetParent(null, true);
                    if (holder != null)
                    {
                        holder.transform.SetSiblingIndex(part.sibling);
                        holder.own = true;
                        holder.part = part.gameObject;
                        holder.gameObject.SetActive(own);
                    }
                    count++;
                }
            }
            Debug.Log("CROW_SPLIT " + mode + ": " + count + " parts " + (mode == "off" ? "back in their places" : "out"));
        }

        // -crow-cycle splittoggle=parents-off|parents-on|self-off|self-on: every held part's old parent (as
        // an ancestor's ActivateGameObject would) or its holder (as ActivateChildren on its parent would)
        // switched off, or back as it was; with -crow-scene-split check, a test of the holders.
        static readonly List<KeyValuePair<GameObject, bool>> toggled = new List<KeyValuePair<GameObject, bool>>();

        internal static void Toggle(string value)
        {
            if (value.EndsWith("-on"))
            {
                for (int i = toggled.Count - 1; i >= 0; i--) if (toggled[i].Key != null) toggled[i].Key.SetActive(toggled[i].Value);
                Debug.Log("CROW_SPLIT toggle " + value + ": " + toggled.Count + " objects back");
                toggled.Clear();
                return;
            }
            foreach (SplitPart part in Resources.FindObjectsOfTypeAll<SplitPart>())
            {
                // Held parts only; parents: its old parent, also with the parts back in place (off).
                if (part == null || part.holder == null || part.parent == null || !part.gameObject.scene.IsValid()) continue;
                GameObject target = value.StartsWith("parents") ? part.parent.gameObject : part.holder.gameObject;
                toggled.Add(new KeyValuePair<GameObject, bool>(target, target.activeSelf));
                target.SetActive(false);
            }
            Debug.Log("CROW_SPLIT toggle " + value + ": " + toggled.Count + " objects off");
        }

        // From a holder's OnEnable and OnDisable: with check, the part against its place right away.
        internal static void Switched(SplitHolder holder)
        {
            if (!checking) return;
            switches++;
            var part = holder.part.GetComponent<SplitPart>();
            SplitFollow.Tick();  // the parts where following puts them this frame
            if (part != null) Compare(part);
        }

        // -crow-scene-split check: each part against its old place.
        static void Check()
        {
            float now = Time.realtimeSinceStartup;
            if (!checking || now < nextCheck) return;
            nextCheck = now + 1f;
            SplitFollow.Tick();
            found.Clear();
            foreach (SplitPart part in Resources.FindObjectsOfTypeAll<SplitPart>())
                if (part != null && part.parent != null && part.gameObject.scene.IsValid() && part.transform.parent != part.parent) found.Add(part);
            foreach (SplitPart part in found) Compare(part);
            if (now - lastSummary < 30f) return;
            lastSummary = now;
            Debug.Log("CROW_SPLIT_CHECK " + found.Count + " parts, " + checks + " checks, " + switches + " holder switches, " + differences + " differences, " +
                      reported.Count + " parts differed");
        }

        // Active exactly when it would be in its old place (its holder's, or its parents' with its own
        // activity), and where it would be there.
        static void Compare(SplitPart part)
        {
            if (!SplitFollow.Ready) return;  // a load's parts before following placed them
            checks++;
            SplitHolder holder = part.holder;
            bool active = part.gameObject.activeInHierarchy;
            // Nothing names a part's root, so it is on exactly while its holder, or its old parents with
            // its own first activity, would show it.
            bool wanted = holder != null ? holder.gameObject.activeInHierarchy : part.parent.gameObject.activeInHierarchy && part.active;
            Matrix4x4 there = holder != null ? holder.transform.localToWorldMatrix
                : part.parent.localToWorldMatrix * Matrix4x4.TRS(part.position, part.rotation, part.scale);
            Matrix4x4 here = part.transform.localToWorldMatrix;
            bool placed = true;
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(here[i] - there[i]) > 1e-3f * Mathf.Max(1f, Mathf.Abs(there[i]))) placed = false;
            if (active == wanted && placed) return;
            differences++;
            if (!reported.Add(part)) return;
            Debug.Log("CROW_SPLIT_CHECK " + Hierarchy.Path(part.transform, 3) + " (from " + Hierarchy.Path(part.parent, 3) + "): " +
                      (active == wanted ? "" : "active " + active + " where it would be " + wanted + " ") + (placed ? "" : "moved from its place (" + (here.GetColumn(3) - there.GetColumn(3)).magnitude.ToString("0.###", CultureInfo.InvariantCulture) + " m, parent " + (part.transform.parent == null ? "none" : part.transform.parent.name) + ")"));
        }
    }

    // In the place of a part PlayerSplit made a root of its own: the part follows the player's
    // activation and goes with it.
    public class SplitMirror : MonoBehaviour
    {
        internal GameObject part;
        internal bool active = true;

        void OnEnable()
        {
            if (part == null) return;
            part.SetActive(active);
            Debug.Log("CROW_SPLIT " + part.name + (active ? " on" : " stays off") + " with the player");
        }

        void OnDisable()
        {
            if (part == null) return;
            active = part.activeSelf;
            part.SetActive(false);
            Debug.Log("CROW_SPLIT " + part.name + " off with the player");
        }

        void OnDestroy()
        {
            if (part != null) Destroy(part);
        }
    }

    // -crow-find-movers <name> [s]: what names a part that Merge.Split's movers would take out. s seconds (default 5)
    // after each area load, every action of every state of every FSM whose GameObject parameters name an object
    // called name (as they resolve then: variables by their current value), with the variable's name
    // (CROW_FIND_MOVER).
    static class MoverFinder
    {
        const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        static Type fsmComponent, ownerType, fsmGameObjectType;
        static PropertyInfo fsmProperty, statesProperty, stateName, actionsProperty, valueProperty, ownerGameObject, useVariable, variableName, fsmName;
        static MethodInfo ownerTarget;
        static readonly Dictionary<Type, FieldInfo[]> actionFields = new Dictionary<Type, FieldInfo[]>();

        internal static void Install(string[] args)
        {
            string name = CrowFlags.Value(args, "-crow-find-movers");
            if (name == null) return;
            float after = CrowFlags.NumberAt(args, "-crow-find-movers", 2, 5f), due = -1f;
            CrowEvents.Loaded("MoverFinder.sceneLoaded", (scene, mode) =>
            {
                if (Scenes.IsArea(scene)) due = Time.realtimeSinceStartup + after;
            });
            CrowEvents.Canvases("MoverFinder.willRenderCanvases", () =>
            {
                if (due < 0f || Time.realtimeSinceStartup < due) return;
                due = -1f;
                Find(name);
            });
        }

        static bool Reflect()
        {
            if (fsmComponent != null) return true;
            Type component = GameTypes.PlayMakerFsm;
            ownerType = Type.GetType("HutongGames.PlayMaker.FsmOwnerDefault, PlayMaker");
            fsmGameObjectType = Type.GetType("HutongGames.PlayMaker.FsmGameObject, PlayMaker");
            if (component == null || ownerType == null || fsmGameObjectType == null) return false;
            fsmProperty = component.GetProperty("Fsm");
            statesProperty = fsmProperty.PropertyType.GetProperty("States");
            PropertyInfo activeState = fsmProperty.PropertyType.GetProperty("ActiveState");
            stateName = activeState.PropertyType.GetProperty("Name");
            actionsProperty = activeState.PropertyType.GetProperty("Actions");
            valueProperty = fsmGameObjectType.GetProperty("Value");
            ownerGameObject = ownerType.GetProperty("GameObject");
            useVariable = fsmGameObjectType.GetProperty("UseVariable");
            variableName = fsmGameObjectType.GetProperty("Name");
            ownerTarget = fsmProperty.PropertyType.GetMethod("GetOwnerDefaultTarget");
            fsmName = fsmProperty.PropertyType.GetProperty("Name");
            fsmComponent = component;
            return true;
        }

        static void Find(string name)
        {
            if (!Reflect()) return;
            int found = 0;
            foreach (UnityEngine.Object component in Resources.FindObjectsOfTypeAll(fsmComponent))
            {
                var behaviour = component as MonoBehaviour;
                if (behaviour == null || !behaviour.gameObject.scene.IsValid()) continue;
                object fsm = fsmProperty.GetValue(component, null);
                var all = fsm == null ? null : statesProperty.GetValue(fsm, null) as Array;
                if (all == null) continue;
                foreach (object state in all)
                {
                    var actions = actionsProperty.GetValue(state, null) as Array;
                    if (actions == null) continue;
                    foreach (object action in actions)
                    {
                        if (action == null) continue;
                        foreach (FieldInfo field in FieldsOf(action.GetType()))
                        {
                            object value = field.GetValue(action);
                            GameObject target = null;
                            object variable = value;
                            try
                            {
                                if (value != null && field.FieldType == ownerType)
                                {
                                    target = ownerTarget.Invoke(fsm, new[] { value }) as GameObject;
                                    variable = ownerGameObject.GetValue(value, null);
                                }
                                else if (value != null) target = valueProperty.GetValue(value, null) as GameObject;
                            }
                            catch (Exception)
                            {
                                continue;
                            }
                            if (target == null || target.name != name) continue;
                            string via = variable != null && (bool)useVariable.GetValue(variable, null) ? " variable=" + variableName.GetValue(variable, null) : "";
                            Debug.Log("CROW_FIND_MOVER " + name + " <- " + Hierarchy.Path(behaviour.transform, 5) + " fsm=" + fsmName.GetValue(fsm, null) +
                                      " state=" + stateName.GetValue(state, null) + " action=" + action.GetType().Name + " field=" + field.Name + via);
                            found++;
                        }
                    }
                }
            }
            Debug.Log("CROW_FIND_MOVER " + name + ": " + found + " actions");
        }

        // An action type's FsmOwnerDefault and FsmGameObject fields.
        static FieldInfo[] FieldsOf(Type type)
        {
            FieldInfo[] fields;
            if (actionFields.TryGetValue(type, out fields)) return fields;
            fields = type.GetFields(Fields).Where(f => f.FieldType == ownerType || f.FieldType == fsmGameObjectType).ToArray();
            return actionFields[type] = fields;
        }
    }

    // The player's prefab instance (CharacterCameraPrefab, kept across areas) is one transform hierarchy
    // of 10,251 transforms; 6,304 of them are its menus' canvases (UI Canvases: the gameplay UI, pause,
    // map and inventory; screen-space overlays, nearly all inactive). Mara's breathing moves about 560
    // of the others every frame, and each engine system that follows transform changes (renderers,
    // particle systems...) then walks the whole hierarchy. On the PS Vita, UI Canvases becomes a root of
    // its own once the player is kept (in DontDestroyOnLoad), right after the player's root, so the
    // overlay canvases sort as before. A placeholder in its old place (SplitMirror) makes it follow the
    // player's activation (CharacterAndCamera's Activate, Deactivate and Reset) and destruction. No FSM
    // looks children up from the player's root (180 child lookups checked in Roadside), and the game's
    // scripts reach the menus through references. Roadside: UpdateAllRenderers 2.7 -> 1.9 ms,
    // ParticleSystemBeginUpdateAll 1.6 -> 0.8 ms, the main thread's work per frame 40.4 -> 39.5 ms.
    // Checked on the PS Vita: the pause menu, examining, a dialogue, Roadside -> Station Square.
    // Not split: the camera rig (camera_follow_player, 2,890 transforms). With it a root of its own, a
    // Title FSM's Rewired action (RewiredGetJoystickCount) threw on the way back to the title.
    // -crow-player-split game|ui|ui+camera: ui (the PS Vita's default) splits the menus, ui+camera the camera rig
    // too, game neither; -crow-cycle takes split=.
    static class PlayerSplit
    {
        static readonly List<string> parts = new List<string>();
        static bool hooked;

        public static void Install(string[] args)
        {
            string value = CrowFlags.Value(args, "-crow-player-split", "ui");
            if (value != null) Set(value);
        }

        internal static void Set(string value)
        {
            foreach (string name in value.Split('+'))
            {
                // Children's names with '_' for a space.
                string part = name == "ui" ? "UI_Canvases" : name == "camera" ? "camera_follow_player" : null;
                if (part != null && !parts.Contains(part)) parts.Add(part);
            }
            if (parts.Count == 0) return;
            if (!hooked)
            {
                hooked = true;
                CrowEvents.Loaded("PlayerSplit.sceneLoaded", (scene, mode) => Split());
            }
            Split();
        }

        // At each scene load: an area's MaraLoader has activated the player in its Awake by then.
        static void Split()
        {
            GameObject player = MaraScene.FindPlayer();
            Transform root = player == null ? null : player.transform.root;
            if (root == null || root.gameObject.scene.name != Scenes.Kept) return;
            if (root.position != Vector3.zero || root.rotation != Quaternion.identity || root.lossyScale != Vector3.one)
            {
                Debug.Log("CROW_SPLIT the player's root isn't at the origin; left whole");
                return;
            }
            // The last child first, each made the root after the player's: they keep their order.
            var children = new List<Transform>();
            foreach (Transform child in root)
                if (parts.Contains(child.name.Replace(' ', '_'))) children.Add(child);
            foreach (Transform part in children.OrderByDescending(t => t.GetSiblingIndex()))
            {
                int before = root.hierarchyCount;
                var holder = new GameObject(part.name + " (split)");
                holder.SetActive(false);
                holder.transform.SetParent(root, false);
                holder.transform.SetSiblingIndex(part.GetSiblingIndex());
                var mirror = holder.AddComponent<SplitMirror>();
                mirror.part = part.gameObject;
                mirror.active = part.gameObject.activeSelf;
                part.SetParent(null, true);
                UnityEngine.Object.DontDestroyOnLoad(part.gameObject);
                part.SetSiblingIndex(root.GetSiblingIndex() + 1);
                if (!root.gameObject.activeInHierarchy) part.gameObject.SetActive(false);
                holder.SetActive(true);
                Debug.Log("CROW_SPLIT " + part.name + " a root of its own: the player's hierarchy " + before + " -> " + root.hierarchyCount + " transforms");
            }
        }
    }
}
