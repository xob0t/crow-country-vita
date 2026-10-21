using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;

// Allocation in play: PlayMaker's DOTween actions' tweens recyclable when CrowCompat TweenRecycle.On
// is set (compat/Alloc.cs says why that is safe there). Each action's OnEnter calls
// tween.SetRecyclable(recyclable.Value); the value becomes recyclable.Value | TweenRecycle.On.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    static int RecyclePlayMakerTweens(ModuleDefinition module)
    {
        FieldReference on = CompatField(module, "TweenRecycle", "On");
        int count = 0;
        foreach (TypeDefinition type in module.Types.Where(t => t.Namespace == "HutongGames.PlayMaker.Actions" && t.Name.StartsWith("DOTween")))
        {
            MethodDefinition enter = type.Methods.SingleOrDefault(m => m.Name == "OnEnter" && m.Parameters.Count == 0 && m.HasBody);
            if (enter == null) continue;
            ILProcessor il = enter.Body.GetILProcessor();
            foreach (Instruction call in enter.Body.Instructions.Where(i => Calls(i, "DG.Tweening.TweenSettingsExtensions", "SetRecyclable") &&
                         ((MethodReference)i.Operand).Parameters.Count == 2).ToArray())
            {
                InsertBefore(il, call, Instruction.Create(OpCodes.Ldsfld, on), Instruction.Create(OpCodes.Or));
                count++;
            }
        }
        return count;
    }

    // ---------------------------------------------------------------- delegates made again and again
    //
    // CacheBoundDelegates: `new D(this.M)` (ldarg.0; ldftn M; newobj D) in an instance method of a class
    // becomes a call of a helper on the class that makes the delegate once per object and keeps it in a
    // private field (crowDelegateN): PlayMaker's DOTween actions hand DOTween a new TweenCallback for
    // their OnStart/OnComplete each time their state is entered (Station Square standing: 43 a second).
    // A delegate to the same method of the same object is equal to the one made before; nothing in the
    // game compares delegates by reference. Not in generic types or methods, value types or helpers made
    // by other passes. CrowCompat SharedClosures.On false (-crow-alloc-share game) makes the helper
    // make a new one each time, as before.
    // CacheStaticDelegates: `new D(M)` for a static M (ldnull; ldftn M; newobj D) the same with a static
    // field: EasingFunction.GetEasingFunction made a delegate per call (each Blob trail piece's OnEnable).
    // ShareShortcutClosures: DOTween's shortcuts (DOLocalRotate, DOMove, ...) make a closure holding their
    // target and a getter and setter delegate on it for every tween (Station Square: 2 x 44 delegates a
    // second). For a closure class whose only field is the target (a UnityEngine.Object), set once from
    // the shortcut's first argument and never written by its lambdas, the closure comes from a factory on
    // its class (crowFor) that asks CrowCompat SharedClosures for the one made before for the same
    // target, and the getter and setter from helpers that make them once per closure: the same
    // delegates, reading and writing the same target.
    //
    // The assemblies whose delegates are cached: the game's, PlayMaker and DOTween.
    static bool CachesDelegates(ModuleDefinition module)
    {
        string name = module.Assembly.Name.Name;
        return IsGameAssembly(module) || name == "PlayMaker" || name == "DOTween";
    }

    static int CacheBoundDelegates(ModuleDefinition module)
    {
        if (!CachesDelegates(module)) return 0;
        FieldReference on = CompatField(module, "SharedClosures", "On");
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types).ToArray())
        {
            if (type.IsValueType || type.IsInterface || type.HasGenericParameters || IsGenericNested(type)) continue;
            var helpers = new Dictionary<string, MethodDefinition>();
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody && m.HasThis && !m.HasGenericParameters && !m.Name.StartsWith("crow")).ToArray())
            {
                Collection<Instruction> code = method.Body.Instructions;
                for (int i = 0; i + 2 < code.Count; i++)
                {
                    Instruction self = code[i], pointer = code[i + 1], make = code[i + 2];
                    if (self.OpCode != OpCodes.Ldarg_0 || pointer.OpCode != OpCodes.Ldftn || make.OpCode != OpCodes.Newobj) continue;
                    var target = (MethodReference)pointer.Operand;
                    var constructor = (MethodReference)make.Operand;
                    if (!target.HasThis || target is GenericInstanceMethod || !IsDelegateConstructor(constructor)) continue;
                    if (constructor.DeclaringType.ContainsGenericParameter || target.DeclaringType.ContainsGenericParameter) continue;
                    if (Targeted(method, pointer) || Targeted(method, make)) continue;
                    string key = target.FullName + "|" + constructor.DeclaringType.FullName;
                    MethodDefinition helper;
                    if (!helpers.TryGetValue(key, out helper))
                    {
                        helper = BoundDelegateHelper(module, type, target, constructor, on, type.Methods.Count(m => m.Name.StartsWith("crowDelegate")));
                        helpers[key] = helper;
                    }
                    pointer.OpCode = OpCodes.Call;
                    pointer.Operand = helper;
                    make.OpCode = OpCodes.Nop;
                    make.Operand = null;
                    count++;
                }
            }
        }
        return count;
    }

    static bool IsGenericNested(TypeDefinition type)
    {
        for (TypeDefinition t = type.DeclaringType; t != null; t = t.DeclaringType)
            if (t.HasGenericParameters) return true;
        return false;
    }

    static bool IsDelegateConstructor(MethodReference constructor)
    {
        if (constructor.Name != ".ctor" || constructor.Parameters.Count != 2) return false;
        if (constructor.Parameters[0].ParameterType.FullName != "System.Object" || constructor.Parameters[1].ParameterType.FullName != "System.IntPtr") return false;
        TypeDefinition type;
        try { type = constructor.DeclaringType.Resolve(); }
        catch (AssemblyResolutionException) { return false; }
        return type != null && type.BaseType != null && type.BaseType.FullName == "System.MulticastDelegate";
    }

    // Whether a branch or an exception handler lands on the instruction.
    static bool Targeted(MethodDefinition method, Instruction instruction)
    {
        foreach (Instruction other in method.Body.Instructions)
        {
            if (other.Operand == instruction) return true;
            var targets = other.Operand as Instruction[];
            if (targets != null && targets.Contains(instruction)) return true;
        }
        return method.Body.ExceptionHandlers.Any(h => h.TryStart == instruction || h.TryEnd == instruction || h.HandlerStart == instruction ||
                                                     h.HandlerEnd == instruction || h.FilterStart == instruction);
    }

    // instance D crowDelegateN() { if (!SharedClosures.On) return new D(this.M); return crowDelegateFieldN ?? (crowDelegateFieldN = new D(this.M)); }
    static MethodDefinition BoundDelegateHelper(ModuleDefinition module, TypeDefinition type, MethodReference target, MethodReference constructor,
                                                FieldReference on, int index)
    {
        TypeReference delegateType = constructor.DeclaringType;
        FieldDefinition field = AddField(type, "crowDelegateField" + index, delegateType);
        var helper = new MethodDefinition("crowDelegate" + index, MethodAttributes.Private | MethodAttributes.HideBySig, delegateType);
        ILProcessor il = helper.Body.GetILProcessor();
        Instruction fresh = Instruction.Create(OpCodes.Ldarg_0);
        Instruction done = Instruction.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldsfld, on);
        il.Emit(OpCodes.Brfalse, fresh);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, done);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldftn, target);
        il.Emit(OpCodes.Newobj, constructor);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        il.Append(fresh);
        il.Emit(OpCodes.Ldftn, target);
        il.Emit(OpCodes.Newobj, constructor);
        il.Append(done);
        type.Methods.Add(helper);
        return helper;
    }

    static int CacheStaticDelegates(ModuleDefinition module)
    {
        if (!CachesDelegates(module)) return 0;
        FieldReference on = CompatField(module, "SharedClosures", "On");
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types).ToArray())
        {
            if (type.IsInterface || type.HasGenericParameters || IsGenericNested(type)) continue;
            var helpers = new Dictionary<string, MethodDefinition>();
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody && !m.HasGenericParameters && !m.Name.StartsWith("crow") && m.Name != ".cctor").ToArray())
            {
                Collection<Instruction> code = method.Body.Instructions;
                for (int i = 0; i + 2 < code.Count; i++)
                {
                    Instruction none = code[i], pointer = code[i + 1], make = code[i + 2];
                    if (none.OpCode != OpCodes.Ldnull || pointer.OpCode != OpCodes.Ldftn || make.OpCode != OpCodes.Newobj) continue;
                    var target = (MethodReference)pointer.Operand;
                    var constructor = (MethodReference)make.Operand;
                    if (target.HasThis || target is GenericInstanceMethod || !IsDelegateConstructor(constructor)) continue;
                    if (constructor.DeclaringType.ContainsGenericParameter || target.DeclaringType.ContainsGenericParameter) continue;
                    if (Targeted(method, pointer) || Targeted(method, make)) continue;
                    string key = target.FullName + "|" + constructor.DeclaringType.FullName;
                    MethodDefinition helper;
                    if (!helpers.TryGetValue(key, out helper))
                    {
                        helper = StaticDelegateHelper(type, target, constructor, on, type.Methods.Count(m => m.Name.StartsWith("crowStaticDelegate")));
                        helpers[key] = helper;
                    }
                    // ldnull stays (a branch may land on it) and is dropped by the helper's pop.
                    none.OpCode = OpCodes.Ldnull;
                    pointer.OpCode = OpCodes.Pop;
                    pointer.Operand = null;
                    make.OpCode = OpCodes.Call;
                    make.Operand = helper;
                    count++;
                }
            }
        }
        return count;
    }

    // static D crowStaticDelegateN() { if (!SharedClosures.On) return new D(M); return field ?? (field = new D(M)); }
    static MethodDefinition StaticDelegateHelper(TypeDefinition type, MethodReference target, MethodReference constructor, FieldReference on, int index)
    {
        TypeReference delegateType = constructor.DeclaringType;
        var field = new FieldDefinition("crowStaticDelegateField" + index, FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.NotSerialized, delegateType);
        type.Fields.Add(field);
        var helper = new MethodDefinition("crowStaticDelegate" + index, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, delegateType);
        ILProcessor il = helper.Body.GetILProcessor();
        Instruction fresh = Instruction.Create(OpCodes.Ldnull);
        Instruction done = Instruction.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldsfld, on);
        il.Emit(OpCodes.Brfalse, fresh);
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, done);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldftn, target);
        il.Emit(OpCodes.Newobj, constructor);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Stsfld, field);
        il.Emit(OpCodes.Ret);
        il.Append(fresh);
        il.Emit(OpCodes.Ldftn, target);
        il.Emit(OpCodes.Newobj, constructor);
        il.Append(done);
        type.Methods.Add(helper);
        return helper;
    }

    static int ShareShortcutClosures(ModuleDefinition module)
    {
        TypeDefinition shortcuts = GameType(module, "DG.Tweening.ShortcutExtensions");
        if (shortcuts == null) return 0;
        int count = 0;
        var closureHelpers = new Dictionary<string, MethodDefinition>();
        foreach (MethodDefinition method in shortcuts.Methods.Where(m => m.HasBody && m.IsStatic && m.Parameters.Count > 0).ToArray())
        {
            Collection<Instruction> code = method.Body.Instructions;
            Instruction[] news = code.Where(i => i.OpCode == OpCodes.Newobj && ((MethodReference)i.Operand).DeclaringType.Resolve().DeclaringType == shortcuts &&
                                                 ((MethodReference)i.Operand).DeclaringType.Name.StartsWith("<>c__DisplayClass")).ToArray();
            if (news.Length != 1) continue;
            TypeDefinition closure = ((MethodReference)news[0].Operand).DeclaringType.Resolve();
            if (closure.HasGenericParameters || closure.Fields.Count(f => !f.IsStatic) != 1) continue;
            FieldDefinition held = closure.Fields.Single(f => !f.IsStatic);
            if (held.Name != "target" || !DerivesFrom(held.FieldType, "UnityEngine.Object")) continue;
            if (closure.Methods.Any(m => m.HasBody && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference &&
                                                                               ((FieldReference)i.Operand).Resolve() == held))) continue;
            // newobj C; stloc x; ... ldloc x; ldarg.0; stfld C::target, and no other store to the closure.
            Instruction store = news[0].Next;
            if (store == null || !IsStloc(store)) continue;
            Instruction[] stores = code.Where(i => i.OpCode == OpCodes.Stfld && ((FieldReference)i.Operand).Resolve() == held).ToArray();
            if (stores.Length != 1 || stores[0].Previous.OpCode != OpCodes.Ldarg_0 || !IsLdlocOf(stores[0].Previous.Previous, store, method)) continue;
            if (code.Any(i => IsStlocOf(i, store, method) && i != store)) continue;
            // The delegates made on it: ldloc x; ldftn C::m; newobj D.
            var sites = new List<Instruction>();
            bool other = false;
            foreach (Instruction pointer in code.Where(i => i.OpCode == OpCodes.Ldftn))
            {
                var target = (MethodReference)pointer.Operand;
                if (target.DeclaringType.Resolve() != closure) continue;
                Instruction make = pointer.Next;
                if (!IsLdlocOf(pointer.Previous, store, method) || make == null || make.OpCode != OpCodes.Newobj || !IsDelegateConstructor((MethodReference)make.Operand) ||
                    Targeted(method, pointer) || Targeted(method, make))
                {
                    other = true;
                    break;
                }
                sites.Add(pointer);
            }
            if (other || sites.Count == 0) continue;
            foreach (Instruction pointer in sites)
            {
                var target = (MethodReference)pointer.Operand;
                var constructor = (MethodReference)pointer.Next.Operand;
                string key = closure.FullName + "|" + target.Name + "|" + constructor.DeclaringType.FullName;
                MethodDefinition helper;
                if (!closureHelpers.TryGetValue(key, out helper))
                {
                    helper = ClosureDelegateHelper(module, closure, target, constructor, closure.Methods.Count(m => m.Name.StartsWith("crowDelegate")));
                    closureHelpers[key] = helper;
                }
                Instruction make = pointer.Next;
                pointer.OpCode = OpCodes.Call;
                pointer.Operand = helper;
                make.OpCode = OpCodes.Nop;
                make.Operand = null;
            }
            // newobj C becomes C.crowFor(target) (the closure made before for the target, or a new one),
            // and the store of the target goes.
            if (Targeted(method, stores[0]) || Targeted(method, stores[0].Previous)) continue;
            MethodDefinition factory;
            if (!closureHelpers.TryGetValue(closure.FullName, out factory))
            {
                factory = ClosureFactory(module, closure, held, closureHelpers.Count + 1);
                closureHelpers[closure.FullName] = factory;
            }
            ILProcessor il = method.Body.GetILProcessor();
            news[0].OpCode = OpCodes.Ldarg_0;
            news[0].Operand = null;
            InsertAfter(il, news[0], Instruction.Create(OpCodes.Call, factory));
            foreach (Instruction gone in new[] { stores[0].Previous.Previous, stores[0].Previous, stores[0] })
            {
                gone.OpCode = OpCodes.Nop;
                gone.Operand = null;
            }
            count++;
        }
        return count;
    }

    static bool DerivesFrom(TypeReference type, string name)
    {
        for (TypeReference t = type; t != null;)
        {
            if (t.FullName == name) return true;
            TypeDefinition resolved;
            try { resolved = t.Resolve(); }
            catch (AssemblyResolutionException) { return false; }
            if (resolved == null) return false;
            t = resolved.BaseType;
        }
        return false;
    }

    static int LocalIndex(Instruction instruction, MethodDefinition method)
    {
        OpCode op = instruction.OpCode;
        if (op == OpCodes.Stloc_0 || op == OpCodes.Ldloc_0) return 0;
        if (op == OpCodes.Stloc_1 || op == OpCodes.Ldloc_1) return 1;
        if (op == OpCodes.Stloc_2 || op == OpCodes.Ldloc_2) return 2;
        if (op == OpCodes.Stloc_3 || op == OpCodes.Ldloc_3) return 3;
        var variable = instruction.Operand as VariableDefinition;
        return variable == null ? -1 : variable.Index;
    }

    static bool IsStloc(Instruction i)
    {
        OpCode op = i.OpCode;
        return op == OpCodes.Stloc || op == OpCodes.Stloc_S || op == OpCodes.Stloc_0 || op == OpCodes.Stloc_1 || op == OpCodes.Stloc_2 || op == OpCodes.Stloc_3;
    }

    static bool IsLdloc(Instruction i)
    {
        OpCode op = i.OpCode;
        return op == OpCodes.Ldloc || op == OpCodes.Ldloc_S || op == OpCodes.Ldloc_0 || op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2 || op == OpCodes.Ldloc_3;
    }

    static bool IsLdlocOf(Instruction i, Instruction store, MethodDefinition method)
    {
        return i != null && IsLdloc(i) && LocalIndex(i, method) == LocalIndex(store, method);
    }

    static bool IsStlocOf(Instruction i, Instruction store, MethodDefinition method)
    {
        return IsStloc(i) && LocalIndex(i, method) == LocalIndex(store, method);
    }

    static Instruction Ldloc(Instruction store, MethodDefinition method)
    {
        return Instruction.Create(OpCodes.Ldloc, method.Body.Variables[LocalIndex(store, method)]);
    }

    static Instruction Stloc(Instruction store, MethodDefinition method)
    {
        return Instruction.Create(OpCodes.Stloc, method.Body.Variables[LocalIndex(store, method)]);
    }

    // On the closure: static C crowFor(T target) { var c = (C)SharedClosures.Find(kind, target); if (c != null) return c;
    // c = new C(); c.target = target; SharedClosures.Keep(kind, c, target); return c; } (kind: the closure class's
    // number in the module)
    static MethodDefinition ClosureFactory(ModuleDefinition module, TypeDefinition closure, FieldDefinition held, int kind)
    {
        MethodDefinition constructor = closure.Methods.Single(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
        var factory = new MethodDefinition("crowFor", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, closure);
        factory.Parameters.Add(new ParameterDefinition("target", ParameterAttributes.None, held.FieldType));
        var made = new VariableDefinition(closure);
        factory.Body.Variables.Add(made);
        factory.Body.InitLocals = true;
        ILProcessor il = factory.Body.GetILProcessor();
        Instruction done = Instruction.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldc_I4, kind);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, CompatMethod(module, "SharedClosures", "Find"));
        il.Emit(OpCodes.Castclass, closure);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, done);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Newobj, constructor);
        il.Emit(OpCodes.Stloc, made);
        il.Emit(OpCodes.Ldloc, made);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stfld, held);
        il.Emit(OpCodes.Ldc_I4, kind);
        il.Emit(OpCodes.Ldloc, made);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, CompatMethod(module, "SharedClosures", "Keep"));
        il.Emit(OpCodes.Ldloc, made);
        il.Append(done);
        closure.Methods.Add(factory);
        return factory;
    }

    // On the closure: instance D crowDelegateN() => crowDelegateFieldN ?? (crowDelegateFieldN = new D(this.m))
    static MethodDefinition ClosureDelegateHelper(ModuleDefinition module, TypeDefinition closure, MethodReference target, MethodReference constructor, int index)
    {
        TypeReference delegateType = constructor.DeclaringType;
        FieldDefinition field = AddField(closure, "crowDelegateField" + index, delegateType);
        var helper = new MethodDefinition("crowDelegate" + index, MethodAttributes.Assembly | MethodAttributes.HideBySig, delegateType);
        ILProcessor il = helper.Body.GetILProcessor();
        Instruction done = Instruction.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, done);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldftn, target);
        il.Emit(OpCodes.Newobj, constructor);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Append(done);
        closure.Methods.Add(helper);
        return helper;
    }
}
