using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// The enemies' polled chores, done once a frame for all of them in C# rather than by each FSM's own
// every-frame actions. An enemy's FSMs poll every frame: Pause/Cull measures the distance to the
// "visibility point" (freezing the enemy beyond 5 m), tests the pause, examine and inventory flags and
// reads spawn/kill's "local killed"; the movement FSMs measure their distance to the player; and so on.
// For each FSM state whose every-frame actions are all of the kinds below, those actions are switched
// off in the FSM and this manager runs the same code in their order, once a frame (Canvas.willRenderCanvases,
// after LateUpdate), sending the FSM the same events; the first event that switches the state ends the
// state's checks, as it ends them in PlayMaker. A state whose remaining actions are all done and whose
// FSM has no delayed events leaves its FSM out of PlayMaker's update altogether (Fsm.ManualUpdate) until
// the state changes (Fsm.StateChanged puts it back at once).
//   GetWorldDistance, FloatCompare, BoolAnyTrue, BoolNoneTrue, BoolTest, GetFsmBool, SetPosition and
//   SetRotation without lateUpdate, and LookAt (a LateUpdate action; the manager runs after LateUpdate).
//   SetPosition, SetRotation and LookAt write the transform only when it differs: a culled enemy's held
//   pose, or a part kept facing another part of the same enemy, no longer counts as a transform change
//   every frame.
// States with a FINISHED transition stay as they are (their every-frame actions keep them unfinished).
// What differs: the checks run after LateUpdate, not in Update (a transition up to a frame later), and
// the stored results of the checks after the first event are not written that frame.
//
//   -crow-enemy-chores on|off   on: the manager (default off); -crow-cycle takes chores=on|off
//
// HalfRate (below): the DOTween tweens that move an enemy's parts run at half rate while none of the
// enemy's renderers is drawn (off screen, culled by Pause/Cull with its model off, or not spawned yet:
// looping tweens go on moving switched-off parts): they move to
// DOTween's manual update, which gets two frames' time every other frame, so they end when they would
// have (within a frame) and FSMs waiting for them go on as before. Tweens on the enemy's own transform
// or on a rigidbody stay at full rate.
//   -crow-enemy-halfrate on|off  (default off); -crow-cycle takes halfrate=on|off
namespace CrowCompat
{
    static class EnemyChores
    {
        static bool on, hooked;
        static float scanAt;
        static readonly List<Machine> machines = new List<Machine>();
        static int liftedStates, liftedActions, skippedStates, frames;
        // The periodic line's breakdown: time per check kind, event sends and transform writes.
        static readonly string[] Kinds = { "GetWorldDistance", "FloatCompare", "BoolAnyTrue", "BoolNoneTrue", "BoolTest", "GetFsmBool", "SetPosition", "SetRotation", "LookAt" };
        static readonly long[] kindTicks = new long[Kinds.Length];
        static readonly int[] kindRuns = new int[Kinds.Length];
        static long sendTicks;
        static int sends, writes;
        static long ticks;
        static readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();

        internal static void Install(string[] args)
        {
            int index = Array.IndexOf(args, "-crow-enemy-chores");
            if (index >= 0 && index + 1 < args.Length) Set(args[index + 1]);
            index = Array.IndexOf(args, "-crow-enemy-halfrate");
            if (index >= 0 && index + 1 < args.Length) HalfRate.Set(args[index + 1]);
        }

        // -crow-cycle chores=on|off
        internal static bool Change(string key, string value)
        {
            if (key == "halfrate")
            {
                HalfRate.Set(value);
                return true;
            }
            if (key != "chores") return false;
            Set(value);
            return true;
        }

        static void Set(string value)
        {
            bool want = value == "on";
            if (want == on) return;
            on = want;
            if (on && !hooked)
            {
                hooked = true;
                Canvas.willRenderCanvases += Frame;
            }
            if (!on)
            {
                foreach (Machine machine in machines) machine.Restore();
                machines.Clear();
                seen.Clear();
                found.Clear();
            }
            scanAt = 0f;
            Debug.Log("CROW_ENEMY chores=" + value);
        }

