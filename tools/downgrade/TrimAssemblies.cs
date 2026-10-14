using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Leaves code the PS Vita's player never runs out of the rewritten assemblies (code_trim.py has
// why). Unity's linker keeps the script assemblies whole, as its roots, so this is the only place
// their dead code can go; what it called in the class libraries and plugins then goes with the
// linker. In place, in the assemblies of the folder:
//  - PlayMaker actions the data never names (a list of the names it uses) and SmoothSave, which
//    only actions use (Unused), unless code that stays references them: their types are removed.
//    A type that quickcode's SerializedLayout lists (a [Serializable] one, such as a nested enum or
//    a compiler closure class) keeps its declarations and gets bodies that throw instead, so the
//    serialized layout stays as it was.
//  - Single methods (MethodRules): their bodies throw or return null.
// Code that stays is everything else: every other type in the folder's assemblies, with its base
// types, fields, signatures, attributes and the members its bodies use, to a fixed point (an
// unused action that a used one derives from or calls stays). Nothing else can reach the removed
// types: PlayMaker creates actions by the names in the data, and CrowCompat's reflection names
// no action.
// Usage: TrimAssemblies <folder> <used action names.txt> <reference folder>...
class TrimAssemblies
{
    const string ActionBase = "HutongGames.PlayMaker.FsmStateAction";

    // Namespaces only unused actions use: SmoothSave, the save-slot library behind PlayMaker's SS*
    // actions, which no FSM has (the game saves with CrowCountrySave). Its XmlSerializer calls kept
    // System.Xml's serializer in the module.
    static readonly string[] Unused = { "ChristopherCreates.SmoothSave" };

    // type, method, what its body becomes: "throw", or "null" (return null).
    static readonly string[][] MethodRules =
    {
        // Rewired's XmlSerializer use, called by nothing in the game or Rewired (Rewired writes its
        // XML with XmlWriter): with SmoothSave's gone, System.Xml's serializer goes.
        new[] { "Rewired.Utils.SerializationTools", "SerializeObjectToXmlString", "throw" },
        // Rewired's Windows input (RawInput, DirectInput, XInput), which the firstpass's
        // ExternalTools hands to Rewired as the platform initializer, compiled with the Windows
        // build's defines. On the PS Vita's platform (rewriter Patches.PatchRewiredPlatform) Rewired
        // takes null as no initializer, as a PS Vita build of Rewired returns; Rewired_Windows then
        // has no references left and the linker drops it whole.
        new[] { "Rewired.Utils.ExternalTools", "GetPlatformInitializer", "null" },
    };

    class Resolver : DefaultAssemblyResolver
    {
        public void Add(AssemblyDefinition assembly) { RegisterAssembly(assembly); }
    }

