using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Rewrites game assemblies built against a newer Unity so they load against an
// older engine's assemblies: calls to an API missing from the target version go to the
// compat assembly (CrowCompat). The other passes (Passes lists them) patch game
// methods for the PS Vita, each with its rule in CrowCompat.
//
// Usage: DowngradeAssemblies <input dir> <output dir> <compat.dll> <roots> <reference dir>...
// <roots> is a comma-separated list of assembly names; only they and the input
// assemblies they reference are written. A name with a leading '-' is never written: its types
// go to CrowCompat like any the target lacks (Addressables, which CrowCompat replaces).
//
// The rewriter is this file and rewriter/*.cs, compiled together in the order of
// pipeline.REWRITER_SOURCES; DowngradeAssemblies is one partial class across them:
//   DowngradeAssemblies.cs  Main, Rewrite, Verify, Passes (every pass in the order they run), body sizes
//   Redirect.cs             missing types and members to CrowCompat, attributes, references,
//                           the IL helpers the passes share
//   Runtime.cs              type layouts the target's Mono rejects (interfaces)
//   Patches.cs              single game methods that call CrowCompat rules (the camera's post chain)
//   ScriptCost.cs           per-frame PlayMaker and fade work skipped where it has no effect
partial class DowngradeAssemblies
{
    // Types that exist in the target under another name.
    static readonly Dictionary<string, string> renamedTypes = new Dictionary<string, string>
    {
        { "UnityEngine.ExecuteAlways", "UnityEngine.ExecuteInEditMode" },
        { "UnityEngine.Rendering.RenderPipelineAsset", "UnityEngine.Experimental.Rendering.RenderPipelineAsset" },
    };

    // Nested types that exist in the target under another name, by full name (outer/inner).
    // InputField's end-edit event is a UnityEvent<string> in both, named SubmitEvent in 2018.
    static readonly Dictionary<string, string> renamedNestedTypes = new Dictionary<string, string>
    {
        { "UnityEngine.UI.InputField/EndEditEvent", "SubmitEvent" },
    };

    // Attributes stripped although the target has their type: its constructors differ, and they
    // only tell the editor which API version the assembly was compiled against.
    static readonly HashSet<string> strippedAttributes = new HashSet<string>
    {
        "UnityEngine.UnityAPICompatibilityVersionAttribute",
    };

    // Unity reserves these names for assemblies compiled from project scripts.
    static readonly Dictionary<string, string> renamedAssemblies = new Dictionary<string, string>
    {
        { "Assembly-CSharp", "Recovered.CrowCountry" },
        { "Assembly-CSharp-firstpass", "Recovered.Firstpass" },
    };

    static readonly Dictionary<string, TypeDefinition> target = new Dictionary<string, TypeDefinition>();
    static readonly Dictionary<string, TypeDefinition> compat = new Dictionary<string, TypeDefinition>();
    static readonly HashSet<string> shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // Input assemblies never written (the roots' "-" names): CrowCompat replaces them.
    static readonly HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static readonly List<string> problems = new List<string>();
    // Methods a pass could not inspect because a type or method they name failed to resolve
    // (printed as SKIPPED; the run still succeeds): the pass left them as they were, so its rule
    // may be missing there. For MarkNativeCallbacks that means a native callback IL2CPP would
    // reject at run time; for CacheEnumNames only a slower enum name.
    static readonly List<string> skipped = new List<string>();
    static readonly HashSet<string> strippedTypes = new HashSet<string>();
    static ModuleDefinition compatModule;

    // Which assemblies a pass runs on: all, or the two game assemblies.
    enum Scope { All, Game }

    static bool IsGameAssembly(ModuleDefinition module)
    {
        return renamedAssemblies.ContainsValue(module.Assembly.Name.Name);
    }

    class Pass
    {
        public readonly string Group;
        public readonly Scope Scope;
        public readonly Func<ModuleDefinition, int> Run;

        public Pass(string group, Scope scope, Func<ModuleDefinition, int> run)
        {
            Group = group;
            Scope = scope;
            Run = run;
        }

        public string Name { get { return Run.Method.Name; } }
    }