        static void Frame()
        {
            if (!on) return;
            clock.Reset();
            clock.Start();
            if (Time.realtimeSinceStartup >= scanAt)
            {
                scanAt = Time.realtimeSinceStartup + 1f;
                try
                {
                    Scan();
                }
                catch (Exception exception)
                {
                    Debug.Log("CROW_ENEMY chores scan failed: " + exception);
                }
            }
            for (int i = machines.Count - 1; i >= 0; i--)
            {
                Machine machine = machines[i];
                if (machine.Component == null)
                {
                    machines.RemoveAt(i);
                    continue;
                }
                try
                {
                    machine.Tick();
                }
                catch (Exception exception)
                {
                    Debug.Log("CROW_ENEMY chores " + machine.Name + " failed, restored: " + exception);
                    machine.Restore();
                    machines.RemoveAt(i);
                }
            }
            clock.Stop();
            ticks += clock.ElapsedTicks;
            if (++frames % 600 == 0)
            {
                double us = 1e6 / System.Diagnostics.Stopwatch.Frequency / 600;
                var line = new System.Text.StringBuilder("CROW_ENEMY chores machines=").Append(machines.Count).Append(" states=").Append(liftedStates)
                    .Append(" actions=").Append(liftedActions).Append(" kept=").Append(skippedStates).Append(" manual=").Append(Manual())
                    .Append(" us/frame=").Append((ticks * us).ToString("0")).Append(" sends=").Append(sends / 600f).Append(" (").Append((sendTicks * us).ToString("0"))
                    .Append(" us) writes=").Append(writes / 600f).Append(" kinds:");
                for (int k = 0; k < Kinds.Length; k++)
                    if (kindRuns[k] > 0) line.Append(' ').Append(Kinds[k]).Append('=').Append(kindRuns[k] / 600f).Append('/').Append((kindTicks[k] * us).ToString("0")).Append("us");
                Debug.Log(line.ToString());
                ticks = sendTicks = 0;
                sends = writes = 0;
                Array.Clear(kindTicks, 0, kindTicks.Length);
                Array.Clear(kindRuns, 0, kindRuns.Length);
            }
        }

        static int Manual()
        {
            int count = 0;
            foreach (Machine machine in machines)
                if (machine.IsManual) count++;
            return count;
        }

        // ---------------------------------------------------------------- half rate

        static class HalfRate
        {
            static bool on, hooked, ready;
            static float scanAt, pendingTime, pendingUnscaled;
            static int frames;
            static long ticks, scanTicks;
            static Type tweenType;
            static FieldInfo targetField, updateTypeField, independentField;
            static PropertyInfo activeProperty;
            static MethodInfo playing, setUpdateType;
            static Action<float, float> manualUpdate;
            static object normal, manual;

            sealed class Enemy
            {
                public Transform Root;
                public Renderer[] Renderers;
                public bool Slow;
            }

            sealed class Tracked
            {
                public object Tween;
                public object Target;
                public Enemy Enemy;
                public bool Manual;
            }

            static readonly List<Enemy> enemies = new List<Enemy>();
            static readonly List<Tracked> tracked = new List<Tracked>();
            static readonly System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();

            internal static void Set(string value)
            {
                bool want = value == "on";
                if (want == on) return;
                if (!Ready())
                {
                    Debug.Log("CROW_ENEMY halfrate: no DOTween");
                    return;
                }
                on = want;
                if (on && !hooked)
                {
                    hooked = true;
                    Canvas.willRenderCanvases += Frame;
                }
                if (!on)
                {
                    foreach (Tracked t in tracked)
                        if (t.Manual && ReferenceEquals(targetField.GetValue(t.Tween), t.Target)) Switch(t, false);
                    tracked.Clear();
                    enemies.Clear();
                    byRoot.Clear();
                }
                scanAt = 0f;
                Debug.Log("CROW_ENEMY halfrate=" + value);
            }

            static bool Ready()
            {
                if (ready) return true;
                Type dotween = Type.GetType("DG.Tweening.DOTween, DOTween");
                tweenType = Type.GetType("DG.Tweening.Tween, DOTween");
                Type updateType = Type.GetType("DG.Tweening.UpdateType, DOTween");
                Type manager = Type.GetType("DG.Tweening.Core.TweenManager, DOTween");
                if (dotween == null || tweenType == null || updateType == null || manager == null) return false;
                targetField = tweenType.GetField("target", Fields);
                updateTypeField = tweenType.GetField("updateType", Fields);
                independentField = tweenType.GetField("isIndependentUpdate", Fields);
                activeProperty = tweenType.GetProperty("active");
                playing = dotween.GetMethod("PlayingTweens");
                setUpdateType = manager.GetMethod("SetUpdateType", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo update = dotween.GetMethod("ManualUpdate", new[] { typeof(float), typeof(float) });
                if (targetField == null || updateTypeField == null || independentField == null || activeProperty == null || playing == null || setUpdateType == null || update == null)
                    return false;
                manualUpdate = (Action<float, float>)Delegate.CreateDelegate(typeof(Action<float, float>), update);
                normal = Enum.ToObject(updateType, 0);
                manual = Enum.ToObject(updateType, 3);
                ready = true;
                return true;
            }

            static void Frame()
            {
                if (!on) return;
                watch.Reset();
                watch.Start();
                if (Time.realtimeSinceStartup >= scanAt)
                {
                    scanAt = Time.realtimeSinceStartup + 1f;
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        Scan();
                    }
                    catch (Exception exception)
                    {
                        Debug.Log("CROW_ENEMY halfrate scan failed: " + exception);
                    }
                    scanTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                }
                foreach (Enemy enemy in enemies)
                {
                    bool seen = false;
                    if (enemy.Root != null)
                        foreach (Renderer renderer in enemy.Renderers)
                            if (renderer != null && renderer.isVisible)
                            {
                                seen = true;
                                break;
                            }
                    enemy.Slow = !seen;
                }
                for (int i = tracked.Count - 1; i >= 0; i--)
                {
                    Tracked t = tracked[i];
                    bool want = t.Enemy.Slow && t.Enemy.Root != null;
                    if (want == t.Manual) continue;
                    // DOTween recycles its tweens: switch only one that still moves what it moved.
                    if (!ReferenceEquals(targetField.GetValue(t.Tween), t.Target) || !(bool)activeProperty.GetValue(t.Tween, null))
                    {
                        tracked.RemoveAt(i);
                        continue;
                    }
                    Switch(t, want);
                }
                // The manual tweens: two frames' time every other frame.
                pendingTime += Time.deltaTime;
                pendingUnscaled += Time.unscaledDeltaTime;
                if ((Time.frameCount & 1) == 0)
                {
                    manualUpdate(pendingTime, pendingUnscaled);
                    pendingTime = pendingUnscaled = 0f;
                }
                watch.Stop();
                ticks += watch.ElapsedTicks;
                if (++frames % 600 == 0)
                {
                    int slow = 0, manualCount = 0;
                    foreach (Enemy enemy in enemies)
                        if (enemy.Slow) slow++;
                    foreach (Tracked t in tracked)
                        if (t.Manual) manualCount++;
                    Debug.Log("CROW_ENEMY halfrate enemies=" + enemies.Count + " slow=" + slow + " tweens=" + tracked.Count + " manual=" + manualCount +
                              " us/frame=" + (ticks * 1e6 / System.Diagnostics.Stopwatch.Frequency / 600).ToString("0") +
                              " (scans " + (scanTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / 600).ToString("0") + ")");
                    ticks = scanTicks = 0;
                }
            }

