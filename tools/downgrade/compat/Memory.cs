using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

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
//   -crow-memory-census     with it, MEMCENSUS lines: managed memory by the Unity object or static that holds it.
//   -crow-memory-unload     before each MEMTYPES pass, Resources.UnloadUnusedAssets and a GC, so the
//                           pass counts what stays loaded.
// MeshRelease, below, frees what MeshCombineStudio keeps after combining (on the PS Vita).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public class Memory : MonoBehaviour
    {
        const float MB = 1048576f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static float every;
        static bool types, typesEvery, unload, fsmCensus, managedCensus;
        static Type vitaDiagnostics;
        float started, lastBeat, typesAt = -1f;

        // From Diag.Install (a RuntimeInitializeOnLoadMethod: the build lists those, so quickcode can't add one).
        internal static void Install()
        {
            string[] args = ProbeFiles.Args;
            MeshRelease.Install(args);
            int index = Array.IndexOf(args, "-crow-memory");
            if (index < 0) return;
            every = 30f;
            float value;
            if (index + 1 < args.Length && float.TryParse(args[index + 1], NumberStyles.Float, Inv, out value)) every = value;
            types = args.Contains("-crow-memory-types");
            typesEvery = args.Contains("-crow-memory-types-every");
            unload = args.Contains("-crow-memory-unload");
            fsmCensus = args.Contains("-crow-memory-fsm");
            managedCensus = args.Contains("-crow-memory-census");
            if (Application.platform == RuntimePlatform.PSP2)
                vitaDiagnostics = Type.GetType("UnityEngine.PSVita.Diagnostics, UnityEngine.PSVitaModule");
            var probe = new GameObject("CrowMemory");
            DontDestroyOnLoad(probe);
            probe.AddComponent<Memory>();
        }

        void Start()
        {
            started = lastBeat = Time.realtimeSinceStartup;
            SceneManager.sceneLoaded += (scene, mode) => { typesAt = Time.realtimeSinceStartup + 5f; };
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
            if (vitaDiagnostics == null) return " vita=missing";
            Func<string, string> free = name => F((int)vitaDiagnostics.GetMethod(name).Invoke(null, null) / MB);
            return " freeLPDDR=" + free("GetFreeMemoryLPDDR") + " freeCDRAM=" + free("GetFreeMemoryCDRAM") +
                   " freePHYSCONT=" + free("GetFreeMemoryPHYSCONT");
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
        static long MeshBytes(Mesh mesh)
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

        // -crow-memory-census: with the MEMTYPES pass, MEMCENSUS lines: the managed memory reachable
        // from each Unity object (MonoBehaviours and ScriptableObjects, by class) and from each class's
        // static fields, walking fields by reflection, each object counted once (for the first root that
        // reaches it). Sizes are estimates for the running player's pointer size. Slow, and it allocates
        // a set of every object it visits: for Windows players.
        sealed class Identity : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }

        static readonly Dictionary<Type, FieldInfo[]> censusFields = new Dictionary<Type, FieldInfo[]>();

        static FieldInfo[] InstanceFields(Type type)
        {
            FieldInfo[] fields;
            if (censusFields.TryGetValue(type, out fields)) return fields;
            var list = new List<FieldInfo>();
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
                list.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(f => !f.FieldType.IsPrimitive && !f.FieldType.IsEnum && !f.FieldType.IsPointer));
            return censusFields[type] = list.ToArray();
        }

        static int PrimitiveSize(Type type)
        {
            if (type == typeof(byte) || type == typeof(bool) || type == typeof(sbyte)) return 1;
            if (type == typeof(short) || type == typeof(ushort) || type == typeof(char)) return 2;
            if (type == typeof(long) || type == typeof(ulong) || type == typeof(double)) return 8;
            if (type.IsPrimitive || type.IsEnum) return 4;
            return -1;
        }

        static long Walk(object root, HashSet<object> seen)
        {
            long total = 0;
            var stack = new Stack<object>();
            stack.Push(root);
            int pointer = IntPtr.Size;
            while (stack.Count > 0)
            {
                object o = stack.Pop();
                if (o == null) continue;
                Type type = o.GetType();
                if (!type.IsValueType && !seen.Add(o)) continue;
                if (o is Type || o is MemberInfo || o is Delegate || o is System.Reflection.Assembly) continue;
                var text = o as string;
                if (text != null) { total += 20 + 2 * text.Length; continue; }
                var array = o as Array;
                if (array != null)
                {
                    Type element = type.GetElementType();
                    int size = PrimitiveSize(element);
                    if (size > 0) { total += 32 + (long)size * array.Length; continue; }
                    total += 32 + (long)pointer * array.Length;
                    if (element.IsValueType) total += 0;  // structs: their fields walked below
                    foreach (object item in array) if (item != null && !(item is UnityEngine.Object)) stack.Push(item);
                    continue;
                }
                if (!type.IsValueType) total += 2 * pointer;
                foreach (FieldInfo field in InstanceFields(type))
                {
                    object value;
                    try { value = field.GetValue(o); } catch (Exception) { continue; }
                    total += field.FieldType.IsValueType ? 0 : pointer;
                    if (value == null || value is UnityEngine.Object) continue;
                    stack.Push(value);
                }
                if (!type.IsValueType) total += InstanceFields(type).Length == 0 ? pointer : 0;
            }
            return total;
        }

        static void ManagedCensus()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var seen = new HashSet<object>(new Identity());
            var groups = new Dictionary<string, long>();
            Action<string, object> add = (name, root) =>
            {
                long bytes = Walk(root, seen);
                long sum;
                groups.TryGetValue(name, out sum);
                groups[name] = sum + bytes;
            };
            foreach (Type type in AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => { try { return a.GetTypes(); } catch (Exception) { return new Type[0]; } }))
            {
                if (type.IsGenericTypeDefinition) continue;
                FieldInfo[] statics;
                try { statics = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch (Exception) { continue; }
                foreach (FieldInfo field in statics)
                {
                    if (field.FieldType.IsPrimitive || field.IsLiteral) continue;
                    object value;
                    try { value = field.GetValue(null); } catch (Exception) { continue; }
                    if (value != null && !(value is UnityEngine.Object)) add("static " + type.FullName, value);
                }
            }
            foreach (UnityEngine.Object asset in Resources.FindObjectsOfTypeAll<MonoBehaviour>().Cast<UnityEngine.Object>().Concat(Resources.FindObjectsOfTypeAll<ScriptableObject>().Cast<UnityEngine.Object>()))
                add(asset.GetType().FullName, asset);
            long all = groups.Values.Sum();
            Debug.Log("MEMCENSUS total=" + F(all / MB) + "MB objects=" + seen.Count + " ms=" + clock.ElapsedMilliseconds);
            foreach (KeyValuePair<string, long> pair in groups.OrderByDescending(p => p.Value).Take(30))
                Debug.Log("MEMCENSUS " + F(pair.Value / MB) + " " + pair.Key);
        }

        // -crow-memory-fsm: with the MEMTYPES pass, a MEMFSM line: PlayMaker FSMs loaded (also inactive),
        // their states, the states whose actions are loaded and those actions, and the item counts of
        // the states' ActionData lists (each serialized action parameter is an object or string there,
        // and a loaded action holds its own copy).
        static string FsmCensus()
        {
            const BindingFlags field = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type fsmComponent = Type.GetType("PlayMakerFSM, PlayMaker");
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
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (unload)
            {
                Resources.UnloadUnusedAssets();
                GC.Collect();
            }
            if (fsmCensus)
            {
                Debug.Log(FsmCensus());
                if (unload) GC.Collect();
            }
            if (managedCensus) ManagedCensus();
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

    // MeshCombineStudio combines the meshes under its MeshCombiners' search parents when a scene
    // starts, into new meshes it makes unreadable. It reads each source mesh (which must be readable,
    // so it keeps a CPU copy of its vertices and indices) into a managed MeshCache, and keeps those
    // caches, and the sources' CPU copies, for as long as the scene is loaded: nothing clears
    // MeshCombineJobManager.meshCacheDictionary. On the PS Vita, once every MeshCombiner has combined
    // and no job is left, MeshRelease clears the caches and makes the cached source meshes unreadable
    // (UploadMeshData(true) frees the CPU copy; the GPU's stays, so the original renderers draw as
    // before), except meshes a MeshCollider or a particle system also uses, and meshes under the search
    // parents of a run-time MeshCombiner that hasn't combined yet, active or not: most areas have
    // several, switched on later (Station Square 14, Gift Shop 7), and a combiner that meets a source
    // mesh made unreadable skips it with an error and leaves its renderer as it was, a draw call of
    // its own (Gift Shop's "Cube" and "gums"). Those are released at the check after they combine.
    // Off with -crow-keep-mesh-caches; on other players with -crow-release-meshes.
    // -crow-no-mesh-combine disables every MeshCombiner when its scene has loaded (before its Start),
    // to measure what combining costs.
    public class MeshRelease : MonoBehaviour
    {
        static Type combinerType, managerType;
        static FieldInfo instancesField, inRuntimeField, combinedField, combinerJobsField, managerField, cacheField, managerJobsField;
        static FieldInfo searchOptionsField, parentField, parentsField;
        static int released;
        static long releasedBytes;
        float nextCheck;

        internal static void Install(string[] args)
        {
            bool on = Application.platform == RuntimePlatform.PSP2 ? !args.Contains("-crow-keep-mesh-caches") : args.Contains("-crow-release-meshes");
            combinerType = Type.GetType("MeshCombineStudio.MeshCombiner, MeshCombineStudio.Runtime");
            managerType = Type.GetType("MeshCombineStudio.MeshCombineJobManager, MeshCombineStudio.Runtime");
            if (combinerType == null || managerType == null) return;
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic;
            instancesField = combinerType.GetField("instances", any | BindingFlags.Static);
            inRuntimeField = combinerType.GetField("combineInRuntime", any | BindingFlags.Instance);
            combinedField = combinerType.GetField("combined", any | BindingFlags.Instance);
            combinerJobsField = combinerType.GetField("meshCombineJobs", any | BindingFlags.Instance);
            managerField = managerType.GetField("instance", any | BindingFlags.Static);
            cacheField = managerType.GetField("meshCacheDictionary", any | BindingFlags.Instance);
            managerJobsField = managerType.GetField("meshCombineJobs", any | BindingFlags.Instance);
            searchOptionsField = combinerType.GetField("searchOptions", any | BindingFlags.Instance);
            if (searchOptionsField != null)
            {
                parentField = searchOptionsField.FieldType.GetField("parent", any | BindingFlags.Instance);
                parentsField = searchOptionsField.FieldType.GetField("parentGOs", any | BindingFlags.Instance);
            }
            if (instancesField == null || inRuntimeField == null || combinedField == null || managerField == null || cacheField == null)
            {
                Debug.Log("MeshRelease: MeshCombineStudio's fields not found");
                combinerType = null;
                return;
            }
            if (args.Contains("-crow-no-mesh-combine"))
                SceneManager.sceneLoaded += (scene, mode) =>
                {
                    var instances = instancesField.GetValue(null) as System.Collections.IList;
                    if (instances == null) return;
                    foreach (object instance in instances.Cast<object>().ToArray())
                    {
                        var behaviour = instance as Behaviour;
                        if (behaviour != null) behaviour.enabled = false;
                    }
                    Debug.Log("MeshRelease: disabled " + instances.Count + " MeshCombiners");
                };
            if (!on) return;
            var holder = new GameObject("CrowMeshRelease");
            DontDestroyOnLoad(holder);
            holder.AddComponent<MeshRelease>();
        }

        static int Count(object collection)
        {
            var items = collection as System.Collections.ICollection;
            return items == null ? 0 : items.Count;
        }

        static System.Collections.IDictionary Cache()
        {
            object manager = managerField.GetValue(null);
            return manager == null ? null : cacheField.GetValue(manager) as System.Collections.IDictionary;
        }

        // Whether every active run-time MeshCombiner has combined and no job is left.
        static bool Idle(out int combiners, out int combined)
        {
            combiners = combined = 0;
            object manager = managerField.GetValue(null);
            bool idle = manager != null && (managerJobsField == null || Count(managerJobsField.GetValue(manager)) == 0);
            var instances = instancesField.GetValue(null) as System.Collections.IList;
            if (instances == null) return idle;
            foreach (object instance in instances)
            {
                var behaviour = instance as Behaviour;
                if (behaviour == null || !behaviour.isActiveAndEnabled || !(bool)inRuntimeField.GetValue(instance)) continue;
                combiners++;
                if ((bool)combinedField.GetValue(instance)) combined++;
                else idle = false;
                if (combinerJobsField != null && Count(combinerJobsField.GetValue(instance)) > 0) idle = false;
            }
            return idle;
        }

        // For the probe's MEMTYPES line: combiners done of active ones, cached meshes, meshes released.
        internal static string Report()
        {
            if (combinerType == null) return "";
            int combiners, combined;
            Idle(out combiners, out combined);
            System.Collections.IDictionary cache = Cache();
            return " mcs=" + combined + "/" + combiners + ",cache" + (cache == null ? 0 : cache.Count) +
                   ",released" + released + "," + (releasedBytes / 1048576f).ToString("0.0", CultureInfo.InvariantCulture) + "MB";
        }

        void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            int combiners, combined;
            System.Collections.IDictionary cache = Cache();
            if (cache == null || cache.Count == 0 || !Idle(out combiners, out combined)) return;
            var keep = new HashSet<Mesh>();
            // Called directly (CrowCompat references the physics module), so the linker keeps the getter.
            foreach (MeshCollider collider in Resources.FindObjectsOfTypeAll<MeshCollider>())
                if (collider.sharedMesh != null) keep.Add(collider.sharedMesh);
            var meshes = new Mesh[8];
            foreach (ParticleSystemRenderer renderer in Resources.FindObjectsOfTypeAll<ParticleSystemRenderer>())
            {
                int count = Math.Min(renderer.GetMeshes(meshes), meshes.Length);
                for (int i = 0; i < count; i++) if (meshes[i] != null) keep.Add(meshes[i]);
            }
            foreach (ParticleSystem system in Resources.FindObjectsOfTypeAll<ParticleSystem>())
            {
                Mesh mesh = system.shape.mesh;
                if (mesh != null) keep.Add(mesh);
            }
            int pending = KeepPending(keep);
            int now = 0;
            long bytes = 0;
            foreach (object key in cache.Keys)
            {
                var mesh = key as Mesh;
                if (mesh == null || !mesh.isReadable || keep.Contains(mesh)) continue;
                long indices = 0;
                for (int sub = 0; sub < mesh.subMeshCount; sub++) indices += mesh.GetIndexCount(sub);
                bytes += mesh.vertexCount * 32L + indices * 2L;
                mesh.UploadMeshData(true);
                now++;
            }
            int entries = cache.Count;
            cache.Clear();
            released += now;
            releasedBytes += bytes;
            Debug.Log("MeshRelease: " + combined + " of " + combiners + " MeshCombiners done; cleared " + entries +
                      " mesh caches, made " + now + " source meshes unreadable (about " +
                      (bytes / 1048576f).ToString("0.0", CultureInfo.InvariantCulture) + " MB); " + pending +
                      " MeshCombiners still to combine keep theirs");
        }

        // Adds the meshes under the search parents of every run-time MeshCombiner in a loaded scene that
        // hasn't combined, active or not, inactive children included (MeshCombiner searches with
        // GetComponentsInChildren(true)); its tag, layer and other conditions are left out, so a few
        // more stay readable than it takes. Returns how many such combiners there are.
        static int KeepPending(HashSet<Mesh> keep)
        {
            if (searchOptionsField == null || (parentField == null && parentsField == null)) return 0;
            int pending = 0;
            foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(combinerType))
            {
                var combiner = found as Component;
                if (combiner == null || !combiner.gameObject.scene.IsValid()) continue;  // a prefab asset
                if (!(bool)inRuntimeField.GetValue(combiner) || (bool)combinedField.GetValue(combiner)) continue;
                pending++;
                object options = searchOptionsField.GetValue(combiner);
                if (options == null) continue;
                var parents = new List<GameObject>();
                var many = parentsField == null ? null : parentsField.GetValue(options) as GameObject[];
                if (many != null) parents.AddRange(many);
                var one = parentField == null ? null : parentField.GetValue(options) as GameObject;
                if (one != null) parents.Add(one);
                foreach (GameObject parent in parents)
                {
                    if (parent == null) continue;
                    foreach (MeshFilter filter in parent.GetComponentsInChildren<MeshFilter>(true))
                        if (filter.sharedMesh != null) keep.Add(filter.sharedMesh);
                }
            }
            return pending;
        }
    }
}
