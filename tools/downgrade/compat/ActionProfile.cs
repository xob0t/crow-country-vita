using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;

// Script time by type and by owner, on players rewritten with CROW_ACTION_PROFILE=1 (the rewriter's
// ActionProfile passes put Begin and End around each action call in FsmState's state loops, and around
// the bodies of every MonoBehaviour's Update, LateUpdate, FixedUpdate and OnCollision*/OnTrigger*
// callbacks in the rewritten assemblies: PlayMakerFSM's, DOTween's, the game's) and run with
//   -crow-action-profile      sum the time of each action type's OnEnter, OnUpdate, OnFixedUpdate and
//                             OnLateUpdate, and of each behaviour type's callbacks; Fight.cs logs, per
//                             fight window (Report):
//                             CROW_ACTIONS: the costliest action types, ms a frame and calls a frame by
//                               kind (u OnUpdate, f OnFixedUpdate, l OnLateUpdate, e OnEnter;
//                               inclusive: an action whose call enters another state includes the new
//                               state's OnEnter calls)
//                             CROW_BEHAVIOURS: the costliest behaviour types (U Update, F FixedUpdate,
//                               L LateUpdate, C collision and trigger callbacks; PlayMakerFSM's include
//                               its actions)
//                             CROW_OWNERS: the behaviours' time by owner: an enemy type (the nearest
//                               ancestor named "Enemy <type>"), the player, or the scene root it is
//                               under, with the objects seen (n=)
//                             CROW_FSMS: the costliest FSMs and other behaviours, by owner, GameObject
//                               and FSM name (or type), summed over the owner's instances
//                             CROW_OWNER_ACTIONS: the costliest action types per owner
// Without the flag Begin and End return at once; builds rewritten without the variable have no calls.
// The timing doubles the scripts' time, so only the proportions count.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class ActionProfile
    {
        static readonly bool on = Array.IndexOf(ProbeFiles.Args, "-crow-action-profile") >= 0;
        const int Depth = 64, Kinds = 8;
        static readonly object[] actions = new object[Depth];
        static readonly object[] contexts = new object[Depth];
        static readonly int[] kinds = new int[Depth];
        static readonly long[] starts = new long[Depth];
        static int depth;
        // Per type: ticks per kind (0-3 actions: update, fixed, late, enter; 4-7 behaviours: Update,
        // FixedUpdate, LateUpdate, collision), then calls per kind.
        static readonly Dictionary<Type, long[]> stats = new Dictionary<Type, long[]>();
        static readonly string[] KindNames = { "u", "f", "l", "e", "U", "F", "L", "C" };
        // Top-level behaviour calls per behaviour (ticks per kind 4-7, then calls); action calls per
        // state and type.
        static readonly Dictionary<object, long[]> byBehaviour = new Dictionary<object, long[]>();
        static readonly Dictionary<StateType, long[]> byState = new Dictionary<StateType, long[]>();
        static readonly IFormatProvider Invariant = System.Globalization.CultureInfo.InvariantCulture;

        struct StateType : IEquatable<StateType>
        {
            public object State;
            public Type Type;

            public bool Equals(StateType other)
            {
                return ReferenceEquals(State, other.State) && Type == other.Type;
            }

            public override bool Equals(object other)
            {
                return other is StateType && Equals((StateType)other);
            }

            public override int GetHashCode()
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(State) * 31 + Type.GetHashCode();
            }
        }

        // An action's call (kind 0-3; context the FsmState) or a behaviour's callback (kind 4-7; action
        // and context the behaviour).
        public static void Begin(object action, int kind, object context)
        {
            if (!on) return;
            if (depth >= Depth) depth = 0;  // a call threw past its End: start over
            actions[depth] = action;
            contexts[depth] = context;
            kinds[depth] = kind;
            starts[depth] = Stopwatch.GetTimestamp();
            depth++;
        }

        public static void End()
        {
            if (!on || depth == 0) return;
            depth--;
            long ticks = Stopwatch.GetTimestamp() - starts[depth];
            object action = actions[depth], context = contexts[depth];
            actions[depth] = null;
            contexts[depth] = null;
            if (action == null) return;
            int kind = kinds[depth];
            long[] slot;
            Type type = action.GetType();
            if (!stats.TryGetValue(type, out slot)) stats[type] = slot = new long[2 * Kinds];
            slot[kind] += ticks;
            slot[Kinds + kind]++;
            if (context == null) return;
            if (kind >= 4)
            {
                if (depth != 0) return;  // nested (a behaviour's callback inside another's): counted there
                if (!byBehaviour.TryGetValue(context, out slot)) byBehaviour[context] = slot = new long[8];
                slot[kind - 4] += ticks;
                slot[kind]++;
                return;
            }
            // An action runs inside its FSM's (or another behaviour's) callback: the outermost one names
            // its owner (an event another FSM sends runs in the sender's).
            if (depth > 0 && kinds[0] >= 4 && contexts[0] != null) context = contexts[0];
            var key = new StateType { State = context, Type = type };
            if (!byState.TryGetValue(key, out slot)) byState[key] = slot = new long[2];
            slot[0] += ticks;
            slot[1]++;
        }

        static string Ms(double value)
        {
            return value.ToString("0.00", Invariant);
        }

        static string PerFrame(double value)
        {
            return value.ToString("0.#", Invariant);
        }

        // Reflection on PlayMaker's types, found from the objects seen.
        static PropertyInfo stateFsm, fsmName, fsmGameObject, componentFsmName;
        // Labels per behaviour or state: (owner, owner instance, detail).
        static readonly Dictionary<object, string[]> labels = new Dictionary<object, string[]>();
        static bool labelFailed;

        static string[] Label(object context)
        {
            string[] label;
            if (labels.TryGetValue(context, out label)) return label;
            label = new[] { "?", "?", "?" };
            try
            {
                var component = context as UnityEngine.Component;
                if ((object)component != null)
                {
                    if (component == null) return label;  // destroyed (Unity's test; not an FsmState)
                    string detail = component.GetType().Name;
                    if (detail == "PlayMakerFSM")
                    {
                        if (componentFsmName == null) componentFsmName = component.GetType().GetProperty("FsmName");
                        string name = componentFsmName == null ? "?" : (string)componentFsmName.GetValue(component, null);
                        detail = name == "FSM" ? "FSM" : "FSM:" + name;
                    }
                    label = Owner(component.gameObject, detail);
                }
                else
                {
                    // An FsmState: its Fsm's GameObject and name.
                    if (stateFsm == null) stateFsm = context.GetType().GetProperty("Fsm");
                    object fsm = stateFsm == null ? null : stateFsm.GetValue(context, null);
                    if (fsm != null)
                    {
                        if (fsmName == null) fsmName = fsm.GetType().GetProperty("Name");
                        if (fsmGameObject == null) fsmGameObject = fsm.GetType().GetProperty("GameObject");
                        var gameObject = fsmGameObject.GetValue(fsm, null) as UnityEngine.GameObject;
                        if (gameObject != null) label = Owner(gameObject, "FSM:" + (string)fsmName.GetValue(fsm, null));
                    }
                }
            }
            catch (Exception error)
            {
                if (!labelFailed)
                {
                    labelFailed = true;
                    UnityEngine.Debug.Log("CROW_ACTIONS label failed for " + context.GetType().Name + ": " + error);
                }
            }
            labels[context] = label;
            return label;
        }

        // An object's owner: an enemy type, the player or its scene root; the owner's instance; its own
        // name and detail.
        static string[] Owner(UnityEngine.GameObject gameObject, string detail)
        {
            UnityEngine.Transform transform = gameObject.transform, root = transform, enemy = null;
            for (UnityEngine.Transform t = transform; t != null; t = t.parent)
            {
                root = t;
                // The outermost: an enemy's own parts include an "Enemy Set".
                if (t.name.StartsWith("Enemy ", StringComparison.Ordinal) && t.name != "Enemy Set") enemy = t;
            }
            if (enemy != null)
            {
                string type = enemy.name.Substring(6);
                int paren = type.IndexOf(" (", StringComparison.Ordinal);
                if (paren > 0) type = type.Substring(0, paren);
                return new[] { "Enemy_" + type.Replace(' ', '_'), enemy.GetInstanceID().ToString(), gameObject.name.Replace(' ', '_') + ":" + detail.Replace(' ', '_') };
            }
            string owner = root.name == MaraScene.PlayerName ? "player" : "root:" + root.name;
            return new[] { owner.Replace(' ', '_'), root.GetInstanceID().ToString(), gameObject.name.Replace(' ', '_') + ":" + detail.Replace(' ', '_') };
        }

        // The lines over the frames since the last report.
        internal static void Report(string label, int frames)
        {
            if (!on || stats.Count == 0 || frames <= 0) return;
            double ms = 1000.0 / Stopwatch.Frequency / frames;
            Calibrate(label);
            Kind(label, frames, ms, "CROW_ACTIONS ", 0);
            Kind(label, frames, ms, "CROW_BEHAVIOURS ", 4);

            // By owner and by behaviour (top-level calls only, so the sums are the scripts' time).
            var owners = new Dictionary<string, double[]>();  // ms per kind (4), calls, action calls
            var ownerInstances = new Dictionary<string, HashSet<string>>();
            var details = new Dictionary<string, double[]>();  // ms, calls
            foreach (KeyValuePair<object, long[]> pair in byBehaviour)
            {
                string[] who = Label(pair.Key);
                double[] sum;
                if (!owners.TryGetValue(who[0], out sum)) owners[who[0]] = sum = new double[6];
                HashSet<string> seen;
                if (!ownerInstances.TryGetValue(who[0], out seen)) ownerInstances[who[0]] = seen = new HashSet<string>();
                seen.Add(who[1]);
                long calls = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum[k] += pair.Value[k] * ms;
                    calls += pair.Value[4 + k];
                }
                sum[4] += calls;
                string key = who[0] + "/" + who[2];
                double[] d;
                if (!details.TryGetValue(key, out d)) details[key] = d = new double[2];
                d[0] += (pair.Value[0] + pair.Value[1] + pair.Value[2] + pair.Value[3]) * ms;
                d[1] += calls;
            }
            // The action calls inside each owner's callbacks (each adds the timing's own cost to them).
            foreach (KeyValuePair<StateType, long[]> pair in byState)
            {
                double[] sum;
                if (owners.TryGetValue(Label(pair.Key.State)[0], out sum)) sum[5] += pair.Value[1];
            }
            double all = owners.Values.Sum(s => s[0] + s[1] + s[2] + s[3]);
            var line = new StringBuilder("CROW_OWNERS ").Append(label).Append(" frames=").Append(frames).Append(" total=").Append(Ms(all));
            foreach (KeyValuePair<string, double[]> pair in owners.OrderByDescending(p => p.Value[0] + p.Value[1] + p.Value[2] + p.Value[3]).Take(30))
            {
                double[] s = pair.Value;
                line.Append(' ').Append(pair.Key).Append('=').Append(Ms(s[0] + s[1] + s[2] + s[3])).Append("[n").Append(ownerInstances[pair.Key].Count);
                for (int k = 0; k < 4; k++)
                    if (s[k] >= 0.005) line.Append(' ').Append(KindNames[4 + k]).Append(Ms(s[k]));
                line.Append(" calls").Append(PerFrame(s[4] / frames)).Append(" actions").Append(PerFrame(s[5] / frames)).Append(']');
            }
            UnityEngine.Debug.Log(line.ToString());
            line = new StringBuilder("CROW_FSMS ").Append(label).Append(" frames=").Append(frames);
            foreach (KeyValuePair<string, double[]> pair in details.OrderByDescending(p => p.Value[0]).Take(50))
                line.Append(' ').Append(pair.Key).Append('=').Append(Ms(pair.Value[0])).Append('[').Append(PerFrame(pair.Value[1] / frames)).Append(']');
            UnityEngine.Debug.Log(line.ToString());

            // Action types per owner.
            var ownerActions = new Dictionary<string, double[]>();
            foreach (KeyValuePair<StateType, long[]> pair in byState)
            {
                string key = Label(pair.Key.State)[0] + "/" + pair.Key.Type.Name;
                double[] d;
                if (!ownerActions.TryGetValue(key, out d)) ownerActions[key] = d = new double[2];
                d[0] += pair.Value[0] * ms;
                d[1] += pair.Value[1];
            }
            line = new StringBuilder("CROW_OWNER_ACTIONS ").Append(label).Append(" frames=").Append(frames);
            foreach (KeyValuePair<string, double[]> pair in ownerActions.OrderByDescending(p => p.Value[0]).Take(60))
                line.Append(' ').Append(pair.Key).Append('=').Append(Ms(pair.Value[0])).Append('[').Append(PerFrame(pair.Value[1] / frames)).Append(']');
            UnityEngine.Debug.Log(line.ToString());

            stats.Clear();
            byBehaviour.Clear();
            byState.Clear();
            if (labels.Count > 20000) labels.Clear();
        }

        class Calibration
        {
        }

        static readonly Calibration calibration = new Calibration();

        // CROW_PROFILE_COST: what one timed call costs (full: Begin and End around nothing, as the frame
        // pays it; recorded: what such a call adds to its own sum), in microseconds, for correcting the
        // sums (a callback's sum includes the full cost of each action call inside it).
        static void Calibrate(string label)
        {
            const int Calls = 2000;
            int saved = depth;
            depth = 0;
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < Calls; i++)
            {
                Begin(calibration, 0, calibration);
                End();
            }
            long full = Stopwatch.GetTimestamp() - start;
            depth = saved;
            long[] slot;
            long recorded = stats.TryGetValue(typeof(Calibration), out slot) ? slot[0] : 0;
            stats.Remove(typeof(Calibration));
            byState.Remove(new StateType { State = calibration, Type = typeof(Calibration) });
            double us = 1e6 / Stopwatch.Frequency / Calls;
            UnityEngine.Debug.Log("CROW_PROFILE_COST " + label + " full=" + (full * us).ToString("0.00", Invariant) + " recorded=" + (recorded * us).ToString("0.00", Invariant));
        }

        // A line of the costliest types of one group of kinds (actions 0-3, behaviours 4-7).
        static void Kind(string label, int frames, double ms, string head, int first)
        {
            Func<long[], long> sum = s => s[first] + s[first + 1] + s[first + 2] + s[first + 3];
            double total = stats.Values.Sum(s => sum(s)) * ms;
            var line = new StringBuilder(head).Append(label).Append(" frames=").Append(frames).Append(" total=").Append(Ms(total));
            foreach (KeyValuePair<Type, long[]> pair in stats.Where(p => sum(p.Value) > 0).OrderByDescending(p => sum(p.Value)).Take(40))
            {
                long[] s = pair.Value;
                line.Append(' ').Append(pair.Key.Name).Append('=').Append(Ms(sum(s) * ms)).Append('[');
                for (int k = first; k < first + 4; k++)
                    if (s[Kinds + k] > 0)
                        line.Append(KindNames[k]).Append(PerFrame(s[Kinds + k] / (double)frames));
                line.Append(']');
            }
            UnityEngine.Debug.Log(line.ToString());
        }
    }
}
