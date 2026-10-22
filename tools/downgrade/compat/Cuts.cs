using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// The fights' cuts (18 Oct): work in a fight frame that only looks, cut or slowed. Each is its own switch,
// on by default on the PS Vita and off (the game's) elsewhere; -crow-cycle / -crow-fight-cycle / -crow-ab
// take the key after each flag, and cuts=off|on sets all of them at once (game | the PS Vita's).
//   -crow-blob-trails on|off       (trails=) off: a Blob's slime trail spawns no pieces (ScriptCost.TrailReady
//                                  says the pool is empty); the pieces out already finish. The trail is looks
//                                  only: its pieces have no collider and no FSM.
//   -crow-enemy-freeze on|culled|off (freeze=) an enemy that its own Pause/Cull FSM holds ("not visible": out of
//                                  the camera's reach, its model and sounds off; "paused": the pause, examine
//                                  or inventory flag up; culled leaves "paused" out) for 10 frames is frozen:
//                                  its FSMs' Update, FixedUpdate and LateUpdate are skipped (FsmGate, through
//                                  rewriter FsmGateHooks), the DOTween tweens and animation scripts on its parts
//                                  get no time (Decor.cs' hooks). Pause/Cull itself runs on (its distance and
//                                  pause checks decide when the enemy wakes, as in the game), and so do spawn/kill
//                                  and ExplorationMode?. A frozen FSM still takes events (hits, story sends,
//                                  callbacks); the enemy wakes the moment any of its FSMs changes state, its
//                                  Pause/Cull leaves those states or stops running, and when the switch goes off.
//                                  Its timers wait while it is frozen (a burning enemy's fire too).
//   -crow-enemy-anim full|half|off (eanim=) the game's animation scripts (RotationAnimationScript, ScaleAnimationScript,
//                                  PositionAnimationScript) on enemies' parts that carry no collider (the Blobs'
//                                  hinges, the Spindle's limbs): every other frame with two frames' time, or not at all
//   -crow-enemy-tweens full|half   (etweens=) DOTween tweens on enemies' parts that carry no collider (the Grinners'
//                                  limb wobble, the Blobs' bends and punches): every other frame with two frames'
//                                  time, each enemy's parts on the same frames; they end when they would have
//   -crow-breath on|half|off       (breath=) Mara's breathing and idle motion (the tweens on breath vert 1, breath vert
//                                  arms, breath hinge 1, breathe head horiz, breathe neck, idle hip rotation): every
//                                  other frame with two frames' time, or held where they are
//   -crow-flicker full|half        (flicker=) the "light flicker" FSMs (the traps' generator lights toggle every
//                                  10-20 ms) and the LightFlicker scripts: every other frame with two frames' time
//   -crow-props-rate <n>, -crow-props <names>  (props=) the FSMs and tweens below objects of these names (default
//                                  bunting: Station Square's 18 swinging flags, 3 tween FSMs each, which carry
//                                  colliders and so aren't Decor's decoration) on one frame in n (1, 2 or 4) with
//                                  the frames' time, on screen too
//   -crow-blood-stains <n>|game    (stains=) at most n of Mara's spawned blood stains (one per hit taken; the oldest go)
//   -crow-burst <f>|game           (burst=) PlayMaker's ParticleSystemEmit bursts (an enemy hit's blood and white
//                                  particles, 30 each) at f of their count (at least 1)
//   -crow-same-pose on|game        (samepose=) PlayMaker's SetPosition and SetRotation leave a transform alone when it
//                                  already has the value (within float rounding): a culled enemy's Pause/Cull holds
//                                  its pose that way every frame, and every write moved its whole hierarchy
//   -crow-rewired-axes <n>|game    (rwaxes=) Rewired reads the first n of each joystick's 29 Unity axes ("Joy1Axis1"...,
//                                  each an Input.GetAxis by name, which Unity looks up in its 567 axes), the rest 0;
//                                  the PS Vita's pad uses axes 1-7
//   -crow-rewired-mouse on|off     (rwmouse=) Rewired's own mouse polling (7 buttons and 3 axes by name, at the end of
//                                  the 567 axes, every frame, whether the mouse controller is on or not)
//   -crow-rewired-joysticks first|all (rwjoys=) Unity on the PS Vita reports four joysticks ("PS Vita" and three
//                                  without a name), and Rewired gives all four to player 0 and polls them every
//                                  frame; first leaves only the first with a name on (looked at every 2 s) and
//                                  Unity's joysticks past it unread (rewriter CutsRewired). Taking the others from
//                                  player 0 didn't hold (Rewired gives them back) nor save anything.
//   -crow-input-bench              8 s after the first area load, the cost of those Unity input calls (CROW_INPUT_BENCH)
//                                  and Rewired's players, controllers and maps (CROW_REWIRED_REPORT)
// Test key: pmglobal=<name>:true|false (a PlayMaker global bool, e.g. pause mode: the enemies held as in a menu).
// Also (other files): -crow-ground-cache (FixedStep.cs, groundcache=), -crow-decor-visible (Decor.cs, decorvisible=).
//   -crow-cuts-log                 every 10 s, CROW_CUTS: frozen enemies, FSM updates skipped and run, wakes by cause;
//                                  and the blood stains removed (until 22 Oct logged without it too: a line, with its
//                                  managed stack trace, for each hit Mara took past the cap)
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Cuts
    {
        static readonly bool Vita = Application.platform == RuntimePlatform.PSP2;
        internal static bool NoTrails;
        internal static int FreezeMode;  // 0 off, 1 culled only, 2 culled and paused
        internal static int EnemyAnim = 1;  // 0 off, 1 full, 2 half
        internal static int EnemyTweens = 1;  // 1 full, 2 half
        internal static int Breath = 1;  // 0 held, 1 on, 2 half
        internal static int FlickerDivisor = 1;
        internal static int PropsDivisor = 1;
        internal static string[] Props = { "bunting" };
        internal static int StainCap;  // 0: the game's (no cap)
        internal static float Burst = 1f;
        // Read by the patched code.
        public static bool SamePose;
        public static int RewiredAxes = 29;
        public static bool RewiredNoMouse;
        public static int RewiredMaxJoystick = 16;  // Unity joystick ids past it read 0 (RewiredProbe.Joysticks sets it)
        static bool log;

        // From Diag.Install (every player).
        internal static void Install(string[] args)
        {
            SetAll(Vita);
            string value = CrowFlags.Value(args, "-crow-blob-trails");
            if (value != null) Change("trails", value);
            if ((value = CrowFlags.Value(args, "-crow-enemy-freeze")) != null) Change("freeze", value);
            if ((value = CrowFlags.Value(args, "-crow-enemy-anim")) != null) Change("eanim", value);
            if ((value = CrowFlags.Value(args, "-crow-enemy-tweens")) != null) Change("etweens", value);
            if ((value = CrowFlags.Value(args, "-crow-breath")) != null) Change("breath", value);
            if ((value = CrowFlags.Value(args, "-crow-flicker")) != null) Change("flicker", value);
            if ((value = CrowFlags.Value(args, "-crow-props-rate")) != null) Change("props", value);
            if ((value = CrowFlags.Value(args, "-crow-props")) != null) Props = value.Split(',');
            if ((value = CrowFlags.Value(args, "-crow-blood-stains")) != null) Change("stains", value);
            if ((value = CrowFlags.Value(args, "-crow-burst")) != null) Change("burst", value);
            if ((value = CrowFlags.Value(args, "-crow-same-pose")) != null) Change("samepose", value);
            if ((value = CrowFlags.Value(args, "-crow-rewired-axes")) != null) Change("rwaxes", value);
            if ((value = CrowFlags.Value(args, "-crow-rewired-mouse")) != null) Change("rwmouse", value);
            if ((value = CrowFlags.Value(args, "-crow-rewired-joysticks")) != null) Change("rwjoys", value);
            if ((value = CrowFlags.Value(args, "-crow-ground-cache")) != null) Change("groundcache", value);
            if ((value = CrowFlags.Value(args, "-crow-decor-visible")) != null) Change("decorvisible", value);
            log = Array.IndexOf(args, "-crow-cuts-log") >= 0;
            if (Array.IndexOf(args, "-crow-input-bench") >= 0) InputBench.Install();
            CrowEvents.Unloaded("Cuts.sceneUnloaded", scene => FsmGate.Clear());
            CrowEvents.Loaded("Cuts.sceneLoaded", (scene, mode) => stainsAt = Time.realtimeSinceStartup + 2f);
            CrowEvents.Canvases("Cuts.Frame", Frame);
            Debug.Log("CROW_CUTS " + Describe());
        }

        // The PS Vita's set (on) or the game's (off).
        static void SetAll(bool on)
        {
            NoTrails = on;
            FreezeMode = on ? 2 : 0;
            EnemyAnim = on ? 2 : 1;
            EnemyTweens = on ? 2 : 1;
            Breath = on ? 2 : 1;
            FlickerDivisor = on ? 2 : 1;
            PropsDivisor = on ? 4 : 1;
            StainCap = on ? 8 : 0;
            Burst = on ? 0.5f : 1f;
            SamePose = on;
            RewiredAxes = on ? 8 : 29;
            RewiredNoMouse = on;
            RewiredProbe.FirstJoystickOnly = on;
            RewiredProbe.Rescan();
            FixedStep.GroundCacheSteps = on ? 4 : 0;
            Decor.VisibleDivisor = on ? 2 : 1;
            Refresh();
        }

        static string Describe()
        {
            return "trails=" + (NoTrails ? "off" : "on") + " freeze=" + (FreezeMode == 2 ? "on" : FreezeMode == 1 ? "culled" : "off") +
                   " eanim=" + Rate(EnemyAnim) + " etweens=" + Rate(EnemyTweens) + " breath=" + Rate(Breath) +
                   " flicker=" + Rate(FlickerDivisor) + " props=" + PropsDivisor + "(" + string.Join(",", Props) + ")" +
                   " stains=" + (StainCap > 0 ? StainCap.ToString() : "game") + " burst=" + Burst.ToString("0.##", CultureInfo.InvariantCulture) +
                   " samepose=" + (SamePose ? "on" : "game") + " rwaxes=" + RewiredAxes + " rwmouse=" + (RewiredNoMouse ? "off" : "on") +
                   " rwjoys=" + (RewiredProbe.FirstJoystickOnly ? "first" : "all") +
                   " groundcache=" + FixedStep.GroundCacheSteps + " decorvisible=" + Decor.VisibleDivisor;
        }

        static string Rate(int value)
        {
            return value == 0 ? "off" : value == 1 ? "full" : "half";
        }

        // The hooks' switches follow the rules (Decor.Change too).
        internal static void Refresh()
        {
            DecorTweens.Active = Decor.On || EnemyTweens > 1 || FreezeMode > 0 || Breath != 1 || PropsDivisor > 1;
            FsmGate.Active = FreezeMode > 0 || FlickerDivisor > 1 || PropsDivisor > 1;
            if (FreezeMode == 0) Enemies.WakeAll("switch");
        }

        internal static bool ScriptRules { get { return FreezeMode > 0 || EnemyAnim != 1 || PropsDivisor > 1; } }

        // A -crow-cycle / -crow-fight-cycle / -crow-ab change; false when it isn't one of these.
        internal static bool Change(string key, string value)
        {
            int number;
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
            switch (key)
            {
                case "cuts":
                    SetAll(value != "off" && value != "game");
                    break;
                case "trails":
                    NoTrails = value == "off";
                    break;
                case "freeze":
                    FreezeMode = value == "on" ? 2 : value == "culled" ? 1 : 0;
                    break;
                case "eanim":
                    EnemyAnim = value == "off" ? 0 : value == "half" ? 2 : 1;
                    break;
                case "etweens":
                    EnemyTweens = value == "half" ? 2 : 1;
                    break;
                case "breath":
                    Breath = value == "off" ? 0 : value == "half" ? 2 : 1;
                    break;
                case "flicker":
                    FlickerDivisor = value == "half" ? 2 : 1;
                    break;
                case "props":
                    PropsDivisor = number >= 4 ? 4 : number >= 2 ? 2 : 1;
                    break;
                case "stains":
                    StainCap = value == "game" ? 0 : Math.Max(0, number);
                    stainsAt = 0f;
                    break;
                case "burst":
                    float fraction;
                    Burst = value == "game" || !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out fraction) ? 1f : Mathf.Clamp01(fraction);
                    break;
                case "samepose":
                    SamePose = value == "on";
                    break;
                case "rwaxes":
                    RewiredAxes = value == "game" || number <= 0 ? 29 : Math.Min(29, number);
                    break;
                case "rwmouse":
                    RewiredNoMouse = value == "off";
                    break;
                case "rwjoys":
                    RewiredProbe.FirstJoystickOnly = value == "first";
                    RewiredProbe.Rescan();
                    break;
                case "groundcache":
                    FixedStep.GroundCacheSteps = value == "off" || value == "game" ? 0 : Math.Max(0, number);
                    break;
                case "pmglobal":
                    SetGlobalBool(value);
                    return true;
                case "decorvisible":
                    Decor.VisibleDivisor = number >= 4 ? 4 : number >= 2 ? 2 : 1;
                    break;
                default:
                    return false;
            }
            Refresh();
            Debug.Log("CROW_CUTS " + key + "=" + value + ": " + Describe());
            return true;
        }

        // A test (cycle key pmglobal=<name>:true|false): a PlayMaker global bool set, e.g. "pause mode", whose
        // enemies' Pause/Cull FSMs then hold them ("paused") as a menu does.
        static void SetGlobalBool(string value)
        {
            int colon = value.LastIndexOf(':');
            if (colon < 0) return;
            string name = value.Substring(0, colon);
            bool on = value.Substring(colon + 1) == "true";
            try
            {
                object variable = ByName.GlobalBool(name);
                variable.GetType().GetProperty("Value").SetValue(variable, on, null);
                Debug.Log("CROW_CUTS pmglobal " + name + "=" + on);
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_CUTS pmglobal " + name + " failed: " + exception);
            }
        }

        static float logAt, stainsAt = -1f;
        static int logFrame;

        static void Frame()
        {
            float now = Time.realtimeSinceStartup;
            RewiredProbe.Joysticks();
            if (StainCap > 0 && stainsAt >= 0f && now >= stainsAt)
            {
                stainsAt = now + 1f;
                Stains();
            }
            if (log && now - logAt >= 10f)
            {
                logAt = now;
                Debug.Log("CROW_CUTS " + FsmGate.Report() + " " + Enemies.Report() + " " + RewiredProbe.Time(Time.frameCount - logFrame));
                logFrame = Time.frameCount;
            }
        }

        // ---------------------------------------------------------------- blood stains

        static readonly List<Transform> stainParents = new List<Transform>();
        static int stainsRemoved;

        // Mara's blood stains: each hit she takes instantiates a "blood stain parent" below a "spawned blood
        // stains" object of her prefab (CreateObjectAdvanced in her Damage me FSM); the oldest beyond the cap go.
        static void Stains()
        {
            stainParents.RemoveAll(t => t == null);
            if (stainParents.Count == 0)
            {
                GameObject player = MaraScene.FindPlayer();
                if (player == null) return;
                foreach (GameObject root in player.scene.GetRootGameObjects())
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                        if (t.name == "spawned blood stains") stainParents.Add(t);
                if (stainParents.Count == 0)
                {
                    stainsAt = Time.realtimeSinceStartup + 30f;
                    return;
                }
            }
            foreach (Transform parent in stainParents)
            {
                int extra = parent.childCount - StainCap;
                for (int i = 0; i < extra; i++)
                {
                    Transform oldest = parent.GetChild(i);
                    if (oldest.gameObject.activeSelf) oldest.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(oldest.gameObject);
                    stainsRemoved++;
                }
                if (extra > 0 && log) Debug.Log("CROW_CUTS stains: " + extra + " old blood stains removed below " + parent.name + " (" + stainsRemoved + " in all)");
            }
        }

        // ---------------------------------------------------------------- patched calls

        // ParticleSystemEmit's Emit (rewriter CutsParticleBursts).
        public static void Emit(ParticleSystem system, int count)
        {
            if (Burst < 1f && count > 1) count = Math.Max(1, Mathf.RoundToInt(count * Burst));
            system.Emit(count);
        }

        // PlayMaker SetPosition's and SetRotation's writes (rewriter CutsSamePose).
        public static void SetPosition(Transform t, Vector3 value)
        {
            if (SamePose && Near(EnemySplit.GetPosition(t), value)) return;
            EnemySplit.SetPosition(t, value);  // as the rewriter's RouteEnemySplit had it
        }

        public static void SetLocalPosition(Transform t, Vector3 value)
        {
            if (SamePose && Near(t.localPosition, value)) return;
            t.localPosition = value;
        }

        public static void SetEulerAngles(Transform t, Vector3 value)
        {
            if (SamePose && Near(EnemySplit.GetRotation(t), Quaternion.Euler(value))) return;
            EnemySplit.SetEulerAngles(t, value);
        }

        public static void SetLocalEulerAngles(Transform t, Vector3 value)
        {
            if (SamePose && Near(t.localRotation, Quaternion.Euler(value))) return;
            t.localEulerAngles = value;
        }

        // Within float rounding: a write that changes nothing but the last bits.
        static bool Near(Vector3 a, Vector3 b)
        {
            return Close(a.x, b.x) && Close(a.y, b.y) && Close(a.z, b.z);
        }

        static bool Close(float a, float b)
        {
            float d = a - b;
            if (d < 0f) d = -d;
            float m = a < 0f ? -a : a;
            return d <= 1e-6f * (m > 1f ? m : 1f);
        }

        static bool Near(Quaternion a, Quaternion b)
        {
            if (Close(a.x, b.x) && Close(a.y, b.y) && Close(a.z, b.z) && Close(a.w, b.w)) return true;
            return Close(a.x, -b.x) && Close(a.y, -b.y) && Close(a.z, -b.z) && Close(a.w, -b.w);
        }

        // LightFlicker.Update's start (rewriter CutsLightFlicker): false skips this frame (its time is kept).
        static float flickerDelta;

        public static bool FlickerStep(Component script, ref float pending)
        {
            float dt = Time.deltaTime;
            if (FlickerDivisor > 1 && ((Time.frameCount + (script.GetInstanceID() & 1)) & 1) != 0)
            {
                pending += dt;
                return false;
            }
            flickerDelta = pending + dt;
            pending = 0f;
            return true;
        }

        public static float FlickerDelta()
        {
            return flickerDelta;
        }

        // ---------------------------------------------------------------- what is what

        internal static bool IsProp(Transform target)
        {
            if (Props.Length == 0) return false;
            for (Transform t = target; t != null; t = t.parent)
                if (Array.IndexOf(Props, t.name) >= 0) return true;
            return false;
        }

        static readonly string[] BreathParts = { "breath vert 1", "breath vert arms", "breath hinge 1", "breathe head horiz", "breathe neck", "idle hip rotation" };

        internal static bool IsBreath(Transform target)
        {
            return Array.IndexOf(BreathParts, target.name) >= 0;
        }

        // The enemy a transform belongs to: the nearest "Enemy <type>" with a rigidbody above it (or itself).
        internal static Transform EnemyRoot(Transform target)
        {
            for (Transform t = target; t != null; t = t.parent)
                if (t.name.StartsWith("Enemy ", StringComparison.Ordinal) && t.GetComponent<Rigidbody>() != null) return t;
            return null;
        }
    }

    // The enemies the cuts know of (by root), and whether each is frozen.
    sealed class EnemyState
    {
        public Transform Root;
        public int Index;
        public bool Frozen;
        public int CullFrame = -100, IdleFrames;
        public string CullState = "";
        public float FrozenAt;
    }

    static class Enemies
    {
        static readonly Dictionary<int, EnemyState> byRoot = new Dictionary<int, EnemyState>();
        static readonly List<EnemyState> list = new List<EnemyState>();
        static readonly Dictionary<string, int> wakes = new Dictionary<string, int>();
        static int freezes;
        static double frozenSeconds;

        internal static EnemyState Of(Transform root)
        {
            EnemyState enemy;
            int id = root.GetInstanceID();
            if (byRoot.TryGetValue(id, out enemy) && enemy.Root != null) return enemy;
            enemy = new EnemyState { Root = root, Index = list.Count };
            byRoot[id] = enemy;
            list.Add(enemy);
            return enemy;
        }

        internal static void Clear()
        {
            WakeAll("unload");
            byRoot.Clear();
            list.Clear();
        }

        internal static void Freeze(EnemyState enemy)
        {
            enemy.Frozen = true;
            enemy.FrozenAt = Time.realtimeSinceStartup;
            freezes++;
        }

        internal static void Wake(EnemyState enemy, string cause)
        {
            if (!enemy.Frozen) return;
            enemy.Frozen = false;
            enemy.IdleFrames = 0;
            frozenSeconds += Time.realtimeSinceStartup - enemy.FrozenAt;
            int count;
            wakes.TryGetValue(cause, out count);
            wakes[cause] = count + 1;
        }

        internal static void WakeAll(string cause)
        {
            foreach (EnemyState enemy in list) Wake(enemy, cause);
        }

        // Once a frame (FsmGate's first call): an enemy whose Pause/Cull no longer runs wakes.
        internal static void Tick(int frame)
        {
            for (int i = 0; i < list.Count; i++)
            {
                EnemyState enemy = list[i];
                if (enemy.Frozen && (enemy.Root == null || frame - enemy.CullFrame > 2)) Wake(enemy, enemy.Root == null ? "gone" : "cull stopped");
            }
        }

        internal static string Report()
        {
            int frozen = 0;
            foreach (EnemyState enemy in list)
                if (enemy.Frozen) frozen++;
            var line = new StringBuilder("enemies=").Append(list.Count).Append(" frozen=").Append(frozen).Append(" freezes=").Append(freezes)
                .Append(" frozenSeconds=").Append(frozenSeconds.ToString("0", CultureInfo.InvariantCulture)).Append(" wakes:");
            foreach (KeyValuePair<string, int> pair in wakes) line.Append(' ').Append(pair.Key.Replace(' ', '_')).Append('=').Append(pair.Value);
            return line.ToString();
        }
    }

    // PlayMaker's Fsm.Update, FixedUpdate and LateUpdate start here (rewriter FsmGateHooks) while Active,
    // for FSMs not yet known to be ungated (crowGate -1). An FSM is looked at once: an enemy's (frozen with
    // it), its Pause/Cull (watched), a light flicker or a prop's (rate-limited), or none.
    public static class FsmGate
    {
        public static bool Active;
        const int None = -1, Member = 1, Cull = 2, Flicker = 3, Prop = 4;

        sealed class Rec
        {
            public int Kind, Phase;
            public EnemyState Enemy;
            public object State;
            public float Pending;
        }

        static readonly List<Rec> recs = new List<Rec>();
        static readonly Dictionary<object, string> stateNames = new Dictionary<object, string>();
        static System.Reflection.PropertyInfo stateName;
        static int generation = 1, lastFrame = -1;
        static bool overriding;
        static float overrideDelta;
        static long skipped, ran, fixedSkipped;
        // Never frozen: spawning and the exploration-mode removal. The damage FSMs (shoot me, damage / kill) are:
        // a held enemy's hit colliders are on its model, which Pause/Cull has switched off (or the game is in a
        // menu), and a hit's event still reaches a frozen FSM and wakes the enemy.
        static readonly string[] Exempt = { "spawn/kill", "ExplorationMode?" };

        internal static void Clear()
        {
            recs.Clear();
            stateNames.Clear();
            Enemies.Clear();
            generation = (generation % 2000) + 1;
        }

        // Fsm.Update's start: false skips the update. state is the FSM's active state.
        public static bool Update(ref int gate, Component owner, object state)
        {
            overriding = false;
            Rec rec = Resolve(ref gate, owner);
            if (rec == null) return true;
            int frame = Time.frameCount;
            if (frame != lastFrame)
            {
                lastFrame = frame;
                Enemies.Tick(frame);
            }
            switch (rec.Kind)
            {
                case Cull:
                    Watch(rec, state, frame);
                    return true;
                case Member:
                    if (rec.Enemy.Frozen)
                    {
                        if (!ReferenceEquals(state, rec.State))
                        {
                            rec.State = state;
                            Enemies.Wake(rec.Enemy, "state");
                            ran++;
                            return true;
                        }
                        skipped++;
                        return false;
                    }
                    rec.State = state;
                    return true;
                case Flicker:
                    return Rate(rec, Cuts.FlickerDivisor, frame);
                case Prop:
                    return Rate(rec, Cuts.PropsDivisor, frame);
            }
            return true;
        }

        // Fsm.FixedUpdate's and LateUpdate's start (crowGate > 0): true skips it.
        public static bool Skip(int gate)
        {
            Rec rec = Lookup(gate);
            if (rec == null || rec.Kind != Member || !rec.Enemy.Frozen) return false;
            fixedSkipped++;
            return true;
        }

        // Before each return of Fsm.Update: the frame time is the plain one again.
        public static void After()
        {
            overriding = false;
        }

        // Time.deltaTime in PlayMaker's code (actions, states, delayed events): inside a rate-limited
        // FSM's update, the time of the frames since its last one.
        public static float DeltaTime()
        {
            return overriding ? overrideDelta : Time.deltaTime;
        }

        static bool Rate(Rec rec, int divisor, int frame)
        {
            float dt = Time.deltaTime;
            if (divisor > 1 && (frame + rec.Phase) % divisor != 0)
            {
                rec.Pending += dt;
                skipped++;
                return false;
            }
            ran++;
            if (rec.Pending > 0f)
            {
                overrideDelta = rec.Pending + dt;
                overriding = true;
                rec.Pending = 0f;
            }
            return true;
        }

        // Pause/Cull's state: an enemy held there for 10 frames freezes; out of it, it wakes.
        static void Watch(Rec rec, object state, int frame)
        {
            EnemyState enemy = rec.Enemy;
            enemy.CullFrame = frame;
            if (!ReferenceEquals(state, rec.State))
            {
                rec.State = state;
                enemy.CullState = Name(state);
                enemy.IdleFrames = 0;
            }
            bool held = enemy.CullState == "not visible" || (Cuts.FreezeMode == 2 && enemy.CullState == "paused");
            if (!held || Cuts.FreezeMode == 0)
            {
                if (enemy.Frozen) Enemies.Wake(enemy, "cull " + enemy.CullState);
                enemy.IdleFrames = 0;
                return;
            }
            if (!enemy.Frozen && ++enemy.IdleFrames >= 10) Enemies.Freeze(enemy);
        }

        static string Name(object state)
        {
            if (state == null) return "";
            string name;
            if (stateNames.TryGetValue(state, out name)) return name;
            if (stateName == null) stateName = state.GetType().GetProperty("Name");
            name = stateName == null ? "" : stateName.GetValue(state, null) as string ?? "";
            stateNames[state] = name;
            return name;
        }

        static Rec Lookup(int gate)
        {
            if (gate <= 0 || (gate >> 20) != generation) return null;
            int index = (gate & 0xFFFFF) - 1;
            return index < recs.Count ? recs[index] : null;
        }

        static Rec Resolve(ref int gate, Component owner)
        {
            if (gate > 0)
            {
                Rec known = Lookup(gate);
                if (known != null) return known;
            }
            Rec rec = Classify(owner);
            if (rec == null || recs.Count >= 0xFFFFE)
            {
                gate = -1;
                return null;
            }
            recs.Add(rec);
            gate = (generation << 20) | recs.Count;
            return rec;
        }

        static Rec Classify(Component owner)
        {
            if (owner == null) return null;
            Transform t = owner.transform;
            string name = owner.gameObject.name;
            Transform root = Cuts.EnemyRoot(t);
            if (root != null)
            {
                if (name == "Pause/Cull" || name == "Pause/Cull?") return new Rec { Kind = Cull, Enemy = Enemies.Of(root) };
                if (Array.IndexOf(Exempt, name) >= 0) return null;
                return new Rec { Kind = Member, Enemy = Enemies.Of(root) };
            }
            if (name == "light flicker") return new Rec { Kind = Flicker, Phase = recs.Count };
            if (Cuts.IsProp(t)) return new Rec { Kind = Prop, Phase = recs.Count };
            return null;
        }

        internal static string Report()
        {
            string line = "fsm gated=" + recs.Count + " skipped=" + skipped + " ran=" + ran + " fixedLateSkipped=" + fixedSkipped;
            skipped = ran = fixedSkipped = 0;
            return line;
        }
    }

    // -crow-input-bench: what Rewired's per-frame Unity input calls cost on this player.
    static class InputBench
    {
        static float due = -1f;

        internal static void Install()
        {
            CrowEvents.Loaded("InputBench.sceneLoaded", (scene, mode) =>
            {
                if (due == -1f && Scenes.IsArea(scene))
                    due = Time.realtimeSinceStartup + 8f;
            });
            CrowEvents.Canvases("InputBench.willRenderCanvases", () =>
            {
                if (due < 0f || Time.realtimeSinceStartup < due) return;
                due = -2f;
                Run();
                RewiredProbe.Report();
            });
        }

        static double Measure(Action action, int count)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < count; i++) action();
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds * 1000.0 / count;
        }

        static void Run()
        {
            var line = new StringBuilder("CROW_INPUT_BENCH us/call:");
            float sink = 0f;
            bool flag = false;
            string[] names = Input.GetJoystickNames();
            line.Append(" GetJoystickNames=").Append(Measure(() => names = Input.GetJoystickNames(), 100).ToString("0.0"));
            line.Append(" GetAxis(Joy1Axis1)=").Append(Measure(() => sink += Input.GetAxis("Joy1Axis1"), 500).ToString("0.0"));
            line.Append(" GetAxis(Joy1Axis29)=").Append(Measure(() => sink += Input.GetAxis("Joy1Axis29"), 500).ToString("0.0"));
            line.Append(" GetAxisRaw(Joy1Axis1)=").Append(Measure(() => sink += Input.GetAxisRaw("Joy1Axis1"), 500).ToString("0.0"));
            line.Append(" GetAxisRaw(MouseAxis1)=").Append(Measure(() =>
            {
                try { sink += Input.GetAxisRaw("MouseAxis1"); } catch (Exception) { }
            }, 200).ToString("0.0"));
            line.Append(" GetButton(MouseButton0)=").Append(Measure(() =>
            {
                try { flag |= Input.GetButton("MouseButton0"); } catch (Exception) { }
            }, 200).ToString("0.0"));
            line.Append(" GetKey(Joystick1Button0)=").Append(Measure(() => flag |= Input.GetKey(KeyCode.Joystick1Button0), 500).ToString("0.0"));
            line.Append(" mousePosition=").Append(Measure(() => sink += Input.mousePosition.x, 500).ToString("0.0"));
            line.Append(" joysticks=").Append(names.Length).Append(" [").Append(string.Join("|", names)).Append("]");
            Debug.Log(line.ToString() + (sink > 1e9f || flag ? "" : ""));
        }
    }

    // Rewired's players and maps, by its public API through reflection (-crow-input-bench).
    // Its InputManager's time (Begin/End, rewriter CutsRewired) for -crow-cuts-log, and the joystick rule
    // (-crow-rewired-joysticks first|all, Cuts.cs): Unity on the PS Vita reports four joysticks, the pad and
    // three with no name, and Rewired gives all four to player 0 and polls each (29 axes by name, 20 buttons)
    // every frame; first keeps only the first joystick with a name.
    public static class RewiredProbe
    {
        static long began, ticks;
        static int calls;

        // Rewired's InputManager updates (rewriter CutsRewired). While the pad reader serves (the PS Vita's
        // default, Pad.cs) PadUpdate returns ahead of Begin, so this reads about 0 there: Rewired's time
        // matters only with -crow-pad rewired|compare. (Mark and Sites, the probing builds' per-call-site
        // booking of Rewired's update, rewriter CutsRewiredSites, went on 21 Oct: they were keyed on
        // Rewired's obfuscated method names, and the pad reader skips that update on the PS Vita.)
        public static void Begin()
        {
            began = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public static void End()
        {
            ticks += System.Diagnostics.Stopwatch.GetTimestamp() - began;
            calls++;
        }

        // The InputManager's time per frame since the last call.
        internal static string Time(int frames)
        {
            string line = "rewired us/frame=" + (frames <= 0 ? 0 : ticks * 1e6 / System.Diagnostics.Stopwatch.Frequency / frames).ToString("0", CultureInfo.InvariantCulture) +
                          " calls/frame=" + (frames <= 0 ? 0 : (float)calls / frames).ToString("0.0", CultureInfo.InvariantCulture);
            ticks = 0;
            calls = 0;
            return line;
        }

        internal static bool FirstJoystickOnly;
        static float joysticksAt;

        internal static void Rescan()
        {
            joysticksAt = 0f;
            settledFor = null;
            if (!FirstJoystickOnly) Cuts.RewiredMaxJoystick = 16;
        }
        static string joystickLog;
        // The Rewired input manager (Pad.ActiveManager) a pass found everything as wanted under: while the pad reader
        // serves, Rewired's update doesn't run, so its joysticks stay as they are (nothing in the game's code enables or
        // disables one) and a pass would set nothing. The passes come back with a new manager (Rewired starts again at
        // each area change), Rescan (rwjoys=) and whenever Rewired updates. Until 22 Oct every 2 s regardless: Type.GetType,
        // reflection reads boxed per joystick and a StringBuilder (0.08% of a fight's main thread, garbage each pass).
        static object settledFor;

        // Every 2 s: the joysticks but the first with a name off (FirstJoystickOnly), or back on.
        internal static void Joysticks()
        {
            if (Pad.Serving && settledFor != null && ReferenceEquals(settledFor, Pad.ActiveManager)) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < joysticksAt || !CrowEvents.Turn(joysticksAt <= 0f)) return;
            joysticksAt = now + 2f;
            settledFor = null;
            try
            {
                object ready = Static("isReady");
                if (!(ready is bool) || !(bool)ready) return;
                var joysticks = Get(Static("controllers"), "Joysticks") as System.Collections.IEnumerable;
                if (joysticks == null) return;
                var line = new StringBuilder();
                bool kept = false, changed = false;
                int max = 0, before = Cuts.RewiredMaxJoystick;
                foreach (object joystick in joysticks)
                {
                    string name = Get(joystick, "hardwareName") as string;
                    bool want = !FirstJoystickOnly || (!kept && !string.IsNullOrEmpty(name));
                    if (want && !string.IsNullOrEmpty(name)) kept = true;
                    if (want) max = Math.Max(max, Convert.ToInt32(Get(joystick, "unityId")));
                    bool enabled = (bool)Get(joystick, "enabled");
                    line.Append(' ').Append(Get(joystick, "unityId")).Append(":'").Append(name).Append("'=").Append(want ? "on" : "off");
                    if (enabled != want)
                    {
                        Set(joystick, "enabled", want);
                        changed = true;
                    }
                }
                // Unity's polling stops past the last joystick kept (all of them while none is known).
                Cuts.RewiredMaxJoystick = FirstJoystickOnly && kept ? Math.Max(1, max) : 16;
                if (!changed && Cuts.RewiredMaxJoystick == before) settledFor = Pad.ActiveManager;
                string text = line.ToString() + " polled<=" + Cuts.RewiredMaxJoystick;
                if (text != joystickLog)
                {
                    joystickLog = text;
                    Debug.Log("CROW_CUTS rewired joysticks" + text);
                }
            }
            catch (Exception exception)
            {
                joysticksAt = now + 60f;
                Debug.Log("CROW_CUTS rewired joysticks failed: " + exception);
            }
        }
        // The most derived public property (or field) of that name, null when there is none (ByName, ProbeFiles.cs).
        static object Get(object target, string name)
        {
            return ByName.GetOrNull(target, name);
        }

        static void Set(object target, string name, bool value)
        {
            ByName.Property(target, name).SetValue(target, value, null);
        }

        static object Static(string name)
        {
            Type input = Type.GetType("Rewired.ReInput, Rewired_Core");
            System.Reflection.PropertyInfo property = input == null ? null : input.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            return property == null ? null : property.GetValue(null, null);
        }

        static System.Collections.IEnumerable Players()
        {
            return Get(Static("players"), "AllPlayers") as System.Collections.IEnumerable ?? new object[0];
        }

        static System.Collections.IEnumerable Maps(object player)
        {
            object maps = Get(Get(player, "controllers"), "maps");
            System.Reflection.MethodInfo all = null;
            if (maps != null)
                foreach (System.Reflection.MethodInfo method in maps.GetType().GetMethods())
                    if (method.Name == "GetAllMaps" && !method.IsGenericMethodDefinition && method.GetParameters().Length == 0) all = method;
            return all == null ? new object[0] : (System.Collections.IEnumerable)all.Invoke(maps, null);
        }

        internal static void Report()
        {
            try
            {
                var line = new StringBuilder("CROW_REWIRED_REPORT");
                object mapping = Static("mapping");
                var actions = Get(mapping, "Actions") as System.Collections.ICollection;
                var categories = Get(mapping, "MapCategories") as System.Collections.ICollection;
                line.Append(" actions=").Append(actions == null ? -1 : actions.Count).Append(" categories=").Append(categories == null ? -1 : categories.Count);
                foreach (object player in Players())
                {
                    object controllers = Get(player, "controllers");
                    line.Append(" | player ").Append(Get(player, "id")).Append(' ').Append(Get(player, "name")).Append(" playing=").Append(Get(player, "isPlaying"))
                        .Append(" joysticks=").Append(Get(controllers, "joystickCount")).Append(" kb=").Append(Get(controllers, "hasKeyboard")).Append(" mouse=").Append(Get(controllers, "hasMouse"));
                    var byType = new Dictionary<string, int[]>();
                    foreach (object map in Maps(player))
                    {
                        string type = Convert.ToString(Get(map, "controllerType"));
                        int[] counts;
                        if (!byType.TryGetValue(type, out counts)) byType[type] = counts = new int[3];
                        counts[0]++;
                        if ((bool)Get(map, "enabled")) counts[1]++;
                        var elements = Get(map, "AllMaps") as System.Collections.ICollection;
                        if (elements != null) counts[2] += elements.Count;
                    }
                    foreach (KeyValuePair<string, int[]> pair in byType)
                        line.Append(' ').Append(pair.Key).Append("=maps").Append(pair.Value[0]).Append("/on").Append(pair.Value[1]).Append("/elements").Append(pair.Value[2]);
                }
                Debug.Log(line.ToString());
            }
            catch (Exception exception)
            {
                Debug.Log("CROW_REWIRED_REPORT failed: " + exception);
            }
        }
    }
}
