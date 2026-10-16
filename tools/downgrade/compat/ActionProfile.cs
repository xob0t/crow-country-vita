using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

// PlayMaker's actions timed by type, on players rewritten with CROW_ACTION_PROFILE=1 (the rewriter's
// ActionProfile pass puts Begin and End around each action call in FsmState's state loops) and run with
//   -crow-action-profile      sum the time of each action type's OnEnter, OnUpdate, OnFixedUpdate and
//                             OnLateUpdate; Fight.cs logs a CROW_ACTIONS line a window (Report): the
//                             costliest types, ms a frame and calls a frame (inclusive: an action whose
//                             call enters another state includes the new state's OnEnter calls)
// Without the flag Begin and End return at once; builds rewritten without the variable have no calls.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class ActionProfile
    {
        static readonly bool on = Array.IndexOf(ProbeFiles.Args, "-crow-action-profile") >= 0;
        const int Depth = 32;
        static readonly object[] actions = new object[Depth];
        static readonly int[] kinds = new int[Depth];
        static readonly long[] starts = new long[Depth];
        static int depth;
        // Per type: ticks per kind (update, fixed, late, enter), then calls per kind.
        static readonly Dictionary<Type, long[]> stats = new Dictionary<Type, long[]>();
        static readonly string[] KindNames = { "u", "f", "l", "e" };

        public static void Begin(object action, int kind)
        {
            if (!on) return;
            if (depth >= Depth) depth = 0;  // an action threw past its End: start over
            actions[depth] = action;
            kinds[depth] = kind;
            starts[depth] = Stopwatch.GetTimestamp();
            depth++;
        }

        public static void End()
        {
            if (!on || depth == 0) return;
            depth--;
            long ticks = Stopwatch.GetTimestamp() - starts[depth];
            object action = actions[depth];
            actions[depth] = null;
            if (action == null) return;
            long[] slot;
            Type type = action.GetType();
            if (!stats.TryGetValue(type, out slot)) stats[type] = slot = new long[8];
            slot[kinds[depth]] += ticks;
            slot[4 + kinds[depth]]++;
        }

        // A CROW_ACTIONS line over the frames since the last: the 30 costliest types.
        internal static void Report(string label, int frames)
        {
            if (!on || stats.Count == 0 || frames <= 0) return;
            double ms = 1000.0 / Stopwatch.Frequency / frames;
            double total = stats.Values.Sum(s => s[0] + s[1] + s[2] + s[3]) * ms;
            var line = new StringBuilder("CROW_ACTIONS ").Append(label).Append(" frames=").Append(frames)
                .Append(" total=").Append(total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            foreach (KeyValuePair<Type, long[]> pair in stats.OrderByDescending(p => p.Value[0] + p.Value[1] + p.Value[2] + p.Value[3]).Take(30))
            {
                long[] s = pair.Value;
                line.Append(' ').Append(pair.Key.Name).Append('=')
                    .Append(((s[0] + s[1] + s[2] + s[3]) * ms).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('[');
                for (int k = 0; k < 4; k++)
                    if (s[4 + k] > 0)
                        line.Append(KindNames[k]).Append((s[4 + k] / (double)frames).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                line.Append(']');
            }
            UnityEngine.Debug.Log(line.ToString());
            stats.Clear();
        }
    }
}