            static void Switch(Tracked t, bool toManual)
            {
                setUpdateType.Invoke(null, new[] { t.Tween, toManual ? manual : normal, independentField.GetValue(t.Tween) });
                t.Manual = toManual;
            }

            static float enemiesAt;
            static readonly Dictionary<Transform, Enemy> byRoot = new Dictionary<Transform, Enemy>();

            static void Scan()
            {
                // The enemies (a walk through the whole scene) every 5 s, their tweens every scan.
                Discover();
                // Enemies whose FSMs are all off (culled, not spawned) aren't in PlayMaker's list: the
                // scene's every 10 s.
                bool refresh = Time.realtimeSinceStartup >= enemiesAt;
                if (refresh)
                {
                    enemiesAt = Time.realtimeSinceStartup + 10f;
                    for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                    {
                        UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                        if (!scene.isLoaded) continue;
                        foreach (GameObject top in scene.GetRootGameObjects())
                            foreach (Rigidbody body in top.GetComponentsInChildren<Rigidbody>(true))
                                if (body.name.StartsWith("Enemy ", StringComparison.Ordinal)) roots.Add(body.transform);
                    }
                    roots.RemoveWhere(root => root == null);
                }
                if (refresh || enemies.Count != roots.Count)
                {
                    enemies.Clear();
                    foreach (Transform root in roots)
                    {
                        Enemy enemy;
                        if (!byRoot.TryGetValue(root, out enemy))
                        {
                            enemy = new Enemy { Root = root };
                            enemy.Renderers = root.GetComponentsInChildren<Renderer>(true);
                        }
                        else if (refresh) enemy.Renderers = root.GetComponentsInChildren<Renderer>(true);
                        enemies.Add(enemy);
                    }
                    byRoot.Clear();
                    foreach (Enemy enemy in enemies) byRoot[enemy.Root] = enemy;
                }
                var old = new Dictionary<object, Tracked>();
                foreach (Tracked t in tracked) old[t.Tween] = t;
                tracked.Clear();
                var list = playing.Invoke(null, new object[] { null }) as IList;
                if (list == null) return;
                foreach (object tween in list)
                {
                    var target = targetField.GetValue(tween) as Transform;
                    if (target == null || target.GetComponent<Rigidbody>() != null) continue;
                    Enemy enemy = null;
                    for (Transform t = target.parent; t != null && enemy == null; t = t.parent) byRoot.TryGetValue(t, out enemy);
                    if (enemy == null) continue;  // not an enemy's part (or the enemy's own transform)
                    Tracked entry;
                    if (!old.TryGetValue(tween, out entry) || !ReferenceEquals(entry.Target, target)) entry = new Tracked { Tween = tween, Target = target };
                    entry.Enemy = enemy;
                    entry.Manual = Convert.ToInt32(updateTypeField.GetValue(tween)) == 3;
                    tracked.Add(entry);
                }
            }
        }

        // ---------------------------------------------------------------- reflection

        static Type fsmComponentType, fsmType, eventType;
        static PropertyInfo fsmProperty, startedProperty;
        static MethodInfo eventMethod, getGameObjectFsm;

