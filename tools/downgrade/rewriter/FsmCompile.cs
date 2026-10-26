using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// PlayMaker's FSM code compiled; CrowCompat FsmCompile (compat/FsmCompile.cs) says what and why.
// FsmCompileMethods:
//   Templates: PlayMaker's methods small enough to inline (Template: not virtual, no generics, not an
//     instance method of a struct, no handlers, at most MaxTemplate instructions, nothing that means
//     something else in another method: no ldftn, localloc, jmp, calli, prefixes, non-virtual call of a
//     virtual method, generic parameter).
//     In the game's assemblies only those whose every reference maps to the target, CrowCompat or
//     PlayMaker (Mapper); PlayMaker's pass checks that its passes so far left each of those as the input
//     has it (the game's assemblies copy the input's), and makes the members they reach internal, with
//     [InternalsVisibleTo] for the game's assemblies.
//   Callers: in PlayMaker, the per-frame methods of CompiledPlayMaker; in the game's assemblies, the
//     actions' updates (UpdateRoots) and the methods of action classes they call (UpdatePaths).
//   A caller with a call of a template gets three bodies (two on the PS Vita; one, the compiled, with
//   CROW_FSM_COMPILE=on when the assemblies are rewritten):
//       switch (FsmCompile.Mode) { case 0: original; case 1: check; default: compiled }
//     compiled: each call of a template replaced by its body (its own calls of templates too, to MaxDepth):
//       the arguments into new locals, the body with ret as a branch to its end; a callvirt whose callee
//       doesn't read a field of this first gets callvirt's null check (NullReferenceException).
//       FsmState.OnUpdate, OnFixedUpdate and OnLateUpdate also leave out a running action whose
//       FsmStateAction.crowHooks (FsmTrim's, filled from FsmTrim.Hooks the first time) says its class
//       doesn't use that update.
//     check (when CrowCompat's FsmCompile.CheckBuilt): the original body, but at each call of a template
//       that only reads (Pure), the compiled copy first, then the original call with the same arguments,
//       and FsmCompile.Same(compiled, original, site); the original value goes on.
//     original: as it was.
//   The bodies after the first get copies of the method's handlers; a handler that ran to the method's
//   end ends at the next body.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    const int MaxTemplate = 24, MaxDepth = 4;
    // How deep the per-frame paths are followed from the actions' update methods into what they call.
    const int MaxUpdatePathDepth = 4;

    // CROW_FSM_COMPILE=on when the assemblies are rewritten: the compiled bodies only (no original body, no
    // switch: FsmCompile.Mode does nothing); the default keeps both and the check body on Windows.
    static bool CompiledOnly { get { return Environment.GetEnvironmentVariable("CROW_FSM_COMPILE") == "on"; } }

    // PlayMaker's per-frame methods (all overloads), by type: updates, the state loops, events and transitions.
    static readonly Dictionary<string, string[]> CompiledPlayMaker = new Dictionary<string, string[]>
    {
        {
            "HutongGames.PlayMaker.Fsm", new[]
            {
                "Update", "FixedUpdate", "LateUpdate", "UpdateDelayedEvents", "UpdateState", "FixedUpdateState", "LateUpdateState",
                "UpdateStateChanges", "SwitchState", "EnterState", "ExitState", "ProcessEvent", "Event", "DoTransition",
                "GetOwnerDefaultTarget", "KillDelayedEvents", "DelayedEvent", "BroadcastEvent", "BroadcastEventToGameObject",
                "SendEventToFsmOnGameObject", "SetEventDataSentByInfo", "GetEventDataSentByInfo", "get_Active", "get_GameObject",
                "Continue", "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit", "OnTriggerEnter", "OnTriggerStay",
                "OnTriggerExit", "OnCollisionEnter2D", "OnCollisionStay2D", "OnCollisionExit2D", "OnTriggerEnter2D",
                "OnTriggerStay2D", "OnTriggerExit2D", "OnParticleCollision", "OnControllerColliderHit",
            }
        },
        {
            "HutongGames.PlayMaker.FsmState", new[]
            {
                "OnEnter", "ActivateActions", "OnEvent", "OnFixedUpdate", "OnUpdate", "OnLateUpdate", "FinishAction",
                "RemoveFinishedActions", "CheckAllActionsFinished", "OnExit", "OnCollisionEnter", "OnCollisionStay",
                "OnCollisionExit", "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit", "OnCollisionEnter2D", "OnCollisionStay2D",
                "OnCollisionExit2D", "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D", "OnParticleCollision",
                "OnControllerColliderHit",
            }
        },
        { "HutongGames.PlayMaker.FsmStateAction", new[] { "Finish", "Init" } },
        { "HutongGames.PlayMaker.DelayedEvent", new[] { "Update" } },
        { "PlayMakerFSM", new[] { "Update", "get_Active" } },
        { "PlayMakerFixedUpdate", new[] { "FixedUpdate" } },
        { "PlayMakerLateUpdate", new[] { "LateUpdate" } },
        { "PlayMakerTriggerEnter", new[] { "OnTriggerEnter" } },
        { "PlayMakerTriggerStay", new[] { "OnTriggerStay" } },
        { "PlayMakerTriggerExit", new[] { "OnTriggerExit" } },
        { "PlayMakerCollisionEnter", new[] { "OnCollisionEnter" } },
        { "PlayMakerCollisionStay", new[] { "OnCollisionStay" } },
        { "PlayMakerCollisionExit", new[] { "OnCollisionExit" } },
    };

    // The actions' per-frame methods, and the methods of action classes they call (UpdatePaths): a frame
    // makes about 520 action update calls in Station Square's fight and 40 OnEnter calls, so the rest of an
    // action's code stays as it is (every method of every action class compiled made the PS Vita's module
    // 1.0 MB bigger than these only).
    static readonly HashSet<string> UpdateRoots = new HashSet<string>
    {
        "OnUpdate", "OnFixedUpdate", "OnLateUpdate", "OnActionUpdate", "DoTriggerStay", "DoCollisionStay", "DoTriggerStay2D", "DoCollisionStay2D",
    };

    static List<MethodDefinition> UpdatePaths(ModuleDefinition module)
    {
        var found = new List<MethodDefinition>();
        var seen = new HashSet<MethodDefinition>();
        foreach (TypeDefinition type in AllTypes(module.Types))
        {
            if (type.IsInterface || type.IsValueType || !DerivesFrom(type, "HutongGames.PlayMaker.FsmStateAction")) continue;
            foreach (MethodDefinition method in type.Methods)
                if (method.HasBody && UpdateRoots.Contains(method.Name) && method.Parameters.Count <= 1 && seen.Add(method)) found.Add(method);
        }
        // what they call in action classes of this module, to MaxUpdatePathDepth
        int from = 0;
        for (int depth = 0; depth < MaxUpdatePathDepth; depth++)
        {
            int to = found.Count;
            for (int i = from; i < to; i++)
                foreach (Instruction instruction in found[i].Body.Instructions)
                {
                    var called = instruction.Operand as MethodReference;
                    if (called == null || (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)) continue;
                    MethodDefinition resolved = Resolve(called);
                    if (resolved == null || resolved.Module != module || !resolved.HasBody || resolved.IsConstructor) continue;
                    if (!DerivesFrom(resolved.DeclaringType, "HutongGames.PlayMaker.FsmStateAction") || !seen.Add(resolved)) continue;
                    found.Add(resolved);
                }
            from = to;
        }
        return found;
    }

    // Calls a check copy may repeat: they only read (or set up once what later calls read).
    static readonly HashSet<string> PureCalls = new HashSet<string>
    {
        "System.Boolean System.String::IsNullOrEmpty(System.String)",
        "System.Boolean System.String::op_Equality(System.String,System.String)",
        "System.Boolean System.String::op_Inequality(System.String,System.String)",
        "System.Boolean UnityEngine.Object::op_Equality(UnityEngine.Object,UnityEngine.Object)",
        "System.Boolean UnityEngine.Object::op_Inequality(UnityEngine.Object,UnityEngine.Object)",
        "System.Boolean UnityEngine.Object::op_Implicit(UnityEngine.Object)",
        "UnityEngine.GameObject UnityEngine.Component::get_gameObject()",
        "UnityEngine.Transform UnityEngine.Component::get_transform()",
        "UnityEngine.Transform UnityEngine.GameObject::get_transform()",
        "UnityEngine.Vector3 UnityEngine.Vector2::op_Implicit(UnityEngine.Vector2)",
        "System.Int32 HutongGames.PlayMaker.NamedVariable::ToInt()",
        "System.Single HutongGames.PlayMaker.NamedVariable::ToFloat()",
        "System.Object HutongGames.PlayMaker.NamedVariable::get_RawValue()",
        "System.String System.Object::ToString()",
        "System.Collections.Generic.List`1<HutongGames.PlayMaker.FsmStateAction> HutongGames.PlayMaker.FsmState::get_ActiveActions()",
        "System.Collections.Generic.List`1<HutongGames.PlayMaker.FsmStateAction> HutongGames.PlayMaker.FsmState::get_finishedActions()",
        "HutongGames.PlayMaker.FsmState HutongGames.PlayMaker.Fsm::get_ActiveState()",
    };

    // Templates PlayMaker's earlier passes change (OpenTemplates fails on any other that differs from the
    // input): the game's assemblies, which copy the input's bodies, leave them as calls.
    static readonly HashSet<string> ChangedTemplates = new HashSet<string>
    {
        // FsmGate's and FsmTrim's starts (Cuts.cs, FsmTrim.cs)
        "System.Void HutongGames.PlayMaker.Fsm::FixedUpdate()",
        "System.Void HutongGames.PlayMaker.Fsm::LateUpdate()",
        // object names through CrowCompat Names (FastPaths.cs), log and console-mode patches
        "System.String HutongGames.PlayMaker.Fsm::get_OwnerName()",
        "System.String HutongGames.PlayMaker.Fsm::get_OwnerDebugName()",
        "System.String HutongGames.PlayMaker.Fsm::get_GameObjectName()",
        "System.String HutongGames.PlayMaker.FsmUtility::GetFullFsmLabel(PlayMakerFSM)",
        "System.Void HutongGames.PlayMaker.FsmDebugUtility::Log(UnityEngine.Object,System.String)",
        "System.Object HutongGames.LogHelper::FormatLog(System.Object,HutongGames.LogColor)",
        "System.Type HutongGames.PlayMaker.FsmVar::get_ObjectType()",
        "System.Void PlayMakerFSM::InitFsm()",
        "System.Boolean PlayMakerGUI::get_EnableStateLabelsInBuild()",
        "System.Boolean PlayMakerGUI::get_Enabled()",
        "System.Void PlayMakerGUI::InitInstance()",
    };

    static readonly Dictionary<MethodDefinition, bool> templateCache = new Dictionary<MethodDefinition, bool>();
    // PlayMaker's templates as they were before its pass compiled any caller (a template that is a caller too
    // is then copied as it was, not with its new bodies): their instructions and number of locals.
    static readonly Dictionary<MethodDefinition, List<Instruction>> frozen = new Dictionary<MethodDefinition, List<Instruction>>();
    static readonly Dictionary<MethodDefinition, int> frozenLocals = new Dictionary<MethodDefinition, int>();

    static IList<Instruction> TemplateCode(MethodDefinition method)
    {
        List<Instruction> code;
        return frozen.TryGetValue(method, out code) ? code : (IList<Instruction>)method.Body.Instructions;
    }

    static IList<VariableDefinition> Locals(MethodDefinition method)
    {
        int count;
        return frozenLocals.TryGetValue(method, out count) ? method.Body.Variables.Take(count).ToList() : (IList<VariableDefinition>)method.Body.Variables;
    }
    static readonly Dictionary<MethodDefinition, bool> pureCache = new Dictionary<MethodDefinition, bool>();
    static int compiledMethods, compiledSites, compiledBytes;

    static bool IsPlayMaker(TypeReference type)
    {
        while (type.DeclaringType != null) type = type.DeclaringType;
        var scope = type.Scope as AssemblyNameReference;
        if (scope != null) return scope.Name == "PlayMaker";
        var module = type.Scope as ModuleDefinition;
        return module != null && module.Assembly.Name.Name == "PlayMaker";
    }

    static bool InPlayMaker(MemberReference member)
    {
        return member.Module != null && member.Module.Assembly != null && member.Module.Assembly.Name.Name == "PlayMaker";
    }

    // A PlayMaker method small enough, and plain enough, to copy into its callers.
    static bool Template(MethodDefinition method)
    {
        bool known;
        if (templateCache.TryGetValue(method, out known)) return known;
        templateCache[method] = false;  // a recursive call isn't a template
        bool ok = InPlayMaker(method) && method.HasBody && !method.IsVirtual && !method.IsConstructor && !method.HasGenericParameters &&
                  !(method.HasThis && method.DeclaringType.IsValueType) &&
                  !method.DeclaringType.HasGenericParameters && !method.Body.HasExceptionHandlers && method.Body.Instructions.Count <= MaxTemplate &&
                  (method.ImplAttributes & MethodImplAttributes.NoInlining) == 0 && !method.IsPInvokeImpl && !method.IsInternalCall;
        if (ok)
            foreach (Instruction instruction in method.Body.Instructions)
            {
                OpCode op = instruction.OpCode;
                if (op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn || op == OpCodes.Localloc || op == OpCodes.Jmp || op == OpCodes.Calli ||
                    op == OpCodes.Tail || op == OpCodes.Constrained || op == OpCodes.Readonly || op == OpCodes.Arglist || op == OpCodes.Leave ||
                    op == OpCodes.Leave_S || op == OpCodes.Endfinally || op == OpCodes.Rethrow)
                {
                    ok = false;
                    break;
                }
                var called = instruction.Operand as MethodReference;
                if (called != null && (called is GenericInstanceMethod || HasGenericParameter(called.DeclaringType)))
                {
                    ok = false;
                    break;
                }
                if (called != null && op == OpCodes.Call && called.HasThis)
                {
                    MethodDefinition resolved = Resolve(called);
                    if (resolved == null || resolved.IsVirtual)
                    {
                        ok = false;
                        break;
                    }
                }
                var field = instruction.Operand as FieldReference;
                if (field != null && HasGenericParameter(field.DeclaringType))
                {
                    ok = false;
                    break;
                }
                var type = instruction.Operand as TypeReference;
                if (type != null && HasGenericParameter(type))
                {
                    ok = false;
                    break;
                }
            }
        templateCache[method] = ok;
        return ok;
    }

    static bool HasGenericParameter(TypeReference type)
    {
        if (type == null) return false;
        if (type.IsGenericParameter) return true;
        var instance = type as GenericInstanceType;
        if (instance != null) return instance.GenericArguments.Any(HasGenericParameter);
        var spec = type as TypeSpecification;
        return spec != null && HasGenericParameter(spec.ElementType);
    }

    static MethodDefinition Resolve(MethodReference method)
    {
        try { return method.Resolve(); }
        catch (AssemblyResolutionException) { return null; }
    }

    // Whether a call may be repeated by a check copy: a template that only reads.
    static bool Pure(MethodDefinition method)
    {
        bool known;
        if (pureCache.TryGetValue(method, out known)) return known;
        pureCache[method] = false;
        bool pure = PureCalls.Contains(method.FullName);
        if (!pure && Template(method))
        {
            pure = true;
            foreach (Instruction instruction in TemplateCode(method))
            {
                Code code = instruction.OpCode.Code;
                if (code == Code.Stfld || code == Code.Stsfld || code == Code.Starg || code == Code.Starg_S || code == Code.Newobj ||
                    code == Code.Newarr || code == Code.Throw || code == Code.Initobj || code == Code.Cpobj || code == Code.Stobj ||
                    code.ToString().StartsWith("Stelem") || code.ToString().StartsWith("Stind"))
                {
                    pure = false;
                    break;
                }
                var called = instruction.Operand as MethodReference;
                if (called == null || (code != Code.Call && code != Code.Callvirt)) continue;
                MethodDefinition resolved = Resolve(called);
                if (PureCalls.Contains(called.FullName)) continue;
                if (resolved == null || !Pure(resolved))
                {
                    pure = false;
                    break;
                }
            }
        }
        pureCache[method] = pure;
        return pure;
    }

    // Whether a template reads a field of this (or calls a template that does) before anything else:
    // a null this then throws the NullReferenceException callvirt would.
    static bool ReadsThisFirst(MethodDefinition method, int depth)
    {
        IList<Instruction> code = TemplateCode(method);
        if (!method.HasThis || code.Count < 2 || depth > MaxDepth) return false;
        Instruction first = code[0], second = code[1];
        if (first.OpCode != OpCodes.Ldarg_0) return false;
        if (second.OpCode == OpCodes.Ldfld || second.OpCode == OpCodes.Ldflda) return true;
        var called = second.Operand as MethodReference;
        if (called == null || (second.OpCode != OpCodes.Call && second.OpCode != OpCodes.Callvirt) || !called.HasThis || called.Parameters.Count > 0) return false;
        MethodDefinition resolved = Resolve(called);
        return resolved != null && Template(resolved) && ReadsThisFirst(resolved, depth + 1);
    }

    // References in a template's body, for a caller in another module: PlayMaker's as they are, the rest
    // from the target (or CrowCompat). Throws Unmappable for anything else.
    sealed class Unmappable : Exception
    {
        public Unmappable(string what) : base(what) { }
    }

    sealed class Mapper
    {
        readonly ModuleDefinition module;
        readonly bool local;  // the caller is in PlayMaker: operands stay as they are

        public Mapper(ModuleDefinition module)
        {
            this.module = module;
            local = module.Assembly.Name.Name == "PlayMaker";
        }

        public object Operand(object operand)
        {
            if (local) return operand;
            var method = operand as MethodReference;
            if (method != null) return Method(method);
            var field = operand as FieldReference;
            if (field != null) return Field(field);
            var type = operand as TypeReference;
            if (type != null) return Type(type);
            return operand;
        }

        public TypeReference Type(TypeReference type)
        {
            if (local) return type;
            if (type.IsGenericParameter) throw new Unmappable(type.FullName);
            var array = type as ArrayType;
            if (array != null) return new ArrayType(Type(array.ElementType), array.Rank);
            var byReference = type as ByReferenceType;
            if (byReference != null) return new ByReferenceType(Type(byReference.ElementType));
            if (type is TypeSpecification) throw new Unmappable(type.FullName);
            if (IsPlayMaker(type))
            {
                TypeDefinition resolved = type.Resolve();
                if (resolved == null) throw new Unmappable(type.FullName);
                return module.ImportReference(resolved);
            }
            TypeDefinition definition;
            if (target.TryGetValue(type.FullName, out definition) || compat.TryGetValue(type.FullName, out definition))
                return module.ImportReference(definition);
            throw new Unmappable(type.FullName);
        }

        public MethodReference Method(MethodReference method)
        {
            if (local) return method;
            if (method is GenericInstanceMethod || method.DeclaringType is TypeSpecification) throw new Unmappable(method.FullName);
            if (IsPlayMaker(method.DeclaringType))
            {
                MethodDefinition resolved = Resolve(method);
                if (resolved == null) throw new Unmappable(method.FullName);
                return module.ImportReference(resolved);
            }
            TypeDefinition owner;
            if (!target.TryGetValue(method.DeclaringType.FullName, out owner) && !compat.TryGetValue(method.DeclaringType.FullName, out owner))
                throw new Unmappable(method.FullName);
            string signature = Signature(method);
            MethodDefinition found = owner.Methods.FirstOrDefault(m => m.Name == method.Name && Signature(m) == signature);
            if (found == null) throw new Unmappable(method.FullName);
            return module.ImportReference(found);
        }

        public FieldReference Field(FieldReference field)
        {
            if (local) return field;
            if (field.DeclaringType is TypeSpecification) throw new Unmappable(field.FullName);
            if (IsPlayMaker(field.DeclaringType))
            {
                FieldDefinition resolved = field.Resolve();
                if (resolved == null) throw new Unmappable(field.FullName);
                return module.ImportReference(resolved);
            }
            TypeDefinition owner;
            if (!target.TryGetValue(field.DeclaringType.FullName, out owner) && !compat.TryGetValue(field.DeclaringType.FullName, out owner))
                throw new Unmappable(field.FullName);
            FieldDefinition found = owner.Fields.FirstOrDefault(f => f.Name == field.Name);
            if (found == null) throw new Unmappable(field.FullName);
            return module.ImportReference(found);
        }

        static string Signature(MethodReference method)
        {
            return method.ReturnType.FullName + "(" + string.Join(",", method.Parameters.Select(p => p.ParameterType.FullName).ToArray()) + ")" +
                   (method.HasThis ? "" : " static");
        }
    }

    // Whether every reference in a template (and the templates it calls) maps into another module (tried on
    // a module of its own, so the tries import nothing into the game's).
    static readonly Dictionary<MethodDefinition, bool> mappableCache = new Dictionary<MethodDefinition, bool>();
    static ModuleDefinition mappingScratch;

    static bool Mappable(MethodDefinition template)
    {
        bool known;
        if (mappableCache.TryGetValue(template, out known)) return known;
        mappableCache[template] = false;
        if (ChangedTemplates.Contains(template.FullName)) return false;
        if (mappingScratch == null) mappingScratch = ModuleDefinition.CreateModule("CrowFsmCompileScratch", ModuleKind.Dll);
        var mapper = new Mapper(mappingScratch);
        bool ok = true;
        try
        {
            mapper.Type(template.DeclaringType);
            foreach (ParameterDefinition parameter in template.Parameters) mapper.Type(parameter.ParameterType);
            foreach (VariableDefinition variable in template.Body.Variables) mapper.Type(variable.VariableType);
            mapper.Type(template.ReturnType);
            foreach (Instruction instruction in template.Body.Instructions) mapper.Operand(instruction.Operand);
        }
        catch (Unmappable)
        {
            ok = false;
        }
        mappableCache[template] = ok;
        return ok;
    }

    // The template a call in this module may inline, or null.
    static MethodDefinition SiteTemplate(Instruction instruction, ModuleDefinition module)
    {
        if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt) return null;
        var called = instruction.Operand as MethodReference;
        if (called == null || called is GenericInstanceMethod || !IsPlayMaker(called.DeclaringType)) return null;
        if (instruction.Previous != null && instruction.Previous.OpCode.OpCodeType == OpCodeType.Prefix) return null;
        MethodDefinition resolved = Resolve(called);
        if (resolved == null || !Template(resolved)) return null;
        if (module.Assembly.Name.Name != "PlayMaker" && !Mappable(resolved)) return null;
        return resolved;
    }

    static int FsmCompileMethods(ModuleDefinition module)
    {
        string name = module.Assembly.Name.Name;
        if (name != "PlayMaker" && name != "Recovered.CrowCountry" && name != "Recovered.Firstpass") return 0;
        TypeDefinition compile = compat["CrowCompat.FsmCompile"];
        bool check = (bool)compile.Fields.Single(f => f.Name == "CheckBuilt").Constant && !CompiledOnly;
        FieldReference mode = module.ImportReference(compile.Fields.Single(f => f.Name == "Mode"));
        MethodReference same = module.ImportReference(compile.Methods.Single(m => m.Name == "Same"));
        int sites = 0;
        if (name == "PlayMaker")
        {
            foreach (TypeDefinition type in AllTypes(module.Types))
                foreach (MethodDefinition method in type.Methods)
                    if (Template(method))
                    {
                        List<ExceptionHandler> none;
                        frozen[method] = CloneBody(method.Body, out none);
                        frozenLocals[method] = method.Body.Variables.Count;
                    }
            sites += OpenTemplates(module);
            foreach (KeyValuePair<string, string[]> pair in CompiledPlayMaker)
            {
                TypeDefinition type = GameType(module, pair.Key);
                if (type == null) continue;
                foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody && pair.Value.Contains(m.Name)).ToArray())
                    sites += CompileMethod(module, method, mode, same, check);
            }
        }
        else
            foreach (MethodDefinition method in UpdatePaths(module))
                sites += CompileMethod(module, method, mode, same, check);
        Count("fsm compile", sites);
        Console.WriteLine("FsmCompile " + name + ": " + compiledMethods + " methods, " + compiledSites + " sites inlined, about " + compiledBytes +
                          " bytes of IL added (check bodies: " + check + ")");
        compiledMethods = compiledSites = compiledBytes = 0;
        return sites;
    }

    // PlayMaker's own pass: the templates the game's assemblies copy are as the input has them, and the
    // members they reach are internal (with InternalsVisibleTo for the game's assemblies).
    static int OpenTemplates(ModuleDefinition module)
    {
        AssemblyDefinition input = module.AssemblyResolver.Resolve(AssemblyNameReference.Parse(module.Assembly.FullName));
        var current = new Dictionary<string, MethodDefinition>();
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods) current[method.FullName] = method;
        // The templates the game's assemblies can reach: those they can call, and the templates those call.
        var reach = new List<MethodDefinition>();
        var seen = new HashSet<MethodDefinition>();
        foreach (TypeDefinition type in AllTypes(input.MainModule.Types))
            if (Visible(type))
                foreach (MethodDefinition method in type.Methods)
                    if ((method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly) && Template(method) && Mappable(method) && seen.Add(method))
                        reach.Add(method);
        for (int i = 0; i < reach.Count; i++)
            foreach (Instruction instruction in reach[i].Body.Instructions)
            {
                var called = instruction.Operand as MethodReference;
                MethodDefinition resolved = called == null ? null : Resolve(called);
                if (resolved != null && Template(resolved) && Mappable(resolved) && seen.Add(resolved)) reach.Add(resolved);
            }
        int opened = 0, templates = 0;
        var changed = new List<string>();
        foreach (MethodDefinition method in reach)
            {
                MethodDefinition now;
                if (!current.TryGetValue(method.FullName, out now) || !SameBody(method.Body, now.Body))
                {
                    changed.Add(method.FullName);
                    continue;
                }
                templates++;
                if (Open(now)) opened++;
                foreach (Instruction instruction in now.Body.Instructions)
                {
                    var member = instruction.Operand as MemberReference;
                    if (member != null && Open(member)) opened++;
                }
            }
        if (changed.Count > 0)
            throw new Exception("FsmCompile: changed by an earlier pass (the game's assemblies would copy the input's bodies; add them to " +
                                "ChangedTemplates): " + string.Join(", ", changed.ToArray()));
        if (!module.Assembly.CustomAttributes.Any(a => a.AttributeType.Name == "InternalsVisibleToAttribute"))
        {
            MethodReference constructor = module.ImportReference(target["System.Runtime.CompilerServices.InternalsVisibleToAttribute"].Methods
                .Single(m => m.IsConstructor && m.Parameters.Count == 1));
            foreach (string friend in new[] { "Recovered.CrowCountry", "Recovered.Firstpass" })
            {
                var attribute = new CustomAttribute(constructor);
                attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, friend));
                module.Assembly.CustomAttributes.Add(attribute);
            }
        }
        Console.WriteLine("FsmCompile PlayMaker: " + templates + " templates for the game's assemblies, " + opened + " members made internal");
        return templates;
    }

    // A type the game's assemblies can name (public, or nested public or protected in one).
    static bool Visible(TypeDefinition type)
    {
        for (; type != null; type = type.DeclaringType)
            if (!(type.DeclaringType == null ? type.IsPublic : type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamilyOrAssembly)) return false;
        return true;
    }

    static bool SameBody(MethodBody a, MethodBody b)
    {
        if (a.Instructions.Count != b.Instructions.Count || a.Variables.Count != b.Variables.Count) return false;
        for (int i = 0; i < a.Instructions.Count; i++)
        {
            Instruction x = a.Instructions[i], y = b.Instructions[i];
            if (x.OpCode != y.OpCode) return false;
            if (x.Operand == null && y.Operand == null) continue;
            if (x.Operand == null || y.Operand == null) return false;
            var xi = x.Operand as Instruction;
            if (xi != null)
            {
                if (a.Instructions.IndexOf(xi) != b.Instructions.IndexOf((Instruction)y.Operand)) return false;
                continue;
            }
            var xm = x.Operand as MemberReference;
            if (xm != null)
            {
                var ym = y.Operand as MemberReference;
                if (ym == null || xm.FullName != ym.FullName) return false;
                continue;
            }
            var xv = x.Operand as VariableDefinition;
            if (xv != null)
            {
                if (xv.Index != ((VariableDefinition)y.Operand).Index) return false;
                continue;
            }
            var xp = x.Operand as ParameterDefinition;
            if (xp != null)
            {
                if (xp.Index != ((ParameterDefinition)y.Operand).Index) return false;
                continue;
            }
            if (x.Operand is Instruction[] || !x.Operand.Equals(y.Operand)) return false;
        }
        return true;
    }

    // A PlayMaker member (and its declaring types) internal at least: true when it changed.
    static bool Open(MemberReference member)
    {
        bool changed = false;
        var field = member as FieldReference;
        var method = member as MethodReference;
        var type = member as TypeReference;
        if (field != null && InPlayMaker(field))
        {
            FieldDefinition definition = field.Resolve();
            if (definition != null && definition.Module.Assembly.Name.Name == "PlayMaker")
            {
                if (definition.IsPrivate || definition.IsFamilyAndAssembly)
                {
                    definition.Attributes = (definition.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Assembly;
                    changed = true;
                }
                else if (definition.IsFamily)
                {
                    definition.Attributes = (definition.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.FamORAssem;
                    changed = true;
                }
                type = definition.DeclaringType;
            }
        }
        else if (method != null && InPlayMaker(method))
        {
            MethodDefinition definition = Resolve(method);
            if (definition != null && definition.Module.Assembly.Name.Name == "PlayMaker")
            {
                if (definition.IsPrivate || definition.IsFamilyAndAssembly)
                {
                    definition.Attributes = (definition.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Assembly;
                    changed = true;
                }
                else if (definition.IsFamily)
                {
                    definition.Attributes = (definition.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.FamORAssem;
                    changed = true;
                }
                type = definition.DeclaringType;
            }
        }
        for (TypeDefinition nested = type == null ? null : type.Resolve(); nested != null && nested.DeclaringType != null; nested = nested.DeclaringType)
        {
            if (nested.Module.Assembly.Name.Name != "PlayMaker") break;
            if (nested.IsNestedPrivate || nested.IsNestedFamilyAndAssembly)
            {
                nested.Attributes = (nested.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NestedAssembly;
                changed = true;
            }
            else if (nested.IsNestedFamily)
            {
                nested.Attributes = (nested.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NestedFamORAssem;
                changed = true;
            }
        }
        return changed;
    }

    // One caller: the three (or two) bodies, when it calls a template. The number of sites inlined.
    static int CompileMethod(ModuleDefinition module, MethodDefinition method, FieldReference mode, MethodReference same, bool check)
    {
        MethodBody body = method.Body;
        bool loop = method.DeclaringType.FullName == "HutongGames.PlayMaker.FsmState" && method.Parameters.Count == 0 &&
                    (method.Name == "OnUpdate" || method.Name == "OnFixedUpdate" || method.Name == "OnLateUpdate");
        Instruction[] found = body.Instructions.Where(i => SiteTemplate(i, module) != null).ToArray();
        if (found.Length == 0 && !loop) return 0;
        int[] siteIndexes = found.Select(i => body.Instructions.IndexOf(i)).ToArray();
        long before = BodySize(body) & 0xFFFFFFFF;
        var mapper = new Mapper(module);
        List<ExceptionHandler> originalHandlers;
        List<Instruction> original = CloneBody(body, out originalHandlers);
        List<ExceptionHandler> checkHandlers = null;
        List<Instruction> checkBody = check ? CloneBody(body, out checkHandlers) : null;
        Instruction[] checkSites = check ? siteIndexes.Select(i => checkBody[i]).ToArray() : null;
        int sites = 0;

        // the compiled body: in place
        MethodDefinition[] templates = found.Select(i => SiteTemplate(i, module)).ToArray();
        for (int s = 0; s < found.Length; s++)
        {
            Instruction call = found[s];
            MethodDefinition template = templates[s];
            List<VariableDefinition> args;
            List<Instruction> code = Expand(module, body, mapper, template, call.OpCode == OpCodes.Callvirt, 0, out args);
            ReplaceRetarget(method, call, code);
            sites++;
        }
        if (loop) SkipUnusedUpdates(module, method);

        // the check body: the original's sites that only read, each also inlined and compared
        if (check)
        {
            for (int s = 0; s < checkSites.Length; s++)
            {
                Instruction call = checkSites[s];
                MethodDefinition template = templates[s];
                if (template == null || !Pure(template) || template.ReturnType.MetadataType == MetadataType.Void) continue;
                List<VariableDefinition> args;
                List<Instruction> code = Expand(module, body, mapper, template, call.OpCode == OpCodes.Callvirt, 0, out args);
                TypeReference returned = mapper.Type(template.ReturnType);
                var compiled = new VariableDefinition(returned);
                var originalValue = new VariableDefinition(returned);
                body.Variables.Add(compiled);
                body.Variables.Add(originalValue);
                code.Add(Instruction.Create(OpCodes.Stloc, compiled));
                foreach (VariableDefinition arg in args) code.Add(Instruction.Create(OpCodes.Ldloc, arg));
                code.Add(Instruction.Create(call.OpCode, (MethodReference)call.Operand));
                code.Add(Instruction.Create(OpCodes.Stloc, originalValue));
                code.Add(Instruction.Create(OpCodes.Ldloc, compiled));
                if (returned.IsValueType) code.Add(Instruction.Create(OpCodes.Box, returned));
                code.Add(Instruction.Create(OpCodes.Ldloc, originalValue));
                if (returned.IsValueType) code.Add(Instruction.Create(OpCodes.Box, returned));
                code.Add(Instruction.Create(OpCodes.Ldstr, method.FullName + " IL_" + call.Offset.ToString("x4") + " <- " + template.FullName));
                code.Add(Instruction.Create(OpCodes.Call, same));
                code.Add(Instruction.Create(OpCodes.Ldloc, originalValue));
                ReplaceRetarget(checkBody, checkHandlers, call, code);
            }
        }

        if (CompiledOnly)
        {
            body.InitLocals = true;
            compiledMethods++;
            compiledSites += sites;
            compiledBytes += (int)((BodySize(body) & 0xFFFFFFFF) - before);
            return sites + (loop ? 1 : 0);
        }
        // switch (FsmCompile.Mode) { 0: original; 1: check; default: compiled }
        List<Instruction> compiledBody = body.Instructions.ToList();
        List<ExceptionHandler> compiledHandlers = body.ExceptionHandlers.ToList();
        Instruction originalStart = original[0];
        var prologue = new List<Instruction> { Instruction.Create(OpCodes.Ldsfld, mode) };
        if (check) prologue.Add(Instruction.Create(OpCodes.Switch, new[] { originalStart, checkBody[0] }));
        else prologue.Add(Instruction.Create(OpCodes.Brfalse, originalStart));
        EndHandlersAt(compiledHandlers, check ? checkBody[0] : originalStart);
        if (check) EndHandlersAt(checkHandlers, originalStart);
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        foreach (Instruction instruction in prologue.Concat(compiledBody).Concat(check ? checkBody : new List<Instruction>()).Concat(original))
            body.Instructions.Add(instruction);
        foreach (ExceptionHandler handler in compiledHandlers.Concat(check ? checkHandlers : new List<ExceptionHandler>()).Concat(originalHandlers))
            body.ExceptionHandlers.Add(handler);
        body.InitLocals = true;
        compiledMethods++;
        compiledSites += sites;
        compiledBytes += (int)((BodySize(body) & 0xFFFFFFFF) - before);
        return sites + (loop ? 1 : 0);
    }

    // A copy of a body's instructions and handlers (branches and handlers pointing into the copy).
    static List<Instruction> CloneBody(MethodBody body, out List<ExceptionHandler> handlers)
    {
        var map = new Dictionary<Instruction, Instruction>();
        var copy = new List<Instruction>();
        foreach (Instruction instruction in body.Instructions)
        {
            Instruction clone = Instruction.Create(OpCodes.Nop);
            clone.OpCode = instruction.OpCode;
            clone.Operand = instruction.Operand;
            clone.Offset = instruction.Offset;
            map[instruction] = clone;
            copy.Add(clone);
        }
        foreach (Instruction clone in copy)
        {
            var target = clone.Operand as Instruction;
            if (target != null) clone.Operand = map[target];
            var targets = clone.Operand as Instruction[];
            if (targets != null) clone.Operand = targets.Select(t => map[t]).ToArray();
        }
        handlers = new List<ExceptionHandler>();
        foreach (ExceptionHandler handler in body.ExceptionHandlers)
            handlers.Add(new ExceptionHandler(handler.HandlerType)
            {
                TryStart = Mapped(map, handler.TryStart),
                TryEnd = Mapped(map, handler.TryEnd),
                HandlerStart = Mapped(map, handler.HandlerStart),
                HandlerEnd = Mapped(map, handler.HandlerEnd),
                FilterStart = Mapped(map, handler.FilterStart),
                CatchType = handler.CatchType,
            });
        return copy;
    }

    static Instruction Mapped(Dictionary<Instruction, Instruction> map, Instruction instruction)
    {
        return instruction == null ? null : map[instruction];
    }

    // Handlers that ran to the end of the method end where the next body starts.
    static void EndHandlersAt(List<ExceptionHandler> handlers, Instruction next)
    {
        foreach (ExceptionHandler handler in handlers)
        {
            if (handler.TryEnd == null) handler.TryEnd = next;
            if (handler.HandlerEnd == null) handler.HandlerEnd = next;
        }
    }

    // A call replaced by code in a method's own body: branches and handlers that pointed at the call
    // point at the code's first instruction.
    static void ReplaceRetarget(MethodDefinition method, Instruction call, List<Instruction> code)
    {
        InsertBeforeRetarget(method, call, code.ToArray());
        method.Body.GetILProcessor().Remove(call);
    }

    // The same in a list (a copied body) and its handlers.
    static void ReplaceRetarget(List<Instruction> list, List<ExceptionHandler> handlers, Instruction call, List<Instruction> code)
    {
        int index = list.IndexOf(call);
        list.RemoveAt(index);
        list.InsertRange(index, code);
        foreach (Instruction instruction in list)
        {
            if (instruction.Operand == call) instruction.Operand = code[0];
            var targets = instruction.Operand as Instruction[];
            if (targets != null)
                for (int i = 0; i < targets.Length; i++) if (targets[i] == call) targets[i] = code[0];
        }
        foreach (ExceptionHandler handler in handlers)
        {
            if (handler.TryStart == call) handler.TryStart = code[0];
            if (handler.TryEnd == call) handler.TryEnd = code[0];
            if (handler.HandlerStart == call) handler.HandlerStart = code[0];
            if (handler.HandlerEnd == call) handler.HandlerEnd = code[0];
            if (handler.FilterStart == call) handler.FilterStart = code[0];
        }
    }

    // A template's body for a call site (the arguments on the stack; its value left there), its own calls
    // of templates expanded too; args: the locals the arguments went into.
    static List<Instruction> Expand(ModuleDefinition module, MethodBody caller, Mapper mapper, MethodDefinition template, bool callvirt,
                                    int depth, out List<VariableDefinition> args)
    {
        var code = new List<Instruction>();
        MethodBody source = template.Body;
        IList<Instruction> instructions = TemplateCode(template);
        int thisCount = template.HasThis ? 1 : 0;
        args = new List<VariableDefinition>();
        for (int k = 0; k < template.Parameters.Count + thisCount; k++)
        {
            TypeReference type = k < thisCount ? mapper.Type(template.DeclaringType) : mapper.Type(template.Parameters[k - thisCount].ParameterType);
            var arg = new VariableDefinition(type);
            caller.Variables.Add(arg);
            args.Add(arg);
        }
        for (int k = args.Count - 1; k >= 0; k--) code.Add(Instruction.Create(OpCodes.Stloc, args[k]));
        if (callvirt && template.HasThis && !ReadsThisFirst(template, 0))
        {
            Instruction ok = Instruction.Create(OpCodes.Nop);
            code.Add(Instruction.Create(OpCodes.Ldloc, args[0]));
            code.Add(Instruction.Create(OpCodes.Brtrue, ok));
            code.Add(Instruction.Create(OpCodes.Newobj, module.ImportReference(target["System.NullReferenceException"].Methods
                .Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0))));
            code.Add(Instruction.Create(OpCodes.Throw));
            code.Add(ok);
        }
        var locals = new List<VariableDefinition>();
        foreach (VariableDefinition variable in Locals(template))
        {
            var local = new VariableDefinition(mapper.Type(variable.VariableType));
            caller.Variables.Add(local);
            locals.Add(local);
        }
        Instruction end = Instruction.Create(OpCodes.Nop);
        var first = new Dictionary<Instruction, Instruction>();  // a template instruction -> the first of its copy
        var branches = new List<Instruction>();
        foreach (Instruction instruction in instructions)
        {
            var piece = new List<Instruction>();
            Code op = instruction.OpCode.Code;
            int argument = ArgumentIndex(instruction, source);
            int variable = VariableIndex(instruction);
            var called = instruction.Operand as MethodReference;
            MethodDefinition nested = called != null && (op == Code.Call || op == Code.Callvirt) && depth + 1 < MaxDepth ? Resolve(called) : null;
            if (nested != null && !Template(nested)) nested = null;
            if (nested != null && module.Assembly.Name.Name != "PlayMaker" && !Mappable(nested)) nested = null;
            if (op == Code.Ret) piece.Add(Instruction.Create(OpCodes.Br, end));
            else if (argument >= 0)
            {
                if (op == Code.Ldarga || op == Code.Ldarga_S) piece.Add(Instruction.Create(OpCodes.Ldloca, args[argument]));
                else if (op == Code.Starg || op == Code.Starg_S) piece.Add(Instruction.Create(OpCodes.Stloc, args[argument]));
                else piece.Add(Instruction.Create(OpCodes.Ldloc, args[argument]));
            }
            else if (variable >= 0)
            {
                if (op == Code.Ldloca || op == Code.Ldloca_S) piece.Add(Instruction.Create(OpCodes.Ldloca, locals[variable]));
                else if (op.ToString().StartsWith("Stloc")) piece.Add(Instruction.Create(OpCodes.Stloc, locals[variable]));
                else piece.Add(Instruction.Create(OpCodes.Ldloc, locals[variable]));
            }
            else if (nested != null)
            {
                List<VariableDefinition> inner;
                piece.AddRange(Expand(module, caller, mapper, nested, op == Code.Callvirt, depth + 1, out inner));
            }
            else
            {
                var member = instruction.Operand as MemberReference;
                if (member != null && module.Assembly.Name.Name == "PlayMaker") Open(member);
                Instruction copy = Instruction.Create(OpCodes.Nop);
                copy.OpCode = instruction.OpCode;
                copy.Operand = instruction.Operand is Instruction || instruction.Operand is Instruction[] ? instruction.Operand : mapper.Operand(instruction.Operand);
                if (instruction.Operand is Instruction || instruction.Operand is Instruction[]) branches.Add(copy);
                piece.Add(copy);
            }
            first[instruction] = piece[0];
            code.AddRange(piece);
        }
        foreach (Instruction branch in branches)
        {
            var single = branch.Operand as Instruction;
            if (single != null) branch.Operand = first[single];
            else branch.Operand = ((Instruction[])branch.Operand).Select(t => first[t]).ToArray();
        }
        code.Add(end);
        return code;
    }

    // ldarg/ldarga/starg's argument number (this is 0), or -1.
    static int ArgumentIndex(Instruction instruction, MethodBody body)
    {
        switch (instruction.OpCode.Code)
        {
            case Code.Ldarg_0: return 0;
            case Code.Ldarg_1: return 1;
            case Code.Ldarg_2: return 2;
            case Code.Ldarg_3: return 3;
            case Code.Ldarg:
            case Code.Ldarg_S:
            case Code.Ldarga:
            case Code.Ldarga_S:
            case Code.Starg:
            case Code.Starg_S:
                var parameter = (ParameterDefinition)instruction.Operand;
                return parameter == body.ThisParameter ? 0 : parameter.Index + (body.Method.HasThis ? 1 : 0);
        }
        return -1;
    }

    // ldloc/ldloca/stloc's variable number, or -1.
    static int VariableIndex(Instruction instruction)
    {
        switch (instruction.OpCode.Code)
        {
            case Code.Ldloc_0:
            case Code.Stloc_0: return 0;
            case Code.Ldloc_1:
            case Code.Stloc_1: return 1;
            case Code.Ldloc_2:
            case Code.Stloc_2: return 2;
            case Code.Ldloc_3:
            case Code.Stloc_3: return 3;
            case Code.Ldloc:
            case Code.Ldloc_S:
            case Code.Ldloca:
            case Code.Ldloca_S:
            case Code.Stloc:
            case Code.Stloc_S:
                return ((VariableDefinition)instruction.Operand).Index;
        }
        return -1;
    }

    // In the compiled body of a state loop: each call of a running action's update, only when its
    // crowHooks has that update's bit (OnUpdate 2, OnFixedUpdate 4, OnLateUpdate 8; filled from
    // FsmTrim.Hooks while 0).
    static void SkipUnusedUpdates(ModuleDefinition module, MethodDefinition method)
    {
        int mask = method.Name == "OnUpdate" ? 2 : method.Name == "OnFixedUpdate" ? 4 : 8;
        TypeDefinition action = module.GetType("HutongGames.PlayMaker.FsmStateAction");
        FieldDefinition hooks = action.Fields.Single(f => f.Name == "crowHooks");
        MethodReference fill = CompatMethod(module, "FsmTrim", "Hooks");
        foreach (Instruction call in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Callvirt && Calls(i, "HutongGames.PlayMaker.FsmStateAction", method.Name)).ToArray())
        {
            var actionLocal = new VariableDefinition(action);
            var bits = new VariableDefinition(module.TypeSystem.Int32);
            method.Body.Variables.Add(actionLocal);
            method.Body.Variables.Add(bits);
            Instruction skip = Instruction.Create(OpCodes.Nop);
            Instruction have = Instruction.Create(OpCodes.Ldloc, bits);
            var code = new List<Instruction>
            {
                Instruction.Create(OpCodes.Stloc, actionLocal),
                Instruction.Create(OpCodes.Ldloc, actionLocal),
                Instruction.Create(OpCodes.Ldfld, hooks),
                Instruction.Create(OpCodes.Stloc, bits),
                Instruction.Create(OpCodes.Ldloc, bits),
                Instruction.Create(OpCodes.Brtrue, have),
                Instruction.Create(OpCodes.Ldloc, actionLocal),
                Instruction.Create(OpCodes.Call, fill),
                Instruction.Create(OpCodes.Stloc, bits),
                Instruction.Create(OpCodes.Ldloc, actionLocal),
                Instruction.Create(OpCodes.Ldloc, bits),
                Instruction.Create(OpCodes.Stfld, hooks),
                have,
                Instruction.Create(OpCodes.Ldc_I4, mask),
                Instruction.Create(OpCodes.And),
                Instruction.Create(OpCodes.Brfalse, skip),
                Instruction.Create(OpCodes.Ldloc, actionLocal),
                Instruction.Create(OpCodes.Callvirt, (MethodReference)call.Operand),
                skip,
            };
            ReplaceRetarget(method, call, code);
        }
    }
}
