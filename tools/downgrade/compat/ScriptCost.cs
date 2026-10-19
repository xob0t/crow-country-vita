using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

// The main thread's script costs at the PS Vita's speed (tools/downgrade/cpuprofile.py samples the
// Windows IL2CPP player at about that speed). PlayMaker's per-frame bookkeeping and the game's
// full-screen fade loop cost more there than the game logic they serve. The rewriter's ScriptCost
// passes (rewriter/ScriptCost.cs) change these methods so that each does the same work and skips
// only what has no effect; on every player:
//   - FsmExecutionStack keeps the executing FSMs in a FastStack instead of a Stack<Fsm>, whose Pop
//     asks RuntimeHelpers.IsReferenceOrContainsReferences<T>(), which the 2018 class library
//     answers by reflection (Type.GetTypeFromHandle) on every call. 2 PlayMaker pops per FSM a frame.
//   - Fsm.UpdateStateChanges ends each FSM's update by setting every state's loop count to 0
//     (the guard against an FSM entering states in an endless loop). Only Fsm.EnterState raises a
//     loop count, so it now marks the FSM, and the reset runs only for marked FSMs.
//   - Fsm.UpdateDelayedEvents clears and refills two lists every frame; with no delayed events and
//     the lists empty, it returns at once. A mark set where events are added (crowDelayedEvents)
//     saves even asking the lists' counts on FSMs that had none.
//   - BlobTrailSpawn.Spawn (a Blob's trail) starts with TrailReady: after the Blob dies its pooled
//     pieces are destroyed and Spawn threw a NullReferenceException every 0.01-0.1 s per dead Blob
//     (on the PS Vita a stack trace and a log line each, the largest allocation source in fights);
//     destroyed pieces leave the pool, and an empty pool spawns nothing, as the throw did. With
//     -crow-blob-trails off (Cuts.cs, the PS Vita's default) it spawns nothing at all.
//   - FullScreenColour.OnGUI (the game's fades: it walks every FSM's active state for actions with
//     an OnGUI, on every GUI event, and calls those that are Active) returns at once while none
//     between its OnEnter and OnExit is Active, and turns its component off until one enters (the game's only OnGUI in play, so
//     Unity's IMGUI pass stops too). The actions (CameraFadeIn, CameraFadeOut,
//     DrawFullscreenColor) report OnEnter and OnExit here (GuiEnter, GuiExit); an action whose FSM
//     stops without OnExit keeps the walk on, as the game has it, until its GameObject is destroyed.
// Flags:
//   -crow-script-cost game   keep the game's per-frame work (all but the FastStack, a type change),
//                            for A/B measurements on one build
//   -crow-script-cost-log    log each fade action's OnEnter and OnExit (CROW_SCRIPT_COST)
//   -crow-blob-trail game    keep the Blob trail's throw only (A/B of TrailReady)
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class ScriptCost
    {
        // Read by the patched methods (ldsfld): whether they may skip work.
        public static readonly bool Fast = !GameFlag();

        static bool GameFlag()
        {
            bool game = CrowFlags.Value(ProbeFiles.Args, "-crow-script-cost") == "game";
            if (game) UnityEngine.Debug.Log("CROW_SCRIPT_COST game: the game's per-frame PlayMaker and fade work");
            return game;
        }

        // BlobTrailSpawn.Spawn's first step (rewriter PatchBlobTrailSpawn): whether the trail's pool has a
        // piece left, its destroyed ones removed. -crow-blob-trail game (or -crow-script-cost game) keeps
        // the game's throw.
        public static bool TrailReady(List<UnityEngine.GameObject> pool)
        {
            if (Cuts.NoTrails) return false;  // -crow-blob-trails off (Cuts.cs): no new pieces
            if (!trailGuard || pool == null) return true;
            if (pool.Count > 0 && pool[0] != null) return true;
            pool.RemoveAll(destroyed);
            return pool.Count > 0;
        }

        static readonly Predicate<UnityEngine.GameObject> destroyed = piece => piece == null;
        static readonly bool trailGuard = Fast && CrowFlags.Value(ProbeFiles.Args, "-crow-blob-trail") != "game";

        // The fade actions between OnEnter and OnExit, with their FSM's GameObject (FsmStateAction.Owner).
        static readonly Dictionary<object, UnityEngine.Object> guiActions = new Dictionary<object, UnityEngine.Object>(new SameObject());
        static readonly List<object> gone = new List<object>();

        static readonly bool log = CrowFlags.Has(ProbeFiles.Args, "-crow-script-cost-log");

        public static void GuiEnter(object action, UnityEngine.Object owner)
        {
            guiActions[action] = owner;
            foreach (UnityEngine.Behaviour gui in sleeping)
                if (gui != null && !gui.enabled) gui.enabled = true;
            sleeping.Clear();
            if (log) UnityEngine.Debug.Log("CROW_SCRIPT_COST gui enter " + action.GetType().Name + " on " + (owner == null ? "?" : owner.name) + ": " + guiActions.Count + " active");
        }

        public static void GuiExit(object action)
        {
            bool removed = guiActions.Remove(action);
            if (log && removed) UnityEngine.Debug.Log("CROW_SCRIPT_COST gui exit " + action.GetType().Name + ": " + guiActions.Count + " active");
        }

        // Whether FullScreenColour.OnGUI can return at once: no fade action is between its OnEnter and
        // OnExit. An action whose GameObject was destroyed without OnExit no longer counts: its FSM
        // left PlayMakerFSM.FsmList, which is all FullScreenColour walks.
        // Idle, FullScreenColour (gui) also turns itself off, so Unity skips its IMGUI pass (the
        // GUI events, about 1% of the main thread at the PS Vita's speed) until GuiEnter turns it
        // back on, before the fade's first OnGUI.
        public static bool GuiIdle(UnityEngine.Behaviour gui)
        {
            if (!Fast || !NoGuiAction()) return false;
            if (gui != null && gui.enabled)
            {
                gui.enabled = false;
                sleeping.Add(gui);
            }
            return true;
        }

        static readonly List<UnityEngine.Behaviour> sleeping = new List<UnityEngine.Behaviour>();

        // Whether no fade action can draw: none is Active (FsmStateAction.Active, which FullScreenColour
        // requires) among those between OnEnter and OnExit. A fade that finished stays entered while
        // its state waits, but is no longer Active.
        static bool NoGuiAction()
        {
            if (guiActions.Count == 0) return true;
            bool active = false;
            foreach (KeyValuePair<object, UnityEngine.Object> pair in guiActions)
            {
                if (!ReferenceEquals(pair.Value, null) && pair.Value == null) gone.Add(pair.Key);
                else if (!(pair.Key is IGuiAction) || ((IGuiAction)pair.Key).CrowGuiActive()) active = true;
            }
            foreach (object action in gone) guiActions.Remove(action);
            gone.Clear();
            return !active;
        }

        sealed class SameObject : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b)
            {
                return ReferenceEquals(a, b);
            }

            public int GetHashCode(object o)
            {
                return RuntimeHelpers.GetHashCode(o);
            }
        }
    }

    // The fade actions' FsmStateAction.Active, which the rewriter adds to them (CrowCompat doesn't
    // reference PlayMaker).
    public interface IGuiAction
    {
        bool CrowGuiActive();
    }

    // Stack<T>'s members FsmExecutionStack uses, without its per-Pop reflection (see ScriptCost).
    public sealed class FastStack<T> where T : class
    {
        T[] items;
        int count;

        public FastStack()
            : this(16)
        {
        }

        public FastStack(int capacity)
        {
            items = new T[capacity > 0 ? capacity : 16];
        }

        public int Count
        {
            get { return count; }
        }

        public void Push(T item)
        {
            if (count == items.Length) Array.Resize(ref items, count * 2);
            items[count++] = item;
        }

        public T Pop()
        {
            if (count == 0) throw new InvalidOperationException("Stack empty.");
            T item = items[--count];
            items[count] = null;
            return item;
        }

        public T Peek()
        {
            if (count == 0) throw new InvalidOperationException("Stack empty.");
            return items[count - 1];
        }

        public void Clear()
        {
            Array.Clear(items, 0, count);
            count = 0;
        }

        public bool Contains(T item)
        {
            for (int i = 0; i < count; i++)
                if (ReferenceEquals(items[i], item)) return true;
            return false;
        }
    }
}