        static bool Types()
        {
            if (fsmComponentType != null) return true;
            fsmComponentType = Type.GetType("PlayMakerFSM, PlayMaker");
            if (fsmComponentType == null) return false;
            fsmProperty = fsmComponentType.GetProperty("Fsm");
            fsmType = fsmProperty.PropertyType;
            startedProperty = fsmType.GetProperty("Started");
            eventType = Type.GetType("HutongGames.PlayMaker.FsmEvent, PlayMaker");
            eventMethod = fsmType.GetMethod("Event", new[] { eventType });
            Type helpers = Type.GetType("HutongGames.PlayMaker.ActionHelpers, Assembly-CSharp");
            getGameObjectFsm = helpers == null ? null : helpers.GetMethod("GetGameObjectFsm", BindingFlags.Public | BindingFlags.Static);
            return eventMethod != null;
        }

        const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        static object Field(object target, string name)
        {
            if (target == null) return null;
            FieldInfo field = target.GetType().GetField(name, Fields);
            return field == null ? null : field.GetValue(target);
        }

        static object Property(object target, string name)
        {
            if (target == null) return null;
            PropertyInfo property = target.GetType().GetProperty(name);
            return property == null ? null : property.GetValue(target, null);
        }

        static Func<T> Getter<T>(object target, string name)
        {
            if (target == null) return null;
            PropertyInfo property = target.GetType().GetProperty(name);
            return property == null ? null : (Func<T>)Delegate.CreateDelegate(typeof(Func<T>), target, property.GetGetMethod());
        }

        static Action<T> Setter<T>(object target, string name)
        {
            if (target == null) return null;
            PropertyInfo property = target.GetType().GetProperty(name);
            return property == null || property.GetSetMethod() == null ? null : (Action<T>)Delegate.CreateDelegate(typeof(Action<T>), target, property.GetSetMethod());
        }

        // A NamedVariable that stands for nothing (UseVariable with no name); fixed at run time.
        static bool IsNone(object variable)
        {
            return variable == null || (bool)Property(variable, "IsNone");
        }

        static bool Flag(object action, string name)
        {
            object value = Field(action, name);
            return value is bool && (bool)value;
        }

        // ---------------------------------------------------------------- scan

        // The enemies' FSMs, found through PlayMaker's own list of enabled FSMs (no walk through the
        // scene), each looked at once: an enemy's (below an "Enemy <Type>" rigidbody) goes to found until
        // it has started, and its enemy to roots.
        static IList fsmList;
        static readonly HashSet<Component> seen = new HashSet<Component>();
        static readonly List<Component> found = new List<Component>();
        static readonly HashSet<Transform> roots = new HashSet<Transform>();

        static void Discover()
        {
            if (!Types()) return;
            if (fsmList == null)
            {
                PropertyInfo list = fsmComponentType.GetProperty("FsmList", BindingFlags.Public | BindingFlags.Static);
                fsmList = list == null ? null : list.GetValue(null, null) as IList;
                if (fsmList == null) return;
            }
            for (int i = 0; i < fsmList.Count; i++)
            {
                var component = fsmList[i] as Component;
                if (component == null || !seen.Add(component)) continue;
                Transform root = EnemyRoot(component.transform);
                if (root == null) continue;
                roots.Add(root);
                found.Add(component);
            }
            roots.RemoveWhere(root => root == null);
        }

        static Transform EnemyRoot(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.name.StartsWith("Enemy ", StringComparison.Ordinal) && t.GetComponent<Rigidbody>() != null) return t;
            return null;
        }

        static int scans;

        static void Scan()
        {
            if (!Types())
            {
                if (scans++ == 0) Debug.Log("CROW_ENEMY chores: no PlayMaker types (" + fsmComponentType + ", " + eventType + ", " + eventMethod + ")");
                return;
            }
            Discover();
            int waiting = 0, made = 0, none = 0;
            for (int i = found.Count - 1; i >= 0; i--)
            {
                Component component = found[i];
                if (component == null)
                {
                    found.RemoveAt(i);
                    continue;
                }
                object fsm = fsmProperty.GetValue(component, null);
                if (fsm == null || !(bool)startedProperty.GetValue(fsm, null))
                {
                    waiting++;
                    continue;  // its actions aren't set up yet
                }
                found.RemoveAt(i);
                Machine machine = Machine.Make(component, fsm);
                if (machine != null)
                {
                    machines.Add(machine);
                    made++;
                }
                else none++;
            }
            if (made + none > 0)
                Debug.Log("CROW_ENEMY chores scan: enemies=" + roots.Count + " waiting=" + waiting + " lifted=" + made + " nothing=" + none +
                          " machines=" + machines.Count + " states=" + liftedStates + " actions=" + liftedActions + " kept=" + skippedStates);
        }

        // ---------------------------------------------------------------- machines

        // One FSM: its lifted states (by name) and the checks of each, in action order.
        sealed class Machine
        {
            public Component Component;
            public string Name;
            object fsm;
            GameObject owner;
            Func<string> activeState;
            Action<bool> manual;
            Func<bool> getManual;
            ICollection delayed;
            readonly Dictionary<string, Lifted> states = new Dictionary<string, Lifted>();
            readonly HashSet<string> globalEvents = new HashSet<string>();
            Lifted current;
            readonly object[] one = new object[1];
            bool isManual;
            Delegate hook;
            FieldInfo stateChanged;

