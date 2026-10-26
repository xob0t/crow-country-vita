using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// The editor's API updater then takes CrowCompat as up to date and leaves it alone, as it does the
// rewritten assemblies (DowngradeAssemblies.MarkApiVersion); its check failed on CrowCompat every
// time it changed (it could not resolve UnityEngine.CoreModule for RuntimeInitializeOnLoadMethod's enum).
// Written out here, while the rewriter's mark takes the target from CROW_API_VERSION (pipeline.py): keep it the
// pipeline's target (common.TARGET) if that ever changes.
[assembly: UnityAPICompatibilityVersion("2018.2.19f1")]

// Implementations of Unity 2022 APIs that Unity 2018.2 lacks. The assembly
// downgrader redirects missing references here by naming convention:
//   missing type  Namespace.Name        -> CrowCompat.Namespace_Name
//   missing member Namespace.Type::M    -> static CrowCompat.Namespace_Type.M
// Instance members take the instance first (by ref for structs); constructors
// are static methods named "ctor".
//
// CrowCompat is this file and compat/*.cs, compiled together in the order of
// pipeline.COMPAT_SOURCES (all in namespace CrowCompat; the rewriter finds its classes
// by name, not by file). Each file's header says what it holds; pipeline.COMPAT_SOURCES lists them all.
// This one: AssemblyAliases and TypeNames (the renamed assemblies; the save binder's cache).