    // The passes after the type retargeting, in the order they run: later ones build on earlier
    // ones (their comments say which). Each returns the number of sites it changed; the stats
    // show them by group, and a pass that changed nothing in any assembly fails the run.
    static readonly Pass[] Passes =
    {
        new Pass("redirect", Scope.All, RedirectMembers),
        new Pass("redirect", Scope.All, MarkNativeCallbacks),
        new Pass("redirect", Scope.All, RetypeFields),
        // Runtime.cs
        new Pass("runtime", Scope.All, ReimplementedInterfaces),
        new Pass("runtime", Scope.All, DefaultInterfaceMethods),
        new Pass("runtime", Scope.All, EmptyPointerConstrainedCalls),
        // Patches.cs
        new Pass("patches", Scope.Game, PatchCamEffect),
        new Pass("patches", Scope.Game, PatchRewiredPlatform),
        new Pass("patches", Scope.Game, PatchKeyboardOrController),
        new Pass("patches", Scope.All, PatchGarbageCollectMesh),
        new Pass("patches", Scope.All, PatchConsoleMode),
        new Pass("patches", Scope.Game, PatchPlatformAnimation),
        new Pass("patches", Scope.Game, PatchMaraLoader),
        new Pass("patches", Scope.Game, PatchLoadSceneAction),
        new Pass("patches", Scope.All, CacheTypeOf),
        // ScriptCost.cs
        new Pass("scriptcost", Scope.All, PatchFsmExecutionStack),
        new Pass("scriptcost", Scope.All, PatchFsmLoopReset),
        new Pass("scriptcost", Scope.All, PatchFsmDelayedEvents),
        new Pass("scriptcost", Scope.Game, PatchFullScreenColour),
    };

    // Sites changed per rule (a pass, a row of a rule table, a game type a pass looks for),
    // summed over the assemblies: a rule with none in any assembly fails the run, as the game
    // no longer has what it expects. Passes run per assembly, so none in one is normal.
    static readonly Dictionary<string, int> matches = new Dictionary<string, int>();

    static void Count(string rule, int sites)
    {
        int sum;
        matches.TryGetValue(rule, out sum);
        matches[rule] = sum + sites;
    }

    // module.GetType(name), counted: a game type a pass looks for must be in some assembly.
    static TypeDefinition GameType(ModuleDefinition module, string name)
    {
        TypeDefinition type = module.GetType(name);
        Count("type " + name, type == null ? 0 : 1);
        return type;
    }