            public bool IsManual
            {
                get { return isManual; }
            }

            sealed class Lifted
            {
                public object State;
                public readonly List<Check> Checks = new List<Check>();
                public readonly List<object> Actions = new List<object>();
                public IList Active;  // FsmState.ActiveActions
                public Func<bool> Entered;  // FsmState.Active
                public readonly HashSet<string> Events = new HashSet<string>();  // its transitions'
                public bool OnEvent;  // FsmState.HandlesOnEvent: an action of it takes events
            }

            public static Machine Make(Component component, object fsm)
            {
                var states = Property(fsm, "States") as Array;
                if (states == null) return null;
                var globals = Property(fsm, "GlobalTransitions") as Array;
                var globalEvents = new HashSet<string>();
                if (globals != null)
                    foreach (object transition in globals)
                    {
                        var name = (string)Property(transition, "EventName");
                        if (name == "FINISHED") return null;
                        if (name != null) globalEvents.Add(name);
                    }
                var machine = new Machine
                {
                    Component = component, Name = Perf.Path(component.transform, 3), fsm = fsm, owner = component.gameObject,
                    activeState = Getter<string>(fsm, "ActiveStateName"), manual = Setter<bool>(fsm, "ManualUpdate"),
                    getManual = Getter<bool>(fsm, "ManualUpdate"), delayed = Property(fsm, "DelayedEvents") as ICollection,
                };
                machine.globalEvents.UnionWith(globalEvents);
                foreach (object state in states)
                {
                    Lifted lifted = machine.Lift(state);
                    if (lifted != null) machine.states[(string)Property(state, "Name")] = lifted;
                }
                if (machine.states.Count == 0) return null;
                // Lifted actions off: in the state that is active now, out of its active actions too.
                string current = machine.activeState();
                foreach (KeyValuePair<string, Lifted> pair in machine.states)
                {
                    foreach (object action in pair.Value.Actions)
                    {
                        action.GetType().GetProperty("Enabled").SetValue(action, false, null);
                        liftedActions++;
                    }
                    pair.Value.Active = Property(pair.Value.State, "ActiveActions") as IList;
                    pair.Value.Entered = Getter<bool>(pair.Value.State, "Active");
                    if (pair.Key == current && pair.Value.Active != null)
                        foreach (object action in pair.Value.Actions) pair.Value.Active.Remove(action);
                    liftedStates++;
                }
                machine.Hook();
                return machine;
            }

            // Fsm.StateChanged: back in PlayMaker's update whenever its state changes (the manager takes
            // it out again at the end of the frame if the new state is a lifted one with nothing left to do).
            void Hook()
            {
                stateChanged = fsm.GetType().GetField("StateChanged");
                if (stateChanged == null) return;
                try
                {
                    Type type = stateChanged.FieldType;
                    hook = Delegate.CreateDelegate(type, this, typeof(Machine).GetMethod("OnStateChanged", BindingFlags.NonPublic | BindingFlags.Instance));
                    stateChanged.SetValue(fsm, Delegate.Combine((Delegate)stateChanged.GetValue(fsm), hook));
                }
                catch (Exception exception)
                {
                    hook = null;
                    Debug.Log("CROW_ENEMY chores: no StateChanged hook (" + exception.Message + "); FSMs stay in PlayMaker's update");
                }
            }

            void OnStateChanged(object state)
            {
                if (isManual)
                {
                    manual(false);
                    isManual = false;
                }
            }

            Lifted Lift(object state)
            {
                var transitions = Property(state, "Transitions") as Array;
                var events = new HashSet<string>();
                if (transitions != null)
                    foreach (object transition in transitions)
                    {
                        var name = (string)Property(transition, "EventName");
                        if (name == "FINISHED")
                        {
                            skippedStates++;
                            return null;
                        }
                        if (name != null) events.Add(name);
                    }
                var actions = Property(state, "Actions") as Array;
                if (actions == null) return null;
                var lifted = new Lifted { State = state };
                lifted.Events.UnionWith(events);
                FieldInfo handles = state.GetType().GetField("HandlesOnEvent");
                lifted.OnEvent = handles != null && (bool)handles.GetValue(state);
                foreach (object action in actions)
                {
                    if (!(bool)Property(action, "Enabled")) continue;
                    string kind = action.GetType().Name;
                    bool everyFrame = Flag(action, "everyFrame");
                    if (!everyFrame) continue;
                    Check check = Check.Make(kind, action, this);
                    if (check == null)
                    {
                        skippedStates++;
                        return null;  // an every-frame action of another kind: the state stays as it is
                    }
                    lifted.Checks.Add(check);
                    lifted.Actions.Add(action);
                }
                return lifted.Checks.Count == 0 ? null : lifted;
            }

            public GameObject Owner
            {
                get { return owner; }
            }

            static readonly Dictionary<object, string> eventNames = new Dictionary<object, string>();

