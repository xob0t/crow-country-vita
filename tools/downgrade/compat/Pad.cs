using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// The PS Vita's pad without Rewired's per-frame work (19 Oct). Rewired's InputManager polls the pad and
// works out every action of every player in its Update, FixedUpdate and LateUpdate (1.5-1.7 ms of a fight
// frame on the console after the fight cuts). The game reads player 0's actions by name (its own GetButton,
// GetButtonDown, GetAxis, GetAxisVector, AxisEvent and AxisEventExtended actions; KeyboardOrControllerCheck's
// GetAnyButtonDown and GetAnyNegativeButtonDown); the reader works them out from the pad the way Rewired
// does and serves them, and Rewired's updates return at once (rewriter Pad.cs sends the queries here first).
// What it knows comes from Rewired as each area sets it up (every area has its own Input Manager, so Rewired
// starts again at each area change; the reader starts again with it): player 0's enabled maps of the PS Vita
// pad (the game's Classic or Modern layout as CombatControlMap loads them, read again after each map change),
// the actions' names and input behaviours (button dead zone 0.5, joystick sensitivity 1, no digital axis
// simulation) and the pad's axis calibration (dead zone 0.2, the Y axes inverted). The elements are those of
// Rewired's PS Vita map (SonyPlaystationVita, fallback_PSVita): the sticks on Unity's axes 1, 2, 4 and 5 (by
// name, Joy1Axis1...) with the radial dead zone of Rewired's 2D axes, Cross, Circle, Square, Triangle, L, R,
// Select and Start on Unity's joystick buttons 0-7, the d-pad on axes 6 and 7. Per action, as Rewired: the
// value is the axis element of the largest magnitude, else the sum of the pressed buttons' contributions;
// the positive and negative buttons, with their down and up, are those of its bindings together: a button
// element's by its contribution, down and up its own changes since the last poll (so a map change makes no
// edge for a button held through it), an axis element's past the button dead zone by sign, down and up per
// binding (released in new maps: an axis held through a map change goes down again). The poll runs where
// Rewired's Update ran (its Update loop only: FixedUpdate reads the same values). Rewired answers
// until the reader has its tables (the first frames after each Rewired start; the reader polls once before
// it serves, so its first edges are right), for names Rewired doesn't know, and for other players.
// Not served (the game doesn't use them): keyboard and mouse (the PS Vita has neither), timed, double and
// repeated presses, Rewired's events (controller connected, last active controller changed: they fire in
// its update; on the PS Vita their FSMs only check again what they found). Joystick counts and names,
// vibration and the maps stay Rewired's, which keeps them without its updates.
//   -crow-pad reader|rewired|compare  (pad=) reader: the reader serves (the PS Vita's default); rewired: Rewired
//                                  serves and the reader is idle (elsewhere); compare: Rewired serves and the
//                                  reader runs beside it: after Rewired's update, each frame, every action's
//                                  button, down, up, negative button, negative down and up, axis and raw axis
//                                  and the any-button queries are compared (CROW_PAD_DIFF with the pad's raw
//                                  input, the first 300; CROW_PAD compare every 10 s)
//   -crow-pad-press "<t>:<inputs>[:<s>];..."  synthetic input for the reader (Windows tests; on the PS Vita
//                                  vitacompanion's press reaches Rewired and the reader alike): t seconds after
//                                  the start (or after -crow-pad-press-from <scene> first loads), held for s
//                                  seconds (0.15): cross, circle, square, triangle, l, r, select, start, up,
//                                  down, left, right joined by '+', and lx=, ly=, rx=, ry= as Unity's axis values;
//                                  shot: a screenshot then, into -crow-pad-shots <folder> (pad-<n>-<t>.png; not on
//                                  the PS Vita, which writes no ScreenCapture file)
//   -crow-pad-log                  CROW_PAD every 10 s: the mode, whether the reader serves, its time a frame,
//                                  its rebuilds; and each table it builds (CROW_PAD maps)
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Pad
    {
        const int Rewired = 0, Reader = 1, Compare = 2;
        static int mode;
        static bool log;
        // Read by the patched code: Rewired's FixedUpdate and LateUpdate return at once while it is set.
        public static bool Serving;

        // Rewired's PS Vita pad (SonyPlaystationVita, fallback_PSVita). Element ids 0-3 are the sticks' axes,
        // read from Unity's axes by number (1-based, as Rewired's Input Manager entries name them), the pairs
        // 0-1 and 2-3 its 2D axes; 4-11 buttons on Unity's joystick buttons 0-7; 12-15 the d-pad (up, right,
        // down, left) from Unity's axes 7 and 6, by sign.
        const string VitaGuid = "d2aef070-7caa-42ff-b2dc-daac7e4a62b4";
        const int Elements = 16, Buttons = 8, FirstButton = 4, FirstDpad = 12;
        static readonly int[] StickAxes = { 1, 2, 4, 5 };
        static readonly int[] DpadAxes = { 7, 6, 7, 6 };
        static readonly bool[] DpadPositive = { true, true, false, false };
        static readonly int[] RawAxes = { 1, 2, 4, 5, 6, 7 };
        static readonly string[] ButtonNames = { "cross", "circle", "square", "triangle", "l", "r", "select", "start", "up", "right", "down", "left" };

        // The sticks' calibration (Rewired's AxisCalibration of each axis; without a Rewired pad, its defaults
        // for this one: dead zone 0.2, the Y axes inverted).
        class Calibration
        {
            public bool Enabled = true, Invert, Range = true;
            public float DeadZone = 0.2f, Zero, Min = -1f, Max = 1f, Sensitivity = 1f;
            public int SensitivityType;  // Multiplier, Power, Curve
            public AnimationCurve Curve;
        }

        static readonly Calibration[] calibration = new Calibration[4];
        static int deadZone2D = 1, sensitivity2D;  // Rewired's DeadZone2DType (Radial 1, Axial 2), AxisSensitivity2DType (Radial 0, Axial 1)
        static string[] axisNames = new string[8];
        static KeyCode[] buttonKeys = new KeyCode[Buttons];

        // The actions (Rewired's, by slot) and their bindings to the pad's elements.
        static int count;
        static int[] actionIds = new int[0];
        static string[] actionNames = new string[0];
        static readonly Dictionary<string, int> byName = new Dictionary<string, int>();
        static readonly Dictionary<int, int> byId = new Dictionary<int, int>();
        static float[] deadZone = new float[0], axisSensitivity = new float[0];
        static int[] bindStart = new int[1], bindElement = new int[0], bindKind = new int[0];  // kind: 0 axis, 1 button +, 2 button -
        static bool[] bindInvert = new bool[0];

        // The last poll.
        static readonly float[] raw = new float[8];
        static readonly bool[] rawButton = new bool[Buttons];
        static readonly float[] element = new float[Elements];
        static readonly bool[] pressed = new bool[Elements];
        static float[] value = new float[0];
        // Per action, Rewired's button flags of its positive (0-2: on, down, up) and negative side (3-5), each the OR of
        // its bindings' own: a button binding's are its element's (pressed, and the change since the last poll), so a
        // map change starts none; an axis binding's come from its own last state (past the dead zone, by sign), which
        // starts released with each new table, as Rewired's per binding (an axis held through a map change goes down).
        static bool[][] flags = new bool[0][];
        static readonly bool[] wasPressed = new bool[Elements];
        static bool[] bindWasPlus = new bool[0], bindWasMinus = new bool[0];
        static readonly bool[] any = new bool[6];  // button, down, up, negative button, negative down, negative up

        static object activeManager, player0;
        static bool ready, primed, mapsDirty;

        // Rewired's input manager Update last saw active (RewiredProbe.Joysticks: a new one is a Rewired start).
        internal static object ActiveManager { get { return activeManager; } }
        static float retryAt;
        static int lastFrame = -1, builds;
        static string unsupported;
        static long ticks;
        static int frames;
        static float logAt;

        // From Diag.Install (every player).
        internal static void Install(string[] args)
        {
            Set(CrowFlags.Value(args, "-crow-pad", "reader", "rewired"));
            log = CrowFlags.Has(args, "-crow-pad-log");
            Presses.Install(CrowFlags.Value(args, "-crow-pad-press"), CrowFlags.Value(args, "-crow-pad-press-from"), CrowFlags.Value(args, "-crow-pad-shots"));
            if (log || mode != (CrowFlags.Vita ? Reader : Rewired)) Debug.Log("CROW_PAD mode=" + Describe());
        }

        static void Set(string value)
        {
            int next = value == "reader" ? Reader : value == "compare" ? Compare : Rewired;
            if (next == mode) return;
            if (mode == Rewired) primed = false;  // the reader didn't poll meanwhile
            mode = next;
            Serving = false;
            if (mode != Compare) return;
            Comparison.Clear();
            if (player0 != null) Comparison.Bind(player0);
        }

        static string Describe()
        {
            return mode == Reader ? "reader" : mode == Compare ? "compare" : "rewired";
        }

        // A -crow-cycle / -crow-fight-cycle / -crow-ab change; false when it isn't one of these.
        internal static bool Change(string key, string value)
        {
            if (key == "pad") Set(value);
            else return false;
            Debug.Log("CROW_PAD " + key + "=" + value + ": mode=" + Describe());
            return true;
        }

        // ---------------------------------------------------------------- Rewired's InputManager

        // InputManager_Base.Update starts here (rewriter Pad.cs): self is the manager, active Rewired's own
        // (ReInput.rewiredInputManager; a new one at each Rewired start). Once a frame, for the active one:
        // the tables when needed, then the poll. True: the reader serves, and Rewired's update is skipped.
        public static bool Update(object self, object active)
        {
            if (mode == Rewired) return false;
            if (!ReferenceEquals(active, activeManager))
            {
                activeManager = active;
                Reset();
            }
            if (active == null || !ReferenceEquals(self, active)) return Serving;
            int frame = Time.frameCount;
            if (frame == lastFrame) return Serving;
            lastFrame = frame;
            long start = log ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;  // timed for -crow-pad-log only
            if ((!ready || mapsDirty) && Time.realtimeSinceStartup >= retryAt) Build();
            if (ready)
            {
                Poll();
                // The first poll after a start only sets the last state: Rewired serves that frame.
                Serving = primed && mode == Reader;
                primed = true;
            }
            else Serving = false;
            if (!log) return Serving;
            ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            frames++;
            if (Time.realtimeSinceStartup - logAt >= 10f) Log();
            return Serving;
        }

        // Before each return of InputManager_Base.Update, when Rewired updated (compare).
        public static void AfterRewired(object self, object active)
        {
            if (mode != Compare || !ready || !primed || !ReferenceEquals(self, active) || lastFrame != Time.frameCount) return;
            Comparison.Frame();
        }

        // After each call that changes a player's maps (rewriter Pad.cs: CombatControlMap's LoadMap and RemoveMap).
        public static void MapsChanged()
        {
            mapsDirty = true;
            retryAt = 0f;
        }

        static void Reset()
        {
            ready = false;
            primed = false;
            Serving = false;
            player0 = null;
            retryAt = 0f;
        }

        static void Log()
        {
            logAt = Time.realtimeSinceStartup;
            Debug.Log("CROW_PAD mode=" + Describe() + " serving=" + Serving + " ready=" + ready + " us/frame=" +
                      (frames == 0 ? 0 : ticks * 1e6 / System.Diagnostics.Stopwatch.Frequency / frames).ToString("0", CultureInfo.InvariantCulture) +
                      " builds=" + builds + " actions=" + count + " bindings=" + bindElement.Length + (unsupported != null ? " unsupported=" + unsupported : ""));
            ticks = 0;
            frames = 0;
        }

        // ---------------------------------------------------------------- the game's queries

        // Rewired.Player's queries in the game's assemblies come here first (rewriter Pad.cs); false: Rewired answers.
        // Button kinds: 0 button, 1 down, 2 up, 3 negative button, 4 negative down, 5 negative up; axis kinds: 6 axis, 7 raw.
        public static bool Button(object player, string action, int kind, out bool result)
        {
            int slot;
            if (!Serving || !ReferenceEquals(player, player0) || action == null || !byName.TryGetValue(action, out slot))
            {
                result = false;
                return false;
            }
            result = ButtonOf(slot, kind);
            return true;
        }

        public static bool ButtonId(object player, int action, int kind, out bool result)
        {
            int slot;
            if (!Serving || !ReferenceEquals(player, player0) || !byId.TryGetValue(action, out slot))
            {
                result = false;
                return false;
            }
            result = ButtonOf(slot, kind);
            return true;
        }

        // The axis kinds (6 axis, 7 raw) read the same value: kind stays for the rewriter, which picks these
        // overloads by their signatures (rewriter Pad.cs).
        public static bool Axis(object player, string action, int kind, out float result)
        {
            int slot;
            if (!Serving || !ReferenceEquals(player, player0) || action == null || !byName.TryGetValue(action, out slot))
            {
                result = 0f;
                return false;
            }
            result = value[slot];
            return true;
        }

        public static bool AxisId(object player, int action, int kind, out float result)
        {
            int slot;
            if (!Serving || !ReferenceEquals(player, player0) || !byId.TryGetValue(action, out slot))
            {
                result = 0f;
                return false;
            }
            result = value[slot];
            return true;
        }

        public static bool Any(object player, int kind, out bool result)
        {
            if (!Serving || !ReferenceEquals(player, player0))
            {
                result = false;
                return false;
            }
            result = any[kind];
            return true;
        }

        static bool ButtonOf(int slot, int kind)
        {
            return flags[slot][kind >= 0 && kind < 5 ? kind : 5];
        }

        // The reader's active actions, for -crow-input while it serves (Diag.cs), or null.
        internal static string Active()
        {
            if (!Serving) return null;
            var line = new StringBuilder();
            for (int slot = 0; slot < count; slot++)
                if (Mathf.Abs(value[slot]) > 0.2f) line.Append(" [").Append(actionNames[slot]).Append("]=").Append(value[slot].ToString("0.0", CultureInfo.InvariantCulture));
            return line.ToString();
        }

        // ---------------------------------------------------------------- the poll

        static void Poll()
        {
            for (int i = 0; i < RawAxes.Length; i++) raw[RawAxes[i]] = Input.GetAxis(axisNames[RawAxes[i]]);
            for (int i = 0; i < Buttons; i++) rawButton[i] = Input.GetKey(buttonKeys[i]);
            int held = Presses.Apply(raw);
            for (int i = 0; i < Buttons; i++) pressed[FirstButton + i] = rawButton[i] || (held & (1 << i)) != 0;
            // The d-pad's buttons from its axes, as Rewired's hardware map (axis dead zone 0).
            for (int i = 0; i < 4; i++)
            {
                float v = raw[DpadAxes[i]];
                pressed[FirstDpad + i] = !(Math.Abs(v) <= 0f) && !(DpadPositive[i] && v < 0f) && !(!DpadPositive[i] && v > 0f) || (held & (1 << (Buttons + i))) != 0;
            }
            Stick(0, 1);
            Stick(2, 3);
            for (int i = 0; i < any.Length; i++) any[i] = false;
            for (int slot = 0; slot < count; slot++)
            {
                float axisValue = 0f, sum = 0f, dead = deadZone[slot];
                bool[] f = flags[slot];
                for (int i = 0; i < 6; i++) f[i] = false;
                for (int b = bindStart[slot]; b < bindStart[slot + 1]; b++)
                {
                    int e = bindElement[b];
                    if (bindKind[b] == 0)
                    {
                        float v = bindInvert[b] ? -element[e] : element[e];
                        float scaled = v * axisSensitivity[slot];
                        if (Math.Abs(scaled) > Math.Abs(axisValue)) axisValue = scaled;
                        bool plus = Math.Abs(v) > dead && v > 0f, minus = Math.Abs(v) > dead && !(v > 0f);
                        Flags(f, 0, plus, bindWasPlus[b]);
                        Flags(f, 3, minus, bindWasMinus[b]);
                        bindWasPlus[b] = plus;
                        bindWasMinus[b] = minus;
                    }
                    else
                    {
                        Flags(f, bindKind[b] == 1 ? 0 : 3, pressed[e], wasPressed[e]);
                        if (pressed[e]) sum += bindKind[b] == 1 ? 1f : -1f;
                    }
                }
                value[slot] = axisValue != 0f ? axisValue : Clamp(sum, -1f, 1f);
                for (int kind = 0; kind < 6; kind++) any[kind] |= f[kind];
            }
            for (int i = 0; i < Elements; i++) wasPressed[i] = pressed[i];
        }

        static void Flags(bool[] f, int side, bool on, bool was)
        {
            f[side] |= on;
            f[side + 1] |= on && !was;
            f[side + 2] |= !on && was;
        }

        // A stick's two axes: Rewired's Axis2DCalibration.GetCalibrated2DValue (radial dead zone; a dead zone
        // per axis, tried on 19 Oct, isn't what Rewired applies).
        static void Stick(int x, int y)
        {
            Calibration cx = calibration[x], cy = calibration[y];
            float rawX = raw[StickAxes[x]], rawY = raw[StickAxes[y]];
            Vector2 v;
            if (deadZone2D == 2 || ApproximatelyZero(cx.DeadZone))
            {
                v.x = Calibrated(cx, rawX, cx.DeadZone);
                v.y = Calibrated(cy, rawY, cy.DeadZone);
            }
            else
            {
                v.x = Transform2D(rawX, cx.Zero, cx.Min, cx.Max);
                v.y = Transform2D(rawY, cy.Zero, cy.Min, cy.Max);
                v = RadialDeadZone(v.x, v.y, cx.DeadZone);
            }
            if (sensitivity2D == 1)
            {
                v.x = Sensitivity(v.x, cx.SensitivityType, cx.Sensitivity, cx.Curve);
                v.y = Sensitivity(v.y, cy.SensitivityType, cy.Sensitivity, cy.Curve);
            }
            else RadialSensitivity(ref v, cx.SensitivityType, cx.Sensitivity, cx.Curve);
            if (cx.Range) v.x = Snap(v.x);
            if (cy.Range) v.y = Snap(v.y);
            if (cx.Invert) v.x = -v.x;
            if (cy.Invert) v.y = -v.y;
            element[x] = v.x;
            element[y] = v.y;
        }

        // Rewired's AxisCalibration.GetCalibratedValue (InputTools.GetCalibratedAxisValue(Clamped)), as Stick calls
        // it: without its sensitivity and inversion, which Stick applies after.
        static float Calibrated(Calibration c, float value, float dead)
        {
            if (!c.Enabled) return 0f;
            if (c.Range)
            {
                if (value < c.Min) value = c.Min;
                else if (value > c.Max) value = c.Max;
                if (Approximately(value, c.Zero)) return 0f;
                if ((value > c.Zero && value <= c.Zero + dead) || (value < c.Zero && value >= c.Zero - dead)) return 0f;
                value = !(value > c.Zero) ? NewRange(value, c.Min, c.Zero - dead, -1f, 0f) : NewRange(value, c.Zero + dead, c.Max, 0f, 1f);
                return Snap(value);
            }
            if (Approximately(value, 0f)) return 0f;
            if ((value > 0f && value <= 0f + dead) || (value < 0f && value >= 0f - dead)) return 0f;
            return value - dead * (value < 0f ? -1f : 1f);
        }

        // Rewired's InputTools and MathTools, as they are.
        static float Transform2D(float value, float zero, float min, float max)
        {
            if (value < min) value = min;
            else if (value > max) value = max;
            if (Approximately(value, zero)) return 0f;
            return !(value > zero) ? NewRange(value, min, zero, -1f, 0f) : NewRange(value, zero, max, 0f, 1f);
        }

        static Vector2 RadialDeadZone(float x, float y, float dead)
        {
            var result = new Vector2(x, y);
            if (result.magnitude < dead) return Vector2.zero;
            float scale = (result.magnitude - dead) / (1f - dead);
            result.Normalize();
            result.x = Clamp(result.x * scale, -1f, 1f);
            result.y = Clamp(result.y * scale, -1f, 1f);
            return result;
        }

        static float Sensitivity(float value, int type, float sensitivity, AnimationCurve curve)
        {
            if (value == 0f) return 0f;
            if (type == 0) return value * sensitivity;
            if (type == 1)
            {
                if (sensitivity < 0f) return 0f;
                return value > 0f ? Mathf.Pow(value, sensitivity) : Mathf.Pow(value * -1f, sensitivity) * -1f;
            }
            if (curve == null) return value;
            float at = Clamp(value, -1f, 1f);
            bool signed = false;
            for (int i = 0; i < curve.length; i++)
                if (curve[i].time < -0.2f) signed = true;
            if (!signed) at = Math.Abs(at);
            return value * curve.Evaluate(at);
        }

        static void RadialSensitivity(ref Vector2 value, int type, float sensitivity, AnimationCurve curve)
        {
            if (type == 0)
            {
                value.x *= sensitivity;
                value.y *= sensitivity;
            }
            else if (type == 1)
            {
                if (sensitivity < 0f)
                {
                    value.x = 0f;
                    value.y = 0f;
                    return;
                }
                float power = Mathf.Pow(value.magnitude, sensitivity);
                value.Normalize();
                value.x *= power;
                value.y *= power;
            }
            else if (curve != null)
            {
                float factor = curve.Evaluate(Mathf.Clamp01(value.magnitude));
                value.x *= factor;
                value.y *= factor;
            }
        }

        static float Snap(float value)
        {
            if (value > 0f)
            {
                if (value > 1f || 1f - value <= 0.001f) value = 1f;
            }
            else if (value < 0f && (value < -1f || value + 1f <= 0.001f)) value = -1f;
            return value;
        }

        static bool Approximately(float a, float b)
        {
            if (a == b) return true;
            float difference = b - a;
            if (difference < 0f) difference = 0f - difference;
            if (a < 0f) a = 0f - a;
            if (b < 0f) b = 0f - b;
            float tolerance = (a > b ? a : b) * 1E-06f;
            return difference < (tolerance > 1.1E-44f ? tolerance : 1.1E-44f);
        }

        static bool ApproximatelyZero(float a)
        {
            if (a == 0f) return true;
            float size = a < 0f ? 0f - a : a;
            float tolerance = size * 1E-06f;
            return size < (tolerance > 1.1E-44f ? tolerance : 1.1E-44f);
        }

        static float NewRange(float value, float oldMin, float oldMax, float newMin, float newMax)
        {
            if (value < oldMin) value = oldMin;
            else if (value > oldMax) value = oldMax;
            float span = oldMax - oldMin;
            if (Approximately(span, 0f)) return newMin;
            return (value - oldMin) * (newMax - newMin) / span + newMin;
        }

        static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            return value > max ? max : value;
        }

        // ---------------------------------------------------------------- the tables, from Rewired

        const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;

        // Player 0, its PS Vita pad, the pad's enabled maps, the actions and their behaviours, the calibration.
        // False (Rewired serves) while Rewired isn't ready or the pad has no map yet; on Windows, with no pad,
        // Rewired's own map of the PS Vita pad for the layout CombatControlMap would load (tests).
        static void Build()
        {
            retryAt = Time.realtimeSinceStartup + 0.1f;
            try
            {
                Type input = Type.GetType("Rewired.ReInput, Rewired_Core");
                if (input == null || !(bool)Static(input, "isReady")) return;
                object player = Call(Static(input, "players"), "GetPlayer", 0);
                object controllers = Get(player, "controllers");
                object maps = Get(controllers, "maps");
                object pad = null;
                foreach (object joystick in (IEnumerable)Get(controllers, "Joysticks"))
                    if (Get(joystick, "hardwareTypeGuid").ToString() == VitaGuid)
                    {
                        pad = joystick;
                        break;
                    }
                var found = new List<object>();
                int unity = 1;
                for (int i = 0; i < 4; i++) calibration[i] = new Calibration { Invert = i % 2 == 1 };
                if (pad != null)
                {
                    fromTemplate = false;
                    foreach (MethodInfo method in maps.GetType().GetMethods(Instance))
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        if (method.Name != "GetMaps" || parameters.Length != 1 || !parameters[0].ParameterType.IsInstanceOfType(pad)) continue;
                        foreach (object map in (IEnumerable)method.Invoke(maps, new[] { pad }))
                            if ((bool)Get(map, "enabled")) found.Add(map);
                    }
                    if (found.Count == 0 && !ready) return;  // not loaded yet (after a change: none, as Rewired has)
                    unity = Convert.ToInt32(Get(pad, "unityId"));
                    object calibrationMap = Get(pad, "calibrationMap");
                    for (int i = 0; i < 4; i++) calibration[i] = ReadCalibration(Call(calibrationMap, "GetAxis", i));
                }
                else if (Application.platform != RuntimePlatform.PSP2)
                {
                    fromTemplate = false;
                    object instance = MapInstance();
                    if (instance == null) return;
                    found.Add(instance);
                }
                else return;
                object configuration = Static(input, "configuration");
                deadZone2D = Convert.ToInt32(Get(configuration, "defaultJoystickAxis2DDeadZoneType"));
                sensitivity2D = Convert.ToInt32(Get(configuration, "defaultJoystickAxis2DSensitivityType"));
                for (int axis = 1; axis < 8; axis++) axisNames[axis] = "Joy" + unity + "Axis" + axis;
                for (int i = 0; i < Buttons; i++) buttonKeys[i] = KeyCode.Joystick1Button0 + 20 * (unity - 1) + i;
                Tables((IEnumerable)Get(Static(input, "mapping"), "Actions"), maps, found);
                if (unsupported != null)
                {
                    Reset();  // Rewired serves
                    retryAt = Time.realtimeSinceStartup + 5f;
                    return;
                }
                player0 = player;
                if (mode == Compare) Comparison.Bind(player);
                ready = true;
                mapsDirty = false;
                builds++;
            }
            catch (Exception exception)
            {
                Reset();
                retryAt = Time.realtimeSinceStartup + 5f;
                Debug.Log("CROW_PAD build failed: " + exception);
            }
        }

        // Windows tests: the PS Vita pad's map in the Input Manager's data for the layout CombatControlMap picks
        // (Classic: "Default", id 0; Modern: "Modern", id 3; by the SETTINGSCombatControls global). The Windows
        // players' data has no PS Vita maps (rewired_vita_overlay is the PS Vita build's), so it is the Dual
        // Analog Gamepad template's map with its elements made the PS Vita pad's as that overlay makes them.
        static object MapInstance()
        {
            bool classic = true;
            try
            {
                object variable = ByName.GlobalBool("SETTINGSCombatControls");
                if (variable != null) classic = (bool)variable.GetType().GetProperty("Value").GetValue(variable, null);
            }
            catch (Exception)
            {
            }
            object data = Get(activeManager, "userData");
            MethodInfo find = data.GetType().GetMethod("GetJoystickMap", new[] { typeof(int), typeof(Guid), typeof(int) });
            object map = find.Invoke(data, new object[] { 0, new Guid(VitaGuid), classic ? 0 : 3 });
            fromTemplate = map == null;
            return map ?? find.Invoke(data, new object[] { 0, new Guid(TemplateGuid), classic ? 0 : 3 });
        }

        // The template's element ids -> the PS Vita pad's (asset_rules.py's REWIRED_VITA_ELEMENTS); -1: none. Shoulder 2
        // (11, 13) keeps only the Combat actions, which then replace shoulder 1's.
        const string TemplateGuid = "83b427e4-086f-47f3-bb06-be266abd1ca5";
        static readonly int[] TemplateElements = { 0, 1, 2, 3, 4, 5, -1, 6, 7, -1, 8, 8, 9, 9, 10, 11, -1, -1, -1, 12, 13, 14, 15 };
        static bool fromTemplate;

        static int Template(int id, string action, List<int> combatShoulders)
        {
            if (id < 0 || id >= TemplateElements.Length) return -1;
            bool combat = action.StartsWith("Combat");
            if ((id == 11 || id == 13) && !combat) return -1;
            if ((id == 10 || id == 12) && combat && combatShoulders.Contains(id + 1)) return -1;
            return TemplateElements[id];
        }

        static Calibration ReadCalibration(object axis)
        {
            object curve = Get(axis, "sensitivityCurve");
            return new Calibration
            {
                Enabled = (bool)Get(axis, "enabled"),
                DeadZone = (float)Get(axis, "deadZone"),
                Zero = (float)Get(axis, "calibratedZero"),
                Min = (float)Get(axis, "calibratedMin"),
                Max = (float)Get(axis, "calibratedMax"),
                Invert = (bool)Get(axis, "invert"),
                SensitivityType = Convert.ToInt32(Get(axis, "sensitivityType")),
                Sensitivity = (float)Get(axis, "sensitivity"),
                Curve = curve as AnimationCurve,
                Range = (bool)Get(axis, "applyRangeCalibration"),
            };
        }

        // The action slots and each action's bindings in the maps.
        static void Tables(IEnumerable actions, object maps, List<object> found)
        {
            var ids = new List<int>();
            var names = new List<string>();
            var behaviours = new List<int>();
            foreach (object action in actions)
            {
                ids.Add(Convert.ToInt32(Get(action, "id")));
                names.Add((string)Get(action, "name"));
                behaviours.Add(Convert.ToInt32(Get(action, "behaviorId")));
            }
            var bindings = new List<int>[ids.Count];
            for (int i = 0; i < bindings.Length; i++) bindings[i] = new List<int>();
            var slotOf = new Dictionary<int, int>();
            for (int i = 0; i < ids.Count; i++) slotOf[ids[i]] = i;
            unsupported = null;
            var described = new StringBuilder();
            foreach (object map in found)
            {
                described.Append(" map ").Append(Get(map, "id")).Append(" layout ").Append(Get(map, "layoutId")).Append(fromTemplate ? " (template)" : "").Append(':');
                bool editor = map.GetType().Name == "ControllerMap_Editor";  // the Input Manager's data (Windows tests)
                var combatShoulders = new List<int>();
                if (fromTemplate)
                    foreach (object entry in (IEnumerable)Get(map, "actionElementMaps"))
                    {
                        int slot;
                        if (slotOf.TryGetValue(Convert.ToInt32(Get(entry, "actionId")), out slot) && names[slot].StartsWith("Combat"))
                            combatShoulders.Add(Convert.ToInt32(Get(entry, "elementIdentifierId")));
                    }
                foreach (object entry in (IEnumerable)Get(map, editor ? "actionElementMaps" : "AllMaps"))
                {
                    if (!editor && !(bool)Get(entry, "enabled")) continue;
                    int slot;
                    int action = Convert.ToInt32(Get(entry, "actionId"));
                    int type = Convert.ToInt32(Get(entry, "elementType")), id = Convert.ToInt32(Get(entry, "elementIdentifierId"));
                    int range = Convert.ToInt32(Get(entry, "axisRange")), pole = Convert.ToInt32(Get(entry, "axisContribution"));
                    bool invert = (bool)Get(entry, "invert");
                    if (!slotOf.TryGetValue(action, out slot)) continue;
                    if (fromTemplate)
                    {
                        if ((id = Template(id, names[slot], combatShoulders)) < 0) continue;
                        type = id < FirstButton ? 0 : 1;  // the template's triggers are axes, the pad's L and R buttons
                    }
                    described.Append(' ').Append(names[slot]).Append('=').Append(id).Append(type == 0 ? "a" : pole == 0 ? "+" : "-");
                    // Axis elements: the sticks, full range; buttons: the pad's 12, by contribution.
                    if (type == 0 && id >= 0 && id < FirstButton && range == 0) bindings[slot].Add(id << 3 | (invert ? 4 : 0));
                    else if (type == 1 && id >= FirstButton && id < Elements) bindings[slot].Add(id << 3 | (pole == 0 ? 1 : 2));
                    else unsupported = (unsupported == null ? "" : unsupported + ",") + names[slot] + "@" + id + "/" + type + "/" + range;
                }
            }
            object[] behaviour = new object[ids.Count];
            float[] dead = new float[ids.Count], sensitivity = new float[ids.Count];
            for (int i = 0; i < ids.Count; i++)
            {
                behaviour[i] = Call(maps, "GetInputBehavior", behaviours[i]);
                dead[i] = (float)Get(behaviour[i], "buttonDeadZone");
                sensitivity[i] = (float)Get(behaviour[i], "joystickAxisSensitivity");
                bool digital = (bool)Get(behaviour[i], "digitalAxisSimulation");
                if (digital && bindings[i].Exists(b => (b & 3) != 0)) unsupported = (unsupported == null ? "" : unsupported + ",") + names[i] + "@digital";
            }
            if (log || unsupported != null) Debug.Log("CROW_PAD maps" + described + (unsupported != null ? " unsupported=" + unsupported : ""));
            if (unsupported != null) return;
            count = ids.Count;
            actionIds = ids.ToArray();
            actionNames = names.ToArray();
            deadZone = dead;
            axisSensitivity = sensitivity;
            byName.Clear();
            byId.Clear();
            for (int slot = 0; slot < count; slot++)
            {
                if (!byName.ContainsKey(actionNames[slot])) byName[actionNames[slot]] = slot;
                byId[actionIds[slot]] = slot;
            }
            var start = new List<int> { 0 };
            var elementList = new List<int>();
            var kinds = new List<int>();
            var inverts = new List<bool>();
            foreach (List<int> list in bindings)
            {
                foreach (int binding in list)
                {
                    elementList.Add(binding >> 3);
                    kinds.Add(binding & 3);
                    inverts.Add((binding & 4) != 0);
                }
                start.Add(elementList.Count);
            }
            bindStart = start.ToArray();
            bindElement = elementList.ToArray();
            bindKind = kinds.ToArray();
            bindInvert = inverts.ToArray();
            value = new float[count];
            flags = new bool[count][];
            for (int slot = 0; slot < count; slot++) flags[slot] = new bool[6];
            bindWasPlus = new bool[bindElement.Length];
            bindWasMinus = new bool[bindElement.Length];
        }

        static object Static(Type type, string name)
        {
            return type.GetProperty(name, BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
        }

        // The most derived public property of that name (Rewired's types redeclare some), or a field; a missing
        // one throws (ByName, ProbeFiles.cs).
        static object Get(object target, string name)
        {
            return ByName.Get(target, name);
        }

        static object Call(object target, string name, int argument)
        {
            MethodInfo method = target.GetType().GetMethod(name, Instance, null, new[] { typeof(int) }, null);
            if (method == null) throw new MissingMethodException(target.GetType().FullName, name);
            return method.Invoke(target, new object[] { argument });
        }

        // ---------------------------------------------------------------- -crow-pad compare

        // Rewired's answers for every action, after its update, against the reader's.
        static class Comparison
        {
            static readonly string[] ButtonQueries = { "GetButton", "GetButtonDown", "GetButtonUp", "GetNegativeButton", "GetNegativeButtonDown", "GetNegativeButtonUp" };
            static readonly string[] AnyQueries = { "GetAnyButton", "GetAnyButtonDown", "GetAnyButtonUp", "GetAnyNegativeButton", "GetAnyNegativeButtonDown", "GetAnyNegativeButtonUp" };
            static Func<int, bool>[] buttons;
            static Func<int, float> axis, axisRaw;
            static Func<bool>[] anyButton;
            static int compared, differing, lines;
            static float reportAt;
            static readonly Dictionary<string, int> counts = new Dictionary<string, int>();
            static readonly StringBuilder line = new StringBuilder();

            internal static void Clear()
            {
                compared = differing = lines = 0;
                counts.Clear();
                reportAt = Time.realtimeSinceStartup + 10f;
            }

            internal static void Bind(object player)
            {
                Type type = player.GetType();
                buttons = new Func<int, bool>[ButtonQueries.Length];
                for (int i = 0; i < ButtonQueries.Length; i++)
                    buttons[i] = (Func<int, bool>)Delegate.CreateDelegate(typeof(Func<int, bool>), player, type.GetMethod(ButtonQueries[i], new[] { typeof(int) }));
                axis = (Func<int, float>)Delegate.CreateDelegate(typeof(Func<int, float>), player, type.GetMethod("GetAxis", new[] { typeof(int) }));
                axisRaw = (Func<int, float>)Delegate.CreateDelegate(typeof(Func<int, float>), player, type.GetMethod("GetAxisRaw", new[] { typeof(int) }));
                anyButton = new Func<bool>[AnyQueries.Length];
                for (int i = 0; i < AnyQueries.Length; i++)
                    anyButton[i] = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), player, type.GetMethod(AnyQueries[i], Type.EmptyTypes));
            }

            internal static void Frame()
            {
                if (buttons == null) return;
                line.Length = 0;
                for (int slot = 0; slot < count; slot++)
                {
                    int id = actionIds[slot];
                    for (int kind = 0; kind < ButtonQueries.Length; kind++)
                    {
                        bool rewired = buttons[kind](id), reader = ButtonOf(slot, kind);
                        if (rewired != reader) Differ(actionNames[slot] + "." + ButtonQueries[kind], rewired ? "1" : "0", reader ? "1" : "0");
                    }
                    float rewiredAxis = axis(id), rewiredRaw = axisRaw(id);
                    if (rewiredAxis != value[slot]) Differ(actionNames[slot] + ".GetAxis", Number(rewiredAxis), Number(value[slot]));
                    if (rewiredRaw != value[slot]) Differ(actionNames[slot] + ".GetAxisRaw", Number(rewiredRaw), Number(value[slot]));
                }
                for (int kind = 0; kind < AnyQueries.Length; kind++)
                {
                    bool rewired = anyButton[kind]();
                    if (rewired != any[kind]) Differ(AnyQueries[kind], rewired ? "1" : "0", any[kind] ? "1" : "0");
                }
                compared++;
                if (line.Length > 0)
                {
                    differing++;
                    if (lines++ < 300) Debug.Log("CROW_PAD_DIFF f=" + Time.frameCount + line + " | raw" + Raw());
                }
                if (Time.realtimeSinceStartup >= reportAt)
                {
                    reportAt = Time.realtimeSinceStartup + 10f;
                    var report = new StringBuilder("CROW_PAD compare frames=").Append(compared).Append(" differing=").Append(differing);
                    foreach (KeyValuePair<string, int> pair in counts) report.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
                    Debug.Log(report.ToString());
                }
            }

            static void Differ(string what, string rewired, string reader)
            {
                int seen;
                counts.TryGetValue(what, out seen);
                counts[what] = seen + 1;
                line.Append(' ').Append(what).Append(" rewired=").Append(rewired).Append(" reader=").Append(reader);
            }

            static string Number(float value)
            {
                return value.ToString("R", CultureInfo.InvariantCulture);
            }

            static string Raw()
            {
                var text = new StringBuilder();
                foreach (int axisNumber in RawAxes) text.Append(" a").Append(axisNumber).Append('=').Append(Number(raw[axisNumber]));
                text.Append(" e=");
                for (int i = 0; i < 4; i++) text.Append(Number(element[i])).Append(i < 3 ? "," : "");
                text.Append(" b=");
                for (int i = FirstButton; i < Elements; i++) text.Append(pressed[i] ? '1' : '0');
                return text.ToString();
            }
        }

        // ---------------------------------------------------------------- -crow-pad-press

        // Synthetic input for the reader: each press's buttons (bits 0-7 Unity's buttons, 8-11 the d-pad) and
        // stick values (Unity's axis values; NaN leaves the axis as Unity reads it) while it lasts.
        static class Presses
        {
            struct Press
            {
                public float Start, End;
                public int Buttons;
                public float[] Axes;  // lx, ly, rx, ry
                public bool Shot;
            }

            static readonly List<Press> presses = new List<Press>();
            static string from, shots;
            static float origin = -1f;
            static int shotsTaken;

            internal static void Install(string value, string scene, string folder)
            {
                shots = folder;
                if (string.IsNullOrEmpty(value)) return;
                foreach (string item in value.Split(';'))
                {
                    string[] parts = item.Trim().Split(':');
                    float start, length = 0.15f;
                    if (parts.Length < 2 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out start)) continue;
                    if (parts.Length > 2) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out length);
                    var press = new Press { Start = start, End = start + length, Axes = new[] { float.NaN, float.NaN, float.NaN, float.NaN } };
                    foreach (string input in parts[1].Split('+'))
                    {
                        int equals = input.IndexOf('=');
                        if (equals > 0)
                        {
                            int axisIndex = Array.IndexOf(new[] { "lx", "ly", "rx", "ry" }, input.Substring(0, equals));
                            float axisValue;
                            if (axisIndex >= 0 && float.TryParse(input.Substring(equals + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out axisValue))
                                press.Axes[axisIndex] = axisValue;
                        }
                        else if (Array.IndexOf(ButtonNames, input) >= 0) press.Buttons |= 1 << Array.IndexOf(ButtonNames, input);
                        else if (input == "shot") press.Shot = true;
                    }
                    presses.Add(press);
                }
                from = scene;
                if (from == null) origin = 0f;
                else
                    CrowEvents.Loaded("Presses.sceneLoaded", (loaded, how) =>
                    {
                        if (origin < 0f && loaded.name == from) origin = Time.realtimeSinceStartup;
                    });
                Debug.Log("CROW_PAD presses=" + presses.Count + (from != null ? " from " + from : ""));
            }

            // The buttons held now; the sticks' Unity values set in place.
            internal static int Apply(float[] axes)
            {
                if (presses.Count == 0 || origin < 0f) return 0;
                float now = Time.realtimeSinceStartup - origin;
                int held = 0;
                for (int i = 0; i < presses.Count; i++)
                {
                    Press press = presses[i];
                    if (press.Shot && shots != null && now >= press.Start)
                    {
                        press.Shot = false;
                        presses[i] = press;
                        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(shots, "pad-" + (shotsTaken++).ToString("00") + "-" +
                                                                               press.Start.ToString("0.0", CultureInfo.InvariantCulture) + ".png"));
                        Debug.Log("CROW_PAD shot at " + now.ToString("0.00", CultureInfo.InvariantCulture));
                    }
                    if (now < press.Start || now >= press.End) continue;
                    held |= press.Buttons;
                    for (int stick = 0; stick < 4; stick++)
                        if (!float.IsNaN(press.Axes[stick])) axes[StickAxes[stick]] = press.Axes[stick];
                }
                return held;
            }
        }
    }

    // Rewired's keyboard and mouse controllers, off on the PS Vita, which has neither: Rewired reads
    // them through Unity's input every frame (each mapped key, the mouse's axes and buttons) for
    // nothing. Roadside: Update 7.7 -> 7.2 ms and the main thread's work per frame 39.2 -> 38.4 ms;
    // Driving Intro: Update 4.8 -> 4.45 ms. The pad is a Rewired joystick and doesn't change.
    static class RewiredPad
    {
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        static bool pad, hooked;
        static float next = -1f;
        static System.Reflection.PropertyInfo ready, controllers;

        public static void Install(string[] args)
        {
            if (CrowFlags.On(args, "-crow-rewired", "pad")) Set(true);
        }

        internal static void Set(bool on)
        {
            pad = on;
            next = 0f;
            if (hooked) return;
            hooked = true;
            CrowEvents.Loaded("RewiredPad.sceneLoaded", (scene, mode) => next = 0f);
            CrowEvents.BeforeRender("RewiredPad.Tick", Tick);
        }

        // Once a second until Rewired is ready, and again after each scene load.
        static void Tick()
        {
            if (next < 0f || Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + 1f;
            if (ready == null)
            {
                Type input = Type.GetType("Rewired.ReInput, Rewired_Core");
                if (input == null)
                {
                    next = -1f;
                    return;
                }
                ready = input.GetProperty("isReady", Any | System.Reflection.BindingFlags.Static);
                controllers = input.GetProperty("controllers", Any | System.Reflection.BindingFlags.Static);
                if (ready == null || controllers == null)
                {
                    next = -1f;
                    Debug.Log("CROW_REWIRED no ReInput.isReady or controllers");
                    return;
                }
            }
            if (!(bool)ready.GetValue(null, null)) return;
            next = -1f;
            object helper = controllers.GetValue(null, null);
            var changed = new List<string>();
            foreach (string name in new[] { "Keyboard", "Mouse" })
            {
                System.Reflection.PropertyInfo property = helper == null ? null : helper.GetType().GetProperty(name, Any | System.Reflection.BindingFlags.Instance);
                object controller = property == null ? null : property.GetValue(helper, null);
                System.Reflection.PropertyInfo enabled = controller == null ? null : controller.GetType().GetProperty("enabled", Any | System.Reflection.BindingFlags.Instance);
                if (enabled == null || (bool)enabled.GetValue(controller, null) == !pad) continue;
                enabled.SetValue(controller, !pad, null);
                changed.Add(name);
            }
            if (changed.Count > 0) Debug.Log("CROW_REWIRED " + string.Join(" and ", changed.ToArray()) + (pad ? " off" : " on"));
        }
    }
}
