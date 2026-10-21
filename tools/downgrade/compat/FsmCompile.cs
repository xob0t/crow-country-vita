using System;
using System.Globalization;
using System.Text;
using UnityEngine;

// PlayMaker's FSM code compiled the way SNC can't (19 Oct). The PS Vita's C++ compiler has no link-time
// optimization, so no call between IL2CPP's C++ files is inlined: every FsmFloat.Value, FsmStateAction.Fsm,
// NamedVariable.IsNone or Fsm.Event(null) an action makes is a real call into PlayMaker's module, and the
// state loops call every running action's OnUpdate, OnFixedUpdate and OnLateUpdate whether its class has
// one or not. Measured on the console first (status.md, FSM compile): the actions' own logic is a small
// part of their time (a polling BoolTest 0.9 us, FloatCompare 2.8, IntCompare 1.6), the rest is the engine
// they call (GetWorldDistance 9.8 us, SmoothLookAtWithLimit 36, SetRotation 27, AddForce 52), so a compiled
// FSM per design could take little more than PlayMaker's own part, and none of the engine's. The rewriter
// (rewriter/FsmCompile.cs) instead compiles every FSM's code, the enemies', Mara's and the level's alike:
//   - PlayMaker's small methods (getters, setters, Fsm.Event(FsmEvent), Finish, GetOwnerDefaultTarget...)
//     inlined into their callers: PlayMaker's own per-frame methods (Fsm.Update, the state loops, events,
//     transitions) and the actions' updates with the action methods they call; the members they reach
//     become internal, with PlayMaker's InternalsVisibleTo for the game's assemblies (Mono's access checks);
//   - in the state loops, an action whose class doesn't use that update (its FsmStateAction.crowHooks,
//     FsmTrim's) isn't called: the base method is empty.
// Each method it changed keeps its original body after the compiled one and starts with a test of Mode:
// 2 the compiled body, 0 the original (also when Install didn't run); assemblies rewritten with
// CROW_FSM_COMPILE=on have the compiled bodies only. Windows builds have a third body (check) that runs
// the original and, at each inlined site whose callee only reads, the inlined code too, comparing the two
// values (CROW_FSM_COMPILE_CHECK names a difference).
// Flags (on by default on the PS Vita, the game's elsewhere):
//   -crow-fsm-compile on|off|check   check: Windows builds only (the PS Vita's has no check bodies)
//   -crow-fsm-compile-log            every 10 s, CROW_FSM_COMPILE: the mode, and in check mode the checks
//                                    and mismatches
// -crow-cycle, -crow-fight-cycle and -crow-ab take fsmcompile=on|off|check, and splitfilter=on|off
// (EnemySplit's filter in front of its lookups, EnemySplit.cs). The fight's windows log fc=<mode>.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class FsmCompile
    {
        // The rewriter reads it: a check body in each compiled method (Windows builds).
#if CROW_VITA
        public const bool CheckBuilt = false;
#else
        public const bool CheckBuilt = true;
#endif
        // Read by the compiled methods (no static constructor: a plain field read in IL2CPP): 0 original,
        // 1 check, 2 compiled.
        public static int Mode;

        static bool log;
        static float logAt;
        static long checks, mismatches;

        // From Diag.Install (every player).
        internal static void Install(string[] args)
        {
            Set(Application.platform == RuntimePlatform.PSP2 ? "on" : "off");
            string value = CrowFlags.Value(args, "-crow-fsm-compile");
            if (value != null) Set(value);
            log = CrowFlags.Has(args, "-crow-fsm-compile-log");
            if (log) CrowEvents.Canvases("FsmCompile.Frame", Frame);
            Debug.Log("CROW_FSM_COMPILE " + Describe());
        }

        static void Set(string value)
        {
            Mode = value == "on" ? 2 : value == "check" && CheckBuilt ? 1 : 0;
            if (value == "check" && !CheckBuilt) Debug.Log("CROW_FSM_COMPILE check: this build has no check bodies (Windows builds only); off");
        }

        static string Describe()
        {
            return "mode=" + (Mode == 2 ? "on" : Mode == 1 ? "check" : "off") + " splitfilter=" + (EnemySplit.Filter ? "on" : "off");
        }

        // A -crow-cycle / -crow-fight-cycle / -crow-ab change; false when it isn't one of these.
        internal static bool Change(string key, string value)
        {
            switch (key)
            {
                case "fsmcompile":
                    Set(value);
                    break;
                case "splitfilter":
                    EnemySplit.Filter = value != "off";
                    break;
                default:
                    return false;
            }
            Debug.Log("CROW_FSM_COMPILE " + key + "=" + value + ": " + Describe());
            return true;
        }

        // For Fight's window lines (through FsmTrim.Report).
        internal static string Report()
        {
            return "fc=" + (Mode == 2 ? "on" : Mode == 1 ? "check" + (mismatches > 0 ? "!" + mismatches : "") : "off") + (EnemySplit.Filter ? "" : " nosplitfilter");
        }

        static void Frame()
        {
            float now = Time.realtimeSinceStartup;
            if (now - logAt < 10f) return;
            logAt = now;
            Debug.Log("CROW_FSM_COMPILE " + Describe() + (Mode == 1 || checks > 0 ? " checks=" + checks + " mismatches=" + mismatches : ""));
        }

        // The check bodies: at an inlined site, the inlined code's value and the original call's (boxed).
        public static void Same(object compiled, object original, string site)
        {
            checks++;
            if (Equal(compiled, original)) return;
            mismatches++;
            if (mismatches <= 40)
                Debug.Log("CROW_FSM_COMPILE_CHECK mismatch at " + site + ": compiled " + Show(compiled) + ", original " + Show(original));
        }

        static bool Equal(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.GetType() != b.GetType()) return false;
            if (a is float) return Bits((float)a) == Bits((float)b);
            if (a is double) return BitConverter.DoubleToInt64Bits((double)a) == BitConverter.DoubleToInt64Bits((double)b);
            if (a is Vector3)
            {
                Vector3 x = (Vector3)a, y = (Vector3)b;
                return Bits(x.x) == Bits(y.x) && Bits(x.y) == Bits(y.y) && Bits(x.z) == Bits(y.z);
            }
            if (a is Vector2)
            {
                Vector2 x = (Vector2)a, y = (Vector2)b;
                return Bits(x.x) == Bits(y.x) && Bits(x.y) == Bits(y.y);
            }
            if (a is Quaternion)
            {
                Quaternion x = (Quaternion)a, y = (Quaternion)b;
                return Bits(x.x) == Bits(y.x) && Bits(x.y) == Bits(y.y) && Bits(x.z) == Bits(y.z) && Bits(x.w) == Bits(y.w);
            }
            if (a is Color)
            {
                Color x = (Color)a, y = (Color)b;
                return Bits(x.r) == Bits(y.r) && Bits(x.g) == Bits(y.g) && Bits(x.b) == Bits(y.b) && Bits(x.a) == Bits(y.a);
            }
            // Other values (bool, int, enums, Rect...) by value; references must be the same object.
            return a.GetType().IsValueType && a.Equals(b);
        }

        static int Bits(float value)
        {
            return BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        }

        static string Show(object value)
        {
            if (value == null) return "null";
            if (value is float) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            var unity = value as UnityEngine.Object;
            if (!ReferenceEquals(unity, null)) return value.GetType().Name + "(" + (unity == null ? "destroyed" : unity.name) + ")";
            return value.GetType().Name + "(" + value + ")";
        }
    }
}