            // Sends an event as an action would; true when the state changed. One that the state has no
            // transition for (nor its FSM a global one), in a state with no action taking events, would do
            // nothing: not sent.
            public bool Send(object fsmEvent, string before)
            {
                if (fsmEvent == null) return false;
                string name;
                if (!eventNames.TryGetValue(fsmEvent, out name)) eventNames[fsmEvent] = name = Property(fsmEvent, "Name") as string ?? "";
                if (name.Length == 0) return false;
                if (current != null && !current.OnEvent && !current.Events.Contains(name) && !globalEvents.Contains(name)) return false;
                one[0] = fsmEvent;
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                eventMethod.Invoke(fsm, one);
                sendTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
                sends++;
                one[0] = null;
                return activeState() != before;
            }

            public void Tick()
            {
                if (!((Behaviour)Component).isActiveAndEnabled) return;
                string name = activeState();
                Lifted lifted;
                if (name == null || !states.TryGetValue(name, out lifted))
                {
                    if (isManual)
                    {
                        manual(false);
                        isManual = false;
                    }
                    return;
                }
                if (!lifted.Entered()) return;
                current = lifted;
                foreach (Check check in lifted.Checks)
                {
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool changed = check.Run(name);
                    kindTicks[check.Kind] += System.Diagnostics.Stopwatch.GetTimestamp() - start;
                    kindRuns[check.Kind]++;
                    if (changed) return;  // the state changed (StateChanged put the FSM back in the update)
                }
                bool idle = hook != null && (lifted.Active == null || lifted.Active.Count == 0) && (delayed == null || delayed.Count == 0);
                if (idle != isManual)
                {
                    manual(idle);
                    isManual = idle;
                }
            }

            public void Restore()
            {
                if (fsm == null) return;
                if (isManual) manual(false);
                isManual = false;
                if (hook != null && stateChanged != null) stateChanged.SetValue(fsm, Delegate.Remove((Delegate)stateChanged.GetValue(fsm), hook));
                string current = activeState();
                foreach (KeyValuePair<string, Lifted> pair in states)
                {
                    foreach (object action in pair.Value.Actions) action.GetType().GetProperty("Enabled").SetValue(action, true, null);
                    if (pair.Key != current || pair.Value.Active == null) continue;
                    // Back among the active actions of the state it is in, in the state's order.
                    var order = (Array)Property(pair.Value.State, "Actions");
                    var active = new List<object>();
                    foreach (object action in order)
                    {
                        bool lifted = pair.Value.Actions.Contains(action);
                        if (lifted)
                        {
                            action.GetType().GetMethod("Init").Invoke(action, new[] { pair.Value.State });
                            action.GetType().GetProperty("Active").SetValue(action, true, null);
                            action.GetType().GetProperty("Finished").SetValue(action, false, null);
                        }
                        if (lifted || pair.Value.Active.Contains(action)) active.Add(action);
                    }
                    pair.Value.Active.Clear();
                    foreach (object action in active) pair.Value.Active.Add(action);
                }
                fsm = null;
            }
        }

        // ---------------------------------------------------------------- checks

        // An FsmOwnerDefault: the FSM's own GameObject or a GameObject variable's.
        sealed class Target
        {
            bool useOwner;
            Func<GameObject> value;
            GameObject owner;

            public static Target Make(object ownerDefault, Machine machine)
            {
                if (ownerDefault == null) return null;
                var target = new Target { owner = machine.Owner };
                target.useOwner = Convert.ToInt32(Property(ownerDefault, "OwnerOption")) == 0;
                if (!target.useOwner) target.value = Getter<GameObject>(Property(ownerDefault, "GameObject"), "Value");
                return target;
            }

            public GameObject Get()
            {
                return useOwner ? owner : value == null ? null : value();
            }
        }

        abstract class Check
        {
            protected Machine machine;
            public int Kind;

            // True when the FSM's state changed.
            public abstract bool Run(string state);

            public static Check Make(string kind, object action, Machine machine)
            {
                Check check;
                switch (kind)
                {
                    case "GetWorldDistance":
                        check = new Distance(action, machine);
                        break;
                    case "FloatCompare":
                        check = new Compare(action);
                        break;
                    case "BoolAnyTrue":
                    case "BoolNoneTrue":
                        check = new AnyNone(action, kind == "BoolAnyTrue");
                        break;
                    case "BoolTest":
                        check = new Test(action);
                        break;
                    case "GetFsmBool":
                        check = OtherBool.Make(action, machine);
                        break;
                    case "SetPosition":
                        check = Flag(action, "lateUpdate") ? null : new Position(action, machine);
                        break;
                    case "SetRotation":
                        check = Flag(action, "lateUpdate") ? null : new Rotation(action, machine);
                        break;
                    case "LookAt":
                        check = Look.Make(action, machine);
                        break;
                    default:
                        return null;
                }
                if (check != null)
                {
                    check.machine = machine;
                    check.Kind = Array.IndexOf(Kinds, kind);
                }
                return check;
            }
        }

        sealed class Distance : Check
        {
            readonly Target from;
            readonly Func<GameObject> to;
            readonly Action<float> store;
            readonly bool x, y, z;

