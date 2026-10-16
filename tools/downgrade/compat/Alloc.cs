using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

// Garbage collections and what allocates, on IL2CPP players (the PS Vita, Windows IL2CPP; nothing on
// Mono). The garbage collector (Boehm, not incremental in 2018.2) stops every thread for a whole
// collection, which marks all live data: about 1 s on the PS Vita with 70-95 MB live in an area. It
// collects once the bytes allocated since the last collection pass about a sixth of the heap's
// scanned size (Memory.cs: free_space_divisor 6), or when the heap may not grow (the cap Memory.cs
// keeps), so the time between collections is the allocation rate's. IL2CPP's profiler interface
// (il2cpp_profiler_*: the GC's start and end, and every object, array, string and box allocated)
// tells both.
//   -crow-gc-log [ms]        each collection (CROW_GC_RUN: when, its pause, the time and MB allocated
//                            since the previous one, the heap after it) and each frame longer than ms
//                            (default 200: CROW_LONG_FRAME, with the collections' pause in it)
//   -crow-alloc [s]          every s seconds (default 10; counting starts 5 s after a scene load and
//                            restarts at each), an ALLOC line: MB and objects allocated, KB a frame,
//                            collections; and ALLOCTOP: the classes that allocated most, bytes and count
//   -crow-alloc-stacks [KB]  with -crow-alloc on Windows IL2CPP players: every KB (default 16) allocated
//                            on any thread, the call stack (RtlCaptureStackBackTrace), summed per stack
//                            and class into alloc-stacks.txt beside the player's executable (or the
//                            path after -crow-alloc-file) at each report; tools/downgrade/allocreport.py
//                            names and sums them.
// The callbacks run inside the allocator and the collector, so they allocate nothing and only touch
// arrays made beforehand.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Alloc
    {
        const float MB = 1048576f;
        const int ProfileAllocations = 1 << 7, ProfileGc = 1 << 8;
        const int GcStart = 0, GcEnd = 5;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void AllocFunc(IntPtr profiler, IntPtr obj, IntPtr klass);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void GcFunc(IntPtr profiler, int gcEvent, int generation);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ResizeFunc(IntPtr profiler, long size);

        [DllImport("__Internal")] static extern void il2cpp_profiler_install(IntPtr profiler, IntPtr shutdown);
        [DllImport("__Internal")] static extern void il2cpp_profiler_set_events(int events);
        [DllImport("__Internal")] static extern void il2cpp_profiler_install_allocation(AllocFunc callback);
        [DllImport("__Internal")] static extern void il2cpp_profiler_install_gc(GcFunc callback, ResizeFunc resize);
        [DllImport("__Internal")] static extern uint il2cpp_object_get_size(IntPtr obj);
        [DllImport("__Internal")] static extern IntPtr il2cpp_class_get_type(IntPtr klass);
        [DllImport("__Internal")] static extern IntPtr il2cpp_type_get_name(IntPtr type);
        [DllImport("__Internal")] static extern void il2cpp_free(IntPtr pointer);
        [DllImport("__Internal")] static extern UIntPtr GC_get_heap_size();
        [DllImport("__Internal")] static extern UIntPtr GC_get_free_bytes();
        [DllImport("__Internal")] static extern UIntPtr GC_get_total_bytes();

        [DllImport("kernel32.dll")] static extern ushort RtlCaptureStackBackTrace(uint skip, uint count, IntPtr frames, IntPtr hash);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetModuleFileNameW(IntPtr module, StringBuilder name, uint size);

        // Kept here: the runtime holds only the function pointers.
        static AllocFunc allocCallback;
        static GcFunc gcCallback;
        static ResizeFunc resizeCallback;

        // Collections, written by OnGc (a ring of start and end timestamps, Stopwatch ticks).
        const int Runs = 64;
        static long[] runStart, runEnd;
        static int runsStarted, runsEnded;

        // For Loading's -crow-load-cpu (with -crow-gc-log): collections ended so far, and the pause
        // (ms) of each of those after a given count (at most the ring's 64).
        internal static int RunsEnded { get { return runsEnded; } }

        internal static double PausesSince(int seen, out int count)
        {
            int ended = runsEnded;
            count = 0;
            double ms = 0;
            if (runStart == null) return 0;
            for (int i = Math.Max(seen, ended - Runs); i < ended; i++, count++)
                ms += (runEnd[i % Runs] - runStart[i % Runs]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return ms;
        }

        // Allocations per class (an open-addressed table keyed by Il2CppClass*).
        const int Slots = 8192;
        static IntPtr[] classes;
        static long[] classBytes;
        static int[] classCount;
        static long allocBytes;
        static int allocObjects;
        static volatile bool counting;

        // Sampled stacks (Windows): every sampleEvery bytes, one stack of up to Frames return addresses.
        const int Frames = 40, StackSlots = 8192;
        static bool stacks;
        static long sampleEvery, untilSample;
        static IntPtr frameBuffer;
        static long[] stackHash, stackFrames;
        static int[] stackDepth, stackCount;
        static IntPtr[] stackClass;
        static int sampling;
        static string stackFile;

        static float longFrameMs = -1f, reportEvery = -1f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static bool on;
        static int seenRuns;
        static long lastStamp, lastRunEnd, totalAtLastRun = -1;
        static float countFrom = -1f, lastReport;
        static int frames, runsAtReport;
        static long totalAtReport;

        // From Diag.Install. Only an IL2CPP player has the profiler interface; Mono's calls fail.
        internal static void Install(string[] args)
        {
            int gcLog = Array.IndexOf(args, "-crow-gc-log"), alloc = Array.IndexOf(args, "-crow-alloc");
            GcPace.Install(args);
            if (gcLog < 0 && alloc < 0) return;
            if (gcLog >= 0) longFrameMs = Quality.Number(args, "-crow-gc-log", 200f);
            if (alloc >= 0) reportEvery = Quality.Number(args, "-crow-alloc", 10f);
            if (longFrameMs <= 0f && gcLog >= 0) longFrameMs = 200f;
            if (reportEvery <= 0f && alloc >= 0) reportEvery = 10f;
            runStart = new long[Runs];
            runEnd = new long[Runs];
            int events = ProfileGc;
            if (alloc >= 0)
            {
                classes = new IntPtr[Slots];
                classBytes = new long[Slots];
                classCount = new int[Slots];
                events |= ProfileAllocations;
                int stackFlag = Array.IndexOf(args, "-crow-alloc-stacks");
                if (stackFlag >= 0 && Application.platform == RuntimePlatform.WindowsPlayer)
                {
                    stacks = true;
                    sampleEvery = (long)(Quality.Number(args, "-crow-alloc-stacks", 16f) * 1024f);
                    if (sampleEvery <= 0) sampleEvery = 16 * 1024;
                    untilSample = sampleEvery;
                    frameBuffer = Marshal.AllocHGlobal(Frames * IntPtr.Size);
                    stackHash = new long[StackSlots];
                    stackFrames = new long[StackSlots * Frames];
                    stackDepth = new int[StackSlots];
                    stackCount = new int[StackSlots];
                    stackClass = new IntPtr[StackSlots];
                    int file = Array.IndexOf(args, "-crow-alloc-file");
                    stackFile = file >= 0 && file + 1 < args.Length ? args[file + 1]
                        : Path.Combine(Path.GetDirectoryName(Application.dataPath), "alloc-stacks.txt");
                }
            }
            try
            {
                allocCallback = OnAlloc;
                gcCallback = OnGc;
                resizeCallback = OnResize;
                // Each callback once before the runtime calls it, so the method's first-call setup
                // (metadata, which may allocate) is over before it runs inside the allocator.
                OnGc(IntPtr.Zero, -1, 0);
                OnResize(IntPtr.Zero, 0);
                OnAlloc(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Total();
                if (classes != null)
                {
                    Slot(new IntPtr(8));
                    Array.Clear(classes, 0, Slots);
                }
                if (stacks)
                {
                    Sample(IntPtr.Zero);
                    Array.Clear(stackHash, 0, StackSlots);
                    Array.Clear(stackCount, 0, StackSlots);
                }
                il2cpp_profiler_install(IntPtr.Zero, IntPtr.Zero);
                il2cpp_profiler_install_gc(gcCallback, resizeCallback);
                if (classes != null) il2cpp_profiler_install_allocation(allocCallback);
                il2cpp_profiler_set_events(events);
            }
            catch (Exception e)
            {
                Debug.Log("CROW_ALLOC no IL2CPP profiler interface: " + e.GetType().Name + " " + e.Message);
                return;
            }
            Debug.Log("CROW_ALLOC on: gc log " + (longFrameMs > 0f ? "frames over " + longFrameMs + " ms" : "off") +
                      ", allocations " + (classes != null ? "every " + reportEvery + " s" : "off") +
                      (stacks ? ", stacks every " + sampleEvery / 1024 + " KB to " + stackFile : ""));
            lastStamp = System.Diagnostics.Stopwatch.GetTimestamp();
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (scene.name == "CrowMara") return;  // the player's scene, loaded beside the title
                counting = false;
                countFrom = Time.realtimeSinceStartup + 5f;
            };
            if (classes != null) countFrom = Time.realtimeSinceStartup + 5f;
            on = true;
        }

        [AOT.MonoPInvokeCallback(typeof(GcFunc))]
        static void OnGc(IntPtr profiler, int gcEvent, int generation)
        {
            if (gcEvent == GcStart)
            {
                runStart[runsStarted % Runs] = System.Diagnostics.Stopwatch.GetTimestamp();
                runsStarted++;
            }
            else if (gcEvent == GcEnd)
            {
                runEnd[runsEnded % Runs] = System.Diagnostics.Stopwatch.GetTimestamp();
                runsEnded++;
            }
        }

        [AOT.MonoPInvokeCallback(typeof(ResizeFunc))]
        static void OnResize(IntPtr profiler, long size)
        {
        }

        [AOT.MonoPInvokeCallback(typeof(AllocFunc))]
        static void OnAlloc(IntPtr profiler, IntPtr obj, IntPtr klass)
        {
            if (!counting || obj == IntPtr.Zero) return;
            long size = il2cpp_object_get_size(obj);
            Interlocked.Add(ref allocBytes, size);
            Interlocked.Increment(ref allocObjects);
            int slot = Slot(klass);
            if (slot >= 0)
            {
                Interlocked.Add(ref classBytes[slot], size);
                Interlocked.Increment(ref classCount[slot]);
            }
            if (stacks && Interlocked.Add(ref untilSample, -size) <= 0)
            {
                Interlocked.Add(ref untilSample, sampleEvery);
                Sample(klass);
            }
        }

        static int Slot(IntPtr klass)
        {
            int index = (int)((klass.ToInt64() >> 3) * 0x9E3779B1L >> 7) & (Slots - 1);
            for (int probe = 0; probe < Slots; probe++)
            {
                IntPtr at = classes[index];
                if (at == klass) return index;
                if (at == IntPtr.Zero)
                {
                    IntPtr was = Interlocked.CompareExchange(ref classes[index], klass, IntPtr.Zero);
                    if (was == IntPtr.Zero || was == klass) return index;
                }
                index = (index + 1) & (Slots - 1);
            }
            return -1;
        }

        // One stack into the table, keyed by its frames and class (one thread at a time; a sample
        // that meets another in progress is dropped).
        static void Sample(IntPtr klass)
        {
            if (Interlocked.CompareExchange(ref sampling, 1, 0) != 0) return;
            int depth = RtlCaptureStackBackTrace(1, Frames, frameBuffer, IntPtr.Zero);
            long hash = klass.ToInt64() * 31 + depth;
            for (int i = 0; i < depth; i++) hash = (hash ^ Marshal.ReadIntPtr(frameBuffer, i * IntPtr.Size).ToInt64()) * 1099511628211L;
            if (hash == 0) hash = 1;
            int index = (int)(hash >> 5) & (StackSlots - 1);
            for (int probe = 0; probe < 64; probe++)
            {
                if (stackHash[index] == hash)
                {
                    stackCount[index]++;
                    break;
                }
                if (stackHash[index] == 0)
                {
                    stackHash[index] = hash;
                    stackClass[index] = klass;
                    stackDepth[index] = depth;
                    for (int i = 0; i < depth; i++) stackFrames[index * Frames + i] = Marshal.ReadIntPtr(frameBuffer, i * IntPtr.Size).ToInt64();
                    stackCount[index] = 1;
                    break;
                }
                index = (index + 1) & (StackSlots - 1);
            }
            Interlocked.Exchange(ref sampling, 0);
        }

        static long Total()
        {
            return (long)(ulong)GC_get_total_bytes();
        }

        static string F(double value, string format = "0.0")
        {
            return value.ToString(format, Inv);
        }

        // From Diag.Update, once a frame.
        internal static void Frame()
        {
            if (!on) return;
            long stamp = System.Diagnostics.Stopwatch.GetTimestamp(), frameStart = lastStamp;
            double frameMs = (stamp - lastStamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            lastStamp = stamp;
            frames++;
            double gcInFrame = 0;
            int ended = runsEnded;
            while (seenRuns < ended)
            {
                int at = seenRuns % Runs;
                double pause = (runEnd[at] - runStart[at]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (runEnd[at] >= frameStart) gcInFrame += pause;
                if (longFrameMs > 0f) LogRun(seenRuns + 1, pause, runEnd[at]);
                seenRuns++;
            }
            if (longFrameMs > 0f && frameMs >= longFrameMs)
                Debug.Log("CROW_LONG_FRAME t=" + F(Time.realtimeSinceStartup, "0.00") + " ms=" + F(frameMs) + " gcMs=" + F(gcInFrame) +
                          " scene=" + SceneManager.GetActiveScene().name.Replace(' ', '_'));
            if (classes == null) return;
            float time = Time.realtimeSinceStartup;
            if (!counting && countFrom >= 0f && time >= countFrom)
            {
                Reset();
                counting = true;
                countFrom = -1f;
            }
            if (counting && time - lastReport >= reportEvery) Report(time);
        }

        static void LogRun(int number, double pause, long end)
        {
            long total = 0, heap = 0, free = 0;
            try
            {
                total = Total();
                heap = (long)(ulong)GC_get_heap_size();
                free = (long)(ulong)GC_get_free_bytes();
            }
            catch (Exception)
            {
            }
            double since = lastRunEnd == 0 ? -1 : (end - lastRunEnd) / (double)System.Diagnostics.Stopwatch.Frequency;
            Debug.Log("CROW_GC_RUN n=" + number + " t=" + F(Time.realtimeSinceStartup, "0.00") + " pauseMs=" + F(pause) +
                      " sinceS=" + F(since) + " allocdMB=" + (totalAtLastRun < 0 ? "?" : F((total - totalAtLastRun) / MB)) +
                      " heapMB=" + F(heap / MB) + " freeMB=" + F(free / MB) + " scene=" + SceneManager.GetActiveScene().name.Replace(' ', '_'));
            lastRunEnd = end;
            totalAtLastRun = total;
        }

        static void Reset()
        {
            counting = false;
            Array.Clear(classBytes, 0, Slots);
            Array.Clear(classCount, 0, Slots);
            Interlocked.Exchange(ref allocBytes, 0);
            Interlocked.Exchange(ref allocObjects, 0);
            if (stacks)
            {
                Array.Clear(stackHash, 0, StackSlots);
                Array.Clear(stackCount, 0, StackSlots);
            }
            frames = 0;
            runsAtReport = runsEnded;
            totalAtReport = Total();
            lastReport = Time.realtimeSinceStartup;
        }

        // The period's totals and top classes; then counting starts over (stacks add up until the
        // next scene load, written whole each time).
        static void Report(float time)
        {
            counting = false;
            long bytes = Interlocked.Read(ref allocBytes), total = Total();
            int objects = allocObjects;
            float seconds = time - lastReport;
            var line = new StringBuilder("ALLOC t=").Append(F(time)).Append(" scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            line.Append(" s=").Append(F(seconds)).Append(" frames=").Append(frames).Append(" MB=").Append(F(bytes / MB, "0.00"));
            line.Append(" KBperFrame=").Append(F(bytes / 1024.0 / Math.Max(frames, 1))).Append(" MBperS=").Append(F(bytes / MB / Math.Max(seconds, 0.001f), "0.00"));
            line.Append(" objects=").Append(objects).Append(" gcTotalMB=").Append(F((total - totalAtReport) / MB, "0.00"));
            line.Append(" gcs=").Append(runsEnded - runsAtReport);
            Debug.Log(line.ToString());
            var top = new List<int>();
            for (int i = 0; i < Slots; i++)
                if (classCount[i] > 0) top.Add(i);
            top.Sort((a, b) => classBytes[b].CompareTo(classBytes[a]));
            var names = new StringBuilder("ALLOCTOP");
            foreach (int i in top.Take(25))
                names.Append(" | ").Append(ClassName(classes[i])).Append(' ').Append(F(classBytes[i] / 1024.0, "0")).Append("KB/").Append(classCount[i]);
            Debug.Log(names.ToString());
            // Objects with a finalizer: Boehm also collects when 500 more of them are registered than at
            // its last collection (GC_collect_or_expand), however little was allocated.
            var finalizable = new StringBuilder();
            int finalizableCount = 0;
            foreach (int i in top.Where(i => Finalizable(classes[i])).OrderByDescending(i => classCount[i]).Take(12))
            {
                finalizableCount += classCount[i];
                finalizable.Append(" | ").Append(ClassName(classes[i])).Append(' ').Append(classCount[i]);
            }
            if (finalizableCount > 0) Debug.Log("ALLOCFIN " + finalizableCount + finalizable);
            if (stacks) WriteStacks();
            Array.Clear(classBytes, 0, Slots);
            Array.Clear(classCount, 0, Slots);
            Interlocked.Exchange(ref allocBytes, 0);
            Interlocked.Exchange(ref allocObjects, 0);
            frames = 0;
            runsAtReport = runsEnded;
            totalAtReport = Total();
            lastReport = time;
            counting = true;
        }

        static readonly Dictionary<IntPtr, string> classNames = new Dictionary<IntPtr, string>();

        static string ClassName(IntPtr klass)
        {
            string name;
            if (classNames.TryGetValue(klass, out name)) return name;
            IntPtr text = il2cpp_type_get_name(il2cpp_class_get_type(klass));
            name = text == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(text);
            if (text != IntPtr.Zero) il2cpp_free(text);
            return classNames[klass] = name.Replace(' ', '_');
        }

        static readonly Dictionary<IntPtr, bool> finalizers = new Dictionary<IntPtr, bool>();

        // Whether the class (or a base below System.Object) declares a finalizer, by reflection on its name.
        static bool Finalizable(IntPtr klass)
        {
            bool has;
            if (finalizers.TryGetValue(klass, out has)) return has;
            string name = ClassName(klass);
            Type type = null;
            if (!name.Contains("<") && !name.EndsWith("]"))
                foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if ((type = assembly.GetType(name, false)) != null) break;
            for (Type t = type; t != null && t != typeof(object) && !has; t = t.BaseType)
                has = t.GetMethod("Finalize", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                                              System.Reflection.BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) != null;
            return finalizers[klass] = has;
        }

        static readonly Dictionary<IntPtr, string> modules = new Dictionary<IntPtr, string>();

        // "module!offset" of the call before each return address, as cpuprofile.py's stacks name them.
        static string Where(long address)
        {
            IntPtr module;
            if (!GetModuleHandleExW(0x4 | 0x2, new IntPtr(address - 1), out module)) return "?0x" + (address - 1).ToString("x");
            string name;
            if (!modules.TryGetValue(module, out name))
            {
                var path = new StringBuilder(520);
                GetModuleFileNameW(module, path, 520);
                modules[module] = name = Path.GetFileName(path.ToString());
            }
            return name + "!0x" + (address - 1 - module.ToInt64()).ToString("x");
        }

        // The sampled stacks since counting started: "<samples>\t<KB a sample>\t<class>\t<frames,
        // innermost first, ';'-separated>".
        static void WriteStacks()
        {
            try
            {
                using (var writer = new StreamWriter(stackFile, false))
                {
                    for (int i = 0; i < StackSlots; i++)
                    {
                        if (stackCount[i] == 0) continue;
                        writer.Write(stackCount[i]);
                        writer.Write('\t');
                        writer.Write(sampleEvery / 1024);
                        writer.Write('\t');
                        writer.Write(ClassName(stackClass[i]));
                        writer.Write('\t');
                        for (int f = 0; f < stackDepth[i]; f++)
                        {
                            if (f > 0) writer.Write(';');
                            writer.Write(Where(stackFrames[i * Frames + f]));
                        }
                        writer.WriteLine();
                    }
                }
            }
            catch (Exception e)
            {
                Debug.Log("CROW_ALLOC stacks not written: " + e.GetType().Name + " " + e.Message);
            }
        }
    }
    // When the garbage collector runs, on the PS Vita (-crow-gc-pace off leaves it to Boehm; on other
    // IL2CPP players -crow-gc-pace on). A collection stops the game for 0.8 s in Roadside and 1.0 s in
    // Station Square (it marks all 75-95 MB of live data, on one core). In play Boehm runs one each
    // time about 5.3 MB have been allocated since the last (Memory.cs's free_space_divisor 6), every
    // 3-5 minutes at the 20-30 KB/s the game allocates there (DOTween tweens from PlayMaker actions,
    // mostly), wherever the game is. GcPace runs it early where a 1 s stop costs little: half a second
    // into a pause (Time.timeScale 0: the status screen, the map), once at least 2 MB (-crow-gc-pace
    // <MB>) have been allocated since the last collection, so that in play the next one comes later.
    // Logged as CROW_GC_PACE (each pause, with the MB allocated since the last collection, and each
    // collection it runs).
    public static class GcPace
    {
        const float MB = 1048576f;
        static bool on;
        static long pauseBytes = 2L << 20;
        static int seenCollections;
        static long totalAtCollection;
        static float pausedSince = -1f;
        static bool pauseDone;

        [DllImport("__Internal")] static extern UIntPtr GC_get_total_bytes();

        static long Total()
        {
            return (long)(ulong)GC_get_total_bytes();
        }

        internal static void Install(string[] args)
        {
            int index = Array.IndexOf(args, "-crow-gc-pace");
            string mode = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            if (mode == "off" || (mode == null && Application.platform != RuntimePlatform.PSP2)) return;
            float megabytes;
            if (float.TryParse(mode, NumberStyles.Float, CultureInfo.InvariantCulture, out megabytes) && megabytes > 0f)
                pauseBytes = (long)(megabytes * MB);
            try
            {
                totalAtCollection = Total();
            }
            catch (Exception e)
            {
                Debug.Log("CROW_GC_PACE off: " + e.GetType().Name);  // Mono: no Boehm API
                return;
            }
            seenCollections = GC.CollectionCount(0);
            on = true;
            Canvas.willRenderCanvases += Frame;
            Debug.Log("CROW_GC_PACE on: a collection in pauses after " + (pauseBytes / MB).ToString("0.#", CultureInfo.InvariantCulture) + " MB");
        }

        // Canvas.willRenderCanvases: once a frame.
        static void Frame()
        {
            if (!on) return;
            int count = GC.CollectionCount(0);
            if (count != seenCollections)
            {
                seenCollections = count;
                totalAtCollection = Total();
            }
            if (Time.timeScale != 0f)
            {
                pausedSince = -1f;
                pauseDone = false;
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (pausedSince < 0f) pausedSince = now;
            else if (!pauseDone && now - pausedSince >= 0.5f)
            {
                pauseDone = true;
                long since = Total() - totalAtCollection;
                string allocated = (since / MB).ToString("0.0", CultureInfo.InvariantCulture) + " MB allocated since the last collection";
                if (since < pauseBytes)
                {
                    Debug.Log("CROW_GC_PACE pause, " + allocated);
                    return;
                }
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                GC.Collect();
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                seenCollections = GC.CollectionCount(0);
                totalAtCollection = Total();
                Debug.Log("CROW_GC_PACE pause, " + allocated + ": collected in " + ms.ToString("0", CultureInfo.InvariantCulture) + " ms");
            }
        }
    }
}

namespace CrowCompat
{
    // PlayMaker's DOTween actions make a new tween each time their state is entered (Roadside in play:
    // about 33 a second, from FSMs that loop over move, rotate and punch states), and with the game's
    // settings DOTween makes each new and drops it when it completes: TweenerCore objects were 45% of
    // the bytes allocated in play. A recyclable tween goes back to DOTween's pool when it is killed (on
    // completion) and is handed out again. The rewriter (rewriter/Alloc.cs) makes those actions' tweens
    // recyclable when On is set (PS Vita default; -crow-tween-recycle game keeps the game's setting,
    // on turns it on elsewhere). The hazard of recycling, a reference kept to a tween after it was
    // killed, does not arise there: each action keeps its tween in a private field that only its
    // OnEnter reads, right after making it; other actions reach tweens through DOTween by id or target,
    // which finds only live ones.
    public static class TweenRecycle
    {
        public static readonly bool On = Setting();

        static bool Setting()
        {
            string[] args = ProbeFiles.Args;
            int index = Array.IndexOf(args, "-crow-tween-recycle");
            string mode = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            return mode == null ? Application.platform == RuntimePlatform.PSP2 : mode == "on";
        }
    }
}