    static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }

    // A top-level type's key: its assembly and full name, for a reference to it or a nested type.
    static string Key(TypeReference type)
    {
        while (type is TypeSpecification) type = ((TypeSpecification)type).ElementType;
        if (type.IsGenericParameter) return null;
        while (type.DeclaringType != null) type = type.DeclaringType;
        var definition = type as TypeDefinition;
        string scope = definition != null ? definition.Module.Assembly.Name.Name
            : type.Scope is AssemblyNameReference ? ((AssemblyNameReference)type.Scope).Name
            : type.Scope is ModuleDefinition ? ((ModuleDefinition)type.Scope).Assembly.Name.Name
            : type.Scope == null ? "" : type.Scope.Name;
        return scope + "|" + type.FullName;
    }

    static bool Derives(TypeDefinition type, string baseName)
    {
        for (TypeReference current = type.BaseType; current != null; )
        {
            if (current.FullName == baseName) return true;
            TypeDefinition definition;
            try { definition = current.Resolve(); }
            catch (AssemblyResolutionException) { return false; }
            if (definition == null) return false;
            current = definition.BaseType;
        }
        return false;
    }

    // What SerializedLayout lists: UnityEngine.Object subclasses and [Serializable] types.
    static bool Serialized(TypeDefinition type)
    {
        return type.IsSerializable || Derives(type, "UnityEngine.Object");
    }

    readonly Dictionary<string, TypeDefinition> candidates = new Dictionary<string, TypeDefinition>();
    // Candidates that stay whole (code that stays references them), and those that stay with bodies
    // that throw (serialized ones, and what their declarations reference); Type() adds to marking.
    readonly HashSet<string> kept = new HashSet<string>();
    readonly HashSet<string> stubbed = new HashSet<string>();
    HashSet<string> marking;
    readonly Queue<TypeDefinition> pending = new Queue<TypeDefinition>();

    void Type(TypeReference type)
    {
        if (type == null) return;
        var instance = type as GenericInstanceType;
        if (instance != null) foreach (TypeReference argument in instance.GenericArguments) Type(argument);
        var specification = type as TypeSpecification;
        if (specification != null)
        {
            Type(specification.ElementType);
            return;
        }
        string key = Key(type);
        if (key != null && candidates.ContainsKey(key) && !kept.Contains(key) && marking.Add(key)) pending.Enqueue(candidates[key]);
    }

    void Member(MemberReference member)
    {
        var type = member as TypeReference;
        if (type != null)
        {
            Type(type);
            return;
        }
        Type(member.DeclaringType);
        var method = member as MethodReference;
        if (method != null)
        {
            Type(method.ReturnType);
            foreach (ParameterDefinition parameter in method.Parameters) Type(parameter.ParameterType);
            var instance = method as GenericInstanceMethod;
            if (instance != null) foreach (TypeReference argument in instance.GenericArguments) Type(argument);
        }
        var field = member as FieldReference;
        if (field != null) Type(field.FieldType);
    }

    void Argument(CustomAttributeArgument argument)
    {
        Type(argument.Type);
        if (argument.Value is TypeReference) Type((TypeReference)argument.Value);
        if (argument.Value is CustomAttributeArgument) Argument((CustomAttributeArgument)argument.Value);
        var array = argument.Value as CustomAttributeArgument[];
        if (array != null) foreach (CustomAttributeArgument item in array) Argument(item);
    }

    void Attributes(ICustomAttributeProvider provider)
    {
        if (!provider.HasCustomAttributes) return;
        foreach (CustomAttribute attribute in provider.CustomAttributes)
        {
            Member(attribute.Constructor);
            foreach (CustomAttributeArgument argument in attribute.ConstructorArguments) Argument(argument);
            foreach (CustomAttributeNamedArgument named in attribute.Fields.Concat(attribute.Properties)) Argument(named.Argument);
        }
    }

    void GenericParameters(IGenericParameterProvider provider)
    {
        foreach (GenericParameter parameter in provider.GenericParameters)
        {
            foreach (TypeReference constraint in parameter.Constraints) Type(constraint);
            Attributes(parameter);
        }
    }

    // Everything a type that stays (and its nested types) references; only its declarations
    // without bodies.
    void Scan(TypeDefinition top, bool bodies)
    {
        foreach (TypeDefinition type in AllTypes(new[] { top }))
        {
            Type(type.BaseType);
            foreach (InterfaceImplementation implementation in type.Interfaces)
            {
                Type(implementation.InterfaceType);
                Attributes(implementation);
            }
            Attributes(type);
            GenericParameters(type);
            foreach (FieldDefinition field in type.Fields)
            {
                Type(field.FieldType);
                Attributes(field);
            }
            foreach (PropertyDefinition property in type.Properties)
            {
                Type(property.PropertyType);
                Attributes(property);
            }
            foreach (EventDefinition e in type.Events)
            {
                Type(e.EventType);
                Attributes(e);
            }
            foreach (MethodDefinition method in type.Methods)
            {
                Type(method.ReturnType);
                Attributes(method);
                Attributes(method.MethodReturnType);
                GenericParameters(method);
                foreach (ParameterDefinition parameter in method.Parameters)
                {
                    Type(parameter.ParameterType);
                    Attributes(parameter);
                }
                foreach (MethodReference overridden in method.Overrides) Member(overridden);
                if (!bodies || !method.HasBody) continue;
                foreach (VariableDefinition variable in method.Body.Variables) Type(variable.VariableType);
                foreach (ExceptionHandler handler in method.Body.ExceptionHandlers) Type(handler.CatchType);
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    var member = instruction.Operand as MemberReference;
                    if (member != null) Member(member);
                    var site = instruction.Operand as CallSite;
                    if (site == null) continue;
                    Type(site.ReturnType);
                    foreach (ParameterDefinition parameter in site.Parameters) Type(parameter.ParameterType);
                }
            }
        }
    }

    static void Replace(MethodDefinition method, bool returnNull)
    {
        method.Body = new MethodBody(method);
        ILProcessor il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Ldnull);
        il.Emit(returnNull ? OpCodes.Ret : OpCodes.Throw);
    }

    static int Main(string[] args)
    {
        var used = new HashSet<string>(File.ReadAllLines(args[1]).Select(l => l.Trim()).Where(l => l.Length > 0));
        var resolver = new Resolver();
        foreach (string folder in resolver.GetSearchDirectories()) resolver.RemoveSearchDirectory(folder);
        foreach (string folder in args.Skip(2)) resolver.AddSearchDirectory(folder);
        // Read from memory, and resolved to each other, so they can be written back in place.
        var files = new Dictionary<ModuleDefinition, string>();
        foreach (string file in Directory.GetFiles(args[0], "*.dll").OrderBy(f => f))
        {
            var module = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(file)), new ReaderParameters { AssemblyResolver = resolver });
            resolver.Add(module.Assembly);
            files[module] = file;
        }
        return new TrimAssemblies().Run(args[0], files, used);
    }

    int Run(string folder, Dictionary<ModuleDefinition, string> files, HashSet<string> used)
    {
        List<ModuleDefinition> modules = files.Keys.ToList();
        int usedFound = 0;
        foreach (ModuleDefinition module in modules)
            foreach (TypeDefinition type in module.Types)
            {
                bool action = Derives(type, ActionBase);
                if (action && used.Contains(type.FullName)) usedFound++;
                if ((action && !used.Contains(type.FullName)) || Unused.Contains(type.Namespace)) candidates[Key(type)] = type;
            }
        // The data names actions the assemblies have: a list that matches none is wrong (a scan that
        // found nothing would remove every action).
        if (usedFound < used.Count / 2)
        {
            Console.WriteLine("FAILED: only " + usedFound + " of the " + used.Count + " used action names are actions in " + folder);
            return 2;
        }
        marking = kept;
        foreach (ModuleDefinition module in modules)
        {
            Attributes(module.Assembly);
            Attributes(module);
            foreach (TypeDefinition type in module.Types)
                if (!candidates.ContainsKey(Key(type))) Scan(type, true);
        }
        while (pending.Count > 0) Scan(pending.Dequeue(), true);
        // Serialized unused types keep their declarations, and so what those reference.
        marking = stubbed;
        foreach (KeyValuePair<string, TypeDefinition> candidate in candidates.OrderBy(c => c.Key))
            if (!kept.Contains(candidate.Key) && AllTypes(new[] { candidate.Value }).Any(Serialized) && stubbed.Add(candidate.Key))
                pending.Enqueue(candidate.Value);
        while (pending.Count > 0) Scan(pending.Dequeue(), false);

        var changed = new HashSet<ModuleDefinition>();
        int removed = 0, stubbedTypes = 0, bodies = 0;
        foreach (KeyValuePair<string, TypeDefinition> candidate in candidates.OrderBy(c => c.Key))
        {
            if (kept.Contains(candidate.Key)) continue;
            TypeDefinition type = candidate.Value;
            changed.Add(type.Module);
            if (!stubbed.Contains(candidate.Key))
            {
                type.Module.Types.Remove(type);
                removed++;
                continue;
            }
            stubbedTypes++;
            foreach (TypeDefinition nested in AllTypes(new[] { type }))
                foreach (MethodDefinition method in nested.Methods.Where(m => m.HasBody))
                {
                    Replace(method, false);
                    bodies++;
                }
        }
        int rules = 0;
        foreach (string[] rule in MethodRules)
        {
            var methods = modules.Select(m => m.GetType(rule[0])).Where(t => t != null)
                .SelectMany(t => t.Methods.Where(m => m.Name == rule[1] && m.HasBody)).ToList();
            if (methods.Count == 0)
            {
                Console.WriteLine("NO MATCH " + rule[0] + "::" + rule[1]);
                return 4;
            }
            foreach (MethodDefinition method in methods)
            {
                Replace(method, rule[2] == "null");
                changed.Add(method.Module);
                rules++;
            }
        }
        foreach (ModuleDefinition module in changed.OrderBy(m => m.Name))
        {
            // Beside, then in place: a write that fails leaves the rewriter's file as it was.
            module.Write(files[module] + ".trim");
            File.Delete(files[module]);
            File.Move(files[module] + ".trim", files[module]);
        }
        Console.WriteLine("Trimmed: " + removed + " unused action and SmoothSave types removed, " + stubbedTypes + " kept with " +
                          bodies + " bodies that throw (serialized), " + kept.Count + " kept for references, " + rules +
                          " methods by rule, in " + string.Join(", ", changed.Select(m => m.Assembly.Name.Name).OrderBy(n => n)));
        return 0;
    }
}
