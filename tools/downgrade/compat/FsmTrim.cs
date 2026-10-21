using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

// PlayMaker's FSM updates that do nothing, skipped (19 Oct). A steady fight frame runs ~360
// PlayMakerFSM Updates and ~100 FixedUpdate and LateUpdate proxy calls, most for FSMs whose active
// state only waits for an event (every action finished: PlayMaker delivers events through
// SendEvent and Fsm.Event, not through the update) or whose running actions don't use that update
// (a Wait in a state of an FSM that has a FixedUpdate action elsewhere). The rewriter
// (rewriter/FsmTrim.cs) starts Fsm.Update, Fsm.FixedUpdate and Fsm.LateUpdate (after FsmGate's
// hooks, Cuts.cs) with Fsm.CrowIdle, and the PlayMakerFixedUpdate and PlayMakerLateUpdate proxies'
// loops with the same test per target. What PlayMaker's update does, and when it is a no-op:
//   Fsm.Update: Continue() unless the active state was entered (activeStateEntered), the delayed
//     events (DelayedEvent.Update for each), then FsmState.OnUpdate: when the state has finished
//     and no action is running, nothing at all (not even StateTime); else StateTime +=
//     Time.deltaTime (FsmGate.DeltaTime), each running action's OnUpdate, CheckAllActionsFinished
//     (removes the actions that called Finish(), and when none left or none BlocksFinish, finishes
//     the state and sends FINISHED). Then UpdateStateChanges: the pending switch (switchToState,
//     a transition an FSM's own event left), the loop counts' reset (ScriptCost's crowStatesEntered
//     mark), EventTarget = null.
//   FixedUpdate and LateUpdate: the same without StateTime and delayed events, only when the
//     active state was entered.
// So an update is skipped when: the active state was entered and is the FSM's (its fsm field),
// no switch is pending, EventTarget is already null, no state was entered since the last loop
// count reset, (Update) no delayed event is queued, and either the state finished with no action
// running (idle: nothing to do), or every running action leaves that update alone and the finish
// check has nothing to do: the state finished, isn't active, or no action is waiting in its
// finished list and a running action blocks the finish (timed: Update only adds the frame time to
// StateTime, which the skip does itself). An action leaves an update alone when its class doesn't
// override that FsmStateAction method; BaseUpdateAction's (the "Update type" actions) only call
// OnActionUpdate for their update type and finish themselves only when not everyFrame, so for
// those the action's own everyFrame and updateType say which (Hooks below, once per action).
// The proxies' test is the same: an idle target's FixedUpdate would do nothing whether it ran
// or not (not Active, no HandleFixedUpdate), and PlayMakerFSM.Fsm's set of Owner is skipped only
// when the owner is already that component.
// Flags (on by default on the PS Vita, the game's elsewhere):
//   -crow-fsm-trim on|off|check   check: every update the test would skip runs as PlayMaker has it,
//                                 and what it changed is compared with what the skip predicts (the
//                                 FSM's state, switch, flags, delayed events, the state's time,
//                                 finished/active flags, loop count, running and finished actions
//                                 and their flags); CROW_FSM_TRIM_CHECK lines name any difference
//   -crow-fsm-trim-log            every 10 s, CROW_FSM_TRIM: updates per frame run and skipped; and
//                                 CROW_FSM_TRIM_CENSUS: the FSMs Unity updates (enabled, active), by what
//                                 their active state holds (finished or manual, idle, no running action
//                                 with an OnUpdate, delayed events), and the running action types with an
//                                 OnUpdate that keep the others' updates, the most numerous first (by
//                                 reflection: about 0.4 s on the PS Vita)
// -crow-cycle, -crow-fight-cycle and -crow-ab take fsmtrim=on|off|check, and fsmupdate=, fsmfixed=,
// fsmlate= (on|off) for each part. The fight's windows log pm=<Update calls>/<idle>/<timed>
// F<calls>/<idle>/<proxy skips> L<the same> per frame.
// Console, steady fights, off -> on: Station Square (330 Updates a frame, 120 skipped; 41 of 47
// FixedUpdate and 17 of 29 LateUpdate targets) main thread -0.3 ms, scripts -0.4; Fairy Forest -0.6 and
// -0.7 (status.md, PlayMaker runtime trim): 1.5-3.5 us a skipped update.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class FsmTrim
    {
        // Read by the patched methods (no static constructor: a plain field read in IL2CPP).
        public static bool Update, Fixed, Late, Check, Checking;
        // Counters the patched methods raise: calls that reached the test, idle and timed skips,
        // proxy skips (those never reach Fsm.FixedUpdate / LateUpdate).
        public static int UpdateCalls, UpdateIdle, UpdateTimed, FixedCalls, FixedIdle, FixedProxy, LateCalls, LateIdle, LateProxy;

        static bool log;
        static float logAt;
        static int logFrame, reportFrame;
        static int[] logStart, reportStart;

        // From Diag.Install (every player).
        internal static void Install(string[] args)
        {
            Set(Application.platform == RuntimePlatform.PSP2 ? "on" : "off");
            string value = CrowFlags.Value(args, "-crow-fsm-trim");
            if (value != null) Set(value);
            logStart = reportStart = Counters();
            log = Array.IndexOf(args, "-crow-fsm-trim-log") >= 0;
            if (log) CrowEvents.Canvases("FsmTrim.Frame", Frame);
            Debug.Log("CROW_FSM_TRIM " + Describe());
        }

        static void Set(string value)
        {
            bool on = value == "on" || value == "check";
            Update = Fixed = Late = on;
            Check = value == "check";
        }

        static string Describe()
        {
            return "update=" + (Update ? "on" : "off") + " fixed=" + (Fixed ? "on" : "off") + " late=" + (Late ? "on" : "off") + (Check ? " check" : "");
        }

        // A -crow-cycle / -crow-fight-cycle / -crow-ab change; false when it isn't one of these.
        internal static bool Change(string key, string value)
        {
            switch (key)
            {
                case "fsmtrim":
                    Set(value);
                    break;
                case "fsmupdate":
                    Update = value == "on";
                    break;
                case "fsmfixed":
                    Fixed = value == "on";
                    break;
                case "fsmlate":
                    Late = value == "on";
                    break;
                default:
                    return false;
            }
            Debug.Log("CROW_FSM_TRIM " + key + "=" + value + ": " + Describe());
            return true;
        }

        static int[] Counters()
        {
            return new[] { UpdateCalls, UpdateIdle, UpdateTimed, FixedCalls, FixedIdle, FixedProxy, LateCalls, LateIdle, LateProxy };
        }

        // Per frame since Mark: Update calls/idle/timed, FixedUpdate calls (with the proxies' skips)/
        // idle/proxy skips, LateUpdate the same (Fight's window lines; a window starts with Mark).
        internal static string Report()
        {
            return PerFrame("pm=", reportStart, Time.frameCount - reportFrame) + " " + FsmCompile.Report();
        }

        internal static void Mark()
        {
            reportStart = Counters();
            reportFrame = Time.frameCount;
        }

        static string PerFrame(string label, int[] start, int frames)
        {
            int[] now = Counters();
            if (frames <= 0) frames = 1;
            Func<int, string> at = i => ((now[i] - start[i]) / (float)frames).ToString("0.#", CultureInfo.InvariantCulture);
            return label + at(0) + "/" + at(1) + "/" + at(2) + " F" +
                   ((now[3] - start[3] + now[5] - start[5]) / (float)frames).ToString("0.#", CultureInfo.InvariantCulture) + "/" + at(4) + "/" + at(5) +
                   " L" + ((now[6] - start[6] + now[8] - start[8]) / (float)frames).ToString("0.#", CultureInfo.InvariantCulture) + "/" + at(7) + "/" + at(8);
        }

        static void Frame()
        {
            float now = Time.realtimeSinceStartup;
            if (now - logAt < 10f) return;
            Debug.Log("CROW_FSM_TRIM " + Describe() + " " + PerFrame("per frame U", logStart, Time.frameCount - logFrame) +
                      TrimProbe.Checks());
            Debug.Log("CROW_FSM_TRIM_CENSUS " + TrimProbe.Census());
            logAt = now;
            logStart = Counters();
            logFrame = Time.frameCount;
        }

        // The updates an action uses (Fsm.CrowIdle reads FsmStateAction.crowHooks and fills it from
        // here the first time; see TrimProbe.Hooks).
        public static int Hooks(object action)
        {
            return TrimProbe.Hooks(action);
        }

        // -crow-fsm-trim check (TrimProbe.CheckBegin, CheckEnd).
        public static void CheckBegin(object fsm, int kind, int tier)
        {
            TrimProbe.CheckBegin(fsm, kind, tier);
        }

        public static void CheckEnd(object fsm)
        {
            TrimProbe.CheckEnd(fsm);
        }
    }

    // FsmTrim's slow parts, apart so FsmTrim's fields (read by every FSM update) need no class
    // initialization.
    static class TrimProbe
    {
        // Hooks bits (FsmStateAction.crowHooks): 1 known, then the updates the action uses.
        const int Known = 1, OnUpdate = 2, OnFixedUpdate = 4, OnLateUpdate = 8;
        static readonly string[] HookNames = { "OnUpdate", "OnFixedUpdate", "OnLateUpdate" };
        static readonly int[] HookBits = { OnUpdate, OnFixedUpdate, OnLateUpdate };
        // BaseUpdateAction.UpdateType's values: OnUpdate, OnLateUpdate, OnFixedUpdate.
        static readonly int[] UpdateTypeBits = { OnUpdate, OnLateUpdate, OnFixedUpdate };

        // The FSMs Unity updates now (PlayMakerFSM.FsmList, enabled and active), by what their active
        // state holds (by reflection: slow, for the log only).
        internal static string Census()
        {
            try
            {
                Type component = Type.GetType("PlayMakerFSM, PlayMaker");
                var list = component.GetProperty("FsmList").GetValue(null, null) as IList;
                FieldInfo fsmField = Field(component, "fsm");
                int updated = 0, finished = 0, idle = 0, quiet = 0, delayed = 0, busy = 0;
                var holders = new Dictionary<string, int>();
                foreach (object item in list)
                {
                    var behaviour = item as Behaviour;
                    if (behaviour == null || !behaviour.isActiveAndEnabled) continue;
                    updated++;
                    object fsm = fsmField.GetValue(item);
                    if (fsm == null) continue;
                    Type fsmType = fsm.GetType();
                    if ((bool)Field(fsmType, "<Finished>k__BackingField").GetValue(fsm) || (bool)Field(fsmType, "manualUpdate").GetValue(fsm))
                    {
                        finished++;
                        continue;
                    }
                    object state = Field(fsmType, "activeState").GetValue(fsm);
                    if (state == null) continue;
                    var running = Field(state.GetType(), "activeActions").GetValue(state) as IList;
                    bool stateFinished = (bool)Field(state.GetType(), "finished").GetValue(state);
                    var events = Field(fsmType, "delayedEvents").GetValue(fsm) as ICollection;
                    if (events != null && events.Count > 0) delayed++;
                    if ((running == null || running.Count == 0) && stateFinished)
                    {
                        idle++;
                        continue;
                    }
                    bool updates = false;
                    if (running != null)
                        foreach (object action in running)
                            if (action != null && (Hooks(action) & OnUpdate) != 0)
                            {
                                updates = true;
                                string name = action.GetType().Name;
                                int count;
                                holders.TryGetValue(name, out count);
                                holders[name] = count + 1;
                            }
                    if (updates) busy++;
                    else quiet++;
                }
                var line = new StringBuilder("updated=").Append(updated).Append(" finished=").Append(finished).Append(" idle=").Append(idle)
                    .Append(" noOnUpdate=").Append(quiet).Append(" withOnUpdate=").Append(busy).Append(" delayedEvents=").Append(delayed).Append(" holders:");
                var sorted = new List<KeyValuePair<string, int>>(holders);
                sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < sorted.Count && i < 30; i++) line.Append(' ').Append(sorted[i].Key).Append('=').Append(sorted[i].Value);
                return line.ToString();
            }
            catch (Exception exception)
            {
                return "failed: " + exception.Message;
            }
        }

        internal static string Checks()
        {
            return checks > 0 || mismatches > 0 ? " checks=" + checks + " mismatches=" + mismatches : "";
        }

        // The updates an action uses: the FsmStateAction methods its class overrides, BaseUpdateAction's
        // by the action's everyFrame and updateType.
        internal static int Hooks(object action)
        {
            Type type = action.GetType();
            TypeHooks hooks;
            if (!types.TryGetValue(type, out hooks)) types[type] = hooks = Classify(type);
            int bits = hooks.Bits;
            if (hooks.BaseBits != 0)
            {
                try
                {
                    bool everyFrame = (bool)hooks.EveryFrame.GetValue(action);
                    int updateType = Convert.ToInt32(hooks.UpdateType.GetValue(action), CultureInfo.InvariantCulture);
                    if (!everyFrame || updateType < 0 || updateType > 2) bits |= hooks.BaseBits;  // Finish() in each
                    else bits |= hooks.BaseBits & UpdateTypeBits[updateType];
                }
                catch (Exception)
                {
                    bits |= hooks.BaseBits;
                }
            }
            return bits;
        }

        sealed class TypeHooks
        {
            public int Bits = Known, BaseBits;
            public FieldInfo EveryFrame, UpdateType;
        }

        static readonly Dictionary<Type, TypeHooks> types = new Dictionary<Type, TypeHooks>();

        static TypeHooks Classify(Type type)
        {
            var hooks = new TypeHooks();
            try
            {
                for (int i = 0; i < HookNames.Length; i++)
                {
                    MethodInfo method = type.GetMethod(HookNames[i], BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                    string declaring = method == null || method.DeclaringType == null ? "" : method.DeclaringType.FullName;
                    if (declaring == "HutongGames.PlayMaker.FsmStateAction") continue;
                    if (declaring == "HutongGames.PlayMaker.Actions.BaseUpdateAction") hooks.BaseBits |= HookBits[i];
                    else hooks.Bits |= HookBits[i];
                }
                if (hooks.BaseBits != 0)
                {
                    hooks.EveryFrame = type.GetField("everyFrame", BindingFlags.Instance | BindingFlags.Public);
                    hooks.UpdateType = type.GetField("updateType", BindingFlags.Instance | BindingFlags.Public);
                    if (hooks.EveryFrame == null || hooks.UpdateType == null)
                    {
                        hooks.Bits |= hooks.BaseBits;
                        hooks.BaseBits = 0;
                    }
                }
            }
            catch (Exception exception)
            {
                hooks.Bits = Known | OnUpdate | OnFixedUpdate | OnLateUpdate;
                hooks.BaseBits = 0;
                Debug.Log("CROW_FSM_TRIM hooks of " + type.FullName + " unknown: " + exception.Message);
            }
            return hooks;
        }

        // -crow-fsm-trim check: before an update the test would skip (kind 0 Update, 1 FixedUpdate,
        // 2 LateUpdate; tier 1 idle, 2 timed), what it reads; at the update's end (CheckEnd, before
        // each of its returns), what it changed. An update the test passes runs nothing of the FSM's
        // own, so none nests inside it.
        static object pendingFsm;
        static int pendingKind, pendingTier;
        static object[] before;
        static float predictedTime;
        static int checks, mismatches;

        internal static void CheckBegin(object fsm, int kind, int tier)
        {
            // the counters the skip would raise
            if (kind == 0 && tier == 1) FsmTrim.UpdateIdle++;
            else if (kind == 0) FsmTrim.UpdateTimed++;
            else if (kind == 1) FsmTrim.FixedIdle++;
            else FsmTrim.LateIdle++;
            FsmTrim.Checking = true;
            pendingFsm = fsm;
            pendingKind = kind;
            pendingTier = tier;
            before = Snapshot(fsm);
            predictedTime = (float)before[StateTimeIndex];
            if (kind == 0 && tier == 2) predictedTime = predictedTime + FsmGate.DeltaTime();
        }

        internal static void CheckEnd(object fsm)
        {
            if (!ReferenceEquals(fsm, pendingFsm)) return;
            FsmTrim.Checking = false;
            pendingFsm = null;
            checks++;
            object[] after = Snapshot(fsm);
            StringBuilder differences = null;
            for (int i = 0; i < after.Length; i++)
            {
                bool same = i == StateTimeIndex ? BitConverter.ToInt32(BitConverter.GetBytes((float)after[i]), 0) ==
                                                  BitConverter.ToInt32(BitConverter.GetBytes(predictedTime), 0)
                                                : Same(before[i], after[i]);
                if (same) continue;
                if (differences == null) differences = new StringBuilder();
                differences.Append(' ').Append(SnapshotNames[i]).Append(": ").Append(Show(i == StateTimeIndex ? predictedTime : before[i]))
                    .Append(" -> ").Append(Show(after[i]));
            }
            if (differences == null) return;
            mismatches++;
            if (mismatches <= 40)
                Debug.Log("CROW_FSM_TRIM_CHECK mismatch " + (pendingKind == 0 ? "Update" : pendingKind == 1 ? "FixedUpdate" : "LateUpdate") +
                          " tier=" + pendingTier + " " + Describe(fsm) + ":" + differences);
        }

        static bool Same(object a, object b)
        {
            var list = a as object[];
            if (list == null) return ReferenceEquals(a, b) || (a != null && a.Equals(b));
            var other = b as object[];
            if (other == null || other.Length != list.Length) return false;
            for (int i = 0; i < list.Length; i++)
                if (!Same(list[i], other[i])) return false;
            return true;
        }

        static string Show(object value)
        {
            var list = value as object[];
            if (list == null) return value == null ? "null" : value is float ? ((float)value).ToString("R", CultureInfo.InvariantCulture) : value.ToString();
            var line = new StringBuilder("[");
            foreach (object item in list) line.Append(line.Length > 1 ? "," : "").Append(Show(item));
            return line.Append(']').ToString();
        }

        // What an update could change: the FSM's fields, its active state's, the state's running and
        // finished actions (and each running action's active, finished and entered flags).
        static readonly string[] FsmFields = { "activeState", "switchToState", "activeStateEntered", "<EventTarget>k__BackingField", "<Finished>k__BackingField",
                                               "crowStatesEntered", "crowDelayedEvents", "previousActiveState", "<LastTransition>k__BackingField",
                                               "<SwitchedState>k__BackingField", "<Started>k__BackingField" };
        static readonly string[] StateFields = { "<StateTime>k__BackingField", "active", "finished", "activeAction", "activeActionIndex", "<loopCount>k__BackingField" };
        static readonly string[] ActionFields = { "active", "finished", "<Entered>k__BackingField" };
        static readonly string[] SnapshotNames = BuildNames();
        static readonly int StateTimeIndex = FsmFields.Length;
        static FieldInfo[] fsmInfo, stateInfo, actionInfo;
        static FieldInfo delayedInfo, activeActionsInfo, finishedActionsInfo;

        static string[] BuildNames()
        {
            var names = new List<string>(FsmFields);
            foreach (string name in StateFields) names.Add("state." + name);
            names.Add("delayedEvents");
            names.Add("state.activeActions");
            names.Add("state.finishedActions");
            names.Add("actions' active/finished/entered");
            names.Add("loopCounts");
            return names.ToArray();
        }

        static FieldInfo Field(Type type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) return field;
            }
            return null;
        }

        static object[] Snapshot(object fsm)
        {
            Type fsmType = fsm.GetType();
            if (fsmInfo == null)
            {
                fsmInfo = Array.ConvertAll(FsmFields, name => Field(fsmType, name));
                delayedInfo = Field(fsmType, "delayedEvents");
            }
            var values = new List<object>();
            foreach (FieldInfo field in fsmInfo) values.Add(field == null ? null : field.GetValue(fsm));
            object state = fsmInfo[0].GetValue(fsm);
            if (stateInfo == null && state != null)
            {
                Type stateType = state.GetType();
                stateInfo = Array.ConvertAll(StateFields, name => Field(stateType, name));
                activeActionsInfo = Field(stateType, "activeActions");
                finishedActionsInfo = Field(stateType, "_finishedActions");
            }
            for (int i = 0; i < StateFields.Length; i++)
                values.Add(state == null || stateInfo == null || stateInfo[i] == null ? (object)0f : stateInfo[i].GetValue(state));
            if (state == null) values[StateTimeIndex] = 0f;
            var delayed = delayedInfo == null ? null : delayedInfo.GetValue(fsm) as ICollection;
            values.Add(delayed == null ? -1 : delayed.Count);
            var running = state == null || activeActionsInfo == null ? null : activeActionsInfo.GetValue(state) as IList;
            var finished = state == null || finishedActionsInfo == null ? null : finishedActionsInfo.GetValue(state) as IList;
            values.Add(List(running));
            values.Add(List(finished));
            var flags = new List<object>();
            if (running != null)
                foreach (object action in running)
                {
                    if (action == null) continue;
                    if (actionInfo == null) actionInfo = Array.ConvertAll(ActionFields, name => Field(action.GetType(), name));
                    foreach (FieldInfo field in actionInfo) flags.Add(field == null ? null : field.GetValue(action));
                }
            values.Add(flags.ToArray());
            values.Add(LoopCounts(fsm, fsmType));
            return values.ToArray();
        }

        static FieldInfo statesInfo, loopInfo;

        // Every state's loop count (UpdateStateChanges resets them).
        static object[] LoopCounts(object fsm, Type fsmType)
        {
            if (statesInfo == null) statesInfo = Field(fsmType, "states");
            var states = statesInfo == null ? null : statesInfo.GetValue(fsm) as Array;
            if (states == null) return new object[0];
            var counts = new object[states.Length];
            for (int i = 0; i < states.Length; i++)
            {
                object state = states.GetValue(i);
                if (state == null) continue;
                if (loopInfo == null) loopInfo = Field(state.GetType(), "<loopCount>k__BackingField");
                counts[i] = loopInfo == null ? null : loopInfo.GetValue(state);
            }
            return counts;
        }

        static object[] List(IList list)
        {
            if (list == null) return new object[0];
            var items = new object[list.Count];
            list.CopyTo(items, 0);
            return items;
        }

        static string Describe(object fsm)
        {
            try
            {
                Type type = fsm.GetType();
                var owner = type.GetProperty("Owner").GetValue(fsm, null) as Component;
                object name = type.GetProperty("Name").GetValue(fsm, null);
                object state = fsmInfo[0].GetValue(fsm);
                object stateName = state == null ? null : state.GetType().GetProperty("Name").GetValue(state, null);
                return (owner == null ? "?" : Perf.Path(owner.transform, 3)) + ":" + name + " state=" + stateName;
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}