    // References in the game assemblies name each other by their new names (Recovered.*);
    // their files keep the original names.
    class RenamedAssemblyResolver : DefaultAssemblyResolver
    {
        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            string original = renamedAssemblies.Where(p => p.Value == name.Name).Select(p => p.Key).FirstOrDefault();
            return base.Resolve(original == null ? name : new AssemblyNameReference(original, name.Version));
        }
    }

    static int Main(string[] args)
    {
        string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
        compatModule = ModuleDefinition.ReadModule(args[2]);
        foreach (TypeDefinition type in compatModule.Types) compat[type.FullName] = type;
        foreach (string dir in args.Skip(4))
            foreach (string file in Directory.GetFiles(dir, "*.dll").OrderBy(Priority))
                Index(file);
        // What the target provides (its class libraries, with netstandard in Facades, and engine
        // modules) comes from the reference folders, never from the input: a game built by a newer
        // Unity ships its own copies.
        var provided = new HashSet<string>(args.Skip(4).SelectMany(dir => Directory.GetFiles(dir, "*.dll")
            .Concat(Directory.Exists(Path.Combine(dir, "Facades")) ? Directory.GetFiles(Path.Combine(dir, "Facades"), "*.dll") : new string[0]))
            .Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
        var available = Directory.GetFiles(input, "*.dll")
            .Where(f => !Path.GetFileName(f).StartsWith("UnityEngine.") && !provided.Contains(Path.GetFileNameWithoutExtension(f)))
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase);
        string[] names = args[3].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        string[] roots = names.Where(n => !n.StartsWith("-")).ToArray();
        foreach (string name in names.Where(n => n.StartsWith("-")))
        {
            available.Remove(name.Substring(1));
            excluded.Add(name.Substring(1));
        }
        string[] absent = roots.Where(root => !available.ContainsKey(root)).ToArray();
        if (absent.Length > 0)
        {
            Console.WriteLine("MISSING root assemblies, not in " + input + ": " + string.Join(", ", absent));
            return 2;
        }
        // The roots, then what they reference from the input folder (the rest, such as
        // mscorlib, comes from the reference folders).
        var pending = new Queue<string>(roots);
        while (pending.Count > 0)
        {
            string name = pending.Dequeue();
            if (!available.ContainsKey(name) || !shipped.Add(name)) continue;
            using (var module = ModuleDefinition.ReadModule(available[name]))
                foreach (AssemblyNameReference reference in module.AssemblyReferences) pending.Enqueue(reference.Name);
        }
        Directory.CreateDirectory(output);
        var resolver = new RenamedAssemblyResolver();
        resolver.AddSearchDirectory(input);
        foreach (string dir in args.Skip(4)) resolver.AddSearchDirectory(dir);
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2])));
        foreach (string file in shipped.Select(n => available[n]).ToArray())
        {
            using (var assembly = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { AssemblyResolver = resolver }))
            {
                string stats = Rewrite(assembly);
                string destination = Path.Combine(output, assembly.Name.Name + ".dll");
                assembly.Write(destination);
                Console.WriteLine(Path.GetFileName(file) + " -> " + Path.GetFileName(destination) + " " + stats);
            }
        }
        Verify(output, args.Skip(4).Concat(new[] { Path.GetDirectoryName(Path.GetFullPath(args[2])) }));
        foreach (string problem in problems.Distinct()) Console.WriteLine("UNRESOLVED " + problem);
        foreach (string site in skipped.Distinct()) Console.WriteLine("SKIPPED " + site);
        string[] unmatched = matches.Where(m => m.Value == 0).Select(m => m.Key).ToArray();
        foreach (string rule in unmatched) Console.WriteLine("NO MATCH " + rule + " (in none of the assemblies; the game no longer has what it expects)");
        return problems.Count > 0 ? 3 : unmatched.Length > 0 ? 4 : 0;
    }

    // Every type and member the written assemblies reference must resolve against each other, the
    // target's engine and CrowCompat, as Unity's linker resolves them (it stops at the first that
    // does not, after a build of the whole player). The input folder is not searched: it holds the
    // source version's assemblies. A failure is reported as UNRESOLVED.
    static void Verify(string output, IEnumerable<string> references)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (string dir in resolver.GetSearchDirectories()) resolver.RemoveSearchDirectory(dir);
        resolver.AddSearchDirectory(output);
        foreach (string dir in references) resolver.AddSearchDirectory(dir);
        foreach (string file in Directory.GetFiles(output, "*.dll"))
        {
            using (var module = ModuleDefinition.ReadModule(file, new ReaderParameters { AssemblyResolver = resolver }))
            {
                foreach (TypeReference type in module.GetTypeReferences())
                {
                    try { if (type.Resolve() == null) problems.Add(module.Name + ": verify type " + type.FullName); }
                    catch (AssemblyResolutionException e) { problems.Add(module.Name + ": verify type " + type.FullName + " (" + e.AssemblyReference.Name + ")"); }
                }
                if (module.Assembly.HasSecurityDeclarations || AllTypes(module.Types).Any(t => t.HasSecurityDeclarations || t.Methods.Any(m => m.HasSecurityDeclarations)))
                    problems.Add(module.Name + ": verify security declarations left");
                foreach (TypeReference type in AttributeTypes(module))
                {
                    try { if (type.Resolve() == null) problems.Add(module.Name + ": verify attribute type " + type.FullName); }
                    catch (AssemblyResolutionException e) { problems.Add(module.Name + ": verify attribute type " + type.FullName + " (" + e.AssemblyReference.Name + ")"); }
                }
                foreach (MemberReference member in module.GetMemberReferences())
                {
                    // Multidimensional arrays' Get, Set, Address and constructor come from the runtime.
                    if (member.DeclaringType is ArrayType) continue;
                    try
                    {
                        var method = member as MethodReference;
                        var field = member as FieldReference;
                        if ((method != null && method.Resolve() == null) || (field != null && field.Resolve() == null))
                            problems.Add(module.Name + ": verify member " + member.FullName);
                    }
                    catch (AssemblyResolutionException e) { problems.Add(module.Name + ": verify member " + member.FullName + " (" + e.AssemblyReference.Name + ")"); }
                }
            }
        }
    }

    // The types named in a module's attribute arguments.
    static IEnumerable<TypeReference> AttributeTypes(ModuleDefinition module)
    {
        var found = new List<TypeReference>();
        foreach (ICustomAttributeProvider provider in AttributeProviders(module))
        {
            if (!provider.HasCustomAttributes) continue;
            foreach (CustomAttribute attribute in provider.CustomAttributes)
            {
                try
                {
                    foreach (CustomAttributeArgument argument in attribute.ConstructorArguments
                        .Concat(attribute.Fields.Select(f => f.Argument)).Concat(attribute.Properties.Select(p => p.Argument)))
                        CollectTypes(argument, found);
                }
                catch (AssemblyResolutionException) { }
            }
        }
        return found;
    }

    static void CollectTypes(CustomAttributeArgument argument, List<TypeReference> found)
    {
        var type = argument.Value as TypeReference;
        if (type != null && !(type is TypeSpecification) && !type.IsGenericParameter) found.Add(type);
        var array = argument.Value as CustomAttributeArgument[];
        if (array != null) foreach (CustomAttributeArgument item in array) CollectTypes(item, found);
        if (argument.Value is CustomAttributeArgument) CollectTypes((CustomAttributeArgument)argument.Value, found);
    }

    static int Priority(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        return name == "mscorlib" ? 0 : name == "System" ? 1 : name == "System.Core" ? 2 : 3;
    }

    // Namespaces CrowCompat replaces on every target, even one that has them: UnityEngine.Video, which
    // the PS Vita's player lacks, plays the game's videos as frame atlases (compat/Video.cs), and the
    // Windows players do the same, to show what the PS Vita does.
    static readonly string[] ReplacedNamespaces = { "UnityEngine.Video" };

    // The public types of a reference assembly. The module stays open (it is never disposed):
    // Cecil reads the indexed types' members from it later, on demand.
    static void Index(string file)
    {
        ModuleDefinition module;
        try { module = ModuleDefinition.ReadModule(file); }
        catch (BadImageFormatException) { return; }
        foreach (TypeDefinition type in AllTypes(module.Types))
        {
            TypeDefinition top = type;
            while (top.DeclaringType != null) top = top.DeclaringType;
            if (ReplacedNamespaces.Contains(top.Namespace)) continue;
            if (type.IsPublic || type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamilyOrAssembly)
                if (!target.ContainsKey(type.FullName)) target[type.FullName] = type;
        }
    }

    static string Rewrite(AssemblyDefinition assembly)
    {
        ModuleDefinition module = assembly.MainModule;
        string newName;
        if (renamedAssemblies.TryGetValue(assembly.Name.Name, out newName))
        {
            assembly.Name.Name = newName;
            module.Name = newName + ".dll";
        }
        foreach (AssemblyNameReference reference in module.AssemblyReferences)
            if (renamedAssemblies.TryGetValue(reference.Name, out newName)) reference.Name = newName;

        // Each body's size before the passes (see WidenChangedBodies).
        var sizes = new Dictionary<MethodDefinition, long>();
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody)) sizes[method] = BodySize(method.Body);
        // Before the retargeting: it fills strippedTypes, whose references the loop leaves alone.
        int stripped = StripAttributes(module) + StripSecurityDeclarations(module);
        int retargeted = 0, shimmedTypes = 0;
        foreach (TypeReference type in module.GetTypeReferences().ToArray())
        {
            string inner;
            if (type.DeclaringType != null && renamedNestedTypes.TryGetValue(type.FullName, out inner))
            {
                type.Name = inner;
                retargeted++;
            }
            if (type.DeclaringType != null || !IsExternal(type) || strippedTypes.Contains(type.FullName)) continue;
            string name;
            if (renamedTypes.TryGetValue(type.FullName, out name))
            {
                int dot = name.LastIndexOf('.');
                type.Namespace = name.Substring(0, dot);
                type.Name = name.Substring(dot + 1);
            }
            TypeDefinition definition;
            if (target.TryGetValue(type.FullName, out definition))
            {
                if (SetScope(module, type, definition.Module.Assembly.Name)) retargeted++;
            }
            else if (compat.TryGetValue(CompatName(type.FullName), out definition))
            {
                type.Namespace = definition.Namespace;
                type.Name = definition.Name;
                SetScope(module, type, compatModule.Assembly.Name);
                shimmedTypes++;
            }
            else problems.Add(module.Name + ": type " + type.FullName);
        }
        int attributeTypes = RetargetAttributeTypes(module);
        // Before the passes: references unused after the retargeting are read from the file's own
        // type references, which do not list what a pass imports.
        int removed = RemoveUnusedReferences(module);
        // After it: the reference to the attribute's module may be new.
        MarkApiVersion(assembly);
        // Each group's line of the stats, "  <group>: <pass>=<sites> ...", for groups that changed something.
        var groups = new List<string>();
        var lines = new Dictionary<string, string>();
        var changed = new HashSet<string>();
        foreach (Pass pass in Passes)
        {
            if (pass.Scope == Scope.Game && !IsGameAssembly(module)) continue;
            int sites = pass.Run(module);
            Count("pass " + pass.Name, sites);
            if (!lines.ContainsKey(pass.Group))
            {
                groups.Add(pass.Group);
                lines[pass.Group] = "\n  " + pass.Group + ":";
            }
            lines[pass.Group] += " " + pass.Name + "=" + sites;
            if (sites > 0) changed.Add(pass.Group);
        }
        int outOfRange;
        int widened = WidenChangedBodies(module, sizes, out outOfRange);
        return "retargeted=" + retargeted + " shimTypes=" + shimmedTypes + " attributeTypes=" + attributeTypes +
               " strippedAttributes=" + stripped +
               " widenedBodies=" + widened + " (short branches out of range in " + outOfRange + ")" +
               " removedReferences=" + removed + string.Concat(groups.Where(changed.Contains).Select(g => lines[g]));
    }

    // [assembly: UnityAPICompatibilityVersion("<target>")] (CROW_API_VERSION, set by pipeline.py): the
    // editor's API updater then takes the assembly as up to date and does not open it. Without it,
    // the updater's check failed on five of the rewritten assemblies: it reads attribute arguments
    // and could not resolve the engine modules their enums come from.
    static void MarkApiVersion(AssemblyDefinition assembly)
    {
        string version = Environment.GetEnvironmentVariable("CROW_API_VERSION");
        if (string.IsNullOrEmpty(version)) return;
        ModuleDefinition module = assembly.MainModule;
        MethodDefinition constructor = target["UnityEngine.UnityAPICompatibilityVersionAttribute"].Methods
            .Single(m => m.IsConstructor && m.Parameters.Count == 1);
        var attribute = new CustomAttribute(module.ImportReference(constructor));
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, version));
        assembly.CustomAttributes.Add(attribute);
    }

    // Compat's method CrowCompat.<type>.<name> (the only one of that name), imported, and
    // instantiated with the type arguments given.
    static MethodReference CompatMethod(ModuleDefinition module, string type, string name, params TypeReference[] typeArguments)
    {
        MethodReference method = module.ImportReference(compat["CrowCompat." + type].Methods.Single(m => m.Name == name));
        if (typeArguments.Length == 0) return method;
        var instance = new GenericInstanceMethod(method);
        foreach (TypeReference argument in typeArguments) instance.GenericArguments.Add(argument);
        return instance;
    }

    // Instruction count and encoded size of a body.
    static long BodySize(MethodBody body)
    {
        long size = 0;
        foreach (Instruction instruction in body.Instructions) size += instruction.GetSize();
        return ((long)body.Instructions.Count << 32) | size;
    }

    // Cecil writes a short branch whose target ended up out of its range without complaint
    // (the offset wraps; it reads back with no target). After the passes, every body whose
    // instructions changed (inserted, removed or resized), every new body, and any body with
    // a short branch out of range gets the long branch forms.
    static int WidenChangedBodies(ModuleDefinition module, Dictionary<MethodDefinition, long> sizes, out int outOfRange)
    {
        int count = 0;
        outOfRange = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                long size;
                bool changed = !sizes.TryGetValue(method, out size) || size != BodySize(method.Body);
                bool wrong = ShortBranchOutOfRange(method.Body);
                if (wrong) outOfRange++;
                if (!changed && !wrong) continue;
                if (!method.Body.Instructions.Any(i => i.OpCode.OperandType == OperandType.ShortInlineBrTarget)) continue;
                WidenBranches(method.Body);
                count++;
            }
        return count;
    }

    static bool ShortBranchOutOfRange(MethodBody body)
    {
        var offsets = new Dictionary<Instruction, int>();
        int offset = 0;
        foreach (Instruction instruction in body.Instructions)
        {
            offsets[instruction] = offset;
            offset += instruction.GetSize();
        }
        foreach (Instruction instruction in body.Instructions)
        {
            if (instruction.OpCode.OperandType != OperandType.ShortInlineBrTarget) continue;
            var target = instruction.Operand as Instruction;
            int at;
            if (target == null || !offsets.TryGetValue(target, out at)) return true;
            int delta = at - (offsets[instruction] + instruction.GetSize());
            if (delta < sbyte.MinValue || delta > sbyte.MaxValue) return true;
        }
        return false;
    }
}
