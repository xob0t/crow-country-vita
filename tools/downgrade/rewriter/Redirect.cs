using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The translation itself: missing types and members to CrowCompat (naming convention in
// Compat2018.cs), attributes the target lacks, native callbacks, unused references; the IL
// helpers the passes share (inserting code, erasing an instruction, what a call names).
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    // Inserting code may put a short branch's target out of its reach.
    static void WidenBranches(MethodBody body)
    {
        var longForms = new Dictionary<Code, OpCode>
        {
            { Code.Br_S, OpCodes.Br }, { Code.Brfalse_S, OpCodes.Brfalse }, { Code.Brtrue_S, OpCodes.Brtrue },
            { Code.Beq_S, OpCodes.Beq }, { Code.Bne_Un_S, OpCodes.Bne_Un }, { Code.Bge_S, OpCodes.Bge },
            { Code.Bge_Un_S, OpCodes.Bge_Un }, { Code.Bgt_S, OpCodes.Bgt }, { Code.Bgt_Un_S, OpCodes.Bgt_Un },
            { Code.Ble_S, OpCodes.Ble }, { Code.Ble_Un_S, OpCodes.Ble_Un }, { Code.Blt_S, OpCodes.Blt },
            { Code.Blt_Un_S, OpCodes.Blt_Un }, { Code.Leave_S, OpCodes.Leave },
        };
        OpCode wide;
        foreach (Instruction instruction in body.Instructions)
            if (longForms.TryGetValue(instruction.OpCode.Code, out wide)) instruction.OpCode = wide;
    }

    // Mono marshals any delegate to native code; IL2CPP only methods marked
    // [MonoPInvokeCallback]. Mark methods wrapped in delegates of native callback
    // types (those with [UnmanagedFunctionPointer]).
    static int MarkNativeCallbacks(ModuleDefinition module)
    {
        int count = 0;
        MethodDefinition attribute = target["AOT.MonoPInvokeCallbackAttribute"].Methods.First(m => m.IsConstructor && m.Parameters.Count == 1);
        foreach (TypeDefinition type in AllTypes(module.Types))
        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.HasBody) continue;
            foreach (Instruction instruction in method.Body.Instructions)
            {
                var wrapped = instruction.Operand as MethodReference;
                var construct = instruction.Next != null ? instruction.Next.Operand as MethodReference : null;
                if (instruction.OpCode != OpCodes.Ldftn || construct == null || instruction.Next.OpCode != OpCodes.Newobj) continue;
                TypeDefinition callback;
                MethodDefinition callee;
                try { callback = construct.DeclaringType.Resolve(); callee = wrapped.Resolve(); }
                catch (AssemblyResolutionException e) { skipped.Add("MarkNativeCallbacks " + method.FullName + ": " + e.Message); continue; }
                if (callback == null || callee == null || callee.Module != module || !callee.IsStatic) continue;
                if (!callback.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.InteropServices.UnmanagedFunctionPointerAttribute")) continue;
                if (callee.CustomAttributes.Any(a => a.AttributeType.FullName == "AOT.MonoPInvokeCallbackAttribute")) continue;
                MethodReference constructor = module.ImportReference(attribute);
                var marker = new CustomAttribute(constructor);
                marker.ConstructorArguments.Add(new CustomAttributeArgument(constructor.Parameters[0].ParameterType, module.ImportReference(callback)));
                callee.CustomAttributes.Add(marker);
                count++;
            }
        }
        return count;
    }

    static string CompatName(string fullName)
    {
        return "CrowCompat." + fullName.Replace('.', '_').Replace("/", "__");
    }

    static bool IsExternal(TypeReference type)
    {
        while (type.DeclaringType != null) type = type.DeclaringType;
        var scope = type.Scope as AssemblyNameReference;
        return scope != null && !shipped.Contains(scope.Name) && !renamedAssemblies.ContainsValue(scope.Name)
            && scope.Name != compatModule.Assembly.Name.Name;
    }

    static bool SetScope(ModuleDefinition module, TypeReference type, AssemblyNameDefinition assembly)
    {
        var current = type.Scope as AssemblyNameReference;
        if (current != null && current.Name == assembly.Name) return false;
        AssemblyNameReference reference = module.AssemblyReferences.FirstOrDefault(r => r.Name == assembly.Name);
        if (reference == null)
        {
            reference = new AssemblyNameReference(assembly.Name, assembly.Version) { PublicKeyToken = assembly.PublicKeyToken };
            module.AssemblyReferences.Add(reference);
        }
        type.Scope = reference;
        return true;
    }

    // Attributes whose types the target lacks only affect the editor; drop them.
    // Everything in a module that can carry custom attributes.
    static List<ICustomAttributeProvider> AttributeProviders(ModuleDefinition module)
    {
        var providers = new List<ICustomAttributeProvider> { module, module.Assembly };
        foreach (TypeDefinition type in AllTypes(module.Types))
        {
            providers.Add(type);
            providers.AddRange(type.Fields);
            providers.AddRange(type.Properties);
            providers.AddRange(type.Events);
            foreach (MethodDefinition method in type.Methods)
            {
                providers.Add(method);
                providers.AddRange(method.Parameters);
            }
        }
        return providers;
    }

    // Types named in attribute arguments (typeof in [CheckForComponent(typeof(VideoPlayer))], on 11
    // PlayMaker actions) live in the attribute's blob as names, not in the module's type references,
    // so the retargeting in Rewrite does not see them. They get the same treatment: the target's
    // assembly, or CrowCompat's type for one the target lacks. Unity's linker stopped on the Vita's
    // VideoPlayer, which its VideoModule does not have.
    static int RetargetAttributeTypes(ModuleDefinition module)
    {
        int count = 0;
        foreach (ICustomAttributeProvider provider in AttributeProviders(module))
        {
            if (!provider.HasCustomAttributes) continue;
            foreach (CustomAttribute attribute in provider.CustomAttributes)
            {
                try
                {
                    for (int i = 0; i < attribute.ConstructorArguments.Count; i++)
                        attribute.ConstructorArguments[i] = RetargetArgument(module, attribute.ConstructorArguments[i], ref count);
                    for (int i = 0; i < attribute.Fields.Count; i++)
                        attribute.Fields[i] = new CustomAttributeNamedArgument(attribute.Fields[i].Name,
                            RetargetArgument(module, attribute.Fields[i].Argument, ref count));
                    for (int i = 0; i < attribute.Properties.Count; i++)
                        attribute.Properties[i] = new CustomAttributeNamedArgument(attribute.Properties[i].Name,
                            RetargetArgument(module, attribute.Properties[i].Argument, ref count));
                }
                catch (AssemblyResolutionException e) { skipped.Add("RetargetAttributeTypes " + attribute.AttributeType.FullName + ": " + e.Message); }
            }
        }
        return count;
    }

    static CustomAttributeArgument RetargetArgument(ModuleDefinition module, CustomAttributeArgument argument, ref int count)
    {
        var type = argument.Value as TypeReference;
        if (type != null) return new CustomAttributeArgument(argument.Type, RetargetAttributeType(module, type, ref count));
        var array = argument.Value as CustomAttributeArgument[];
        if (array != null)
        {
            for (int i = 0; i < array.Length; i++) array[i] = RetargetArgument(module, array[i], ref count);
            return argument;
        }
        if (argument.Value is CustomAttributeArgument)
            return new CustomAttributeArgument(argument.Type, RetargetArgument(module, (CustomAttributeArgument)argument.Value, ref count));
        return argument;
    }

    static TypeReference RetargetAttributeType(ModuleDefinition module, TypeReference type, ref int count)
    {
        if (type is TypeSpecification || type.IsGenericParameter) return type;
        TypeReference top = type;
        while (top.DeclaringType != null) top = top.DeclaringType;
        if (!IsExternal(top)) return type;
        TypeDefinition definition;
        if (target.TryGetValue(top.FullName, out definition))
        {
            if (SetScope(module, top, definition.Module.Assembly.Name)) count++;
            return type;
        }
        if (compat.TryGetValue(CompatName(type.FullName), out definition))
        {
            count++;
            return module.ImportReference(definition);
        }
        problems.Add(module.Name + ": attribute type " + type.FullName);
        return type;
    }

    // Security declarations ([assembly: SecurityPermission(SkipVerification = true)], which compilers add
    // to assemblies with unsafe code). The target's unityaot class libraries have no code access
    // security types, so Unity's linker stopped on SecurityPermissionAttribute; Mono and IL2CPP ignore
    // the declarations anyway.
    static int StripSecurityDeclarations(ModuleDefinition module)
    {
        int count = 0;
        var providers = new List<ISecurityDeclarationProvider> { module.Assembly };
        foreach (TypeDefinition type in AllTypes(module.Types))
        {
            providers.Add(type);
            providers.AddRange(type.Methods);
        }
        foreach (ISecurityDeclarationProvider provider in providers)
        {
            if (!provider.HasSecurityDeclarations) continue;
            count += provider.SecurityDeclarations.Count;
            provider.SecurityDeclarations.Clear();
        }
        return count;
    }

    static int StripAttributes(ModuleDefinition module)
    {
        int count = 0;
        foreach (ICustomAttributeProvider provider in AttributeProviders(module))
        {
            if (!provider.HasCustomAttributes) continue;
            for (int i = provider.CustomAttributes.Count - 1; i >= 0; i--)
            {
                TypeReference type = provider.CustomAttributes[i].AttributeType;
                if (strippedAttributes.Contains(type.FullName) || (IsExternal(type) && !renamedTypes.ContainsKey(type.FullName) &&
                    !target.ContainsKey(type.FullName) && !compat.ContainsKey(CompatName(type.FullName))))
                {
                    strippedTypes.Add(type.FullName);
                    provider.CustomAttributes.RemoveAt(i);
                    count++;
                }
            }
        }
        return count;
    }

    // Engine calls that go to CrowCompat Window, which keeps a test run's window a window
    // (-crow-windowed): the game's fullscreen and resolution settings.
    static readonly string[] ScreenCalls = { "set_fullScreen", "set_fullScreenMode", "SetResolution" };

    static int RedirectMembers(ModuleDefinition module)
    {
        // Sites of each rule but the shims, for the NO MATCH check: Type.GetType, Screen calls,
        // BinaryFormatter, CrowCountrySave's "Saves" literals, the clock, persistentDataPath. The two save
        // path rules count apart: I2 Localization, SmoothSave and three screenshot actions call
        // persistentDataPath too, so its sites would hide a miss in CrowCountrySave (saves written to
        // the read-only app folder on the PS Vita).
        var sites = new int[6];
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.HasBody) continue;
            foreach (Instruction instruction in method.Body.Instructions)
            {
                var field = instruction.Operand as FieldReference;
                if (field != null && IsMissing(field)) problems.Add(module.Name + ": field " + field.FullName);
                // Save paths: CrowCountrySave's relative "Saves" folder, and persistentDataPath, which
                // the PS Vita player leaves empty. See CrowCompat Saves. OuterType: the literals may sit in
                // one of the class's lambdas or iterators.
                var literal = instruction.Operand as string;
                if (instruction.OpCode == OpCodes.Ldstr && (literal == "Saves" || literal == "Saves/") &&
                    OuterType(type).FullName == "com.sfbgames.playmaker.CrowCountrySave")
                {
                    string name = literal == "Saves" ? "Folder" : "Prefix";
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = module.ImportReference(compat["CrowCompat.Saves"].Methods.First(m => m.Name == name));
                    sites[3]++;
                    count++;
                    continue;
                }
                var called = instruction.Operand as MethodReference;
                if (called == null) continue;
                if (called.DeclaringType.FullName == "UnityEngine.Application" && called.Name == "get_persistentDataPath")
                {
                    instruction.Operand = module.ImportReference(compat["CrowCompat.Saves"].Methods.First(m => m.Name == "PersistentDataPath"));
                    sites[5]++;
                    count++;
                    continue;
                }
                if (called.DeclaringType.FullName == "UnityEngine.Screen" && ScreenCalls.Contains(called.Name))
                {
                    MethodDefinition window = compat["CrowCompat.Window"].Methods.FirstOrDefault(m =>
                        m.IsStatic && m.Name == called.Name && Matches(m, called, 0));
                    if (window == null)
                    {
                        problems.Add(module.Name + ": no CrowCompat Window for " + called.FullName);
                        continue;
                    }
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = module.ImportReference(window);
                    sites[1]++;
                    count++;
                    continue;
                }
                // DateTime.Now and UtcNow, which the PS Vita's IL2CPP can't tell (save slots dated
                // 1601). See CrowCompat Saves.
                if (called.DeclaringType.FullName == "System.DateTime" && (called.Name == "get_Now" || called.Name == "get_UtcNow"))
                {
                    string name = called.Name == "get_Now" ? "Now" : "UtcNow";
                    instruction.Operand = module.ImportReference(compat["CrowCompat.Saves"].Methods.First(m => m.Name == name));
                    sites[4]++;
                    count++;
                    continue;
                }
                // BinaryFormatter data the game ships or saved names Assembly-CSharp. Every formatter
                // gets CrowCompat's alias binder (see TypeNames.NewBinaryFormatter), since IL2CPP
                // never asks AssemblyResolve; a binder the game sets is wrapped in it.
                if (called.DeclaringType.FullName == "System.Runtime.Serialization.Formatters.Binary.BinaryFormatter" &&
                    ((instruction.OpCode == OpCodes.Newobj && called.Name == ".ctor" && called.Parameters.Count == 0) || called.Name == "set_Binder"))
                {
                    string name = called.Name == ".ctor" ? "NewBinaryFormatter" : "SetBinder";
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = module.ImportReference(compat["CrowCompat.TypeNames"].Methods.First(m => m.Name == name));
                    sites[2]++;
                    count++;
                    continue;
                }
                // Assembly-qualified names, e.g. in saves, still name the renamed assemblies;
                // see Compat TypeNames. Mono can resolve them by event, IL2CPP cannot.
                if (called.DeclaringType.FullName == "System.Type" && called.Name == "GetType" && called.Parameters.Count > 0 &&
                    called.Parameters.All(p => p.ParameterType.FullName == "System.String" || p.ParameterType.FullName == "System.Boolean"))
                {
                    instruction.Operand = module.ImportReference(compat["CrowCompat.TypeNames"].Methods.First(m =>
                        m.Name == "GetType" && m.Parameters.Count == called.Parameters.Count));
                    sites[0]++;
                    count++;
                    continue;
                }
                var generic = called as GenericInstanceMethod;
                MethodReference element = generic != null ? generic.ElementMethod : called;
                if (!IsMissing(element)) continue;
                // A shim replaces a call: an ldftn or ldvirtftn made a call would be invalid IL, and so
                // would a call after constrained., which only prefixes callvirt.
                if ((instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt && instruction.OpCode != OpCodes.Newobj) ||
                    (instruction.Previous != null && instruction.Previous.OpCode == OpCodes.Constrained))
                {
                    problems.Add(module.Name + ": " + (instruction.Previous != null && instruction.Previous.OpCode == OpCodes.Constrained ? "constrained. " : "") +
                                 instruction.OpCode + " of missing member " + element.FullName + " in " + method.FullName + " (only a call can go to a shim)");
                    continue;
                }
                MethodDefinition shim = FindShim(element);
                if (shim == null) { problems.Add(module.Name + ": member " + element.FullName); continue; }
                MethodReference replacement = module.ImportReference(shim);
                if (generic != null)
                {
                    var instance = new GenericInstanceMethod(replacement);
                    foreach (TypeReference argument in generic.GenericArguments) instance.GenericArguments.Add(argument);
                    replacement = instance;
                }
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = replacement;
                count++;
            }
        }
        string[] rules = { "Type.GetType", "Screen fullscreen and resolution", "BinaryFormatter binder", "CrowCountrySave Saves literals", "DateTime.Now",
                           "persistentDataPath" };
        for (int i = 0; i < sites.Length; i++) Count("redirect " + rules[i], sites[i]);
        return count;
    }

    // Fields whose type changed: (declaring type, field, the source's type, the target's type, the
    // target type's conversion from the source's). Unity 2019 widened UIVertex's UVs to Vector4;
    // TMP_InputField stores zero in its cursor's uv0. A store converts the value it stores.
    static readonly string[][] RetypedFields =
    {
        new[] { "UnityEngine.UIVertex", "uv0", "UnityEngine.Vector4", "UnityEngine.Vector2" },
        new[] { "UnityEngine.UIVertex", "uv1", "UnityEngine.Vector4", "UnityEngine.Vector2" },
        new[] { "UnityEngine.UIVertex", "uv2", "UnityEngine.Vector4", "UnityEngine.Vector2" },
        new[] { "UnityEngine.UIVertex", "uv3", "UnityEngine.Vector4", "UnityEngine.Vector2" },
    };

    static int RetypeFields(ModuleDefinition module)
    {
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
        foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
        {
            ILProcessor il = method.Body.GetILProcessor();
            foreach (Instruction instruction in method.Body.Instructions.ToArray())
            {
                var field = instruction.Operand as FieldReference;
                if (field == null) continue;
                string[] rule = RetypedFields.FirstOrDefault(r => field.DeclaringType.FullName == r[0] && field.Name == r[1] && field.FieldType.FullName == r[2]);
                if (rule == null) continue;
                if (instruction.OpCode != OpCodes.Stfld)
                {
                    problems.Add(module.Name + ": RetypeFields " + instruction.OpCode + " of " + field.FullName + " in " + method.FullName);
                    continue;
                }
                TypeDefinition to = target[rule[3]];
                // C# declares a conversion on either of its two types.
                MethodDefinition convert = to.Methods.Concat(target[rule[2]].Methods).Single(m => m.Name == "op_Implicit" &&
                    m.ReturnType.FullName == rule[3] && m.Parameters[0].ParameterType.FullName == rule[2]);
                il.InsertBefore(instruction, il.Create(OpCodes.Call, module.ImportReference(convert)));
                instruction.Operand = new FieldReference(field.Name, module.ImportReference(to), field.DeclaringType);
                count++;
            }
        }
        return count;
    }

    static bool IsMissing(MemberReference member)
    {
        TypeReference owner = member.DeclaringType;
        if (owner == null || owner is ArrayType) return false;
        while (owner is TypeSpecification) owner = ((TypeSpecification)owner).ElementType;
        if (!IsExternal(owner)) return false;
        TypeDefinition definition;
        // A type the target lacks was moved to its Compat shim in Rewrite (no longer external
        // then) or reported there as unresolved.
        if (!target.TryGetValue(owner.FullName, out definition)) return false;
        for (TypeDefinition current = definition; current != null; current = current.BaseType == null ? null : Lookup(current.BaseType))
        {
            var field = member as FieldReference;
            if (field != null && current.Fields.Any(f => f.Name == field.Name)) return false;
            var method = member as MethodReference;
            if (method != null && current.Methods.Any(m => m.Name == method.Name && Matches(m, method, 0))) return false;
            if (method != null && method.Name == ".ctor") break;
        }
        return true;
    }

    static TypeDefinition Lookup(TypeReference type)
    {
        TypeDefinition definition;
        return target.TryGetValue(type.FullName, out definition) ? definition : null;
    }

    static MethodDefinition FindShim(MethodReference missing)
    {
        TypeReference owner = missing.DeclaringType;
        while (owner is TypeSpecification) owner = ((TypeSpecification)owner).ElementType;
        TypeDefinition shimType;
        if (!compat.TryGetValue(CompatName(owner.FullName), out shimType)) return null;
        bool constructor = missing.Name == ".ctor";
        string name = constructor ? "ctor" : missing.Name;
        int offset = missing.HasThis && !constructor ? 1 : 0;
        // The receiver a call pushes: a struct's address, a class's reference. A shim that takes the
        // other would get the wrong argument (none does today; RenderTextureDescriptor's takes ref).
        TypeDefinition ownerDefinition = Lookup(owner);
        bool byAddress = ownerDefinition != null && ownerDefinition.IsValueType;
        foreach (MethodDefinition candidate in shimType.Methods.Where(m => m.IsStatic && m.Name == name))
        {
            // Matches first: it checks the parameter count.
            if (!Matches(candidate, missing, offset)) continue;
            if (offset == 1)
            {
                string receiver = Shape(candidate.Parameters[0].ParameterType);
                if (receiver.TrimEnd('&') != Shape(owner)) continue;
                if (receiver.EndsWith("&") != byAddress)
                {
                    problems.Add("shim " + candidate.FullName + " takes its " + owner.FullName + " receiver " +
                                 (byAddress ? "by value; a struct's comes by address (ref)" : "by ref; a class's comes by value"));
                    continue;
                }
            }
            if (constructor && Shape(candidate.ReturnType) != Shape(owner)) continue;
            return candidate;
        }
        return null;
    }

    static bool Matches(MethodDefinition candidate, MethodReference reference, int offset)
    {
        if (candidate.Parameters.Count != reference.Parameters.Count + offset) return false;
        if (candidate.GenericParameters.Count != reference.GenericParameters.Count) return false;
        if (reference.Name != ".ctor" && Shape(candidate.ReturnType) != Shape(reference.ReturnType)) return false;
        for (int i = 0; i < reference.Parameters.Count; i++)
            if (Shape(candidate.Parameters[i + offset].ParameterType) != Shape(reference.Parameters[i].ParameterType)) return false;
        return true;
    }

    // Generic parameters compare by position, other types by full name.
    static string Shape(TypeReference type)
    {
        var generic = type as GenericParameter;
        if (generic != null) return (generic.Type == GenericParameterType.Method ? "!!" : "!") + generic.Position;
        if (type is ByReferenceType) return Shape(((TypeSpecification)type).ElementType) + "&";
        if (type is ArrayType) return Shape(((TypeSpecification)type).ElementType) + "[]";
        var instance = type as GenericInstanceType;
        if (instance != null) return Shape(instance.ElementType) + "<" + string.Join(",", instance.GenericArguments.Select(Shape)) + ">";
        return type.FullName;
    }

    static int RemoveUnusedReferences(ModuleDefinition module)
    {
        var used = new HashSet<string>(module.GetTypeReferences().Select(t =>
        {
            while (t.DeclaringType != null) t = t.DeclaringType;
            var scope = t.Scope as AssemblyNameReference;
            return scope == null ? null : scope.Name;
        }));
        int count = 0;
        for (int i = module.AssemblyReferences.Count - 1; i >= 0; i--)
        {
            string name = module.AssemblyReferences[i].Name;
            // An excluded assembly's types all went to CrowCompat (Verify reports any that did not),
            // but the file's own type references still name it.
            if (excluded.Contains(name) || (!used.Contains(name) && (name.StartsWith("UnityEngine") || name == "netstandard")))
            {
                module.AssemblyReferences.RemoveAt(i);
                count++;
            }
        }
        return count;
    }

    // ---- IL helpers ----
    // Where inserted code goes:
    // - Prepend: at a method's start, run once on entry (a loop back to the first instruction
    //   does not run it again; a try block that started there still starts there).
    // - InsertBeforeRetarget: before a statement, as part of it (branches to the statement and
    //   handler bounds there take in the inserted code); also BeforeRets.
    // - InsertBefore: before an instruction whose operands are already on the stack, such as a
    //   call's added arguments (branches to the anchor skip the inserted code).
    // - InsertAfter and CallAfter: after an instruction, with its value on the stack.

    static void Prepend(MethodDefinition method, params Instruction[] added)
    {
        InsertBefore(method.Body.GetILProcessor(), method.Body.Instructions[0], added);
    }

    // Inserts instructions, in order, before an existing one; branches to it still skip them.
    static void InsertBefore(ILProcessor il, Instruction anchor, params Instruction[] added)
    {
        foreach (Instruction instruction in added) il.InsertBefore(anchor, instruction);
    }

    // Inserts instructions, in order, after an existing one.
    static void InsertAfter(ILProcessor il, Instruction anchor, params Instruction[] added)
    {
        foreach (Instruction instruction in added)
        {
            il.InsertAfter(anchor, instruction);
            anchor = instruction;
        }
    }

    // Inserts "call target" after each instruction of a method that matches (where it leaves a
    // value, the call takes it and leaves its own); the number of calls inserted.
    static int CallAfter(MethodDefinition method, Func<Instruction, bool> match, MethodReference target)
    {
        ILProcessor il = method.Body.GetILProcessor();
        Instruction[] found = method.Body.Instructions.Where(match).ToArray();
        foreach (Instruction instruction in found) il.InsertAfter(instruction, il.Create(OpCodes.Call, target));
        return found.Length;
    }

    // Inserts code before each ret of a method (branches to a ret run it too); the number of rets.
    static int BeforeRets(MethodDefinition method, Func<Instruction[]> code)
    {
        Instruction[] rets = method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
        foreach (Instruction ret in rets) InsertBeforeRetarget(method, ret, code());
        return rets.Length;
    }

    // Inserts instructions before an existing one; branches and handlers that pointed at it
    // point at the first inserted instruction instead.
    static void InsertBeforeRetarget(MethodDefinition method, Instruction anchor, params Instruction[] added)
    {
        InsertBefore(method.Body.GetILProcessor(), anchor, added);
        foreach (Instruction instruction in method.Body.Instructions)
        {
            if (instruction.Operand == anchor && !added.Contains(instruction)) instruction.Operand = added[0];
            var targets = instruction.Operand as Instruction[];
            if (targets != null)
                for (int i = 0; i < targets.Length; i++) if (targets[i] == anchor) targets[i] = added[0];
        }
        foreach (ExceptionHandler handler in method.Body.ExceptionHandlers)
        {
            if (handler.TryStart == anchor) handler.TryStart = added[0];
            if (handler.TryEnd == anchor) handler.TryEnd = added[0];
            if (handler.HandlerStart == anchor) handler.HandlerStart = added[0];
            if (handler.HandlerEnd == anchor) handler.HandlerEnd = added[0];
            if (handler.FilterStart == anchor) handler.FilterStart = added[0];
        }
    }

    // Starts a void method with a test that returns at once: the test's instructions leave a bool,
    // and the method returns (by its last ret) when it is true (exitWhen brtrue) or false (brfalse).
    static void ReturnAtStartIf(MethodDefinition method, OpCode exitWhen, params Instruction[] test)
    {
        Instruction ret = method.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
        Prepend(method, test.Concat(new[] { Instruction.Create(exitWhen, ret) }).ToArray());
    }

    // The name of the method an instruction names (a call, ldftn, ...), or "".
    static string CalledName(Instruction instruction)
    {
        var called = instruction.Operand as MethodReference;
        return called == null ? "" : called.Name;
    }

    // Whether an instruction names the method <type>::<name>, the type by full name.
    static bool Calls(Instruction instruction, string type, string name)
    {
        var called = instruction.Operand as MethodReference;
        return called != null && called.Name == name && called.DeclaringType.FullName == type;
    }

    // The type a nested type is nested in at the top, or the type itself: rules for a type also
    // cover its lambdas, closures and iterators.
    static TypeDefinition OuterType(TypeDefinition type)
    {
        while (type.DeclaringType != null) type = type.DeclaringType;
        return type;
    }

    static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }

    // The first method that matches in the base types of a type, nearest first (an inherited getter,
    // the virtual method an override would override), or null.
    static MethodDefinition BaseMethod(TypeDefinition type, Func<MethodDefinition, bool> match)
    {
        for (TypeReference current = type.BaseType; current != null;)
        {
            TypeDefinition resolved = current.Resolve();
            MethodDefinition found = resolved.Methods.SingleOrDefault(match);
            if (found != null) return found;
            current = resolved.BaseType;
        }
        return null;
    }

    // A new private [NonSerialized] field, added at the end of the type's fields.
    static FieldDefinition AddField(TypeDefinition type, string name, TypeReference fieldType)
    {
        var field = new FieldDefinition(name, FieldAttributes.Private | FieldAttributes.NotSerialized, fieldType);
        type.Fields.Add(field);
        return field;
    }

    // Compat's static field CrowCompat.<type>.<name>, imported (a switch or counter the patched code reads).
    static FieldReference CompatField(ModuleDefinition module, string type, string name)
    {
        return module.ImportReference(compat["CrowCompat." + type].Fields.Single(f => f.Name == name));
    }
}