            public Distance(object action, Machine machine)
            {
                from = Target.Make(Field(action, "gameObject"), machine);
                to = Getter<GameObject>(Field(action, "target"), "Value");
                object result = Field(action, "storeResult");
                store = result == null ? null : Setter<float>(result, "Value");
                x = Flag(action, "ignoreX");
                y = Flag(action, "ignoreY");
                z = Flag(action, "ignoreZ");
            }

            public override bool Run(string state)
            {
                GameObject a = from == null ? null : from.Get();
                GameObject b = to == null ? null : to();
                if (a == null || b == null || store == null) return false;
                if (!x && !y && !z)
                {
                    store(Vector3.Distance(a.transform.position, b.transform.position));
                    return false;
                }
                Vector3 vector = b.transform.position - a.transform.position;
                if (x) vector.x = 0f;
                if (y) vector.y = 0f;
                if (z) vector.z = 0f;
                store(vector.magnitude);
                return false;
            }
        }

        sealed class Compare : Check
        {
            readonly Func<float> a, b, tolerance;
            readonly object equal, less, greater;

            public Compare(object action)
            {
                a = Getter<float>(Field(action, "float1"), "Value");
                b = Getter<float>(Field(action, "float2"), "Value");
                tolerance = Getter<float>(Field(action, "tolerance"), "Value");
                equal = Field(action, "equal");
                less = Field(action, "lessThan");
                greater = Field(action, "greaterThan");
            }

            public override bool Run(string state)
            {
                float one = a(), two = b();
                if (Mathf.Abs(one - two) <= tolerance()) return machine.Send(equal, state);
                if (one < two) return machine.Send(less, state);
                if (one > two) return machine.Send(greater, state);
                return false;
            }
        }

        sealed class AnyNone : Check
        {
            readonly Func<bool>[] bools;
            readonly object send;
            readonly Action<bool> store;
            readonly bool any;

            public AnyNone(object action, bool any)
            {
                this.any = any;
                var variables = Field(action, "boolVariables") as Array ?? new object[0];
                bools = new Func<bool>[variables.Length];
                for (int i = 0; i < bools.Length; i++) bools[i] = Getter<bool>(variables.GetValue(i), "Value");
                send = Field(action, "sendEvent");
                object result = Field(action, "storeResult");
                store = result == null ? null : Setter<bool>(result, "Value");
            }

            public override bool Run(string state)
            {
                if (bools.Length == 0) return false;
                bool found = false;
                for (int i = 0; i < bools.Length; i++)
                    if (bools[i]())
                    {
                        found = true;
                        break;
                    }
                if (any)
                {
                    if (store != null) store(found);
                    return found && machine.Send(send, state);
                }
                if (store != null) store(!found);
                return !found && machine.Send(send, state);
            }
        }

        sealed class Test : Check
        {
            readonly Func<bool> value;
            readonly object yes, no;

            public Test(object action)
            {
                value = Getter<bool>(Field(action, "boolVariable"), "Value");
                yes = Field(action, "isTrue");
                no = Field(action, "isFalse");
            }

            public override bool Run(string state)
            {
                return machine.Send(value() ? yes : no, state);
            }
        }

        // GetFsmBool: another FSM's bool, found again when its GameObject or FSM name changes.
        sealed class OtherBool : Check
        {
            Target target;
            Func<string> fsmName, variableName;
            Action<bool> store;
            GameObject lastObject;
            string lastFsm, lastVariable;
            Func<bool> value;

            public static Check Make(object action, Machine machine)
            {
                if (getGameObjectFsm == null) return null;
                object result = Field(action, "storeValue");
                return new OtherBool
                {
                    target = Target.Make(Field(action, "gameObject"), machine), fsmName = Getter<string>(Field(action, "fsmName"), "Value"),
                    variableName = Getter<string>(Field(action, "variableName"), "Value"), store = result == null ? null : Setter<bool>(result, "Value"),
                };
            }

            public override bool Run(string state)
            {
                if (store == null || target == null) return false;
                GameObject go = target.Get();
                if (go == null) return false;
                string name = fsmName(), variable = variableName();
                if (go != lastObject || name != lastFsm || variable != lastVariable)
                {
                    lastObject = go;
                    lastFsm = name;
                    lastVariable = variable;
                    value = null;
                    var component = getGameObjectFsm.Invoke(null, new object[] { go, name }) as Component;
                    if (component != null)
                    {
                        object variables = Property(component, "FsmVariables");
                        object found = variables.GetType().GetMethod("GetVariable", new[] { typeof(string) }).Invoke(variables, new object[] { variable });
                        object fsmBool = variables.GetType().GetMethod("GetFsmBool", new[] { typeof(string) }).Invoke(variables, new object[] { variable });
                        // A missing or other-typed variable: GetFsmBool makes a new FsmBool each time (false).
                        value = ReferenceEquals(found, fsmBool) ? Getter<bool>(fsmBool, "Value") : () => false;
                    }
                }
                if (value != null) store(value());
                return false;
            }
        }