namespace CrowCompat
{
    // The downgrader renames Assembly-CSharp(-firstpass), which Unity reserves for
    // project scripts. Saves written by BinaryFormatter and assembly-qualified type
    // names still use the original names; resolve those to the renamed assemblies.
    public static class AssemblyAliases
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string name = new System.Reflection.AssemblyName(args.Name).Name;
                // The renamed assemblies: keep in step with DowngradeAssemblies.cs' renames and pipeline.py's names.
                string target = name == "Assembly-CSharp" ? "Recovered.CrowCountry"
                    : name == "Assembly-CSharp-firstpass" ? "Recovered.Firstpass" : null;
                return target == null ? null
                    : AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == target);
            };
        }
    }

    // Type.GetType calls in the renamed assemblies. Names written by the source version
    // (BinaryFormatter saves, generic arguments the game's own binder passes through)
    // still say Assembly-CSharp; AssemblyResolve never fires under IL2CPP (the PS Vita's player).
    // A name without an assembly is looked up in the calling assembly, which is now this one
    // instead of the game's, so such names are also looked up in every loaded assembly.
    public static class TypeNames
    {
        // The PS Vita's CrowCompat (lookups also run on the loading thread, where Unity's API is off limits).
#if CROW_VITA
        const bool Vita = true;
#else
        const bool Vita = false;
#endif
        public static Type GetType(string name) { return GetType(name, false, false); }
        public static Type GetType(string name, bool throwOnError) { return GetType(name, throwOnError, false); }
        // Names found are remembered: IL2CPP parses the name and searches its images every time.
        // On every thread: PlayMaker looks its action and field types up on the loading thread, as
        // each FSM is read (1-2 s of the PS Vita's title load went to these lookups uncached).
        static readonly Dictionary<string, Type> found = new Dictionary<string, Type>();
        static readonly Dictionary<string, Type> foundIgnoringCase = new Dictionary<string, Type>();

        public static Type GetType(string name, bool throwOnError, bool ignoreCase)
        {
            Dictionary<string, Type> cache = name != null ? ignoreCase ? foundIgnoringCase : found : null;
            Type type;
            bool known = false;
            if (cache != null)
                lock (cache) known = cache.TryGetValue(name, out type);
            else type = null;
            if (known)
                return type != null || !throwOnError ? type : Type.GetType(Rename(name), true, ignoreCase);
            string renamed = Rename(name);
            type = Type.GetType(renamed, false, ignoreCase);
            if (type == null && renamed.IndexOf(',') < 0)
                foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if ((type = assembly.GetType(renamed, false, ignoreCase)) != null) break;
            if (type == null)
            {
                // A miss scans every assembly. The PS Vita's IL2CPP player never loads another
                // one, so a miss there stays a miss; other players (Mono) could, so they keep asking.
                if (cache != null && Vita) lock (cache) cache[name] = null;
                return throwOnError ? Type.GetType(renamed, true, ignoreCase) : null;
            }
            if (cache != null) lock (cache) cache[name] = type;
            return type;
        }

        // Replaces new BinaryFormatter(): the formatter starts with the alias binder.
        public static System.Runtime.Serialization.Formatters.Binary.BinaryFormatter NewBinaryFormatter()
        {
            return new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter { Binder = new AliasBinder(null) };
        }

        // Replaces BinaryFormatter.Binder assignments. When the game's binder cannot
        // resolve a name, the formatter would load the named assembly itself, which
        // only works for the renamed ones through AssemblyResolve.
        public static void SetBinder(System.Runtime.Serialization.Formatters.Binary.BinaryFormatter formatter,
                                     System.Runtime.Serialization.SerializationBinder binder)
        {
            formatter.Binder = binder is AliasBinder ? binder : new AliasBinder(binder);
        }

        sealed class AliasBinder : System.Runtime.Serialization.SerializationBinder
        {
            readonly System.Runtime.Serialization.SerializationBinder inner;  // the game's, or null
            public AliasBinder(System.Runtime.Serialization.SerializationBinder inner) { this.inner = inner; }

            // -compat-log-binds logs each binding.
            static readonly bool log = Environment.GetCommandLineArgs().Contains("-compat-log-binds");

            // The answer for a name and binder type does not change; remember it.
            static readonly Dictionary<string, Type> bindings = new Dictionary<string, Type>();

            public override Type BindToType(string assemblyName, string typeName)
            {
                if (log || !MainThread.IsCurrent) return Bind(assemblyName, typeName);
                string key = (inner == null ? "" : inner.GetType().FullName) + "|" + assemblyName + "|" + typeName;
                Type type;
                if (!bindings.TryGetValue(key, out type)) bindings[key] = type = Bind(assemblyName, typeName);
                return type;
            }

            Type Bind(string assemblyName, string typeName)
            {
                // Saves the port wrote before BindToName left the formatter's own names alone (below)
                // record a Type value as System.RuntimeType, with UnitySerializationHolder's members.
                if (typeName == "System.RuntimeType" && assemblyName.StartsWith("mscorlib"))
                    return Type.GetType("System.UnitySerializationHolder");
                Type bound = inner == null ? null : inner.BindToType(assemblyName, typeName);
                Type type = bound ?? Type.GetType(Rename(typeName) + ", " + RenameAssembly(assemblyName));
                if (log) Debug.Log("BIND " + assemblyName + " | " + typeName + " -> " + (bound != null ? "game " : "alias ") +
                                   (type == null ? "null" : type.AssemblyQualifiedName));
                return type;
            }

            // Saves name the original assemblies, so they stay compatible with the source version. Only
            // names that change are given: a name the binder gives replaces the one an ISerializable
            // object chose (SerializationInfo.SetType), so a Type value (FsmObject's, FsmArray's and
            // FsmEnum's types, in every gameplay save's globals) was written as System.RuntimeType
            // instead of UnitySerializationHolder, and no save with one loaded again ("The
            // constructor to deserialize an object of type 'System.RuntimeType' was not found"): Load
            // Game read an empty scene name, and the PS Vita player died on the black screen after.
            public override void BindToName(Type serializedType, out string assemblyName, out string typeName)
            {
                assemblyName = typeName = null;
                if (inner != null) inner.BindToName(serializedType, out assemblyName, out typeName);
                if (assemblyName != null || typeName != null)
                {
                    if (assemblyName == null) assemblyName = Original(serializedType.Assembly.FullName);
                    if (typeName == null) typeName = Original(serializedType.FullName);
                    return;
                }
                string assembly = serializedType.Assembly.FullName, full = serializedType.FullName;
                if (Original(assembly) == assembly && Original(full) == full) return;
                assemblyName = Original(assembly);
                typeName = Original(full);
            }
        }

        static string Original(string name)
        {
            return name.Replace("Recovered.Firstpass,", "Assembly-CSharp-firstpass,").Replace("Recovered.CrowCountry,", "Assembly-CSharp,");
        }

        // The original assemblies' names after a comma (in a type name), or at the start (an
        // assembly name).
        static readonly System.Text.RegularExpressions.Regex assemblies =
            new System.Text.RegularExpressions.Regex(@"(?<=,\s*)Assembly-CSharp(-firstpass)?(?=\s*(,|\]|$))");
        static readonly System.Text.RegularExpressions.Regex assemblyName =
            new System.Text.RegularExpressions.Regex(@"(?<=^\s*)Assembly-CSharp(-firstpass)?(?=\s*(,|\]|$))");

        static string Rename(string name)
        {
            return Rename(name, assemblies);
        }

        static string RenameAssembly(string name)
        {
            return Rename(name, assemblyName);
        }

        static string Rename(string name, System.Text.RegularExpressions.Regex original)
        {
            if (name == null || name.IndexOf("Assembly-CSharp", StringComparison.Ordinal) < 0) return name;
            return original.Replace(name, m => m.Groups[1].Success ? "Recovered.Firstpass" : "Recovered.CrowCountry");
        }
    }

    // Whether code runs on the main thread, for caches that take no locks.
    public static class MainThread
    {
        static System.Threading.Thread main;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Record() { main = System.Threading.Thread.CurrentThread; }

        public static bool IsCurrent { get { return main == System.Threading.Thread.CurrentThread; } }
    }
}
