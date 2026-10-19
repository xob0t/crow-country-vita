using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// The physics step. The game steps physics every 0.02 s (Time.fixedDeltaTime), so a 30 fps frame runs
// 1.67 steps and a 54 ms fight frame 2.7, each with its physics, the FSMs' FixedUpdate actions and the
// audio's fixed update. -crow-fixed-dt <s> (or <fps> above 1: 30, 25; game for 0.02) steps physics that
// often instead, with the game kept as it moves at 0.02 (the PS Vita's default: 30, one step a frame;
// Station Square's fight 54.1 -> 49.2 ms a frame, Mara's walk 0.990 m/s at both):
//   - Impulses applied in every step (PlayMaker AddForce with forceMode Impulse or VelocityChange and
//     everyFrame, run in FixedUpdate: Mara's walk, a Blob's move) are scaled so a body under them reaches
//     the speed it reaches at 0.02, ground friction included (the rewriter sends AddForce's Rigidbody
//     calls here, AddForce).
//   - Each rigidbody's drag and angular drag are set so a velocity decays as fast per second as with the
//     game's drag at 0.02 (PhysX damps by 1 - drag * step each step), at each scene load, for the player,
//     every second for bodies not seen before, and at a new body's first AddForce (Adjust). A clone of an
//     adjusted body (Instantiate copies its drags: the handgun's bullets and spent chambers, cloned from
//     templates in Mara's prefab; Fight's stress copies of enemies) is known by its drags, a pair FixedStep
//     set (madeFrom; no game or prefab body has such a pair), and gets its original's, not Drag() twice.
//   - The bullets (handgun, shotgun, magnum, flame bullets: 18 m/s, 0.6 m a step at 1/30) detect
//     collisions continuously, so they don't pass through an enemy's small colliders
//     (-crow-bullet-ccd off leaves them discrete).
// One-off impulses, set velocities (the Grinners' walk), gravity and forces are already step-independent.
// -crow-physics-cap on|off|<n> (on is 1; the PS Vita's default is 2): Time.maximumDeltaTime at n physics
// steps, so a frame slower than n steps runs n steps and game time advances by n steps' time (Unity clamps
// Time.deltaTime, Time.time and the fixed steps' clock alike), instead of 3 or more steps in one long frame
// making the next one long too; off keeps the project's maximumDeltaTime (0.3333 s). In a clamped frame
// the whole game slows with it, physics, scripts, animation, tweens and timers together; sound and
// unscaled time don't. One step a frame (on) saves 2-4 ms of physics in a 50-65 ms fight frame but runs
// the fight at 55-66% of real time; at 2 a frame up to 66.7 ms keeps the game's speed and only hitches
// are clamped. -crow-cycle / -crow-fight-cycle / -crow-ab take physcap=on|off|<n>.
// -crow-ground-cache <steps>|off (groundcache=; Cuts.cs sets the PS Vita's 4): the ground friction under a body
// that Gain looks up with a raycast at every AddForce (84 us a call on the PS Vita, ~1 ms a frame in Station
// Square's fight) is kept per body for that many physics steps (off: looked up at every call, as before).
// -crow-fixed-check: bodies scanned every frame; CROW_FIXED_CLONE for each clone of an adjusted body (its game
// drags, the copied ones, and what Drag() twice would have made), CROW_FIXED_AMBIGUOUS for a prefab's drags
// that look like a pair FixedStep set, and every 10 s CROW_FIXED_CHECK: the bodies whose drags aren't Drag()
// of their game drags, and how far bodies that appear in play (clones, debris switched on; not kinematic) moved
// 1 and 2 s of game time after they appeared and their peak speed (CROW_FIXED_MOVED; also at the game's step).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class FixedStep
    {
        public const float GameStep = 0.02f;
        static float step = GameStep;
        static bool on, compensate = true, bulletCcd = true;
        // Per body: the game's drag and angular drag, before Adjust.
        static readonly Dictionary<Rigidbody, Vector2> gameDrag = new Dictionary<Rigidbody, Vector2>();
        // The drags (drag, angular drag; their bits) FixedStep has set, at every step used, with the game's they
        // were made from: a new body with such a pair is a clone of an adjusted body.
        static readonly Dictionary<long, Vector2> madeFrom = new Dictionary<long, Vector2>();
        static float nextScan = -1f;
        static bool fullScan;
        // -crow-physics-cap: steps a frame at most (0: off), and the project's maximumDeltaTime.
        static float capSteps;
        static float gameMaxDelta = -1f;

        // -crow-cycle / -crow-fight-cycle / -crow-ab: fixeddt=<s or fps> (game for 0.02), as -crow-fixed-dt.
        internal static bool Change(string key, string value)
        {
            if (key == "physcap")
            {
                Cap(value);
                return true;
            }
            if (key != "fixeddt") return false;
            float number;
            if (value == "game") number = GameStep;
            else if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) || number <= 0f)
                return true;
            Hook();
            Set(number > 1f ? 1f / number : number);
            return true;
        }

        static bool hooked;

        static void Hook()
        {
            if (hooked) return;
            hooked = true;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                nextScan = Time.realtimeSinceStartup + 0.5f;
                fullScan = true;
            };
            SceneManager.sceneUnloaded += scene => Forget();
            Canvas.willRenderCanvases += Frame;
        }

        // The destroyed bodies' entries (spent chambers, debris and an unloaded area's bodies), at each unload.
        static void Forget()
        {
            var dead = new List<Rigidbody>();
            foreach (Rigidbody body in gameDrag.Keys) if (body == null) dead.Add(body);
            foreach (Rigidbody body in dead) gameDrag.Remove(body);
            dead.Clear();
            foreach (Rigidbody body in ownCollider.Keys) if (body == null) dead.Add(body);
            foreach (Rigidbody body in dead) ownCollider.Remove(body);
        }

        // From Diag.Install (every player, flags or not).
        internal static void Install(string[] args)
        {
            string value = CrowFlags.Value(args, "-crow-fixed-dt");
            bulletCcd = CrowFlags.Value(args, "-crow-bullet-ccd") != "off";
            Check.On = CrowFlags.Has(args, "-crow-fixed-check");
            Check.Old = CrowFlags.Optional(args, "-crow-fixed-check") == "old";
            string capValue = CrowFlags.Value(args, "-crow-physics-cap") ?? (Application.platform == RuntimePlatform.PSP2 ? "2" : null);
            if (capValue != null) Cap(capValue);
            // The PS Vita's default: 30 steps a second, one a frame at its 30 fps.
            if (value == null && Application.platform == RuntimePlatform.PSP2) value = "30";
            float number;
            if (Check.On) Hook();  // also at the game's step, for its numbers
            if (value == null || value == "game" || !float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out number) || number <= 0f) return;
            Hook();
            Set(number > 1f ? 1f / number : number);
        }

        // The step from now on (also Fight's physics test).
        internal static void Set(float seconds, bool keepGame = true)
        {
            step = seconds;
            compensate = keepGame;
            on = Mathf.Abs(step - GameStep) > 1e-6f && keepGame;
            Time.fixedDeltaTime = step;
            if (capSteps > 0f) ApplyCap();
            // The bodies seen so far get their drags for the new step, from the game's.
            var known = new List<Rigidbody>(gameDrag.Keys);
            foreach (Rigidbody body in known)
            {
                if (body == null)
                {
                    gameDrag.Remove(body);
                    continue;
                }
                Vector2 game = gameDrag[body];
                body.drag = on ? Drag(game.x) : game.x;
                body.angularDrag = on ? Drag(game.y) : game.y;
                if (on) madeFrom[Key(body.drag, body.angularDrag)] = game;
            }
            if (on && Time.timeSinceLevelLoad > 1f) fullScan = true;
            Debug.Log("CROW_FIXED step " + step.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + " s" + (on ? " (game kept as at 0.02)" : compensate ? "" : " (uncompensated)"));
        }

        // -crow-physics-cap's value: on (1 step), off, or a number of steps.
        static void Cap(string value)
        {
            if (gameMaxDelta < 0f) gameMaxDelta = Time.maximumDeltaTime;
            float steps;
            if (value == "on") steps = 1f;
            else if (value == "off" || value == "game") steps = 0f;
            else if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out steps) || steps < 0f)
                return;
            capSteps = steps;
            if (capSteps > 0f) ApplyCap();
            else Time.maximumDeltaTime = gameMaxDelta;
            Debug.Log("CROW_FIXED cap " + (capSteps > 0f ? capSteps.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " steps" : "off") +
                      ": maximumDeltaTime=" + Time.maximumDeltaTime.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) +
                      " fixedDeltaTime=" + Time.fixedDeltaTime.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture));
        }

        // Unity keeps maximumDeltaTime at least fixedDeltaTime.
        static void ApplyCap()
        {
            Time.maximumDeltaTime = Mathf.Max(Time.fixedDeltaTime * capSteps, Time.fixedDeltaTime);
        }

        static void Frame()
        {
            if (Time.fixedDeltaTime != step) Time.fixedDeltaTime = step;  // a game script setting it back
            float now = Time.realtimeSinceStartup;
            if (!on && Check.On) Check.Watch();
            if ((now < nextScan && !fullScan && !Check.On) || !on) return;
            nextScan = now + 1f;
            if (fullScan)
            {
                fullScan = false;
                Check.Scanning = true;
                // After a load: every body, inactive ones too (templates the game clones later).
                int count = 0;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    foreach (GameObject root in scene.GetRootGameObjects())
                        foreach (Rigidbody body in root.GetComponentsInChildren<Rigidbody>(true)) count += Adjust(body);
                }
                GameObject player = GameObject.Find("CharacterCameraPrefab(Clone)");
                if (player != null)
                    foreach (GameObject root in player.scene.GetRootGameObjects())
                        foreach (Rigidbody body in root.GetComponentsInChildren<Rigidbody>(true)) count += Adjust(body);
                Check.Scanning = false;
                Debug.Log("CROW_FIXED adjusted " + count + " bodies");
                if (Check.On) Check.Prefabs();
            }
            foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>()) Adjust(body);
            if (Check.On) Check.Frame();
        }

        static long Key(float drag, float angularDrag)
        {
            return (long)(((ulong)new Bits { Float = drag }.Int << 32) | new Bits { Float = angularDrag }.Int);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        struct Bits
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float Float;
            [System.Runtime.InteropServices.FieldOffset(0)] public uint Int;
        }

        // A body's drags for the step (once per body; a clone of an adjusted body its original's game drags);
        // bullets continuous. 1 when it was new.
        internal static int Adjust(Rigidbody body)
        {
            if (!on || body == null || gameDrag.ContainsKey(body)) return 0;
            float drag = body.drag, angularDrag = body.angularDrag;
            Vector2 game;
            bool copied = madeFrom.TryGetValue(Key(drag, angularDrag), out game);
            if (!copied || Check.Old) game = new Vector2(drag, angularDrag);
            gameDrag[body] = game;
            float made = Drag(game.x), madeAngular = Drag(game.y);
            if (made != drag) body.drag = made;
            if (madeAngular != angularDrag) body.angularDrag = madeAngular;
            madeFrom[Key(body.drag, body.angularDrag)] = game;
            if (Check.On) Check.Seen(body, game, copied, drag, angularDrag);
            if (bulletCcd && step > GameStep && body.name.EndsWith("bullet", StringComparison.OrdinalIgnoreCase) && !body.isKinematic)
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            return 1;
        }

        // -crow-fixed-check (old: clones get Drag() of their copied drags, as before 19 Oct, for A/B).
        static class Check
        {
            internal static bool On, Old, Scanning;
            static readonly Dictionary<string, int> clones = new Dictionary<string, int>();
            static readonly HashSet<string> ambiguous = new HashSet<string>();
            class Track
            {
                public Rigidbody Body;
                public string Name;
                public Vector3 Start;
                public float Since, Peak, At1;
            }
            static readonly List<Track> tracks = new List<Track>();
            static readonly Dictionary<string, float[]> moved = new Dictionary<string, float[]>();  // n, at 1 s, at 2 s, peak
            static float nextReport;
            static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

            static string F(float value)
            {
                return value.ToString("0.####", Inv);
            }

            // Per second, what a velocity keeps under the drag at this step.
            static float Keep(float drag, float seconds)
            {
                return Mathf.Pow(Mathf.Max(0f, 1f - drag * seconds), 1f / seconds);
            }

            internal static void Seen(Rigidbody body, Vector2 game, bool copied, float drag, float angularDrag)
            {
                Transform root = body.transform.root;
                string name = (root == body.transform ? body.name : root.name + "/" + body.name).Replace(' ', '_');
                float twice = Drag(drag), twiceAngular = Drag(angularDrag);
                string key = null;
                if (copied && (twice != drag || twiceAngular != angularDrag))
                    key = "CROW_FIXED_CLONE " + name + " game=" + F(game.x) + "," + F(game.y) + " copied=" + F(drag) + "," + F(angularDrag) +
                          " twice=" + F(twice) + "," + F(twiceAngular) + " keep/s game=" + Keep(game.x, GameStep).ToString("0.00000", Inv) +
                          " twice=" + Keep(twice, step).ToString("0.00000", Inv) + (Old ? " (old: twice)" : "");
                else if (!Scanning && Time.timeSinceLevelLoad > 2f)
                    key = "CROW_FIXED_NEW " + name + " drags=" + F(drag) + "," + F(angularDrag) + (copied ? " (copied, unchanged by Drag)" : "");
                else if (Scanning && !body.gameObject.activeInHierarchy && !copied)
                    key = "CROW_FIXED_INACTIVE " + name + " drags=" + F(drag) + "," + F(angularDrag);  // templates the game may clone
                if (key == null) return;
                int count;
                if (!clones.TryGetValue(key, out count)) Debug.Log(key);
                clones[key] = count + 1;
            }

            // At the game's step (nothing adjusted): the new bodies, and the report.
            static readonly HashSet<Rigidbody> watched = new HashSet<Rigidbody>();

            internal static void Watch()
            {
                foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
                    if (watched.Add(body)) Seen(body, new Vector2(body.drag, body.angularDrag), false, body.drag, body.angularDrag);
                if (watched.Count > 4096) watched.RemoveWhere(body => body == null);
                Frame();
            }

            // Prefabs in memory (not in a scene) whose drags are a pair FixedStep set for other game drags: their
            // clones would be taken for an adjusted body's.
            internal static void Prefabs()
            {
                int count = 0;
                var pairs = new HashSet<string>();
                foreach (Rigidbody body in Resources.FindObjectsOfTypeAll<Rigidbody>())
                {
                    if (body == null) continue;
                    Vector2 game = new Vector2(body.drag, body.angularDrag);
                    if (!body.gameObject.scene.IsValid())
                    {
                        count++;
                        pairs.Add(F(game.x) + "," + F(game.y));
                        Vector2 made;
                        if (madeFrom.TryGetValue(Key(game.x, game.y), out made) && made != game && ambiguous.Add(body.name))
                            Debug.Log("CROW_FIXED_AMBIGUOUS prefab " + body.name.Replace(' ', '_') + " drags " + F(game.x) + "," + F(game.y) +
                                      " = FixedStep's for " + F(made.x) + "," + F(made.y));
                    }
                }
                foreach (Vector2 game in gameDrag.Values) pairs.Add(F(game.x) + "," + F(game.y));
                Debug.Log("CROW_FIXED_PAIRS prefab bodies=" + count + " ambiguous=" + ambiguous.Count + " game drags: " + string.Join(" ", new List<string>(pairs).ToArray()));
            }

            static readonly HashSet<Rigidbody> active = new HashSet<Rigidbody>();

            // Every frame: the bodies that appear active (clones, debris switched on) followed; every 10 s the report.
            internal static void Frame()
            {
                float now = Time.time;
                bool settled = Time.timeSinceLevelLoad > 2f;
                foreach (Rigidbody body in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
                {
                    if (!active.Add(body) || !settled || body.isKinematic) continue;
                    Transform root = body.transform.root;
                    string name = (root == body.transform ? body.name : root.name + "/" + body.name).Replace(' ', '_');
                    int cut = name.LastIndexOf("_(", StringComparison.Ordinal);  // GrinnerBlobShard_(10): one kind
                    int number;
                    if (cut > 0 && name.EndsWith(")", StringComparison.Ordinal) && int.TryParse(name.Substring(cut + 2, name.Length - cut - 3), out number))
                        name = name.Substring(0, cut);
                    tracks.Add(new Track { Body = body, Name = name, Start = body.position, Since = now, At1 = -1f });
                }
                if (active.Count > 4096) active.RemoveWhere(body => body == null);
                for (int i = tracks.Count - 1; i >= 0; i--)
                {
                    Track track = tracks[i];
                    if (track.Body == null)
                    {
                        tracks.RemoveAt(i);
                        continue;
                    }
                    track.Peak = Mathf.Max(track.Peak, track.Body.velocity.magnitude);
                    float distance = Vector3.Distance(track.Body.position, track.Start);
                    if (track.At1 < 0f && now - track.Since >= 1f) track.At1 = distance;
                    if (now - track.Since < 2f) continue;
                    float[] sums;
                    if (!moved.TryGetValue(track.Name, out sums)) moved[track.Name] = sums = new float[4];
                    sums[0]++;
                    sums[1] += track.At1;
                    sums[2] += distance;
                    sums[3] += track.Peak;
                    tracks.RemoveAt(i);
                }
                float real = Time.realtimeSinceStartup;
                if (real < nextReport) return;
                nextReport = real + 10f;
                int wrong = 0;
                var names = new List<string>();
                foreach (KeyValuePair<Rigidbody, Vector2> pair in gameDrag)
                {
                    Rigidbody body = pair.Key;
                    if (body == null || (body.drag == Drag(pair.Value.x) && body.angularDrag == Drag(pair.Value.y))) continue;
                    wrong++;
                    if (names.Count < 5)
                        names.Add(body.name.Replace(' ', '_') + "=" + F(body.drag) + "," + F(body.angularDrag) + "(game " + F(pair.Value.x) + "," + F(pair.Value.y) + ")");
                }
                int clonesSeen = 0, kinds = 0, inactive = 0;
                foreach (KeyValuePair<string, int> pair in clones)
                    if (pair.Key.StartsWith("CROW_FIXED_CLONE", StringComparison.Ordinal))
                    {
                        clonesSeen += pair.Value;
                        kinds++;
                    }
                    else if (pair.Key.StartsWith("CROW_FIXED_INACTIVE", StringComparison.Ordinal)) inactive++;
                Debug.Log("CROW_FIXED_CHECK t=" + now.ToString("0.0", Inv) + " bodies=" + gameDrag.Count + " wrong=" + wrong + (names.Count > 0 ? " " + string.Join(" ", names.ToArray()) : "") +
                          " clones=" + clonesSeen + " kinds=" + kinds + " new=" + (clones.Count - kinds - inactive) + " ambiguous=" + ambiguous.Count + (Old ? " old" : ""));
                foreach (KeyValuePair<string, float[]> pair in moved)
                {
                    float n = pair.Value[0];
                    Debug.Log("CROW_FIXED_MOVED " + pair.Key.Replace(' ', '_') + " n=" + n + " at1s=" + (pair.Value[1] / n).ToString("0.000", Inv) +
                              " at2s=" + (pair.Value[2] / n).ToString("0.000", Inv) + " peak=" + (pair.Value[3] / n).ToString("0.000", Inv));
                }
            }
        }

        // The drag that damps as much per second at this step as the game's at 0.02: each step multiplies
        // a velocity by 1 - drag * step.
        static float Drag(float drag)
        {
            float keep = 1f - drag * GameStep;
            if (drag <= 0f || keep <= 0f) return drag;
            return (1f - Mathf.Pow(keep, step / GameStep)) / step;
        }

        // How much more each step's impulse must be for the same speed: under an impulse J every step and
        // damping k a step, the speed settles at J/m * k / (1 - k).
        // A body walking on the ground also loses mu * g * step of speed a step to friction (Mara: 0.4 *
        // 30 * 0.02 = 0.24 m/s against a walk of about 1 m/s), which a longer step doesn't scale like
        // the impulse: with friction the speed settles at (J/m - mu g h) * k / (1 - k), and the gain
        // makes that equal at both steps. Grounded: a ray from the centre of mass down meets a collider
        // within the body's own reach (its colliders' bounds) and 5 cm.
        static float Gain(Rigidbody body, Vector3 force, ForceMode mode)
        {
            Vector2 game;
            if (!gameDrag.TryGetValue(body, out game))
            {
                Adjust(body);  // a body new since the last scan
                if (!gameDrag.TryGetValue(body, out game)) game = new Vector2(body.drag, body.angularDrag);
            }
            float drag = game.x;
            float keep = 1f - drag * GameStep;
            if (drag <= 0f || keep <= 0f || keep >= 1f) return step / GameStep;
            float now = Mathf.Pow(keep, step / GameStep);
            float plain = keep / (1f - keep) / (now / (1f - now));
            float mu = CachedGroundFriction(body);
            if (mu <= 0f) return plain;
            float impulse = force.magnitude / (mode == ForceMode.Impulse ? body.mass : 1f);  // speed it adds a step at 0.02
            float g = -Physics.gravity.y;
            float speed = (impulse - mu * g * GameStep) * keep / (1f - keep);
            if (impulse <= 0f || speed <= 0f) return plain;  // friction holds it at 0.02 too
            return (speed * (1f - now) / now + mu * g * step) / impulse;
        }

        static readonly Dictionary<Rigidbody, Collider> ownCollider = new Dictionary<Rigidbody, Collider>();
        internal static int GroundCacheSteps;
        static readonly Dictionary<Rigidbody, Vector2> groundCache = new Dictionary<Rigidbody, Vector2>();  // friction, step

        // GroundFriction, kept per body for GroundCacheSteps physics steps.
        static float CachedGroundFriction(Rigidbody body)
        {
            if (GroundCacheSteps <= 0) return GroundFriction(body);
            float stepNow = Mathf.Round(Time.fixedTime / Time.fixedDeltaTime);
            Vector2 cached;
            if (groundCache.TryGetValue(body, out cached) && stepNow - cached.y < GroundCacheSteps && stepNow >= cached.y) return cached.x;
            if (groundCache.Count > 256) groundCache.Clear();
            float mu = GroundFriction(body);
            groundCache[body] = new Vector2(mu, stepNow);
            return mu;
        }

        // The combined dynamic friction between the body's (first solid) collider and the ground under it;
        // 0 when it isn't on the ground.
        static float GroundFriction(Rigidbody body)
        {
            Collider own;
            if (!ownCollider.TryGetValue(body, out own))
            {
                own = null;
                foreach (Collider collider in body.GetComponentsInChildren<Collider>(false))
                    if (!collider.isTrigger && collider.enabled && collider.attachedRigidbody == body)
                    {
                        own = collider;
                        break;
                    }
                ownCollider[body] = own;
            }
            if (own == null || !own.enabled) return 0f;
            Vector3 centre = body.worldCenterOfMass;
            float reach = centre.y - own.bounds.min.y + 0.05f;
            RaycastHit ground;
            if (!Physics.Raycast(centre, Vector3.down, out ground, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return 0f;
            return Combine(own.sharedMaterial, ground.collider.sharedMaterial);
        }

        // Unity's friction combine: the mode of higher priority wins (Maximum, Multiply, Minimum, Average);
        // no material is 0.6 and Average.
        static float Combine(PhysicMaterial a, PhysicMaterial b)
        {
            float fa = a == null ? 0.6f : a.dynamicFriction, fb = b == null ? 0.6f : b.dynamicFriction;
            PhysicMaterialCombine ca = a == null ? PhysicMaterialCombine.Average : a.frictionCombine;
            PhysicMaterialCombine cb = b == null ? PhysicMaterialCombine.Average : b.frictionCombine;
            PhysicMaterialCombine mode = (PhysicMaterialCombine)Math.Max((int)Priority(ca), (int)Priority(cb));
            mode = FromPriority((int)mode);
            switch (mode)
            {
                case PhysicMaterialCombine.Maximum: return Mathf.Max(fa, fb);
                case PhysicMaterialCombine.Multiply: return fa * fb;
                case PhysicMaterialCombine.Minimum: return Mathf.Min(fa, fb);
                default: return (fa + fb) / 2f;
            }
        }

        static int Priority(PhysicMaterialCombine combine)
        {
            switch (combine)
            {
                case PhysicMaterialCombine.Maximum: return 3;
                case PhysicMaterialCombine.Multiply: return 2;
                case PhysicMaterialCombine.Minimum: return 1;
                default: return 0;
            }
        }

        static PhysicMaterialCombine FromPriority(int priority)
        {
            return priority == 3 ? PhysicMaterialCombine.Maximum : priority == 2 ? PhysicMaterialCombine.Multiply :
                priority == 1 ? PhysicMaterialCombine.Minimum : PhysicMaterialCombine.Average;
        }

        static bool Repeated(ForceMode mode, bool everyFrame)
        {
            return on && everyFrame && Time.inFixedTimeStep && (mode == ForceMode.Impulse || mode == ForceMode.VelocityChange);
        }

        // PlayMaker AddForce's Rigidbody calls (rewriter PatchAddForce), with the action's everyFrame.
        public static void AddForce(Rigidbody body, Vector3 force, ForceMode mode, bool everyFrame)
        {
            if (Repeated(mode, everyFrame)) force *= Gain(body, force, mode);
            body.AddForce(force, mode);
        }

        public static void AddRelativeForce(Rigidbody body, Vector3 force, ForceMode mode, bool everyFrame)
        {
            if (Repeated(mode, everyFrame)) force *= Gain(body, force, mode);
            body.AddRelativeForce(force, mode);
        }

        public static void AddForceAtPosition(Rigidbody body, Vector3 force, Vector3 position, ForceMode mode, bool everyFrame)
        {
            if (Repeated(mode, everyFrame)) force *= Gain(body, force, mode);
            body.AddForceAtPosition(force, position, mode);
        }
    }
}
