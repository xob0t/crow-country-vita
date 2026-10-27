using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using GCHandle = System.Runtime.InteropServices.GCHandle;

// The memory probe, for the PS Vita's budget (512 MB shared by the CPU and GPU, +109 MB expansion):
//   -crow-memory <s>        every s seconds, and 5 s after each scene load, a MEMORY line: Unity's
//                           allocator totals (Profiler: allocated, reserved, graphics driver, Mono used
//                           and heap), the PS Vita's free LPDDR, CDRAM and physically contiguous memory
//                           (UnityEngine.PSVita.Diagnostics, kept by link.xml), the GC heap.
//   -crow-memory-types      with it, after each scene load (and with -crow-memory-types-every, every
//                           MEMORY line) a MEMTYPES line: count and size per asset type in memory
//                           (Profiler.GetRuntimeMemorySizeLong where the player reports it, else an
//                           estimate from the texture format and size, the mesh's vertex and index
//                           counts, the clip's samples as HEVAG), readable meshes and textures, loaded
//                           clips per load type; and MEMTOP lines with the largest textures and meshes.
//   -crow-memory-fsm        with it, a MEMFSM line: PlayMaker's FSMs, states, loaded actions, parameters.
//   -crow-memory-unload     before each MEMTYPES pass, Resources.UnloadUnusedAssets and a GC, so the
//                           pass counts what stays loaded.
//   -crow-memory-watch [ms] a thread of its own samples memory every ms (default 250), also through
//                           the long frames of a scene load, when the main thread logs nothing: a
//                           MEMWATCH line when a value moved by 1 MB or more, and every 10 s (the GC
//                           heap's size and free part, bytes allocated since the last line, the PS
//                           Vita's free LPDDR, CDRAM and PHYSCONT, and the GXM shader patcher's USSE
//                           memory in use: usseKB=fragment+vertex/size, ShaderPatcher). The last lines
//                           before an out-of-memory crash show which pool ran out.
//   -crow-gc-retries <n>    Boehm's GC_max_retries: collections tried when the GC heap can't grow,
//                           before the out-of-memory abort (PS Vita default 2; Boehm's own is 0).
//   -crow-gc-divisor <n>    Boehm's GC_free_space_divisor: the heap grows by about heap/n at a time,
//                           and collects after about (live data)/n has been allocated (PS Vita
//                           default 6; Boehm's own is 3).
//   -crow-gc-reserve <MB>   the main memory the GC heap's cap leaves to Unity (PS Vita default 16; 0:
//                           no cap). See GcSettings.
//   -crow-gc-max <MB>       also never past this size: the heap collects instead of growing (an area's
//                           load leaves garbage the heap grows into, and the heap never shrinks).
//   -crow-no-phycont-heap   keep the GC heap out of the PHYCONT memory (see PhyContPool).
//   -crow-heap-sort off     leave the low-level allocator's block sorting off (PS Vita default on; see HeapSort).
//   -crow-gc-handles <n>    IL2CPP's table of GC handles the engine uses grown to n slots at the start (PS
//                           Vita default 131072; 0: as IL2CPP has it, the default on other players). See GcHandles.
//   -crow-gc-handles-count [s]  s seconds (default 2) after each scene load, the GC handles in use per table
//                           (CROW_GC handles). A probe: it reads every slot of the tables.
// MeshRelease.cs frees what MeshCombineStudio keeps after combining (on the PS Vita; -crow-keep-mesh-caches,
// -crow-release-meshes).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public class Memory : MonoBehaviour
    {
        const float MB = 1048576f;
        const long Bytes = 1048576L;  // a megabyte
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static float every;
        static bool types, typesEvery, unload, fsmCensus;
        float started, lastBeat, typesAt = -1f;

        // From Diag.Install (a RuntimeInitializeOnLoadMethod: the build lists those, so quickcode can't add one).
        internal static void Install()
        {
            string[] args = ProbeFiles.Args;
            // The GC's settings first, on their own (the out-of-memory protection: retries, divisor, governor,
            // PHYCONT pool), then the mesh caches' release: one failing doesn't take the other with it.
            Diag.Guard("Memory.GcSettings", () => GcSettings(args));
            Diag.Guard("MeshRelease", () => MeshRelease.Install(args));
            if (CrowFlags.Has(args, "-crow-memory-watch"))
            {
                int ms = CrowFlags.Int(args, "-crow-memory-watch", 250);
                Watch(ms > 0 ? ms : 250);
            }
            if (!CrowFlags.Has(args, "-crow-memory")) return;
            every = CrowFlags.Number(args, "-crow-memory", 30f);
            types = CrowFlags.Has(args, "-crow-memory-types");
            typesEvery = CrowFlags.Has(args, "-crow-memory-types-every");
            unload = CrowFlags.Has(args, "-crow-memory-unload");
            fsmCensus = CrowFlags.Has(args, "-crow-memory-fsm");
            var probe = new GameObject("CrowMemory");
            DontDestroyOnLoad(probe);
            probe.AddComponent<Memory>();
        }

        void Start()
        {
            started = lastBeat = Time.realtimeSinceStartup;
            CrowEvents.Loaded("Memory.sceneLoaded", (scene, mode) => { typesAt = Time.realtimeSinceStartup + 5f; });
            Line("start");
            if (types) typesAt = started + 5f;
        }

        void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (typesAt >= 0f && now >= typesAt)
            {
                typesAt = -1f;
                Line("loaded");
                if (types) Types();
                lastBeat = now;
            }
            else if (every > 0f && now - lastBeat >= every)
            {
                lastBeat = now;
                Line("beat");
                if (typesEvery) Types();
            }
        }

        static string F(double megabytes)
        {
            return megabytes.ToString("0.0", Inv);
        }

        void Line(string why)
        {
            var line = new StringBuilder("MEMORY");
            line.Append(" t=").Append((Time.realtimeSinceStartup - started).ToString("F1", Inv));
            line.Append(" why=").Append(why);
            line.Append(" scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            Section(line, "profiler", () =>
                " allocMB=" + F(Profiler.GetTotalAllocatedMemoryLong() / MB) +
                " reservedMB=" + F(Profiler.GetTotalReservedMemoryLong() / MB) +
                " unusedMB=" + F(Profiler.GetTotalUnusedReservedMemoryLong() / MB) +
                " gfxMB=" + F(Profiler.GetAllocatedMemoryForGraphicsDriver() / MB) +
                " monoUsedMB=" + F(Profiler.GetMonoUsedSizeLong() / MB) +
                " monoHeapMB=" + F(Profiler.GetMonoHeapSizeLong() / MB));
            Section(line, "vita", VitaFree);
            Section(line, "gc", GcHeap);
            Debug.Log(line.ToString());
        }

        static void Section(StringBuilder line, string name, Func<string> section)
        {
            try { line.Append(section()); }
            catch (Exception e) { line.Append(' ').Append(name).Append("=error:").Append(e.GetType().Name); }
        }

        static string VitaFree()
        {
            if (Application.platform != RuntimePlatform.PSP2) return "";
            if (VitaMemory.FreeLPDDR == null) return " vita=missing";
            Func<Func<int>, string> free = read => read == null ? "missing" : F(read() / MB);
            return " freeLPDDR=" + free(VitaMemory.FreeLPDDR) + " freeCDRAM=" + free(VitaMemory.FreeCDRAM) +
                   " freePHYSCONT=" + free(VitaMemory.FreePHYSCONT);
        }

        // The IL2CPP garbage collector's heap (Boehm, linked into the game module): its size as taken
        // from the system, which it never gives back, and the part free after the last collection.
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern UIntPtr GC_get_heap_size();
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern UIntPtr GC_get_free_bytes();
        static bool noGcHeap;

        static string GcHeap()
        {
            string managed = " gcUsedMB=" + F(GC.GetTotalMemory(false) / MB);
            if (noGcHeap || Application.platform != RuntimePlatform.PSP2) return managed;
            try
            {
                return managed + " gcHeapMB=" + F((ulong)GC_get_heap_size() / MB) + " gcFreeMB=" + F((ulong)GC_get_free_bytes() / MB);
            }
            catch (Exception)
            {
                noGcHeap = true;
                return managed;
            }
        }

        // The garbage collector's heap on the PS Vita (IL2CPP links Boehm into the game module, so its C
        // API is reachable as __Internal). Boehm takes its heap from the system in sections of about a
        // third of the heap at a time (free_space_divisor 3) and never gives them back; when a section
        // can't be had, it aborts without collecting first (max_retries 0). In Station Square, from
        // the title, the area's read left 35 MB of main memory, the full GC heap (91.6 MB, 2 MB free)
        // grew by 31 MB, and Unity's allocator then failed with 3 MB left. The PS Vita defaults: a
        // section of a sixth of the heap (collections come sooner too, after a sixth of the live data
        // allocated instead of a third), and two collections tried before giving up.
        // -crow-gc-retries <n> and -crow-gc-divisor <n> override them; 0 keeps Boehm's own.
        // Boehm still grows the heap rather than collect while less than that sixth has been allocated
        // since the last collection, even when most of the heap is garbage and main memory is nearly
        // gone (Station Square: a 120 MB heap, 4 MB of it free, and 25 MB of main memory left, which
        // its next 20 MB section would take). Governor caps the heap at its size plus the free main
        // memory beyond a reserve for Unity (default 16 MB, -crow-gc-reserve <MB>, 0 for none): where
        // the cap stops a section, Boehm collects instead (the retries above), and only data that is
        // really live can make it fail.
        const int VitaRetries = 2, VitaDivisor = 6, VitaReserveMB = 16;
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern void GC_set_max_heap_size(UIntPtr value);
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern void GC_set_max_retries(UIntPtr value);
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern UIntPtr GC_get_max_retries();
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern void GC_set_free_space_divisor(UIntPtr value);
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern UIntPtr GC_get_free_space_divisor();

        static void GcSettings(string[] args)
        {
            if (Application.platform != RuntimePlatform.PSP2)
            {
                GcHandles.Install(args);  // other players: only with -crow-gc-handles / -crow-gc-handles-count
                return;
            }
            int retries = CrowFlags.Int(args, "-crow-gc-retries", VitaRetries), divisor = CrowFlags.Int(args, "-crow-gc-divisor", VitaDivisor);
            string pool = HeapSort(args) + PhyContPool(args);
            try
            {
                if (retries > 0) GC_set_max_retries((UIntPtr)(uint)retries);
                if (divisor > 0) GC_set_free_space_divisor((UIntPtr)(uint)divisor);
                int reserve = CrowFlags.Int(args, "-crow-gc-reserve", VitaReserveMB);
                maxHeap = CrowFlags.Int(args, "-crow-gc-max", 0) * Bytes;
                Debug.Log("CROW_GC max_retries=" + (ulong)GC_get_max_retries() + " free_space_divisor=" + (ulong)GC_get_free_space_divisor() +
                          " heapMB=" + F((ulong)GC_get_heap_size() / MB) + pool + " reserveMB=" + reserve + " maxMB=" + maxHeap / Bytes);
                // The handle table's growth while the heap can still grow: before the governor can hold it.
                GcHandles.Install(args);
                if (reserve > 0) Governor(reserve);
            }
            catch (Exception e)
            {
                Debug.Log("CROW_GC settings failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        // Every 100 ms on a thread of its own (scene loads allocate on the loading thread while the main
        // thread waits): the heap's cap from the free main memory. Logs when the cap starts and stops
        // holding the heap at its size.
        static long maxHeap;  // -crow-gc-max, bytes (0: none)

        static void Governor(int reserveMB)
        {
            // A delegate (VitaMemory), not MethodInfo.Invoke: Invoke allocates (a parameter array, the boxed
            // result) ten times a second, for as long as the game runs.
            Func<int> free = VitaMemory.FreeLPDDR;
            if (free == null)
            {
                Debug.Log("CROW_GC no governor: UnityEngine.PSVita.Diagnostics.GetFreeMemoryLPDDR missing");
                return;
            }
            long reserve = reserveMB * Bytes;
            var thread = new System.Threading.Thread(() =>
            {
                bool held = false;
                while (true)
                {
                    try
                    {
                        long heap = (long)(ulong)GC_get_heap_size();
                        long freeNow = free();
                        long room = Math.Max(0L, freeNow - reserve);
                        // Not by slivers: each section is a memory block of its own, and Boehm has a
                        // fixed number of section slots.
                        if (room < 4 * Bytes) room = 0;
                        if (maxHeap > 0 && heap + room > maxHeap) room = Math.Max(0L, maxHeap - heap);
                        GC_set_max_heap_size((UIntPtr)(ulong)(heap + room));
                        bool holds = room == 0;
                        if (holds != held)
                        {
                            held = holds;
                            Debug.Log("CROW_GC " + (held ? "heap held at " : "heap free to grow from ") + F(heap / MB) + " MB, " +
                                      F(freeNow / MB) + " MB of main memory free");  // not room + reserve: room is 0 when held
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.Log("CROW_GC governor stopped: " + e.GetType().Name + " " + e.Message);
                        GC_set_max_heap_size(UIntPtr.Zero);
                        return;
                    }
                    System.Threading.Thread.Sleep(100);
                }
            });
            thread.IsBackground = true;
            thread.Name = "CrowGcGovernor";
            thread.Start();
            Governing = true;
        }

        // Whether the governor runs (it may hold the heap at its size from then on: GcHandles.Change grows no table).
        internal static bool Governing;

        // The PS Vita player's low-level allocator (the memory blocks it takes from the system for Unity's
        // heaps and memalign calls) keeps its free blocks sorted (PSVita.Utility.EnableHeapBlockSorting:
        // "can help to reduce memory fragmentation at the cost of reduced performance"). Without it the
        // main memory free in each Ocean Kingdom -> Haunted Hilltop gap fell by 2-3 MB per round trip
        // that Unity's own totals (reserved, allocated) and the GC heap don't account for (22 Oct, six
        // round trips: 56, 44, 44, 40, 35 MB, the load's lowest point 13, 12, 12, 6, 12); with it that
        // part stays flat (57, 56, 50, 49, 43; lowest 15, 15, 15, 15, 13), and Unity's own holes
        // (reserved, not allocated: +3 MB a trip either way) are mostly reused by the next load. Station
        // Square's fight bench keeps its frame time (38.8 against 38.4 ms, 5 runs each) with 8 MB more
        // free; the first two area loads after the start take 3-5 s longer, the later ones 2-3 s less
        // (Ocean Kingdom <-> Haunted Hilltop: 15 s instead of 18). Off with -crow-heap-sort off.
        static string HeapSort(string[] args)
        {
            if (CrowFlags.Value(args, "-crow-heap-sort") == "off") return " heapSort=off";
            try
            {
                UnityEngine.PSVita.Utility.EnableHeapBlockSorting();
                return " heapSort=on";
            }
            catch (Exception e)
            {
                return " heapSort=error:" + e.GetType().Name;
            }
        }

        // The PS Vita's 26 MB of physically contiguous memory, which the game leaves unused (Unity's
        // PSVitaVideoPlayer, which needs it for its decoder, is never used: CrowCompat plays the game's
        // videos). PSVitaVideoPlayer.TransferMemToMonoHeap makes all of it a pool of Unity's
        // BigAlignmentKernelAllocator, which serves memalign calls with a large alignment, the GC
        // heap's sections among them, and which takes main memory blocks ("heapbb") only for a
        // request the pool can't fit. So up to 26 MB of the GC heap comes from there instead of main
        // memory. Before the heap grows past its first sections; off with -crow-no-phycont-heap.
        static string PhyContPool(string[] args)
        {
            if (CrowFlags.Has(args, "-crow-no-phycont-heap")) return " phycontPool=off";
            try
            {
                return " phycontPool=" + (UnityEngine.PSVita.PSVitaVideoPlayer.TransferMemToMonoHeap() ? "on" : "failed");
            }
            catch (Exception e)
            {
                return " phycontPool=error:" + e.GetType().Name;
            }
        }

        // -crow-memory-watch: memory sampled on a thread of its own (the main thread logs nothing during
        // a scene load's long frames). Each sample: GC heap, GC free, free LPDDR, CDRAM, PHYSCONT (MB).
        static void Watch(int ms)
        {
            Func<int>[] free = { VitaMemory.FreeLPDDR, VitaMemory.FreeCDRAM, VitaMemory.FreePHYSCONT };
            bool gc = Application.platform == RuntimePlatform.PSP2;
            bool patcher = gc && ShaderPatcher.Find();
            var thread = new System.Threading.Thread(() =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                float[] last = null;
                long lastAt = -100000, lastTotal = 0;
                int lastUsse = 0;
                float lowest = float.MaxValue;
                string[] names = { "gcHeap", "gcFree", "freeLPDDR", "freeCDRAM", "freePHYSCONT" };
                while (true)
                {
                    var now = new float[5];
                    long total = 0;
                    try
                    {
                        if (gc)
                        {
                            now[0] = (ulong)GC_get_heap_size() / MB;
                            now[1] = (ulong)GC_get_free_bytes() / MB;
                            total = GcTotal.Bytes();
                        }
                        else
                        {
                            now[0] = GC.GetTotalMemory(false) / MB;
                        }
                        for (int i = 0; i < 3; i++)
                            if (free[i] != null) now[2 + i] = free[i]() / MB;
                    }
                    catch (Exception e)
                    {
                        Debug.Log("MEMWATCH failed: " + e.GetType().Name + " " + e.Message);
                        return;
                    }
                    if (free[0] != null && now[2] < lowest) lowest = now[2];
                    int fragment = 0, vertex = 0;
                    if (patcher) ShaderPatcher.Usse(out fragment, out vertex);
                    bool moved = last == null || Math.Abs(fragment + vertex - lastUsse) >= 32 * 1024;
                    for (int i = 0; !moved && i < now.Length; i++) moved = Math.Abs(now[i] - last[i]) >= 1f;
                    long at = clock.ElapsedMilliseconds;
                    if (moved || at - lastAt >= 10000)
                    {
                        var line = new StringBuilder("MEMWATCH t=").Append((at / 1000.0).ToString("F2", Inv));
                        for (int i = 0; i < now.Length; i++)
                            if (i < 2 ? gc || i == 0 : free[i - 2] != null)
                                line.Append(' ').Append(names[i]).Append('=').Append(F(now[i]));
                        if (gc) line.Append(" allocdMB=").Append(F((total - lastTotal) / MB));
                        if (free[0] != null) line.Append(" lowestLPDDR=").Append(F(lowest));
                        if (patcher)
                            line.Append(" usseKB=").Append(fragment / 1024).Append('+').Append(vertex / 1024).Append('/').Append(ShaderPatcher.UsseBytes / 1024);
                        Debug.Log(line.ToString());
                        lastUsse = fragment + vertex;
                        last = now;
                        lastAt = at;
                        lastTotal = total;
                    }
                    System.Threading.Thread.Sleep(ms);
                }
            });
            thread.IsBackground = true;
            thread.Name = "CrowMemWatch";
            thread.Start();
            Debug.Log("MEMWATCH every " + ms + " ms");
        }

        // Bits per texel of the TextureFormats the game's textures use on the PS Vita and Windows.
        static float BitsPerTexel(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.Alpha8: case TextureFormat.R8: return 8f;
                case TextureFormat.RGB24: return 24f;
                case TextureFormat.RGBA32: case TextureFormat.ARGB32: case TextureFormat.BGRA32: case TextureFormat.RFloat: case TextureFormat.RGHalf: return 32f;
                case TextureFormat.RGB565: case TextureFormat.RGBA4444: case TextureFormat.ARGB4444: case TextureFormat.R16: case TextureFormat.RHalf: case TextureFormat.RG16: return 16f;
                case TextureFormat.DXT1: case TextureFormat.PVRTC_RGB4: case TextureFormat.PVRTC_RGBA4: case TextureFormat.ETC_RGB4: return 4f;
                case TextureFormat.DXT5: return 8f;
                case TextureFormat.PVRTC_RGB2: case TextureFormat.PVRTC_RGBA2: return 2f;
                case TextureFormat.RGBAHalf: return 64f;
                case TextureFormat.RGBAFloat: return 128f;
                default: return 32f;
            }
        }

        static long TextureBytes(Texture texture)
        {
            var t2 = texture as Texture2D;
            if (t2 != null)
                return (long)(t2.width * (long)t2.height * BitsPerTexel(t2.format) / 8f * (t2.mipmapCount > 1 ? 4f / 3f : 1f));
            var cube = texture as Cubemap;
            if (cube != null)
                return (long)(cube.width * (long)cube.height * 6 * BitsPerTexel(cube.format) / 8f * (cube.mipmapCount > 1 ? 4f / 3f : 1f));
            var rt = texture as RenderTexture;
            if (rt != null)
                return rt.width * (long)rt.height * ((rt.format == RenderTextureFormat.ARGBHalf ? 8 : 4) + (rt.depth > 0 ? 4 : 0));
            return texture.width * (long)texture.height * 4;
        }

        // A mesh's vertex data is not readable from scripts when the mesh is not readable, so its
        // size is estimated: 32 bytes a vertex (position, normal, one uv) and 2 bytes an index;
        // a readable mesh also keeps that copy on the CPU.
        internal static long MeshBytes(Mesh mesh)
        {
            long indices = 0;
            for (int sub = 0; sub < mesh.subMeshCount; sub++) indices += mesh.GetIndexCount(sub);
            return mesh.vertexCount * 32L + indices * 2L;
        }

        class Tally
        {
            public int count, flagged;
            public long runtime, estimate, flaggedBytes;
        }

        static void Add(Dictionary<string, Tally> tallies, string name, UnityEngine.Object asset, long estimate, bool flag)
        {
            Tally tally;
            if (!tallies.TryGetValue(name, out tally)) tallies[name] = tally = new Tally();
            tally.count++;
            tally.runtime += Profiler.GetRuntimeMemorySizeLong(asset);
            tally.estimate += estimate;
            if (flag)
            {
                tally.flagged++;
                tally.flaggedBytes += estimate;
            }
        }

        // -crow-memory-fsm: with the MEMTYPES pass, a MEMFSM line: PlayMaker FSMs loaded (also inactive),
        // their states, the states whose actions are loaded and those actions, and the item counts of
        // the states' ActionData lists (each serialized action parameter is an object or string there,
        // and a loaded action holds its own copy).
        static string FsmCensus()
        {
            const BindingFlags field = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type fsmComponent = GameTypes.PlayMakerFsm;
            Type fsmType = Type.GetType("HutongGames.PlayMaker.Fsm, PlayMaker");
            Type stateType = Type.GetType("HutongGames.PlayMaker.FsmState, PlayMaker");
            Type dataType = Type.GetType("HutongGames.PlayMaker.ActionData, PlayMaker");
            if (fsmComponent == null || fsmType == null || stateType == null || dataType == null) return "MEMFSM types missing";
            FieldInfo fsmField = fsmComponent.GetField("fsm", field), statesField = fsmType.GetField("states", field);
            FieldInfo actionsField = stateType.GetField("actions", field), dataField = stateType.GetField("actionData", field);
            FieldInfo[] lists = dataType.GetFields(field).Where(f => typeof(System.Collections.IList).IsAssignableFrom(f.FieldType)).ToArray();
            var counts = new long[lists.Length];
            int components = 0, states = 0, loadedStates = 0, actions = 0;
            foreach (UnityEngine.Object component in Resources.FindObjectsOfTypeAll(fsmComponent))
            {
                components++;
                object fsm = fsmField.GetValue(component);
                var stateArray = fsm == null ? null : statesField.GetValue(fsm) as Array;
                if (stateArray == null) continue;
                foreach (object state in stateArray)
                {
                    if (state == null) continue;
                    states++;
                    var loaded = actionsField.GetValue(state) as Array;
                    if (loaded != null)
                    {
                        loadedStates++;
                        actions += loaded.Length;
                    }
                    object data = dataField.GetValue(state);
                    if (data == null) continue;
                    for (int i = 0; i < lists.Length; i++)
                    {
                        var list = lists[i].GetValue(data) as System.Collections.IList;
                        if (list != null) counts[i] += list.Count;
                    }
                }
            }
            var line = new StringBuilder("MEMFSM components=").Append(components).Append(" states=").Append(states)
                .Append(" loadedStates=").Append(loadedStates).Append(" actions=").Append(actions);
            foreach (int i in Enumerable.Range(0, lists.Length).OrderByDescending(i => counts[i]).Take(14))
                line.Append(' ').Append(lists[i].Name).Append('=').Append(counts[i]);
            return line.ToString();
        }

        void Types()
        {
            if (!unload)
            {
                TypesPass();
                return;
            }
            // The unload runs over the next frames: the pass once it is done.
            Resources.UnloadUnusedAssets().completed += operation =>
            {
                GC.Collect();
                TypesPass();
            };
        }

        void TypesPass()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (fsmCensus)
            {
                Debug.Log(FsmCensus());
                if (unload) GC.Collect();
            }
            var tallies = new Dictionary<string, Tally>();
            var textures = new List<KeyValuePair<long, string>>();
            foreach (Texture texture in Resources.FindObjectsOfTypeAll<Texture>())
            {
                long bytes = TextureBytes(texture);
                var t2 = texture as Texture2D;
                string kind = texture is RenderTexture ? "RenderTexture" : texture is Cubemap ? "Cubemap" : t2 != null ? "Texture2D" : "Texture";
                Add(tallies, kind, texture, bytes, false);
                textures.Add(new KeyValuePair<long, string>(bytes, texture.name + " " + texture.width + "x" + texture.height + (t2 != null ? " " + t2.format : "")));
            }
            var meshes = new List<KeyValuePair<long, string>>();
            foreach (Mesh mesh in Resources.FindObjectsOfTypeAll<Mesh>())
            {
                long bytes = MeshBytes(mesh);
                Add(tallies, "Mesh", mesh, bytes, mesh.isReadable);
                meshes.Add(new KeyValuePair<long, string>(bytes, mesh.name + " v" + mesh.vertexCount + (mesh.isReadable ? " rw" : "")));
            }
            var loaded = new Dictionary<string, int>();
            foreach (AudioClip clip in Resources.FindObjectsOfTypeAll<AudioClip>())
            {
                bool inMemory = clip.loadState == AudioDataLoadState.Loaded && clip.loadType != AudioClipLoadType.Streaming;
                Add(tallies, "AudioClip", clip, inMemory ? (long)(clip.samples * (long)clip.channels * 0.5714f) : 0L, inMemory);
                string key = clip.loadType + "/" + clip.loadState;
                int count;
                loaded.TryGetValue(key, out count);
                loaded[key] = count + 1;
            }
            // AnimationClip by name: CrowCompat does not reference the animation module.
            Type animationClip = Type.GetType("UnityEngine.AnimationClip, UnityEngine.AnimationModule");
            foreach (Type type in new[] { typeof(Material), typeof(Shader), animationClip, typeof(Font), typeof(Sprite), typeof(GameObject), typeof(Transform), typeof(MonoBehaviour), typeof(ScriptableObject) })
            {
                if (type == null) continue;
                UnityEngine.Object[] found = Resources.FindObjectsOfTypeAll(type);
                var tally = new Tally { count = found.Length };
                if (type != typeof(GameObject) && type != typeof(Transform) && type != typeof(MonoBehaviour))
                    foreach (UnityEngine.Object asset in found) tally.runtime += Profiler.GetRuntimeMemorySizeLong(asset);
                tallies[type.Name] = tally;
            }
            var line = new StringBuilder("MEMTYPES");
            line.Append(" scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            foreach (KeyValuePair<string, Tally> pair in tallies)
            {
                Tally t = pair.Value;
                line.Append(' ').Append(pair.Key).Append('=').Append(t.count);
                if (t.runtime > 0) line.Append(",rt").Append(F(t.runtime / MB));
                if (t.estimate > 0) line.Append(",est").Append(F(t.estimate / MB));
                if (t.flagged > 0) line.Append(pair.Key == "AudioClip" ? ",inMemory" : ",readable").Append(t.flagged).Append(",").Append(F(t.flaggedBytes / MB));
            }
            foreach (KeyValuePair<string, int> pair in loaded) line.Append(" clips:").Append(pair.Key).Append('=').Append(pair.Value);
            if (unload) line.Append(" gcAfterCollectMB=").Append(F(GC.GetTotalMemory(true) / MB));
            line.Append(MeshRelease.Report());
            line.Append(" passMs=").Append(clock.ElapsedMilliseconds);
            Debug.Log(line.ToString());
            Debug.Log("MEMTOP textures " + string.Join(" | ", textures.OrderByDescending(p => p.Key).Take(12).Select(p => F(p.Key / MB) + " " + p.Value).ToArray()));
            Debug.Log("MEMTOP meshes " + string.Join(" | ", meshes.OrderByDescending(p => p.Key).Take(8).Select(p => F(p.Key / MB) + " " + p.Value).ToArray()));
        }
    }

    // IL2CPP's tables of GC handles (libil2cpp gc/GCHandle.cpp, 2018.2.19f1): weak, weak tracking, normal and pinned,
    // a handle being (slot << 3) | (table + 1). The engine holds the managed object of every engine object scripts
    // have reached (each MonoBehaviour, and a GameObject, Transform, Renderer, Material... once a script has asked for
    // it: FindObjectsOfType, .gameObject, .transform) by a PINNED handle until the engine object is destroyed: on the
    // PS Vita (21-22 Oct, -crow-gc-handles-count) the pinned table held 499 handles at the start, 45,700-50,900 in Fairy
    // Forest, Station Square and Ocean Kingdom and about 64,000 in Haunted Hilltop (the same on every visit; the
    // Windows IL2CPP player read 15,666 on the title, 57,000-63,000 in those areas and Pool, 79,600-80,900 in Haunted
    // Hilltop), the normal table none. The normal and pinned tables start at 32 slots and
    // double when every slot is taken, and never shrink: a new entries array twice the size from the GC heap
    // (GarbageCollector::AllocateFixed, GC_MALLOC_UNCOLLECTABLE, which every collection scans),
    // the old one copied in and freed, and a bitmap of a bit a slot (malloc). alloc_handle doesn't check that the new
    // array came: on 21 Oct the first frame after Haunted Hilltop's load (29,908 GameObjects, 14,325 behaviours, 8,612
    // mesh renderers) took handle 65,537 (VitaLit's material pass), the 512 KB array for 131,072 slots couldn't be had
    // with the governor holding the GC heap at 75.3 MB, and the copy into NULL crashed the game (a data abort in
    // memcpy). Grow takes handles on one byte array at the start, while the heap can still grow, until the table has
    // doubled to the size asked for, then frees them all: the table keeps its size, so the game's own handles can't
    // make it grow below that. 131,072 slots cost 512 KB of the GC heap and 16 KB of main memory for good, twice the
    // PS Vita's Haunted Hilltop (64,000; 1.6 times the Windows player's 81,000); 262,144 would be 512 KB more, scanned
    // by every collection. Only at the start: -crow-ab's gchandles=<n> grows nothing once the governor runs.
    //   -crow-gc-handles <n>    the table grown to n slots (a power of two; the PS Vita's default 131072, logged as
    //                           CROW_GC handles: <table> table ...); 0 leaves it to IL2CPP (the default elsewhere).
    //   -crow-gc-handles-count [s]  s seconds (default 2) after each scene load, and at the start, a CROW_GC
    //                           handles line: per table (pinned, normal, weak) the handles in use, the highest slot in
    //                           use and the table's size as far as seen. libil2cpp has no count, so each slot is read
    //                           (GCHandle.Target of the handle made from its number: GetTarget returns the entry when
    //                           the slot's bit is set, null otherwise and past the table's end), twice the size seen
    //                           (at least 131,072 slots, and on while the upper half read has handles): a probe, off
    //                           by default, and a main-thread hitch of its own (524,288 slots read in 362-379 ms on
    //                           the PS Vita, 7-13 in the Windows player): frame times measured with it on are off.
    //                           IL2CPP players (Mono refuses a free slot's handle: it says so).
    // -crow-ab / -crow-cycle take gchandles=<n> (grows the table then, unless the PS Vita's governor runs: see above)
    // and gchandles=count (a count line then).
    static class GcHandles
    {
        const int VitaSlots = 131072, MaxSlots = 1 << 20;
        const int Weak = 1, Normal = 3, Pinned = 4;  // a handle's low 3 bits: its table + 1
        static readonly byte[] anchor = new byte[1];  // pinnable (GCHandle.Alloc checks)
        const int grown = Pinned;  // the engine's table
        static readonly Dictionary<int, int> sizes = new Dictionary<int, int>();  // per table: the power of two above the highest slot seen
        static float countAfter = -1f, countAt = -1f;

        // From Memory.GcSettings, at the start (on the PS Vita before the governor starts).
        internal static void Install(string[] args)
        {
            try
            {
                Grow(CrowFlags.Int(args, "-crow-gc-handles", Application.platform == RuntimePlatform.PSP2 ? VitaSlots : 0));
                if (!CrowFlags.Has(args, "-crow-gc-handles-count")) return;
                string after = CrowFlags.Optional(args, "-crow-gc-handles-count");
                float seconds;
                countAfter = after != null && float.TryParse(after, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds >= 0f ? seconds : 2f;
                Count("start");
                CrowEvents.Loaded("GcHandles.sceneLoaded", (scene, mode) => countAt = Time.realtimeSinceStartup + countAfter);
                CrowEvents.BeforeRender("GcHandles.Tick", () =>
                {
                    if (countAt < 0f || Time.realtimeSinceStartup < countAt) return;
                    countAt = -1f;
                    Count("loaded");
                });
            }
            catch (Exception e)
            {
                Debug.Log("CROW_GC handles failed: " + e.GetType().Name + " " + e.Message);
            }
        }

        internal static bool Change(string key, string value)
        {
            if (key != "gchandles") return false;
            int slots;
            if (value == "count") Count("asked");
            else if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out slots)) Debug.Log("CROW_GC handles: no change " + value);
            // With the governor running, the doubling Grow makes is the one that crashed Haunted Hilltop on 21 Oct: a new
            // entries array the held heap may not have, which alloc_handle copies into unchecked.
            else if (Memory.Governing) Debug.Log("CROW_GC handles: not grown to " + slots + " while the governor runs (it may hold the GC heap); grow at the start (-crow-gc-handles)");
            else Grow(slots);
            return true;
        }

        static string Name(int table)
        {
            return table == Pinned ? "pinned" : table == Normal ? "normal" : "weak";
        }

        static int Size(int table)
        {
            int size;
            return sizes.TryGetValue(table, out size) ? size : 0;
        }

        static void Seen(int table, int slot)
        {
            int size = 32;
            while (size <= slot && size < MaxSlots * 2) size <<= 1;
            if (slot >= 0 && size > Size(table)) sizes[table] = size;
        }

        // Handles on one object until one lands in the upper half of the size asked for (the table has doubled to it,
        // or was that big), then all freed. At most half the slots and one: 65,537 handles and a 256 KB array of them
        // for 131,072 (103-104 ms at the PS Vita's start, 65,038 handles; 2-4 ms in the Windows IL2CPP player).
        static void Grow(int slots)
        {
            if (slots <= 0) return;
            int wanted = 32;
            while (wanted < slots && wanted < MaxSlots) wanted <<= 1;
            string name = Name(grown);
            if (Size(grown) >= wanted)
            {
                Debug.Log("CROW_GC handles: the " + name + " table has " + Size(grown) + " slots already (asked " + wanted + ")");
                return;
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var taken = new GCHandle[wanted / 2 + 1];
            var type = System.Runtime.InteropServices.GCHandleType.Pinned;
            int count = 0, highest = -1;
            try
            {
                while (count < taken.Length)
                {
                    GCHandle handle = GCHandle.Alloc(anchor, type);
                    taken[count++] = handle;
                    int slot = (int)(GCHandle.ToIntPtr(handle).ToInt64() >> 3);
                    if (slot > highest) highest = slot;
                    if (slot >= wanted / 2) break;
                }
            }
            catch (Exception e)
            {
                Debug.Log("CROW_GC handles: growing stopped after " + count + " handles: " + e.GetType().Name + " " + e.Message);
            }
            finally
            {
                for (int i = 0; i < count; i++) taken[i].Free();
            }
            Seen(grown, highest);
            Debug.Log("CROW_GC handles: " + name + " table " + (highest >= wanted / 2 ? "of " + Size(grown) + " slots" : "not grown, highest slot " + highest) +
                      " (asked " + wanted + "): " + count + " handles taken and freed in " + clock.ElapsedMilliseconds + " ms");
        }

        // The handles in use per table, slot by slot (-crow-gc-handles-count).
        static void Count(string why)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var line = new StringBuilder("CROW_GC handles ").Append(why).Append(" scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            long read = 0;
            foreach (int table in new[] { Pinned, Normal, Weak })
            {
                int limit = Math.Min(Math.Max(Size(table), 65536) * 2, MaxSlots * 2);
                int used = 0, highest = -1, slot = 0;
                try
                {
                    for (; slot < limit; slot++)
                    {
                        if (GCHandle.FromIntPtr(new IntPtr((slot << 3) | table)).Target != null)
                        {
                            used++;
                            highest = slot;
                        }
                        // Handles in the upper half: the table may have doubled past what was read; read on.
                        if (slot == limit - 1 && highest >= limit / 2 && limit < MaxSlots * 2) limit *= 2;
                    }
                }
                catch (Exception e)
                {
                    Debug.Log("CROW_GC handles: no count on this player (" + Name(table) + " slot " + slot + ": " + e.GetType().Name + " " + e.Message + ")");
                    return;
                }
                Seen(table, highest);
                read += limit;
                line.Append(' ').Append(Name(table)).Append(": used=").Append(used).Append(" highest=").Append(highest).Append(" table=").Append(Size(table));
            }
            Debug.Log(line.Append(" (").Append(read).Append(" slots read in ").Append(clock.ElapsedMilliseconds).Append(" ms)").ToString());
        }
    }

    // Bytes IL2CPP's collector (Boehm, linked into the game module) has handed out since the start, for every module
    // that measures allocation: -1 where there is no such function (Mono players), found at the first call.
    static class GcTotal
    {
        [System.Runtime.InteropServices.DllImport("__Internal")] static extern UIntPtr GC_get_total_bytes();
        static bool missing;

        internal static long Bytes()
        {
            if (missing) return -1;
            try
            {
                return Native();
            }
            catch (Exception)
            {
                missing = true;
                return -1;
            }
        }

        // Apart, as Mono looks the function up when it compiles a caller (see Audio.cs's Threads.SelfNative).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static long Native()
        {
            return (long)(ulong)GC_get_total_bytes();
        }
    }

    // The PS Vita's free memory from UnityEngine.PSVita.Diagnostics (kept by link.xml; by reflection, as other players
    // have no PSVitaModule), each function as a delegate made once: MethodInfo.Invoke allocates at every call. Null
    // where the module or the function is missing.
    static class VitaMemory
    {
        internal static readonly Func<int> FreeLPDDR, FreeCDRAM, FreePHYSCONT;

        static VitaMemory()
        {
            Type diagnostics = Type.GetType("UnityEngine.PSVita.Diagnostics, UnityEngine.PSVitaModule");
            if (diagnostics == null) return;
            FreeLPDDR = Make(diagnostics.GetMethod("GetFreeMemoryLPDDR"));
            FreeCDRAM = Make(diagnostics.GetMethod("GetFreeMemoryCDRAM"));
            FreePHYSCONT = Make(diagnostics.GetMethod("GetFreeMemoryPHYSCONT"));
        }

        static Func<int> Make(MethodInfo method)
        {
            if (method == null) return null;
            try
            {
                return (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), method);
            }
            catch (Exception)
            {
                return () => (int)method.Invoke(null, null);
            }
        }

        // The free main memory in MB, or -1 where there is none to read.
        internal static float FreeMainMB()
        {
            try
            {
                return FreeLPDDR == null ? -1f : FreeLPDDR() / 1048576f;
            }
            catch (Exception)
            {
                return -1f;
            }
        }
    }

    // The USSE memory of GXM's shader patcher in Unity 2018.2.19f1's PS Vita player, read from the player's
    // own objects (no API gives it): every patched vertex and fragment program's code lives there, Unity
    // frees a shader's only when the shader unloads, and when it is full the player draws with a fragment
    // program pointer it never set and crashes on the render thread (pipeline.patcher_usse,
    // work/scratch/fix/CRASH.md). The player's data segment loads at 0x82600000 (linked at 0x825CB000; both
    // crash dumps): g_RealGfxDevice (the GfxDeviceGXM, its vtable at 0x825B00DC in the text, which is not
    // relocated) and the patcher's buffer and USSE sizes are fixed addresses there; the device holds its
    // SceGxmShaderPatcher at +0x2E90. Find checks every step (the
    // patcher's first words are Unity's host allocation callbacks and itself; its USSE heap at +0x180 has
    // the base at +0x74 and the size of the global) and gives up otherwise. The two counters read are the
    // patcher's +0x8C and +0x90, taken for its fragment and vertex USSE bytes: in the crash dumps they add
    // up to 97-98% of the 512 KB.
    static class ShaderPatcher
    {
        const int RealDevice = unchecked((int)0x82606A5C), BufferSize = unchecked((int)0x8260D1E0), UsseSize = unchecked((int)0x8260D1E4);
        const int DevicePatcher = 0x2E90, DeviceVtable = unchecked((int)0x825B00DC);  // GfxDeviceGXM's vtable (text, not relocated)
        const int HostAlloc = unchecked((int)0x818AA3A9), HostFree = unchecked((int)0x818AA3DB);
        static IntPtr patcher;
        internal static int UsseBytes;

        static int Word(int address)
        {
            return System.Runtime.InteropServices.Marshal.ReadInt32(new IntPtr(address));
        }

        // A pointer into the user heaps, word-aligned.
        static bool Plausible(int address)
        {
            uint a = (uint)address;
            return a >= 0x81000000u && a < 0xA0000000u && (a & 3u) == 0u;
        }

        // Whether the patcher was found; logs CROW_PATCHER either way.
        internal static bool Find()
        {
            string why = null;
            try
            {
                if (Application.unityVersion != "2018.2.19f1") why = "another player (" + Application.unityVersion + ")";
                else if (Word(BufferSize) != 0x80000 || (Word(UsseSize) & 0xFFF) != 0 || Word(UsseSize) < 0x80000)
                    why = "the player's data segment is not where it was (sizes " + Word(BufferSize).ToString("X") + " " + Word(UsseSize).ToString("X") + ")";
                else
                {
                    int device = Word(RealDevice);
                    int p = Plausible(device) && Word(device) == DeviceVtable ? Word(device + DevicePatcher) : 0;
                    if (!Plausible(p)) why = "no device or patcher (" + device.ToString("X8") + " " + p.ToString("X8") + ")";
                    else if (Word(p + 4) != HostAlloc || Word(p + 8) != HostFree || Word(p + 0xC) != p)
                        why = "the patcher at " + p.ToString("X8") + " is not Unity's";
                    else if (Word(p + 0x184) != Word(p + 0x74) || Word(p + 0x188) != Word(UsseSize))
                        why = "the patcher's USSE heap is not as expected";
                    else
                    {
                        patcher = new IntPtr(p);
                        UsseBytes = Word(UsseSize);
                        Debug.Log("CROW_PATCHER USSE " + UsseBytes / 1024 + " KB at " + Word(p + 0x74).ToString("X8") +
                                  ", buffer " + Word(BufferSize) / 1024 + " KB; MEMWATCH lines give usseKB=fragment+vertex/size");
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                why = e.GetType().Name;
            }
            Debug.Log("CROW_PATCHER not read: " + why);
            return false;
        }

        internal static void Usse(out int fragment, out int vertex)
        {
            int p = patcher.ToInt32();
            fragment = Word(p + 0x8C);
            vertex = Word(p + 0x90);
        }
    }
}

// The engine's internal call behind UnityEngine.PSVitaModule's PSVitaVideoPlayer.TransferMemToMonoHeap
// (Memory.PhyContPool). The linker strips that module's class from the build, and quickcode can't
// change what it keeps, so CrowCompat declares the call under the name the PS Vita player registers
// it by ("UnityEngine.PSVita.PSVitaVideoPlayer::TransferMemToMonoHeap()"). PS Vita only: other
// players have no such call.
namespace UnityEngine.PSVita
{
    public static class PSVitaVideoPlayer
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall)]
        public static extern bool TransferMemToMonoHeap();
    }

    // UnityEngine.PSVitaModule's Utility.EnableHeapBlockSorting (Memory.HeapSort), declared the same way
    // ("UnityEngine.PSVita.Utility::EnableHeapBlockSorting").
    public static class Utility
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall)]
        public static extern void EnableHeapBlockSorting();
    }
}
