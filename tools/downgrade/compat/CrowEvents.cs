using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// CrowEvents: the engine events every module hangs its work on, each handler in a try/catch of its own (and timed
// with -crow-hook-cost, which Diag's Help lists); Turn, the periodic scans' one-a-frame turns.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    // The engine events CrowCompat's modules hang their work on: Canvas.willRenderCanvases (every frame),
    // Application.onBeforeRender (every frame), SceneManager.sceneLoaded and sceneUnloaded (every load and
    // unload), and the cameras' Camera.onPreCull, onPreRender and onPostRender (every camera's render). Each is
    // one multicast delegate that Unity 2018.2 invokes with no try/catch, so a handler that throws skips every
    // handler after it, for that frame, load or render: 26 CrowCompat handlers share willRenderCanvases, MaraScene's
    // Poll and ScenePrefetch's Request among them; the gap's drain is the last onBeforeRender handler (hooked at the
    // first area change); SceneGap.Arrived (the area made active: its fog) comes after Quality's, Perf's and VitaLit's
    // load handlers; NearPlane's fog put back and Cameras.AfterRender (the skipped render-texture cameras on again)
    // share onPostRender; EnemySplit.Purge shares sceneUnloaded with up to ten others.
    // One module failing every frame would hold the player on a black screen with an exception a frame in the log.
    // So every module subscribes to those here: each handler runs in its own try/catch, a failure is logged with the
    // handler's name (CROW_HOOK_FAIL: the first with its stack, then a count every 1000th) and the handlers after
    // it run. Each is subscribed where it was, so the order is the same (21 Oct; the camera callbacks and
    // sceneUnloaded since the second round that day). Left raw: activeSceneChanged (the load trace's one handler).
    // -crow-hook-cost [s] (22 Oct; off by default, and then the handlers are subscribed exactly as without it):
    // each handler timed (two Stopwatch reads a call), and every s seconds (default 10) a CROW_HOOKS line: the
    // frames, the handlers' total, and per handler by name, the most first, its ms a frame on average over the
    // window, its ms in its worst frame and its calls (name=mean/worst/calls; handlers of the same name add up).
    // A sceneLoaded handler's worst frame is its load's. Not in it: MonoBehaviours' own Updates (Diag, Memory...)
    // and the rewriter's per-call hooks.
    // Turn: the periodic scans (a FindObjectsOfType or the like every few seconds) take turns, at most one a
    // frame (22 Oct): their timers start from load-relative times, so several came due in the same frame for a
    // whole session (VitaLit's light list, FixedStep's bodies, FilterGate's filters, Draws' casters, IdleAudio's
    // sources, Cameras' render-texture cameras, Rewired's joysticks). A scan that is due waits for a later frame
    // without one (a few frames at most; VitaLit's list, for its next selection, 0.25 s); the first after a load runs
    // at once (urgent) and takes the frame's turn.
    public static class CrowEvents
    {
        static readonly Dictionary<string, int> failures = new Dictionary<string, int>();
        // The wrapper of each willRenderCanvases handler, for CanvasesOff (Delegate equality: method and target).
        static readonly Dictionary<Action, Canvas.WillRenderCanvases> canvases = new Dictionary<Action, Canvas.WillRenderCanvases>();
        static int turnFrame = -1;

        // Whether a periodic scan that is due runs in this frame: urgent, or no other has run in it.
        internal static bool Turn(bool urgent = false)
        {
            int frame = Time.frameCount;
            if (!urgent && frame == turnFrame) return false;
            turnFrame = frame;
            return true;
        }

        internal static void Canvases(string name, Action handler)
        {
            Canvas.WillRenderCanvases wrapped;
            if (!canvases.TryGetValue(handler, out wrapped))
            {
                Action timed = Timed(name, handler);
                canvases[handler] = wrapped = () =>
                {
                    try
                    {
                        timed();
                    }
                    catch (Exception exception)
                    {
                        Fail(name, exception);
                    }
                };
            }
            Canvas.willRenderCanvases += wrapped;
        }

        internal static void CanvasesOff(Action handler)
        {
            Canvas.WillRenderCanvases wrapped;
            if (canvases.TryGetValue(handler, out wrapped)) Canvas.willRenderCanvases -= wrapped;
        }

        internal static void BeforeRender(string name, Action handler)
        {
            handler = Timed(name, handler);
            Application.onBeforeRender += () =>
            {
                try
                {
                    handler();
                }
                catch (Exception exception)
                {
                    Fail(name, exception);
                }
            };
        }

        internal static void Loaded(string name, Action<Scene, LoadSceneMode> handler)
        {
            handler = Timed(name, handler);
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                try
                {
                    handler(scene, mode);
                }
                catch (Exception exception)
                {
                    Fail(name, exception);
                }
            };
        }

        internal static void Unloaded(string name, Action<Scene> handler)
        {
            handler = Timed(name, handler);
            SceneManager.sceneUnloaded += scene =>
            {
                try
                {
                    handler(scene);
                }
                catch (Exception exception)
                {
                    Fail(name, exception);
                }
            };
        }

        internal static void PreCull(string name, Camera.CameraCallback handler)
        {
            Camera.onPreCull += Guarded(name, handler);
        }

        internal static void PreRender(string name, Camera.CameraCallback handler)
        {
            Camera.onPreRender += Guarded(name, handler);
        }

        internal static void PostRender(string name, Camera.CameraCallback handler)
        {
            Camera.onPostRender += Guarded(name, handler);
        }

        static Camera.CameraCallback Guarded(string name, Camera.CameraCallback handler)
        {
            handler = Timed(name, handler);
            return camera =>
            {
                try
                {
                    handler(camera);
                }
                catch (Exception exception)
                {
                    Fail(name, exception);
                }
            };
        }

        static void Fail(string name, Exception exception)
        {
            int count;
            failures.TryGetValue(name, out count);
            failures[name] = ++count;
            if (count == 1) Debug.LogError("CROW_HOOK_FAIL " + name + ": " + exception);
            else if (count % 1000 == 0) Debug.LogError("CROW_HOOK_FAIL " + name + " (" + count + " times): " + exception.GetType().Name + " " + exception.Message);
        }

        // ---------------------------------------------------------------- -crow-hook-cost

        class Cost
        {
            public string Name;
            public long Ticks, FrameTicks, WorstFrame;
            public int Calls, Frame = -1;

            public void Add(long ticks)
            {
                int frame = Time.frameCount;
                if (frame != Frame)
                {
                    if (FrameTicks > WorstFrame) WorstFrame = FrameTicks;
                    FrameTicks = 0;
                    Frame = frame;
                }
                FrameTicks += ticks;
                Ticks += ticks;
                Calls++;
            }
        }

        static int timing = -1;  // -1 not read yet, 0 off, 1 on
        static float costEvery, costAt;
        static int costFrame;
        static readonly List<Cost> costs = new List<Cost>();
        static readonly Dictionary<string, Cost> costByName = new Dictionary<string, Cost>();

        static Cost CostOf(string name)
        {
            if (timing < 0)
            {
                timing = 0;
                try
                {
                    string[] args = ProbeFiles.Args;
                    if (CrowFlags.Has(args, "-crow-hook-cost"))
                    {
                        float every;
                        string value = CrowFlags.Optional(args, "-crow-hook-cost");
                        costEvery = value != null && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out every) && every > 0f ? every : 10f;
                        timing = 1;
                        costAt = Time.realtimeSinceStartup + costEvery;
                        costFrame = Time.frameCount;
                        Application.onBeforeRender += CostReport;
                        Debug.Log("CROW_HOOKS every " + costEvery + " s");
                    }
                }
                catch (Exception exception)
                {
                    Debug.Log("CROW_HOOKS off: " + exception.GetType().Name + " " + exception.Message);
                }
            }
            if (timing == 0) return null;
            Cost cost;
            if (!costByName.TryGetValue(name, out cost))
            {
                costByName[name] = cost = new Cost { Name = name };
                costs.Add(cost);
            }
            return cost;
        }

        // The handler itself without -crow-hook-cost; with it, the handler between two Stopwatch reads.
        static Action Timed(string name, Action handler)
        {
            Cost cost = CostOf(name);
            if (cost == null) return handler;
            return () =>
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    handler();
                }
                finally
                {
                    cost.Add(System.Diagnostics.Stopwatch.GetTimestamp() - start);
                }
            };
        }

        static Action<Scene, LoadSceneMode> Timed(string name, Action<Scene, LoadSceneMode> handler)
        {
            Cost cost = CostOf(name);
            if (cost == null) return handler;
            return (scene, mode) =>
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    handler(scene, mode);
                }
                finally
                {
                    cost.Add(System.Diagnostics.Stopwatch.GetTimestamp() - start);
                }
            };
        }

        static Action<Scene> Timed(string name, Action<Scene> handler)
        {
            Cost cost = CostOf(name);
            if (cost == null) return handler;
            return scene =>
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    handler(scene);
                }
                finally
                {
                    cost.Add(System.Diagnostics.Stopwatch.GetTimestamp() - start);
                }
            };
        }

        static Camera.CameraCallback Timed(string name, Camera.CameraCallback handler)
        {
            Cost cost = CostOf(name);
            if (cost == null) return handler;
            return camera =>
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    handler(camera);
                }
                finally
                {
                    cost.Add(System.Diagnostics.Stopwatch.GetTimestamp() - start);
                }
            };
        }

        // onBeforeRender (subscribed at the first timed handler, so before them): the window's line, then a new window.
        static void CostReport()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now < costAt) return;
                costAt = now + costEvery;
                int frames = Math.Max(1, Time.frameCount - costFrame);
                costFrame = Time.frameCount;
                double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                long total = 0;
                foreach (Cost cost in costs)
                {
                    if (cost.FrameTicks > cost.WorstFrame) cost.WorstFrame = cost.FrameTicks;
                    total += cost.Ticks;
                }
                var line = new StringBuilder("CROW_HOOKS frames=").Append(frames)
                    .Append(" scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'))
                    .Append(" ms/frame=").Append((total * ms / frames).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                foreach (Cost cost in costs.Where(c => c.Calls > 0).OrderByDescending(c => c.Ticks))
                    line.Append(' ').Append(cost.Name.Replace(' ', '_')).Append('=')
                        .Append((cost.Ticks * ms / frames).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append('/')
                        .Append((cost.WorstFrame * ms).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('/')
                        .Append(cost.Calls);
                Debug.Log(line.ToString());
                foreach (Cost cost in costs)
                {
                    cost.Ticks = cost.FrameTicks = cost.WorstFrame = 0;
                    cost.Calls = 0;
                }
            }
            catch (Exception exception)
            {
                Fail("CrowEvents.CostReport", exception);
            }
        }
    }
}