        // SetPosition (not lateUpdate): written only when the transform differs.
        sealed class Position : Check
        {
            readonly Target target;
            readonly Func<Vector3> vector;
            readonly Func<float> x, y, z;
            readonly bool world;

            public Position(object action, Machine machine)
            {
                target = Target.Make(Field(action, "gameObject"), machine);
                object v = Field(action, "vector");
                vector = IsNone(v) ? null : Getter<Vector3>(v, "Value");
                x = Axis(Field(action, "x"));
                y = Axis(Field(action, "y"));
                z = Axis(Field(action, "z"));
                world = Convert.ToInt32(Field(action, "space")) == (int)Space.World;
            }

            internal static Func<float> Axis(object variable)
            {
                return IsNone(variable) ? null : Getter<float>(variable, "Value");
            }

            public override bool Run(string state)
            {
                GameObject go = target == null ? null : target.Get();
                if (go == null) return false;
                Transform transform = go.transform;
                Vector3 now = world ? transform.position : transform.localPosition;
                Vector3 value = vector != null ? vector() : now;
                if (x != null) value.x = x();
                if (y != null) value.y = y();
                if (z != null) value.z = z();
                if (Same(value.x, now.x) && Same(value.y, now.y) && Same(value.z, now.z)) return false;
                writes++;
                if (world) transform.position = value;
                else transform.localPosition = value;
                return false;
            }
        }

        // The same to well below what moves anything on screen (a world position read back through its
        // parents differs from the one written in the last bits).
        static bool Same(float a, float b)
        {
            return Math.Abs(a - b) <= 1e-5f * Math.Max(1f, Math.Abs(a));
        }

        // LookAt: Transform.LookAt only when the rotation it would set differs (debug lines left out).
        sealed class Look : Check
        {
            Target target;
            Func<GameObject> other;
            Func<Vector3> position, up;
            Func<bool> keepVertical;

            public static Check Make(object action, Machine machine)
            {
                object targetPosition = Field(action, "targetPosition"), upVector = Field(action, "upVector");
                return new Look
                {
                    target = Target.Make(Field(action, "gameObject"), machine), other = Getter<GameObject>(Field(action, "targetObject"), "Value"),
                    position = IsNone(targetPosition) ? null : Getter<Vector3>(targetPosition, "Value"), up = IsNone(upVector) ? null : Getter<Vector3>(upVector, "Value"),
                    keepVertical = Getter<bool>(Field(action, "keepVertical"), "Value"),
                };
            }

            public override bool Run(string state)
            {
                GameObject go = target == null ? null : target.Get();
                if (go == null) return false;
                GameObject goal = other == null ? null : other();
                if (goal == null && position == null) return false;
                Vector3 at = goal != null ? (position != null ? goal.transform.TransformPoint(position()) : goal.transform.position) : position();
                Transform transform = go.transform;
                Vector3 from = transform.position;
                if (keepVertical != null && keepVertical()) at.y = from.y;
                Vector3 upward = up == null ? Vector3.up : up();
                Vector3 direction = at - from;
                if (direction != Vector3.zero)
                {
                    Quaternion want = Quaternion.LookRotation(direction, upward), now = transform.rotation;
                    if (SameRotation(want, now)) return false;
                }
                writes++;
                transform.LookAt(at, upward);
                return false;
            }
        }

        // The same rotation (either sign) to within about 0.05 degrees.
        static bool SameRotation(Quaternion a, Quaternion b)
        {
            return Math.Abs(Quaternion.Dot(a, b)) >= 1f - 2e-7f;
        }

        // SetRotation (not lateUpdate): written only when the rotation differs.
        sealed class Rotation : Check
        {
            readonly Target target;
            readonly Func<Quaternion> quaternion;
            readonly Func<Vector3> vector;
            readonly Func<float> x, y, z;
            readonly bool self;

            public Rotation(object action, Machine machine)
            {
                target = Target.Make(Field(action, "gameObject"), machine);
                object q = Field(action, "quaternion");
                quaternion = IsNone(q) ? null : Getter<Quaternion>(q, "Value");
                object v = Field(action, "vector");
                vector = IsNone(v) ? null : Getter<Vector3>(v, "Value");
                x = Position.Axis(Field(action, "xAngle"));
                y = Position.Axis(Field(action, "yAngle"));
                z = Position.Axis(Field(action, "zAngle"));
                self = Convert.ToInt32(Field(action, "space")) == (int)Space.Self;
            }

            public override bool Run(string state)
            {
                GameObject go = target == null ? null : target.Get();
                if (go == null) return false;
                Transform transform = go.transform;
                Vector3 value = quaternion != null ? quaternion().eulerAngles : vector != null ? vector() : self ? transform.localEulerAngles : transform.eulerAngles;
                if (x != null) value.x = x();
                if (y != null) value.y = y();
                if (z != null) value.z = z();
                Quaternion want = Quaternion.Euler(value), now = self ? transform.localRotation : transform.rotation;
                if (SameRotation(want, now)) return false;
                writes++;
                if (self) transform.localEulerAngles = value;
                else transform.eulerAngles = value;
                return false;
            }
        }
    }
}
