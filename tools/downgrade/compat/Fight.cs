using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// The fight benchmark: one launch that loads an area, sets up a fight and measures it, the same way
// every time (tools/downgrade/fightbench.py runs it on the console or a Windows player).
//   -crow-fight <mode>         real: the area's own enemies, switched on through the game's globals
//                              (its story items and visited flags, set before the area loads, so the
//                              area's FSMs wake them as on a later visit); stress: the same plus
//                              copies of the area's enemies around Mara (-crow-fight-extra, default
//                              12), dead copies replaced from a pool (-crow-fight-pool); idle: the same setup,
//                              aiming with the laser, without enemies or shots (the baseline); stand: the
//                              area as the game loads it, Mara left where it puts her, nothing set, no
//                              aim (the plain standing frame rate); walk: Mara on the spot, no enemies or
//                              aim, her position every 0.1 s of game time (CROW_FIGHT_POS) while the pad
//                              walks her (fightbench.py --input); discover: log the enemies and quit
//   -crow-fight-area <scene>   the area (default Station Square); -crow-scene is set from it
//   -crow-fight-spot x,y,z,yaw[,camyaw]  where Mara stands, her facing and the camera's (default: the
//                              area's entry in Areas, else near the biggest group of enemies)
//   -crow-fight-extra <n>      stress mode's copies (default 12); -crow-fight-pool <n> spare copies
//                              that replace dead ones, one a second at most (default 12)
//   -crow-fight-seconds <s>    measured time (default 60), in windows of -crow-fight-window <s> (10)
//   -crow-fight-settle <s>     seconds from the area's load to the setup (default 12: after VitaLit's
//                              passes and MeshRelease), then -crow-fight-engage <s> (6) of fighting
//                              before the first window
//   -crow-fight-fire <s>       seconds between shots (default 0.7); empty clips are reloaded
//   -crow-fight-kill <s>       every s seconds the target dies through its own damage FSM (default 8 in
//                              real mode, 6 in stress; 0 for none): shots alone kill few, as Grinners
//                              grab Mara and low shots at Blobs hit the floor. The death a bullet gives
//                              (its event with a stand-in bullet); -crow-fight-kill-mode explosion gives
//                              the grenade's instead (a Grinner blows apart into 22 dynamic pieces)
//   -crow-fight-log            log the first 40 shots: target, aim, the target's health
//   -crow-fight-aim off        no aiming and no shots: the enemies awake around Mara, the pad free for
//                              the menus (fightbench --input)
//   -crow-fight-globals "<name>=<value>;..."  more PlayMaker globals set before the load, typed as the
//                              variable is (e.g. "item: map=1;MapFF=true": the map and its Fairytale Town page)
//   -crow-fight-cycle <sets>   a change set per window, the sets in turn ('/' between sets, ',' between
//                              changes, "-" for none; Quality.Change's keys, so -crow-ab's and every
//                              module's), logged as set= (A/B in one launch: with
//                              -crow-fight-kill 0 -crow-fight-fire 1000 the fight holds steady)
//   -crow-fight-run <name>     ignored: fightbench.py names each run, so its log's args line differs
//                              from the last run's
//   -crow-fight-moves "t:x,y,z,yaw[,camyaw];..."  t seconds after the fight starts (CROW_FIGHT_START), Mara and
//                              the camera to another spot (as the setup puts them), pinned there from then on
//                              (CROW_FIGHT_MOVE): enemies near the new spot wake from their culling, those at
//                              the old one are culled
//   -crow-fight-hold <type>:x,z  every enemy of that type (Lumber, Human_Guest...) held at x,z on the ground
//                              (its body put back there each frame, its height left to physics): it walks
//                              towards Mara on the spot, its scripts and body as busy as when it walks, at a
//                              distance of the run's choosing (an enemy the fight wakes walks up to her)
// The dumps of 16-18 Oct (-crow-fight-dump, -crow-fight-fixed, -crow-fight-fsm) and the fixed step's test
// (-crow-fight physics, FixedStep.cs' rule since 19 Oct) are gone; CROW_FIGHT_TYPE, one line per enemy type at the
// setup, stays.
// Mara: on the spot, pinned there (pushes undone every half second), the handgun with the laser
// sight and a full box, unhurtable (DefenseMulti 0, Health kept at its maximum, never KO, poisoned or
// on fire), the camera locked (CameraLock, no aim swivel). Aiming: the combat FSM (character
// RIGIDBODY's "combat mode controls (+ KEYBOARD)") taken to On by its own event and kept there (the
// action that leaves On when the aim button is up is disabled); every shot her body turns to the
// nearest live enemy that is not culled, the aim angle (global "Aiming Angle") points at it, and the
// FSM gets "shoot", or "reload" on an empty clip. Targets nearer than 1.5 m (below the aim's reach)
// only when no other is awake. The bullets are the game's (physics projectiles).
// Enemies cull themselves beyond 5 m of the camera's visibility point (their Pause/Cull FSMs), so a
// spot near them matters. UnityEngine.Random is seeded (1234) at the setup.
// Each window logs CROW_FIGHT_WINDOW: frame time mean and percentiles, the main thread's own run time
// (PS Vita), player loop time by subsystem (from Quality.cs's LoopProfile: -crow-loop-profile, which
// Fight installs if missing), collections and their pause (-crow-gc-log), the allocation rate (IL2CPP),
// enemies alive and awake, shots and kills, the lowest free main memory (PS Vita), each awake enemy's distance and
// renderers on and drawn at the window's end (near=); the windows'
// CROW_LOOP and CROW_CAMS lines follow (cycle=fight-wN). Then CROW_FIGHT_SUMMARY over all windows,
// and the player quits.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Fight
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        // Seconds since the player started after which an area still loading fails the run (area-not-loaded), short of
        // fightbench.py's own --timeout (420 s by default), so the run ends with a reason.
        const float LoadGiveUp = 400f;

        // Per area: the globals that switch its enemies on as on a later visit, and the fight's spot.
        class Area
        {
            public string Scene;
            public string[] Bools = new string[0];
            public string[] Items = new string[0];  // 'item: N' set to 1
            public string[] IntNames = new string[0];
            public int[] IntValues = new int[0];
            public float[] Spot;  // x, y, z, Mara's yaw, the camera's yaw; null: picked at run time
        }

        static readonly Area[] Areas =
        {
            // 9 enemies (6 Blobs, 2 Grinners, a Spindle) under Revisiting room (VisitedCrowCountry), after
            // items 2, 5 and 9; Harrison James (NPC6) switches them off below story step 3.
            // The spot: by the entry -crow-scene uses, 7 of them within 5 m; the camera as the load sets it.
            new Area { Scene = "Station Square", Bools = new[] { "VisitedCrowCountry" }, Items = new[] { "2", "5", "9" },
                       IntNames = new[] { "NPC6" }, IntValues = new[] { 3 }, Spot = new[] { -1.0f, -0.02f, 2.2f, 180f, 180f } },
            // 12 (6 Blobs, 2 Grinners, 2 Guests, a Goblin, a Lumber); the Goblin and Guests only on Hard Mode.
            new Area { Scene = "Ocean Kingdom", Bools = new[] { "VisitedOceanKingdom", "VisitedSubmarine" }, Items = new[] { "3", "4" } },
            // 10: 2 Guests on a first visit; 4 Blobs, 2 Arms after Big Metal Door; a Puddle, a Goblin after item 10.
            new Area { Scene = "Haunted Hilltop", Bools = new[] { "VisitedHauntedHilltop", "Big Metal Door" }, Items = new[] { "10" } },
            // 8 (4 Blobs, 2 Grinners, 2 Guests) under Revisiting room (VisitedFairytaleTown), after moving
            // Arthur (VisitedHauntedHilltop, or NPC1 3): 2 Grinners after item 6 (the gem), the Guests and
            // Blobs after VisitedSaveLounge. They stand along the town's street (x -0.9..0.7, z 5-25); the
            // spot is on the street, 3-4 m from a Grinner, a Blob and a Guest (the automatic spot fell
            // through the floor by the mushroom house).
            new Area { Scene = "Fairy Forest", Bools = new[] { "VisitedFairytaleTown", "VisitedHauntedHilltop", "VisitedSaveLounge" },
                       Items = new[] { "6" }, Spot = new[] { 0f, 0f, 12f, 0f, 0f } },
            new Area { Scene = "Underground SW" },
            new Area { Scene = "Pre Machine Centre" },
        };

        static readonly string[] EnemyArrays =
        {
            "EnemyArrayGuest", "EnemyArrayBlob", "EnemyArrayGrinner", "EnemyArrayPuddle", "EnemyArrayGoblin",
            "EnemyArrayArms", "EnemyArraySpindle", "EnemyArrayLumber", "EnemyArrayWaller",
        };

        enum Phase { Off, Loading, Settle, Engage, Measure, Done }

        static string mode;
        static Area area;
        static float[] spot;
        static int extra = 12, poolSize = 12;
        static float seconds = 60f, window = 10f, settle = 12f, engage = 6f, fireEvery = 0.7f, killEvery = -1f;
        static Phase phase = Phase.Off;
        static float phaseStart, loadedAt = -1f;
        static bool beforeLoadDone;

        internal static bool On { get { return mode != null; } }

        // Diag's -crow-scene when none is given.
        internal static string Scene { get { return mode == null ? null : area.Scene; } }

        // From Diag.Install.
        internal static void Install(string[] args)
        {
            mode = CrowFlags.Value(args, "-crow-fight");
            if (mode == null) return;
            if (mode != "real" && mode != "stress" && mode != "idle" && mode != "stand" && mode != "discover" && mode != "walk")
            {
                Debug.Log("CROW_FIGHT_FAIL reason=unknown-mode mode=" + mode);
                mode = null;
                return;
            }
            string scene = CrowFlags.Value(args, "-crow-fight-area") ?? CrowFlags.Value(args, "-crow-scene") ?? "Station Square";
            area = Areas.FirstOrDefault(a => a.Scene == scene) ?? new Area { Scene = scene };
            spot = area.Spot;
            string spotText = CrowFlags.Value(args, "-crow-fight-spot");
            if (spotText != null && (spot = Floats(spotText)) == null)
            {
                Debug.Log("CROW_FIGHT bad -crow-fight-spot " + spotText + ": the area's spot instead");
                spot = area.Spot;
            }
            extra = (int)CrowFlags.Number(args, "-crow-fight-extra", extra);
            poolSize = (int)CrowFlags.Number(args, "-crow-fight-pool", poolSize);
            seconds = CrowFlags.Number(args, "-crow-fight-seconds", seconds);
            window = CrowFlags.Number(args, "-crow-fight-window", window);
            settle = CrowFlags.Number(args, "-crow-fight-settle", settle);
            engage = CrowFlags.Number(args, "-crow-fight-engage", engage);
            fireEvery = CrowFlags.Number(args, "-crow-fight-fire", fireEvery);
            if (CrowFlags.Has(args, "-crow-fight-log")) shotLog = 40;
            string cycle = CrowFlags.Value(args, "-crow-fight-cycle");
            if (cycle != null) cycleSets = cycle.Split('/');
            killEvery = CrowFlags.Number(args, "-crow-fight-kill", mode == "stress" ? 6f : 8f);
            explosionKills = CrowFlags.Value(args, "-crow-fight-kill-mode") == "explosion";
            noAim = CrowFlags.Value(args, "-crow-fight-aim") == "off";
            extraGlobals = CrowFlags.Value(args, "-crow-fight-globals");
            string holdValue = CrowFlags.Value(args, "-crow-fight-hold");
            if (holdValue != null)
            {
                int colon = holdValue.LastIndexOf(':');
                float[] place = colon > 0 ? Floats(holdValue.Substring(colon + 1)) : null;
                if (place != null && place.Length == 2)
                {
                    holdType = holdValue.Substring(0, colon);
                    holdAt = new Vector2(place[0], place[1]);
                }
                else Debug.Log("CROW_FIGHT bad -crow-fight-hold " + holdValue + ": <type>:x,z");
            }
            string movesValue = CrowFlags.Value(args, "-crow-fight-moves");
            if (movesValue != null)
                foreach (string move in movesValue.Split(';'))
                {
                    string[] parts = move.Split(':');
                    float at;
                    if (parts.Length != 2 || !float.TryParse(parts[0], NumberStyles.Float, Inv, out at)) continue;
                    float[] place = Floats(parts[1]);
                    if (place != null && place.Length >= 4) moves.Add(new KeyValuePair<float, float[]>(at, place));
                }
            LoopProfile.Install(1e9f);  // only Fight's flushes report
            if (Application.platform == RuntimePlatform.PSP2) mainUid = Threads.SelfAnyway();
            phase = Phase.Loading;
            CrowEvents.Loaded("Fight.sceneLoaded", (loaded, how) =>
            {
                if (loaded.name == area.Scene && phase == Phase.Loading)
                {
                    loadedAt = Time.realtimeSinceStartup;
                    phase = Phase.Settle;
                    phaseStart = loadedAt;
                }
            });
            Debug.Log("CROW_FIGHT installed mode=" + mode + " area=" + area.Scene.Replace(' ', '_') + " seconds=" + F(seconds) +
                      " window=" + F(window) + " extra=" + extra + " fire=" + F(fireEvery));
        }

        // "x,y,z,..." as numbers, or null when one isn't a number.
        static float[] Floats(string text)
        {
            string[] parts = text.Split(',');
            var values = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!float.TryParse(parts[i], NumberStyles.Float, Inv, out values[i])) return null;
            return values;
        }

        // From Diag, just before it loads the area: the globals the area's FSMs read as they start.
        internal static void BeforeLoad()
        {
            if (mode == null || mode == "stand" || mode == "walk" || beforeLoadDone) return;
            beforeLoadDone = true;
            if (!Reflect())
            {
                Fail("no-playmaker");
                return;
            }
            var set = new StringBuilder();
            Global("ExplorationMode", false, set);
            Global("Hard Mode", false, set);
            Global("item: 12", 0, set);  // night: every enemy stays off
            if (mode != "idle")
            {
                foreach (string name in area.Bools) Global(name, true, set);
                foreach (string item in area.Items) Global("item: " + item, 1, set);
                for (int i = 0; i < area.IntNames.Length; i++) Global(area.IntNames[i], area.IntValues[i], set);
                ClearEnemyArrays();
            }
            if (extraGlobals != null)
                foreach (string pair in extraGlobals.Split(';'))
                {
                    int equals = pair.LastIndexOf('=');
                    if (equals <= 0) continue;
                    string name = pair.Substring(0, equals).Trim();
                    string text = pair.Substring(equals + 1).Trim();
                    object current = Variable(name) == null ? null : GlobalValue(name);
                    object value = text;
                    if (current is bool) value = text == "1" || text.ToLowerInvariant() == "true";
                    else if (current is int) value = int.Parse(text, Inv);
                    else if (current is float) value = float.Parse(text, Inv);
                    Global(name, value, set);
                }
            Global("item: handgun", true, set);
            Global("gun equipped", 1, set);
            Global("Handgun Laser", true, set);
            Global("DefenseMulti", 0f, set);
            Global("CameraLock", true, set);
            Global("SETTINGSAimCameraSwivel", false, set);
            Global("Ammo in Clip", 8, set);
            object boxMax = GlobalValue("Ammo in Box MAX");
            if (boxMax is int) Global("Ammo in Box", (int)boxMax, set);
            Debug.Log("CROW_FIGHT globals" + set);
        }

        // ------------------------------------------------------------------------------ reflection

        static Type fsmComponent;
        static object globals;
        static MethodInfo findVariable;
        static PropertyInfo rawValue;

        static bool Reflect()
        {
            if (fsmComponent != null) return true;
            fsmComponent = GameTypes.PlayMakerFsm;
            Type variables = Type.GetType("HutongGames.PlayMaker.FsmVariables, PlayMaker");
            Type named = Type.GetType("HutongGames.PlayMaker.NamedVariable, PlayMaker");
            if (fsmComponent == null || variables == null || named == null) return false;
            globals = variables.GetProperty("GlobalVariables", BindingFlags.Static | BindingFlags.Public).GetValue(null, null);
            findVariable = variables.GetMethod("FindVariable", new[] { typeof(string) });
            rawValue = named.GetProperty("RawValue");
            return globals != null && findVariable != null && rawValue != null;
        }

        static object Variable(string name)
        {
            return findVariable.Invoke(globals, new object[] { name });
        }

        static object GlobalValue(string name)
        {
            object variable = Variable(name);
            return variable == null ? null : rawValue.GetValue(variable, null);
        }

        static void Global(string name, object value, StringBuilder log)
        {
            object variable = Variable(name);
            if (variable == null)
            {
                log.Append(" missing:").Append(name.Replace(' ', '_'));
                return;
            }
            try
            {
                rawValue.SetValue(variable, value, null);
                log.Append(' ').Append(name.Replace(' ', '_')).Append('=').Append(Convert.ToString(value, Inv));
            }
            catch (Exception e)
            {
                log.Append(" failed:").Append(name.Replace(' ', '_')).Append('(').Append(e.GetType().Name).Append(')');
            }
        }

        // The killed-enemy flags: every entry false, so every enemy (and copy) spawns alive.
        static void ClearEnemyArrays()
        {
            foreach (string name in EnemyArrays)
            {
                object array = Variable(name);
                if (array == null) continue;
                try
                {
                    Type type = array.GetType();
                    int length = (int)type.GetProperty("Length").GetValue(array, null);
                    MethodInfo setItem = type.GetMethod("Set", new[] { typeof(int), typeof(object) });
                    for (int i = 0; i < length; i++) setItem.Invoke(array, new object[] { i, false });
                }
                catch (Exception e)
                {
                    Debug.Log("CROW_FIGHT clearing " + name + ": " + e.GetType().Name);
                }
            }
        }

        // A typed accessor of a variable's Value, made once, so the per-frame calls neither look up
        // nor box (the benchmark's own garbage would count in its collections).
        static T Accessor<T>(object variable, bool setter) where T : class
        {
            if (variable == null) return null;
            PropertyInfo value = variable.GetType().GetProperty("Value");
            MethodInfo method = value == null ? null : setter ? value.GetSetMethod() : value.GetGetMethod();
            if (method == null) return null;
            try
            {
                return Delegate.CreateDelegate(typeof(T), variable, method) as T;
            }
            catch (Exception e)
            {
                Debug.Log("CROW_FIGHT no delegate for " + variable + ": " + e.GetType().Name);
                return null;
            }
        }

        static Func<string> StateOf(Component fsm)
        {
            if (fsm == null) return null;
            MethodInfo getter = fsmComponent.GetProperty("ActiveStateName").GetGetMethod();
            return (Func<string>)Delegate.CreateDelegate(typeof(Func<string>), fsm, getter);
        }

        static Component FsmOn(GameObject gameObject)
        {
            return gameObject == null ? null : gameObject.GetComponent(fsmComponent);
        }

        // A descendant by name: names here contain '/', which Transform.Find takes as a path.
        static Transform Descendant(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = Descendant(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        // ------------------------------------------------------------------------------ the fight

        class Enemy
        {
            public GameObject Root;
            public string Type;
            public Func<string> SpawnState, CullState;
            public Component Damage;  // its damage FSM ("damage / kill ...", Blobs' and Spiders' "shoot me")
            public object HealthVariable;  // that FSM's "my health" (none on Blobs)
            public bool KillFailed, WasAlive, Forced;
            public float LastHealth = -1f;
            public Func<float> ReadHealth;
            public Collider[] Colliders;
            public bool Copy;
        }

        static readonly List<Enemy> enemies = new List<Enemy>();
        static readonly List<GameObject> pool = new List<GameObject>();
        static readonly Dictionary<GameObject, Transform> modelOf = new Dictionary<GameObject, Transform>();
        static Transform poolHolder;
        static GameObject body;
        static Rigidbody bodyRigidbody;
        static Vector3 pinned;
        static Transform aimOrigin;
        static Component combat;
        static Func<string> combatState;
        static Action<string> sendCombat;
        static Action<float> setHealth, setAimAngle;
        static Func<float> getMaxHealth;
        static Action<bool> setKo, setPoisoned, setOnFire;
        static Func<int> getClip, getKills;
        static Action<int> setBox;
        static int boxMax = 99;
        static float nextShot, nextPin, nextCombat, nextRefill, nextSpawn;
        static int lastClip, shots, kills0, reloads;

        static void Fail(string reason)
        {
            Debug.Log("CROW_FIGHT_FAIL reason=" + reason + " mode=" + mode + " area=" + area.Scene.Replace(' ', '_'));
            phase = Phase.Done;
            Quit();
        }

        static void Quit()
        {
            ProbeFiles.Flush(3000);
            Application.Quit();
        }

        // From Diag.Update, every frame.
        internal static void Frame()
        {
            if (mode == null || phase == Phase.Off || phase == Phase.Done) return;
            float now = Time.realtimeSinceStartup;
            // The frame's time by the wall clock: Unity clamps Time.unscaledDeltaTime to
            // maximumDeltaTime too, which -crow-physics-cap sets to its cap (2/30 s on the PS Vita).
            frameMs = lastFrameAt >= 0f ? (now - lastFrameAt) * 1000f : Time.unscaledDeltaTime * 1000f;
            lastFrameAt = now;
            if (phase == Phase.Loading)
            {
                if (now > LoadGiveUp) Fail("area-not-loaded");
                return;
            }
            if (phase == Phase.Settle)
            {
                if (now - phaseStart < settle) return;
                bool ready;
                try
                {
                    ready = Setup();
                }
                catch (Exception exception)
                {
                    // Tried again each frame (the player may not be ready yet), logged once, and given
                    // up after 20 s with the reason (fightbench.py reads it).
                    if (setupFailedAt < 0f)
                    {
                        setupFailedAt = now;
                        Debug.Log("CROW_FIGHT setup failed (trying again): " + exception);
                    }
                    if (now - setupFailedAt >= 20f) Fail("setup-" + exception.GetType().Name);
                    return;
                }
                if (!ready) return;
                if (mode == "discover")
                {
                    phase = Phase.Done;
                    Quit();
                    return;
                }
                phase = Phase.Engage;
                phaseStart = now;
                fightStart = now;
                Debug.Log("CROW_FIGHT_START t=" + F(now) + " engage=" + F(engage));
                return;
            }
            CountHits();
            if (moves.Count > 0 && now - fightStart >= moves[0].Key) Move(moves[0].Value, now);
            if (moveCheckAt > 0f && now >= moveCheckAt)
            {
                moveCheckAt = -1f;
                Debug.Log("CROW_FIGHT_MOVED t=" + F(now) + " alive=" + Alive(false) + " awake=" + enemies.Count(e => IsAwake(e)) + " culls=" +
                          string.Join(",", enemies.Select(e => e.Root == null ? "gone" : e.Type.Replace("Enemy_", "") + ":" + (e.CullState == null ? "-" : e.CullState().Replace(' ', '_'))).ToArray()));
            }
            if (mode == "walk") LogPosition();
            else if (mode != "stand") Keep(now);
            if (mode != "idle" && mode != "stand" && mode != "walk") Shoot(now);
            if (mode == "stress") Replace(now);
            if (phase == Phase.Engage)
            {
                if (now - phaseStart < engage) return;
                phase = Phase.Measure;
                phaseStart = now;
                StartWindow(now);
                windowIndex = 0;
                LoopProfile.Flush("fight-engage");
                ActionProfile.Report("fight-engage", 1);  // starts the first window's sums afresh
                return;
            }
            Measure(now);
        }

        static readonly List<KeyValuePair<float, float[]>> moves = new List<KeyValuePair<float, float[]>>();
        static float fightStart, moveCheckAt = -1f;

        // -crow-fight-moves: Mara and the camera to the next spot, pinned there.
        static void Move(float[] place, float now)
        {
            moves.RemoveAt(0);
            GameObject player = MaraScene.FindPlayer();
            MonoBehaviour rig = player == null ? null : player.GetComponents<MonoBehaviour>().FirstOrDefault(b => b != null && b.GetType().Name == "CharacterAndCamera");
            if (rig == null || body == null) return;
            var marker = new GameObject("CrowFightSpot");
            marker.transform.position = new Vector3(place[0], place[1], place[2]);
            rig.GetType().GetMethod("SetPosition").Invoke(rig, new object[] { marker.transform, place[3], place.Length >= 5 ? place[4] : place[3] });
            UnityEngine.Object.Destroy(marker);
            if (bodyRigidbody != null)
            {
                bodyRigidbody.position = body.transform.position;
                bodyRigidbody.velocity = Vector3.zero;
            }
            pinned = body.transform.position;
            Debug.Log("CROW_FIGHT_MOVE t=" + F(now) + " to " + V(pinned).Replace(' ', ',') + " alive=" + Alive(false) + " awake=" + enemies.Count(e => IsAwake(e)));
            moveCheckAt = now + 2f;
        }

        static bool Setup()
        {
            Scene scene = SceneManager.GetSceneByName(area.Scene);
            GameObject player = MaraScene.FindPlayer();
            MonoBehaviour rig = player == null ? null : player.GetComponents<MonoBehaviour>().FirstOrDefault(b => b != null && b.GetType().Name == "CharacterAndCamera");
            if (!Reflect())
            {
                Fail("no-playmaker");
                return false;
            }
            if (!scene.IsValid() || rig == null)
            {
                Fail(rig == null ? "no-player" : "no-scene");
                return false;
            }
            UnityEngine.Random.InitState(1234);
            body = rig.GetType().GetField("character").GetValue(rig) as GameObject;
            bodyRigidbody = body == null ? null : body.GetComponent<Rigidbody>();
            FindEnemies(scene);
            Vector3 entry = body.transform.position;
            Debug.Log("CROW_FIGHT player at " + V(entry) + " yaw " + F(body.transform.eulerAngles.y));
            foreach (Enemy enemy in enemies) LogEnemy("enemy", enemy);
            if (mode == "stand")
            {
                // The area as the game loads it: Mara where it put her, nothing set, no aim.
                Debug.Log("CROW_FIGHT_SETUP spot=" + V(entry).Replace(' ', ',') + " enemies=" + enemies.Count + " alive=" + Alive(false) + " mode=stand");
                return true;
            }
            foreach (Enemy enemy in enemies.GroupBy(e => e.Type).Select(g => g.First())) Census(enemy.Root);
            if (mode == "discover") return true;

            // The spot: given, or 3 m from the enemy with the most live neighbours within 5 m, toward the entry.
            Vector3 place;
            float yaw, cameraYaw;
            if (spot != null && spot.Length >= 4)
            {
                place = new Vector3(spot[0], spot[1], spot[2]);
                yaw = spot[3];
                cameraYaw = spot.Length >= 5 ? spot[4] : spot[3];
            }
            else
            {
                place = AutoSpot(entry);
                Vector3 toward = Centre(place) - place;
                yaw = toward.sqrMagnitude > 0.01f ? Mathf.Atan2(toward.x, toward.z) * Mathf.Rad2Deg : body.transform.eulerAngles.y;
                // The camera as the area's load set it (its yaw and the player's turn of it): the
                // area's walls and roofs are laid out for its views.
                var cameraObject = rig.GetType().GetField("camera").GetValue(rig) as GameObject;
                var turn = rig.GetType().GetField("cameraRotateMe").GetValue(rig) as GameObject;
                cameraYaw = (cameraObject == null ? yaw : cameraObject.transform.eulerAngles.y) + (turn == null ? 0f : turn.transform.localEulerAngles.y);
            }
            var marker = new GameObject("CrowFightSpot");
            marker.transform.position = place;
            rig.GetType().GetMethod("SetPosition").Invoke(rig, new object[] { marker.transform, yaw, cameraYaw });
            UnityEngine.Object.Destroy(marker);
            if (bodyRigidbody != null)
            {
                bodyRigidbody.position = body.transform.position;
                bodyRigidbody.velocity = Vector3.zero;
            }
            pinned = body.transform.position;
            Debug.Log("CROW_FIGHT_SETUP spot=" + V(place).Replace(' ', ',') + " yaw=" + F(yaw) + " camyaw=" + F(cameraYaw) +
                      " enemies=" + enemies.Count + " alive=" + Alive(false) + " mode=" + mode);

            if (mode == "walk")
            {
                // On the spot, nothing else: the pad walks her (fightbench --input), positions logged.
                walkStart = Time.time;
                return true;
            }

            // Mara's state, her gun and the combat FSM.
            setHealth = Accessor<Action<float>>(Variable("Health"), true);
            getMaxHealth = Accessor<Func<float>>(Variable("MAX Health"), false);
            setKo = Accessor<Action<bool>>(Variable("KO"), true);
            setPoisoned = Accessor<Action<bool>>(Variable("Poisoned"), true);
            setOnFire = Accessor<Action<bool>>(Variable("on fire"), true);
            setAimAngle = Accessor<Action<float>>(Variable("Aiming Angle"), true);
            getClip = Accessor<Func<int>>(Variable("Ammo in Clip"), false);
            getKills = Accessor<Func<int>>(Variable("Kills"), false);
            setBox = Accessor<Action<int>>(Variable("Ammo in Box"), true);
            object max = GlobalValue("Ammo in Box MAX");
            if (max is int) boxMax = (int)max;
            kills0 = getKills == null ? 0 : getKills();
            lastClip = getClip == null ? 0 : getClip();
            Transform combatObject = Descendant(body.transform, "combat mode controls (+ KEYBOARD)");
            combat = combatObject == null || noAim ? null : FsmOn(combatObject.gameObject);
            Transform aim = Descendant(body.transform, "aim raycast");
            aimOrigin = aim != null ? aim : body.transform;
            if (combat != null)
            {
                combatState = StateOf(combat);
                sendCombat = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), combat, fsmComponent.GetMethod("SendEvent", new[] { typeof(string) }));
                KeepAiming();
            }
            else Debug.Log("CROW_FIGHT no combat FSM: no shots");
            if (mode == "stress") Spawn(place);
            nextShot = Time.realtimeSinceStartup + 1f;
            nextKill = Time.realtimeSinceStartup + engage;  // the first in the first window
            return true;
        }

        // The combat FSM's On state ends when neither the aim button nor its key is held (a
        // BoolNoneTrue action that sends "done"): off, so On holds without input.
        static void KeepAiming()
        {
            try
            {
                object fsm = fsmComponent.GetProperty("Fsm").GetValue(combat, null);
                object state = fsm.GetType().GetMethod("GetState", new[] { typeof(string) }).Invoke(fsm, new object[] { "On" });
                var actions = state.GetType().GetProperty("Actions").GetValue(state, null) as Array;
                foreach (object action in actions)
                {
                    if (action == null || action.GetType().Name != "BoolNoneTrue") continue;
                    action.GetType().GetProperty("Enabled").SetValue(action, false, null);
                    Debug.Log("CROW_FIGHT combat On: BoolNoneTrue off");
                }
            }
            catch (Exception e)
            {
                Debug.Log("CROW_FIGHT combat On action: " + e.GetType().Name + " " + e.Message);
            }
        }

        static void FindEnemies(Scene scene)
        {
            enemies.Clear();
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Rigidbody rigidbody in root.GetComponentsInChildren<Rigidbody>(true))
                {
                    GameObject gameObject = rigidbody.gameObject;
                    if (!EnemyNames.Named(gameObject.name)) continue;
                    enemies.Add(Track(gameObject, false));
                }
        }

        static Enemy Track(GameObject root, bool copy)
        {
            Transform spawnKill = Descendant(root.transform, EnemyNames.SpawnKill);
            Transform cull = Descendant(root.transform, EnemyNames.Cull) ?? Descendant(root.transform, EnemyNames.CullOther);
            string type = EnemyNames.TypeOf(root.transform);
            Component damage = null;
            object health = null;
            foreach (Component fsm in root.GetComponentsInChildren(fsmComponent, true))
            {
                string name = fsm.gameObject.name;
                if (!EnemyNames.IsDamage(name)) continue;
                damage = fsm;
                object variables = fsmComponent.GetProperty("FsmVariables").GetValue(fsm, null);
                health = variables.GetType().GetMethod("FindVariable", new[] { typeof(string) }).Invoke(variables, new object[] { "my health" });
                break;
            }
            return new Enemy
            {
                Damage = damage,
                HealthVariable = health,
                Root = root,
                Type = type,
                Copy = copy,
                SpawnState = StateOf(spawnKill == null ? null : FsmOn(spawnKill.gameObject)),
                CullState = StateOf(cull == null ? null : FsmOn(cull.gameObject)),
                Colliders = root.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger).ToArray(),
            };
        }

        static bool IsAlive(Enemy enemy)
        {
            if (enemy.Root == null || !enemy.Root.activeInHierarchy) return false;
            return enemy.SpawnState == null || enemy.SpawnState() == "currently alive";
        }

        static bool IsAwake(Enemy enemy)
        {
            return IsAlive(enemy) && (enemy.CullState == null || enemy.CullState() == "visible");
        }

        static string holdType;
        static Vector2 holdAt;

        // -crow-fight-hold: the type's enemies back on their spot, their height and velocity's fall kept.
        static void Hold()
        {
            foreach (Enemy enemy in enemies)
            {
                if (enemy.Root == null || enemy.Type.Replace("Enemy_", "").Replace(' ', '_') != holdType) continue;
                Rigidbody rigid = enemy.Root.GetComponent<Rigidbody>();
                if (rigid == null) continue;
                Vector3 place = rigid.position;
                place.x = holdAt.x;
                place.z = holdAt.y;
                rigid.position = place;
                enemy.Root.transform.position = place;
                rigid.velocity = new Vector3(0f, rigid.velocity.y, 0f);
            }
        }

        // The awake enemies at the window's end (the window line's near=): type, metres from Mara on the ground and
        // above her, renderers on and of those drawn in the last frame, as "Lumber:5.2:-0.3:31/27,..."; "-" for none.
        static string Near()
        {
            var parts = new List<string>();
            foreach (Enemy enemy in enemies)
            {
                if (!IsAwake(enemy)) continue;
                Rigidbody rigid = enemy.Root.GetComponentInChildren<Rigidbody>();  // the root's, or its walking body's
                Vector3 offset = (rigid != null ? rigid.position : enemy.Root.transform.position) - pinned;
                float above = offset.y;
                offset.y = 0f;
                int on = 0, drawn = 0;
                foreach (Renderer renderer in enemy.Root.GetComponentsInChildren<Renderer>(false))
                {
                    if (!renderer.enabled) continue;
                    on++;
                    if (renderer.isVisible) drawn++;
                }
                parts.Add(enemy.Type.Replace("Enemy_", "").Replace(' ', '_') + ":" + F(offset.magnitude) + ":" + F(above) + ":" + on + "/" + drawn);
            }
            return parts.Count == 0 ? "-" : string.Join(",", parts.ToArray());
        }

        static int Alive(bool awake)
        {
            int count = 0;
            for (int i = 0; i < enemies.Count; i++)
                if (awake ? IsAwake(enemies[i]) : IsAlive(enemies[i])) count++;
            return count;
        }

        static void LogEnemy(string what, Enemy enemy)
        {
            var path = new StringBuilder();
            for (Transform t = enemy.Root.transform.parent; t != null; t = t.parent) path.Insert(0, t.name.Replace(' ', '_') + "/");
            Debug.Log("CROW_FIGHT " + what + " " + enemy.Root.name.Replace(' ', '_') + " at " + V(enemy.Root.transform.position).Replace(' ', ',') +
                      " active=" + (enemy.Root.activeInHierarchy ? 1 : 0) + " spawn=" + (enemy.SpawnState == null ? "-" : enemy.SpawnState()) +
                      " cull=" + (enemy.CullState == null ? "-" : enemy.CullState()) + " path=" + path);
        }

        // What one enemy of each type is made of (CROW_FIGHT_TYPE), for what its physics and scripts cost.
        static void Census(GameObject root)
        {
            var line = new StringBuilder("CROW_FIGHT_TYPE ").Append(root.name.Replace(' ', '_'));
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            line.Append(" transforms=").Append(all.Length).Append(" activeTransforms=").Append(all.Count(t => t.gameObject.activeInHierarchy));
            line.Append(" activeRenderers=").Append(root.GetComponentsInChildren<Renderer>(false).Count(r => r.enabled));
            line.Append(" activeFsms=").Append(root.GetComponentsInChildren(fsmComponent, false).Count(c => ((Behaviour)c).enabled));
            line.Append(" activeAudio=").Append(root.GetComponentsInChildren<AudioSource>(false).Count(a => a.enabled));
            Rigidbody[] bodies = root.GetComponentsInChildren<Rigidbody>(true);
            line.Append(" bodies=").Append(bodies.Length).Append(" kinematic=").Append(bodies.Count(b => b.isKinematic));
            line.Append(" interpolate=").Append(bodies.Count(b => b.interpolation != RigidbodyInterpolation.None));
            line.Append(" continuous=").Append(bodies.Count(b => b.collisionDetectionMode != CollisionDetectionMode.Discrete));
            var kinds = new Dictionary<string, int>();
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                var mesh = collider as MeshCollider;
                string kind = (mesh != null ? (mesh.convex ? "MeshConvex" : "Mesh") : collider.GetType().Name.Replace("Collider", "")) +
                              (collider.isTrigger ? "Trigger" : "") + (collider.enabled && collider.gameObject.activeInHierarchy ? "" : "Off");
                int count;
                kinds.TryGetValue(kind, out count);
                kinds[kind] = count + 1;
            }
            foreach (KeyValuePair<string, int> pair in kinds.OrderBy(p => p.Key)) line.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            line.Append(" fsms=").Append(root.GetComponentsInChildren(fsmComponent, true).Length);
            line.Append(" behaviours=").Append(root.GetComponentsInChildren<MonoBehaviour>(true).Length);
            line.Append(" renderers=").Append(root.GetComponentsInChildren<Renderer>(true).Length);
            line.Append(" skinned=").Append(root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length);
            line.Append(" audio=").Append(root.GetComponentsInChildren<AudioSource>(true).Length);
            line.Append(" particles=").Append(root.GetComponentsInChildren<ParticleSystem>(true).Length);
            line.Append(" lights=").Append(root.GetComponentsInChildren<Light>(true).Length);
            line.Append(" joints=").Append(root.GetComponentsInChildren<Joint>(true).Length);
            Debug.Log(line.ToString());
        }

        static Vector3 TargetPoint(Enemy enemy)
        {
            bool any = false;
            Bounds bounds = new Bounds();
            foreach (Collider collider in enemy.Colliders)
            {
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                if (!any) bounds = collider.bounds;
                else bounds.Encapsulate(collider.bounds);
                any = true;
            }
            return any ? bounds.center : enemy.Root.transform.position + Vector3.up * 0.5f;
        }

        static Vector3 Centre(Vector3 from)
        {
            Vector3 sum = Vector3.zero;
            int count = 0;
            foreach (Enemy enemy in enemies)
            {
                if (!IsAlive(enemy) || (enemy.Root.transform.position - from).magnitude > 8f) continue;
                sum += enemy.Root.transform.position;
                count++;
            }
            return count == 0 ? from + Vector3.forward : sum / count;
        }

        // Mara's spot when none is set: where she entered if 3 or more live enemies are within 6 m,
        // else 3 m from the enemy with the most live neighbours within 5 m, toward the entry.
        static Vector3 AutoSpot(Vector3 entry)
        {
            Enemy best = null;
            int most = -1;
            foreach (Enemy enemy in enemies)
            {
                if (!IsAlive(enemy)) continue;
                int near = enemies.Count(o => o != enemy && IsAlive(o) && (o.Root.transform.position - enemy.Root.transform.position).magnitude < 5f);
                if (near > most)
                {
                    most = near;
                    best = enemy;
                }
            }
            if (best == null) return entry;
            // Where the area's load put her, when a group is already there (Station Square's entry
            // has 7 of its 9 within 5 m).
            if (enemies.Count(e => IsAlive(e) && (e.Root.transform.position - entry).magnitude < 6f) >= 3) return entry;
            Vector3 from = best.Root.transform.position;
            Vector3 toward = entry - from;
            toward.y = 0f;
            if (toward.sqrMagnitude < 0.01f) toward = Vector3.forward;
            Vector3 place = from + toward.normalized * Clear(from, toward.normalized, 3f);
            return Ground(place, from.y);
        }

        const int Player = 18, NotPlayer = 31;
        static readonly int Solid = Physics.DefaultRaycastLayers & ~(1 << Player) & ~(1 << NotPlayer);

        // How far one can go from a point (at waist height) along a direction, up to a distance.
        static float Clear(Vector3 from, Vector3 direction, float distance)
        {
            RaycastHit hit;
            if (Physics.Raycast(from + Vector3.up, direction, out hit, distance, Solid, QueryTriggerInteraction.Ignore))
                return Mathf.Max(0f, hit.distance - 0.5f);
            return distance;
        }

        static Vector3 Ground(Vector3 at, float fallbackY)
        {
            RaycastHit hit;
            if (Physics.Raycast(at + Vector3.up * 2f, Vector3.down, out hit, 6f, Solid, QueryTriggerInteraction.Ignore))
                return hit.point;
            at.y = fallbackY;
            return at;
        }

        // Stress mode: copies of the area's enemies (round robin over its types) in a ring around Mara
        // (2.5-4 m), and a pool of inactive spares that replace dead ones.
        static void Spawn(Vector3 centre)
        {
            var models = enemies.Where(e => e.SpawnState != null).GroupBy(e => e.Type).Select(g => g.First()).ToList();
            if (models.Count == 0) models = enemies.GroupBy(e => e.Type).Select(g => g.First()).ToList();
            if (models.Count == 0)
            {
                Debug.Log("CROW_FIGHT no enemies to copy in " + area.Scene);
                return;
            }
            var holder = new GameObject("CrowFightPool");
            holder.SetActive(false);
            SceneManager.MoveGameObjectToScene(holder, SceneManager.GetSceneByName(area.Scene));
            poolHolder = holder.transform;
            for (int i = 0; i < extra + poolSize; i++)
            {
                Enemy model = models[i % models.Count];
                EnemySplit.RestoreAll(model.Root.transform);  // a copy needs all its parts
                GameObject copy = UnityEngine.Object.Instantiate(model.Root, poolHolder);
                EnemySplit.Mirror(model.Root.transform, copy.transform);  // the build's moved debris: the copy's out too
                EnemySplit.Mirror(model.Root.transform, model.Root.transform);  // and the model's again
                copy.name = model.Root.name + " (fight " + i + ")";
                OwnTrail(copy);
                EnemyCost.Prepare(copy);
                copy.SetActive(true);  // under the inactive holder: wakes when placed
                pool.Add(copy);
                modelOf[copy] = model.Root.transform;
            }
            for (int i = 0; i < extra; i++) Place(centre, i);
            Debug.Log("CROW_FIGHT spawned " + extra + " copies of " + string.Join(",", models.Select(m => m.Type).ToArray()) + ", " + poolSize + " spare");
        }

        // A Blob's trail (BlobTrailSpawn.pool) is pieces that leave the Blob's hierarchy once used, so
        // a copy made in play still lists the original's (and throws once the original dies and its
        // pieces go): the copy gets copies of those pieces.
        static void OwnTrail(GameObject copy)
        {
            foreach (MonoBehaviour behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null || behaviour.GetType().Name != "BlobTrailSpawn") continue;
                FieldInfo field = behaviour.GetType().GetField("pool");
                var pieces = field == null ? null : field.GetValue(behaviour) as List<GameObject>;
                if (pieces == null) continue;
                for (int i = 0; i < pieces.Count; i++)
                {
                    GameObject piece = pieces[i];
                    if (piece == null || piece.transform.IsChildOf(copy.transform)) continue;
                    GameObject own = UnityEngine.Object.Instantiate(piece, piece.transform.parent);
                    own.SetActive(false);
                    pieces[i] = own;
                }
            }
        }

        static int placed;

        static void Place(Vector3 centre, int slot)
        {
            if (pool.Count == 0) return;
            GameObject copy = pool[0];
            pool.RemoveAt(0);
            float angle = slot * 137.5f + UnityEngine.Random.Range(-10f, 10f);
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            float radius = Clear(centre, direction, 2.5f + (slot % 3) * 0.75f);
            if (radius < 1.2f) radius = 1.2f;
            Vector3 at = Ground(centre + direction * radius, centre.y);
            Transform original = modelOf[copy];
            float lift = original.position.y - Ground(original.position, original.position.y).y;
            ClearEnemyArrays();
            Transform parent = original.parent;
            if (parent != null && parent.gameObject.activeInHierarchy) copy.transform.SetParent(parent, false);
            else
            {
                copy.transform.SetParent(null, false);
                SceneManager.MoveGameObjectToScene(copy, SceneManager.GetSceneByName(area.Scene));
            }
            copy.transform.position = at + Vector3.up * Mathf.Max(lift, 0f);
            copy.transform.rotation = Quaternion.LookRotation(-direction);
            var rigidbody = copy.GetComponent<Rigidbody>();
            if (rigidbody != null)
            {
                rigidbody.position = copy.transform.position;
                rigidbody.velocity = Vector3.zero;
            }
            Enemy tracked = Track(copy, true);
            enemies.Add(tracked);
            placed++;
        }

        static void Replace(float now)
        {
            if (pool.Count == 0 || now < nextSpawn) return;
            int alive = 0;
            foreach (Enemy enemy in enemies)
                if (enemy.Copy && IsAlive(enemy)) alive++;
            if (alive >= extra) return;
            nextSpawn = now + 1f;
            Place(pinned, placed);
            replaced++;
        }

        static int replaced;

        // Every frame: Mara unhurt and on her spot, the box full, the combat FSM aiming.
        static void Keep(float now)
        {
            if (setHealth != null && getMaxHealth != null) setHealth(getMaxHealth());
            if (setKo != null) setKo(false);
            if (setPoisoned != null) setPoisoned(false);
            if (setOnFire != null) setOnFire(false);
            if (now >= nextRefill)
            {
                nextRefill = now + 1f;
                if (setBox != null) setBox(boxMax);
            }
            if (holdType != null) Hold();
            if (body != null && now >= nextPin)
            {
                nextPin = now + 0.5f;
                Vector3 drift = body.transform.position - pinned;
                drift.y = 0f;
                if (drift.sqrMagnitude > 0.09f)
                {
                    body.transform.position = pinned;
                    if (bodyRigidbody != null)
                    {
                        bodyRigidbody.position = pinned;
                        bodyRigidbody.velocity = Vector3.zero;
                    }
                }
            }
            if (getClip != null)
            {
                int clip = getClip();
                if (clip < lastClip) shots += lastClip - clip;
                if (clip > lastClip) reloads++;
                lastClip = clip;
            }
            if (combatState != null && now >= nextCombat && combatState() == "Off")
            {
                nextCombat = now + 0.5f;
                sendCombat("on");
            }
        }

        const float MinRange = 1.5f;

        // Hits: a live enemy's "my health" going down (the damage FSMs take it from a bullet; Fight's own
        // kills set it to 0 and are left out), and shot kills: deaths that weren't Fight's (Blobs die at
        // the first hit, with no health to watch).
        static int hits, shotKills;

        static void CountHits()
        {
            // Every 10th frame: a hit takes the enemy's FSM through several states, and its health stays
            // down; deaths are seen by IsAlive. (Every frame, through reflection, this cost 2% of the
            // main thread at the PS Vita's speed.)
            if (Time.frameCount % 10 != 0) return;
            for (int i = 0; i < enemies.Count; i++)
            {
                Enemy enemy = enemies[i];
                bool alive = IsAlive(enemy);
                if (enemy.WasAlive && !alive && !enemy.Forced) shotKills++;
                enemy.WasAlive = alive;
                if (!alive || enemy.HealthVariable == null) continue;
                if (enemy.ReadHealth == null) enemy.ReadHealth = Accessor<Func<float>>(enemy.HealthVariable, false);
                if (enemy.ReadHealth == null) continue;
                float health = enemy.ReadHealth();
                if (enemy.LastHealth >= 0f && health < enemy.LastHealth - 1e-4f && !enemy.Forced) hits++;
                enemy.LastHealth = health;
            }
        }

        // -crow-fight walk: Mara's body every 0.1 s of game time (CROW_FIGHT_POS game time, x, y, z, speed).
        static float walkStart, nextPosition;

        static void LogPosition()
        {
            float t = Time.time - walkStart;
            if (body == null || t < nextPosition) return;
            nextPosition = Mathf.Floor(t * 10f + 1f) / 10f;
            Vector3 p = body.transform.position;
            Debug.Log("CROW_FIGHT_POS " + t.ToString("0.000", Inv) + " " + p.x.ToString("0.000", Inv) + " " + p.y.ToString("0.000", Inv) + " " +
                      p.z.ToString("0.000", Inv) + " " + (bodyRigidbody == null ? 0f : bodyRigidbody.velocity.magnitude).ToString("0.000", Inv));
        }
        static float nextKill, victimCheck;
        static int killed;
        static Enemy victim;

        // A kill the FSM ignored (not in its waiting state: a Grinner holding Mara) is tried again on
        // the next nearest; the one that ignored it waits for the round after.
        static void CheckKill(float now)
        {
            if (victim == null || now < victimCheck) return;
            if (IsAlive(victim))
            {
                victim.KillFailed = true;
                nextKill = now;
            }
            victim = null;
        }

        static Enemy KillTarget(Vector3 from)
        {
            Enemy best = null;
            float nearest = float.MaxValue;
            int pass;
            for (pass = 0; pass < 2 && best == null; pass++)
                for (int i = 0; i < enemies.Count; i++)
                {
                    Enemy enemy = enemies[i];
                    if (!IsAwake(enemy) || enemy.Damage == null || (pass == 0 && enemy.KillFailed)) continue;
                    float distance = FlatSquared(enemy, from);
                    if (distance >= nearest) continue;
                    nearest = distance;
                    best = enemy;
                }
            if (pass == 2)  // every awake one had ignored a kill: a new round
                for (int i = 0; i < enemies.Count; i++) enemies[i].KillFailed = false;
            return best;
        }
        static MethodInfo sendEvent;

        // -crow-fight-kill: the target dies through its own damage FSM, as from a last hit (Blobs and
        // Spiders: "hit"; the others: "my health" 0, then "explosion", which their FSMs take from a
        // grenade), so deaths and their effects come at a steady rate whatever the shots hit.
        static bool explosionKills;
        static string extraGlobals;  // -crow-fight-globals "name=value;...": more globals before the load
        static bool noAim;  // -crow-fight-aim off: no aiming and no shots (the pad free for menus)
        static GameObject fakeBullet;
        static float setupFailedAt = -1f;  // the first Setup exception

        // The death a bullet gives (no explosion: a Grinner falls, as when it's shot): health just above
        // the damage FSM's 0.01, its "the bullet" a stand-in at the enemy's middle (the headshot check
        // reads its height), and the bullet event ("bullet", or "hit" where that is the bullet's event);
        // "explosion" if neither moves the FSM out of its waiting state.
        static void GunKill(Enemy enemy)
        {
            if (fakeBullet == null) fakeBullet = new GameObject("CrowFightBullet");
            fakeBullet.transform.position = TargetPoint(enemy);
            object variables = fsmComponent.GetProperty("FsmVariables").GetValue(enemy.Damage, null);
            object bullet = variables.GetType().GetMethod("FindVariable", new[] { typeof(string) }).Invoke(variables, new object[] { "the bullet" });
            if (bullet != null) rawValue.SetValue(bullet, fakeBullet, null);
            if (enemy.HealthVariable != null) rawValue.SetValue(enemy.HealthVariable, 0.012f, null);
            Func<string> state = StateOf(enemy.Damage);
            string before = state();
            foreach (string name in new[] { "bullet", "hit", "explosion" })
            {
                sendEvent.Invoke(enemy.Damage, new object[] { name });
                if (state() != before) return;
            }
        }

        static void Kill(Enemy enemy)
        {
            if (enemy.Damage == null) return;
            try
            {
                if (sendEvent == null) sendEvent = fsmComponent.GetMethod("SendEvent", new[] { typeof(string) });
                bool shootMe = enemy.Damage.gameObject.name == EnemyNames.ShootMe;  // Blobs (no health: "hit" kills) and Spiders
                if (shootMe) sendEvent.Invoke(enemy.Damage, new object[] { "hit" });
                else if (explosionKills)
                {
                    if (enemy.HealthVariable != null) rawValue.SetValue(enemy.HealthVariable, 0f, null);
                    sendEvent.Invoke(enemy.Damage, new object[] { "explosion" });
                }
                else GunKill(enemy);
                killed++;
                enemy.Forced = true;
                victim = enemy;
                victimCheck = Time.realtimeSinceStartup + 1.5f;
            }
            catch (Exception e)
            {
                Debug.Log("CROW_FIGHT kill " + enemy.Root.name + ": " + e.GetType().Name);
            }
        }

        static Enemy Nearest(Vector3 from, float minRange)
        {
            Enemy target = null;
            float nearest = float.MaxValue;
            for (int i = 0; i < enemies.Count; i++)
            {
                if (!IsAwake(enemies[i])) continue;
                Vector3 offset = enemies[i].Root.transform.position - from;
                offset.y = 0f;
                float distance = offset.sqrMagnitude;
                if (distance < minRange * minRange || distance >= nearest) continue;
                nearest = distance;
                target = enemies[i];
            }
            return target;
        }

        static void Shoot(float now)
        {
            if (combatState == null || now < nextShot) return;
            nextShot = now + fireEvery;
            if (combatState() != "On") return;
            Vector3 from = body.transform.position;
            // Not one at her feet if another is awake (the aim's 40 degrees down can't reach it), but
            // the shots go on whatever the enemies do: a steady rate of fire is the benchmark's load.
            Enemy target = Nearest(from, MinRange) ?? Nearest(from, 0f);
            if (target == null) return;
            CheckKill(now);
            if (killEvery > 0f && now >= nextKill && victim == null)
            {
                nextKill = now + killEvery;
                Enemy dies = KillTarget(from);
                if (dies != null) Kill(dies);
            }
            Vector3 point = TargetPoint(target);
            Vector3 flat = point - from;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.0001f)
            {
                Quaternion facing = Quaternion.Euler(0f, Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg, 0f);
                body.transform.rotation = facing;
                if (bodyRigidbody != null) bodyRigidbody.rotation = facing;
            }
            if (setAimAngle != null)
            {
                Vector3 origin = aimOrigin.position;
                float rise = point.y - origin.y, run = new Vector2(point.x - origin.x, point.z - origin.z).magnitude;
                setAimAngle(Mathf.Clamp(-Mathf.Atan2(rise, Mathf.Max(run, 0.1f)) * Mathf.Rad2Deg, -40f, 40f));
            }
            string fire = getClip != null && getClip() <= 0 ? "reload" : "shoot";
            if (shotLog > 0)
            {
                shotLog--;
                Vector3 o = aimOrigin.position;
                Debug.Log("CROW_FIGHT shot " + fire + " at " + target.Root.name.Replace(' ', '_') + " point " + V(point).Replace(' ', ',') +
                          " from " + V(o).Replace(' ', ',') + " flat " + F(Mathf.Sqrt(FlatSquared(target, from))) + " yaw " + F(body.transform.eulerAngles.y) +
                          " health " + Health(target));
            }
            sendCombat(fire);
        }

        static int shotLog;
        static MethodInfo totalPlaying;
        static bool noTweens;

        // DOTween's playing tweens now (DG.Tweening.DOTween.TotalPlayingTweens), -1 without DOTween.
        static int PlayingTweens()
        {
            if (noTweens) return -1;
            try
            {
                if (totalPlaying == null)
                {
                    Type dotween = GameTypes.DOTween;
                    totalPlaying = dotween == null ? null : dotween.GetMethod("TotalPlayingTweens", BindingFlags.Static | BindingFlags.Public);
                    if (totalPlaying == null)
                    {
                        noTweens = true;
                        return -1;
                    }
                }
                return (int)totalPlaying.Invoke(null, null);
            }
            catch (Exception)
            {
                noTweens = true;
                return -1;
            }
        }

        static float FlatSquared(Enemy enemy, Vector3 from)
        {
            Vector3 offset = enemy.Root.transform.position - from;
            offset.y = 0f;
            return offset.sqrMagnitude;
        }

        // -crow-fight-log: the target's "my health" (its damage FSM), to see that shots land.
        static string Health(Enemy enemy)
        {
            return enemy.HealthVariable == null ? "-" : F(Convert.ToDouble(rawValue.GetValue(enemy.HealthVariable, null), Inv));
        }

        // ------------------------------------------------------------------------------ measurement

        static int mainUid;
        static long lastRun = -1;
        static int windowIndex;
        static float windowStart;
        const int MaxFrames = 20000;
        static readonly float[] windowFrames = new float[MaxFrames], allFrames = new float[MaxFrames];
        static readonly float[] sorted = new float[MaxFrames];
        static int windowCount, allCount;
        static double windowCpu, allCpu;
        static int cpuFrames, allCpuFrames;
        static long[] loopStart, loopRunStart;
        static int gcStart, gcRunsStart, gcRunStart, gcAllRuns;
        static double gcMsAll;
        static long allocStart = -1, allocRunStart = -1;
        static int shotsStart, killsStart, shotsRunStart, killsRunStart, reloadsStart, hitsStart, hitsRunStart, shotKillsStart, shotKillsRunStart;
        static double aliveSum, awakeSum, aliveRunSum, awakeRunSum;
        static int aliveSamples, aliveRunSamples;
        static float freeMin = float.MaxValue, freeRunMin = float.MaxValue, nextFree;
        static readonly List<string> windowLines = new List<string>();
        static int[] categories;
        static readonly string[] CategoryNames = { "scripts", "physics", "anim", "skin", "particles", "audio", "render", "ui", "wait", "other" };

        static float fixedStart, fixedRunStart = -1f;

        // -crow-fight-cycle: each window's change set (Quality.Change: -crow-ab's keys and every module's), the sets
        // in turn; "-" changes nothing.
        static string[] cycleSets;
        static string windowSet;

        static void StartWindow(float now)
        {
            if (cycleSets != null)
            {
                windowSet = cycleSets[windowIndex % cycleSets.Length];
                if (windowSet != "-") Quality.Change(windowSet);
            }
            windowStart = now;
            gameStart = Time.time;
            fixedStart = Time.fixedTime;
            if (fixedRunStart < 0f) fixedRunStart = fixedStart;
            windowCount = 0;
            windowCpu = 0;
            cpuFrames = 0;
            FsmTrim.Mark();
            TweenProbe.Mark();
            loopStart = (long[])LoopProfile.Totals.Clone();
            if (loopRunStart == null) loopRunStart = loopStart;
            gcStart = GC.CollectionCount(0);
            gcRunsStart = Alloc.RunsEnded;
            long alloc = AllocatedBytes();
            if (allocRunStart < 0) allocRunStart = alloc;
            allocStart = alloc;
            if (windowIndex == 0)
            {
                gameRunStart = gameStart;
                gcRunStart = gcStart;
                shotsRunStart = shots;
                killsRunStart = getKills == null ? 0 : getKills();
                hitsRunStart = hits;
                shotKillsRunStart = shotKills;
            }
            hitsStart = hits;
            shotKillsStart = shotKills;
            shotsStart = shots;
            killsStart = getKills == null ? 0 : getKills();
            reloadsStart = reloads;
            aliveSum = awakeSum = 0;
            aliveSamples = 0;
            freeMin = float.MaxValue;
        }

        static float frameMs, lastFrameAt = -1f, gameStart, gameRunStart;

        static void Measure(float now)
        {
            float ms = frameMs;
            if (windowCount < MaxFrames) windowFrames[windowCount++] = ms;
            if (allCount < MaxFrames) allFrames[allCount++] = ms;
            if (mainUid > 0)
            {
                long run = Threads.RunMicros(mainUid);
                if (run >= 0 && lastRun >= 0)
                {
                    windowCpu += (run - lastRun) / 1000.0;
                    allCpu += (run - lastRun) / 1000.0;
                    cpuFrames++;
                    allCpuFrames++;
                }
                lastRun = run;
            }
            if (Time.frameCount % 10 == 0)
            {
                int alive = Alive(false), awake = Alive(true);
                aliveSum += alive;
                awakeSum += awake;
                aliveSamples++;
                aliveRunSum += alive;
                awakeRunSum += awake;
                aliveRunSamples++;
            }
            if (now >= nextFree)
            {
                nextFree = now + 1f;
                float free = FreeMainMB();
                if (free >= 0f)
                {
                    freeMin = Math.Min(freeMin, free);
                    freeRunMin = Math.Min(freeRunMin, free);
                }
            }
            if (now - windowStart < window) return;
            windowIndex++;
            int gcRuns;
            double gcMs = Alloc.PausesSince(gcRunsStart, out gcRuns);
            gcMsAll += gcMs;
            gcAllRuns += gcRuns;
            stepsFrom = fixedStart;
            const string tag = "CROW_FIGHT_WINDOW";
            var line = Line(tag, windowFrames, windowCount, now - windowStart, cpuFrames > 0 ? windowCpu / cpuFrames : -1,
                            loopStart, GC.CollectionCount(0) - gcStart, gcMs, allocStart, shots - shotsStart,
                            (getKills == null ? 0 : getKills()) - killsStart, aliveSamples > 0 ? aliveSum / aliveSamples : -1,
                            aliveSamples > 0 ? awakeSum / aliveSamples : -1, freeMin);
            line.Insert(tag.Length, " w=" + windowIndex);
            line.Append(" reloads=").Append(reloads - reloadsStart).Append(" hits=").Append(hits - hitsStart)
                .Append(" shotkills=").Append(shotKills - shotKillsStart);
            if (windowSet != null) line.Append(" set=").Append(windowSet);
            // Game time against the wall clock's (below 1 when -crow-physics-cap clamps long frames), and
            // the physics clock's against game time's (1: the two keep together).
            float gameSpan = Time.time - gameStart;
            line.Append(" rate=").Append(F(now > windowStart ? gameSpan / (now - windowStart) : 0f))
                .Append(" fixedRate=").Append(F(gameSpan > 0f ? (Time.fixedTime - fixedStart) / gameSpan : 0f));
            int debrisBodies = 0, debrisAwake = 0;
            foreach (Rigidbody rigid in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
                if (EnemyCost.Debris(rigid))
                {
                    debrisBodies++;
                    if (!rigid.IsSleeping()) debrisAwake++;
                }
            line.Append(" debris=").Append(debrisBodies).Append('/').Append(debrisAwake);
            line.Append(" near=").Append(Near());
            int tweens = PlayingTweens();
            if (tweens >= 0) line.Append(" tweens=").Append(tweens);
            if (EnemySplit.Report() != null) line.Append(' ').Append(EnemySplit.Report());
            line.Append(' ').Append(FsmTrim.Report());
            line.Append(' ').Append(TweenProbe.Report());
            Debug.Log(line.ToString());
            LoopProfile.Flush("fight-w" + windowIndex);
            ActionProfile.Report("fight-w" + windowIndex + (windowSet != null ? " set=" + windowSet : ""), windowCount);
            if (windowIndex * window >= seconds - 0.01f)
            {
                Summary(now);
                return;
            }
            StartWindow(now);
        }

        static void Summary(float now)
        {
            phase = Phase.Done;
            stepsFrom = fixedRunStart;
            var line = Line("CROW_FIGHT_SUMMARY", allFrames, allCount, now - phaseStart, allCpuFrames > 0 ? allCpu / allCpuFrames : -1,
                            loopRunStart, GC.CollectionCount(0) - gcRunStart, gcMsAll, allocRunStart, shots - shotsRunStart,
                            (getKills == null ? 0 : getKills()) - killsRunStart, aliveRunSamples > 0 ? aliveRunSum / aliveRunSamples : -1,
                            aliveRunSamples > 0 ? awakeRunSum / aliveRunSamples : -1, freeRunMin);
            float gameRun = Time.time - gameRunStart;
            line.Append(" rate=").Append(F(now > phaseStart ? gameRun / (now - phaseStart) : 0f))
                .Append(" fixedRate=").Append(F(gameRun > 0f ? (Time.fixedTime - fixedRunStart) / gameRun : 0f));
            line.Append(" windows=").Append(windowIndex).Append(" copies=").Append(placed).Append(" replaced=").Append(replaced)
                .Append(" forced=").Append(killed).Append(" hits=").Append(hits - hitsRunStart).Append(" shotkills=").Append(shotKills - shotKillsRunStart)
                .Append(" hitsAll=").Append(hits).Append(" shotkillsAll=").Append(shotKills);
            Debug.Log(line.ToString());
            Quit();
        }

        static StringBuilder Line(string kind, float[] frames, int count, float span, double cpu, long[] loopFrom, int gc, double gcMs,
                                  long allocFrom, int shotCount, int killCount, double alive, double awake, float free)
        {
            Array.Copy(frames, sorted, count);
            Array.Sort(sorted, 0, count);
            double sum = 0;
            for (int i = 0; i < count; i++) sum += sorted[i];
            var line = new StringBuilder(kind);
            line.Append(" mode=").Append(mode).Append(" area=").Append(area.Scene.Replace(' ', '_'));
            line.Append(" t=").Append(F(Time.realtimeSinceStartup)).Append(" frames=").Append(count);
            line.Append(" ms=").Append(F(count > 0 ? sum / count : 0));
            line.Append(" p50=").Append(F(Pct(count, 0.50))).Append(" p95=").Append(F(Pct(count, 0.95)));
            line.Append(" p99=").Append(F(Pct(count, 0.99))).Append(" worst=").Append(F(count > 0 ? sorted[count - 1] : 0));
            line.Append(" fps=").Append(F(span > 0 ? count / span : 0));
            if (cpu >= 0) line.Append(" cpu=").Append(F(cpu));
            // Player loop time per frame by subsystem (LoopProfile's steps between its markers).
            List<string> labels = LoopProfile.Labels;
            long[] totals = LoopProfile.Totals;
            if (categories == null) categories = labels.Select(Category).ToArray();
            double tick = 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(count, 1);
            var byCategory = new double[CategoryNames.Length];
            var steps = new List<KeyValuePair<int, double>>();
            for (int i = 0; i < totals.Length; i++)
            {
                double each = (totals[i] - loopFrom[i]) * tick;
                byCategory[categories[i]] += each;
                steps.Add(new KeyValuePair<int, double>(i, each));
            }
            for (int c = 0; c < CategoryNames.Length; c++) line.Append(' ').Append(CategoryNames[c]).Append('=').Append(F(byCategory[c]));
            // Physics steps per frame (Time.fixedDeltaTime each): a long frame runs more of them, and
            // each runs FixedUpdate's scripts and audio too.
            double fixedSteps = (Time.fixedTime - stepsFrom) / Time.fixedDeltaTime / Math.Max(count, 1);
            line.Append(" steps=").Append(F(fixedSteps)).Append(" physicsPerStep=").Append(F(fixedSteps > 0 ? byCategory[1] / fixedSteps : 0));
            line.Append(" gc=").Append(gc).Append(" gcms=").Append(F(gcMs));
            long alloc = AllocatedBytes();
            if (alloc >= 0 && allocFrom >= 0 && span > 0) line.Append(" allocKBs=").Append(F((alloc - allocFrom) / 1024.0 / span));
            line.Append(" shots=").Append(shotCount).Append(" kills=").Append(killCount);
            if (alive >= 0) line.Append(" alive=").Append(F(alive)).Append(" awake=").Append(F(awake));
            if (free < float.MaxValue) line.Append(" freeMB=").Append(F(free));
            line.Append(" top=");
            foreach (KeyValuePair<int, double> step in steps.OrderByDescending(s => s.Value).Take(6))
                line.Append(labels[step.Key]).Append(':').Append(F(step.Value)).Append(',');
            return line;
        }

        static double Pct(int count, double fraction)
        {
            if (count == 0) return 0;
            return sorted[Math.Min(count - 1, (int)Math.Floor(fraction * (count - 1) + 0.5))];
        }

        // A player loop step's subsystem, by its Unity name.
        static int Category(string label)
        {
            if (label.Contains("ScriptRun") || label.Contains("Coroutine")) return 0;
            if (label.Contains("Physics")) return 1;
            if (label.Contains("Director") || label.Contains("Animation") || label.Contains("Animator") || label.Contains("Constraint")) return 2;
            if (label.Contains("SkinnedMesh")) return 3;
            if (label.Contains("ParticleSystem")) return 4;
            if (label.Contains("Audio")) return 5;
            if (label.Contains("FinishFrameRendering") || label.Contains("Renderer") || label.Contains("RenderTexture") ||
                label.Contains("LightProbe") || label.Contains("Enlighten") || label.Contains("LOD") || label.Contains("VideoTextures")) return 6;
            if (label.Contains("Canvas") || label.Contains("RectTransform") || label.Contains("GUI")) return 7;
            if (label.Contains("PresentAfterDraw") || label.Contains("PlayerUpdateTime") || label.Contains("PlayerSendFrame") ||
                label.Contains("WaitForTargetFPS") || label.Contains("GpuTimestamp")) return 8;
            return 9;
        }

        // Bytes the IL2CPP collector has handed out since startup; -1 elsewhere (Mono).
        static long AllocatedBytes()
        {
            return GcTotal.Bytes();
        }

        static float FreeMainMB()
        {
            return VitaMemory.FreeMainMB();
        }

        static float stepsFrom;

        static string F(double value)
        {
            return value.ToString("0.0#", Inv);
        }

        static string V(Vector3 v)
        {
            return F(v.x) + " " + F(v.y) + " " + F(v.z);
        }
    }
}
