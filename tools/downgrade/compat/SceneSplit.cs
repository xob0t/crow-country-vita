using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    // DowngradeBuild's CrowMergeScenes; CROW_SPLIT=on, the default, or off), each with a SplitPart
    // naming its old parent and place; below objects the game switches on and off, a SplitHolder in
    // the part's place switches it. A first run-time test that took out every subtree that didn't move
    // in 60 frames: main thread Station Square 30.6 -> 26.8 ms, Pool 31.0 -> 24.4 ms.
    //   -crow-scene-split off|on|check|report
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
            SceneManager.sceneLoaded += (scene, mode) => Dirty();
            SceneManager.sceneUnloaded += scene => Dirty();
            Application.onBeforeRender += Tick;
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
                    // Each part where it would be below the old parent: its old local position through the
                    // parent's matrix, its rotation after the parent's (Unity's world rotation, also below a
                    // mirroring parent). Its scale stays: the rooms move and turn, they don't scale.
                    Quaternion rotation = place.Old.rotation;
                    int count = 0;
                    foreach (SplitPart part in place.Parts)
                    {
                        if (part == null) continue;
                        Transform t = part.transform;
                        if (t.parent == part.parent) continue;  // put back (-crow-scene-split off)
                        t.SetPositionAndRotation(now.MultiplyPoint3x4(part.position), rotation * part.rotation);
                        count++;
                    }
                    followed += count;
                    if (!place.Logged && count > 0)
                    {
                        place.Logged = true;
                        Debug.Log("CROW_SPLIT_FOLLOW " + Perf.Path(place.Old, 4) + " moved: " + count + " parts taken out from below it follow it");
                    }
                }
                float time = Time.realtimeSinceStartup;
                if (moves > 0 && time >= summaryAt)
                {
                    summaryAt = time + 30f;
                    Debug.Log("CROW_SPLIT_FOLLOW " + places.Count + " old parents watched, " + moves + " moves, " + followed + " parts placed; most: " +
                              string.Join(" ", places.Where(p => p.Moves > 0 && p.Old != null).OrderByDescending(p => p.Moves).Take(4)
                                  .Select(p => Perf.Path(p.Old, 3) + "=" + p.Moves).ToArray()));
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
            Merge.MoverDebug = Array.IndexOf(args, "-crow-split-mover-log") >= 0;
            SplitFollow.Install(args);  // first: its frame step runs before the check's
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
                Application.onBeforeRender += Check;
            }
            if (hooked) return;
            hooked = true;
            SceneManager.sceneLoaded += (scene, loadMode) =>
            {
                bool area = scene.name != "Logos" && scene.name != "Title" && scene.name != "CrowMara" && scene.name != "CrowGap";
                if (area && (mode.StartsWith("report") || mode.StartsWith("apply")))
                {
                    Merge.SplitMovers = mode.Contains("movers");
                    Debug.Log("CROW_" + Merge.Split(scene, !mode.StartsWith("report")));
                }
                else Apply();
            };
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
            Debug.Log("CROW_SPLIT_CHECK " + Perf.Path(part.transform, 3) + " (from " + Perf.Path(part.parent, 3) + "): " +
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
}
