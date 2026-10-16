using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Load time: PlayMaker's ActionData (one per FSM state: Station Square has 5,382, the player's scene
// 5,573) keeps its action parameters in 38 serialized List<T> fields, 199,000 lists in Station Square.
// Unity 2018's IL2CPP serializer reads each List<T> field the slow way (LinearCollectionField: the list
// made through its constructor by reflection, its class's fields looked up to find the backing array;
// the PS Vita's stall dumps in Station Square's load found the loading thread there both times), where
// an array field is one allocation. Lists and arrays serialize the same, so the data needs no change:
// each of those fields becomes a T[] of the same name and place, and the code that only reads them
// reads the arrays (Count -> ldlen, the indexer -> ldelem, ToArray -> the array itself, Contains ->
// CrowCompat ArrayHelper.Contains; found by following each loaded field's value through the method's
// stack). The constructor's list initializers become CrowCompat EmptyArray<T>.Value, and LoadActions
// first puts the shared empty array in place of each empty one the serializer made (16 bytes each,
// most of the 38). The methods that change the lists (SaveActions and its helpers, ClearActionData,
// Copy and its helpers: the editor's, FSM templates, which the game has none of, and a changed action's
// resave) throw NotSupportedException; LoadActions no longer resaves (the actions it loaded, recovered
// when their class changed, are kept, as the resave would load them again). The live data per state
// drops by 38 List objects (912 bytes).
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    // The stack before each instruction, as the instruction that pushed each value (null when paths
    // disagree), from the method's entry and its handlers; null when the method can't be followed.
    static Dictionary<Instruction, Instruction[]> StackProducers(MethodDefinition method, Dictionary<Instruction, List<KeyValuePair<Instruction, int>>> consumers)
    {
        var before = new Dictionary<Instruction, Instruction[]>();
        var work = new Queue<Instruction>();
        Action<Instruction, Instruction[]> reach = (at, stack) =>
        {
            Instruction[] known;
            if (!before.TryGetValue(at, out known))
            {
                before[at] = stack;
                work.Enqueue(at);
                return;
            }
            if (known.Length != stack.Length) throw new InvalidOperationException("stack depth differs at " + at);
            bool changed = false;
            var merged = (Instruction[])known.Clone();
            for (int i = 0; i < merged.Length; i++)
                if (merged[i] != stack[i] && merged[i] != null) { merged[i] = null; changed = true; }
            if (changed) { before[at] = merged; work.Enqueue(at); }
        };
        reach(method.Body.Instructions[0], new Instruction[0]);
        foreach (ExceptionHandler handler in method.Body.ExceptionHandlers)
        {
            bool takesException = handler.HandlerType == ExceptionHandlerType.Catch || handler.HandlerType == ExceptionHandlerType.Filter;
            reach(handler.HandlerStart, takesException ? new Instruction[] { null } : new Instruction[0]);
            if (handler.FilterStart != null) reach(handler.FilterStart, new Instruction[] { null });
        }
        while (work.Count > 0)
        {
            Instruction at = work.Dequeue();
            Instruction[] stack = before[at];
            int pops, pushes;
            StackEffect(method, at, stack.Length, out pops, out pushes);
            if (pops > stack.Length) throw new InvalidOperationException("stack underflow at " + at);
            if (consumers != null)
                for (int i = 0; i < pops; i++)
                {
                    Instruction producer = stack[stack.Length - pops + i];
                    if (producer == null) continue;
                    List<KeyValuePair<Instruction, int>> list;
                    if (!consumers.TryGetValue(producer, out list)) consumers[producer] = list = new List<KeyValuePair<Instruction, int>>();
                    if (!list.Any(p => p.Key == at && p.Value == i)) list.Add(new KeyValuePair<Instruction, int>(at, i));
                }
            var next = new List<Instruction>(stack.Take(stack.Length - pops));
            Instruction dupped = at.OpCode.Code == Code.Dup ? stack[stack.Length - 1] : at;
            for (int i = 0; i < pushes; i++) next.Add(dupped);
            Instruction[] after = next.ToArray();
            FlowControl flow = at.OpCode.FlowControl;
            if (at.OpCode.Code == Code.Leave || at.OpCode.Code == Code.Leave_S)
            {
                reach((Instruction)at.Operand, new Instruction[0]);
                continue;
            }
            if (flow == FlowControl.Branch || flow == FlowControl.Cond_Branch)
            {
                var targets = at.Operand as Instruction[];
                if (targets != null) foreach (Instruction target in targets) reach(target, after);
                else reach((Instruction)at.Operand, after);
            }
            if (flow == FlowControl.Branch || flow == FlowControl.Return || flow == FlowControl.Throw) continue;
            if (at.OpCode.Code == Code.Endfinally || at.OpCode.Code == Code.Endfilter) continue;
            if (at.Next != null) reach(at.Next, after);
        }
        return before;
    }

    static void StackEffect(MethodDefinition method, Instruction at, int depth, out int pops, out int pushes)
    {
        OpCode code = at.OpCode;
        var called = at.Operand as MethodReference;
        if (code.Code == Code.Call || code.Code == Code.Callvirt || code.Code == Code.Newobj)
        {
            pops = called.Parameters.Count + (called.HasThis && code.Code != Code.Newobj ? 1 : 0);
            pushes = code.Code == Code.Newobj || called.ReturnType.MetadataType != MetadataType.Void ? 1 : 0;
            return;
        }
        if (code.Code == Code.Calli) throw new InvalidOperationException("calli");
        if (code.Code == Code.Ret)
        {
            pops = method.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
            pushes = 0;
            return;
        }
        if (code.Code == Code.Leave || code.Code == Code.Leave_S || code.Code == Code.Endfinally) { pops = depth; pushes = 0; return; }
        switch (code.StackBehaviourPop)
        {
            case StackBehaviour.Pop0: pops = 0; break;
            case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: pops = 1; break;
            case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi: case StackBehaviour.Popi_popi8:
            case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8: case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: pops = 2; break;
            case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8: case StackBehaviour.Popref_popi_popr4:
            case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref: pops = 3; break;
            case StackBehaviour.PopAll: pops = depth; break;
            default: throw new InvalidOperationException("stack behaviour " + code.StackBehaviourPop + " at " + at);
        }
        switch (code.StackBehaviourPush)
        {
            case StackBehaviour.Push0: pushes = 0; break;
            case StackBehaviour.Push1_push1: pushes = 2; break;
            case StackBehaviour.Push1: case StackBehaviour.Pushi: case StackBehaviour.Pushi8: case StackBehaviour.Pushr4: case StackBehaviour.Pushr8:
            case StackBehaviour.Pushref: pushes = 1; break;
            default: throw new InvalidOperationException("stack behaviour " + code.StackBehaviourPush + " at " + at);
        }
    }

    static readonly HashSet<string> ArrayReadable = new HashSet<string> { "get_Count", "get_Item", "ToArray", "Contains" };

    static int SerializeActionDataAsArrays(ModuleDefinition module)
    {
        if (module.Assembly.Name.Name != "PlayMaker") return 0;
        TypeDefinition data = GameType(module, "HutongGames.PlayMaker.ActionData");
        if (data == null) return 0;
        var elements = new Dictionary<FieldDefinition, TypeReference>();
        foreach (FieldDefinition field in data.Fields)
        {
            if (field.IsStatic || field.IsNotSerialized) continue;
            var generic = field.FieldType as GenericInstanceType;
            if (generic == null || generic.ElementType.FullName != "System.Collections.Generic.List`1") continue;
            if (!field.IsPublic && !field.CustomAttributes.Any(a => a.AttributeType.FullName == "UnityEngine.SerializeField")) continue;
            elements[field] = generic.GenericArguments[0];
        }
        if (elements.Count == 0) return 0;
        Func<Instruction, FieldDefinition> loadsList = x =>
        {
            var f = x.Operand as FieldReference;
            if (f == null || f.DeclaringType.FullName != data.FullName) return null;
            FieldDefinition definition = f.Resolve();
            return definition != null && elements.ContainsKey(definition) ? definition : null;
        };

        // Which methods only read the lists, and how each loaded list is used.
        var readers = new Dictionary<MethodDefinition, Dictionary<Instruction, List<KeyValuePair<Instruction, int>>>>();
        var writers = new List<MethodDefinition>();
        foreach (MethodDefinition method in data.Methods.Where(m => m.HasBody && !m.IsConstructor))
        {
            if (!method.Body.Instructions.Any(x => loadsList(x) != null)) continue;
            var consumers = new Dictionary<Instruction, List<KeyValuePair<Instruction, int>>>();
            bool reads = true;
            try { StackProducers(method, consumers); }
            catch (InvalidOperationException) { reads = false; }
            foreach (Instruction load in method.Body.Instructions.Where(x => loadsList(x) != null))
            {
                if (!reads) break;
                if (load.OpCode.Code != Code.Ldfld) { reads = false; break; }
                List<KeyValuePair<Instruction, int>> uses;
                if (!consumers.TryGetValue(load, out uses)) continue;  // popped unused
                foreach (KeyValuePair<Instruction, int> use in uses)
                {
                    Instruction user = use.Key;
                    var called = user.Operand as MethodReference;
                    bool listCall = called != null && (user.OpCode.Code == Code.Callvirt || user.OpCode.Code == Code.Call) &&
                                    called.DeclaringType.GetElementType().FullName == "System.Collections.Generic.List`1" && use.Value == 0 && ArrayReadable.Contains(called.Name);
                    bool test = user.OpCode.Code == Code.Brtrue || user.OpCode.Code == Code.Brtrue_S || user.OpCode.Code == Code.Brfalse || user.OpCode.Code == Code.Brfalse_S ||
                                user.OpCode.Code == Code.Ceq || user.OpCode.Code == Code.Cgt_Un || user.OpCode.Code == Code.Pop || user.OpCode.Code == Code.Dup;
                    if (!listCall && !test) { reads = false; break; }
                }
            }
            if (reads) readers[method] = consumers;
            else writers.Add(method);
        }
        foreach (string name in new[] { "LoadActions", "CreateAction", "LoadActionField", "LoadArrayElement", "GetStringParam" })
            if (writers.Any(m => m.Name == name)) throw new InvalidOperationException("ActionData." + name + " can't read the lists as arrays");

        // The fields as arrays.
        var empties = new Dictionary<FieldDefinition, FieldReference>();
        TypeReference emptyOpen = module.ImportReference(compat["CrowCompat.EmptyArray`1"]);
        foreach (KeyValuePair<FieldDefinition, TypeReference> pair in elements)
        {
            pair.Key.FieldType = new ArrayType(pair.Value);
            var instance = new GenericInstanceType(emptyOpen);
            instance.GenericArguments.Add(pair.Value);
            empties[pair.Key] = new FieldReference("Value", new ArrayType(emptyOpen.GenericParameters[0]), instance);
        }
        MethodDefinition containsDefinition = compat["CrowCompat.ArrayHelper"].Methods.Single(m => m.Name == "Contains");
        MethodReference containsOpen = module.ImportReference(containsDefinition);

        int sites = 0;
        foreach (KeyValuePair<MethodDefinition, Dictionary<Instruction, List<KeyValuePair<Instruction, int>>>> reader in readers)
        {
            MethodDefinition method = reader.Key;
            ILProcessor il = method.Body.GetILProcessor();
            var calls = new HashSet<Instruction>();
            foreach (Instruction load in method.Body.Instructions.Where(x => loadsList(x) != null).ToArray())
            {
                List<KeyValuePair<Instruction, int>> uses;
                if (!reader.Value.TryGetValue(load, out uses)) continue;
                foreach (KeyValuePair<Instruction, int> use in uses)
                    if (use.Key.Operand is MethodReference) calls.Add(use.Key);
            }
            foreach (Instruction call in calls)
            {
                var called = (MethodReference)call.Operand;
                TypeReference element = ((GenericInstanceType)called.DeclaringType).GenericArguments[0];
                switch (called.Name)
                {
                    case "get_Count":
                        call.OpCode = OpCodes.Ldlen;
                        call.Operand = null;
                        il.InsertAfter(call, il.Create(OpCodes.Conv_I4));
                        break;
                    case "get_Item":
                        call.OpCode = OpCodes.Ldelem_Any;
                        call.Operand = element;
                        break;
                    case "ToArray":
                        call.OpCode = OpCodes.Nop;
                        call.Operand = null;
                        break;
                    case "Contains":
                        var contains = new GenericInstanceMethod(containsOpen);
                        contains.GenericArguments.Add(element);
                        call.OpCode = OpCodes.Call;
                        call.Operand = contains;
                        break;
                }
                sites++;
            }
        }
        foreach (MethodDefinition method in writers)
        {
            method.Body.Instructions.Clear();
            method.Body.ExceptionHandlers.Clear();
            method.Body.Variables.Clear();
            ILProcessor il = method.Body.GetILProcessor();
            var exception = new MethodReference(".ctor", module.TypeSystem.Void, CorlibType(module, "System", "NotSupportedException", false)) { HasThis = true };
            exception.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            il.Append(il.Create(OpCodes.Ldstr, "CrowCompat: ActionData." + method.Name + " (its lists are arrays on this player; rewriter/PlayMakerData.cs)"));
            il.Append(il.Create(OpCodes.Newobj, exception));
            il.Append(il.Create(OpCodes.Throw));
        }

        // The constructors' list initializers: newobj List<T>::.ctor(), stfld <field> -> the shared empty array.
        int initializers = 0;
        foreach (MethodDefinition constructor in data.Methods.Where(m => m.IsConstructor && !m.IsStatic && m.HasBody))
            foreach (Instruction made in constructor.Body.Instructions)
            {
                var called = made.Operand as MethodReference;
                if (made.OpCode.Code != Code.Newobj || called == null || called.Parameters.Count != 0 || made.Next == null || made.Next.OpCode.Code != Code.Stfld) continue;
                FieldDefinition field = loadsList(made.Next);
                if (field == null) continue;
                made.OpCode = OpCodes.Ldsfld;
                made.Operand = empties[field];
                initializers++;
            }
        foreach (MethodDefinition constructor in data.Methods.Where(m => m.IsConstructor && m.HasBody))
            if (constructor.Body.Instructions.Any(x => loadsList(x) != null && x.OpCode.Code != Code.Stfld))
                throw new InvalidOperationException("ActionData's constructor reads its lists");

        // LoadActions: no resave, and the shared empty array in place of each empty one.
        MethodDefinition loadActions = data.Methods.Single(m => m.Name == "LoadActions");
        FieldDefinition resave = data.Fields.Single(f => f.Name == "resaveActionData");
        int resaves = 0;
        foreach (Instruction x in loadActions.Body.Instructions.ToArray())
        {
            var read = x.Operand as FieldReference;
            if (read == null || read.Name != resave.Name) continue;
            if (x.OpCode.Code == Code.Ldsfld)
            {
                x.OpCode = OpCodes.Ldc_I4_0;
                x.Operand = null;
                resaves++;
            }
            else if (x.OpCode.Code == Code.Ldfld)
            {
                x.OpCode = OpCodes.Pop;
                x.Operand = null;
                loadActions.Body.GetILProcessor().InsertAfter(x, Instruction.Create(OpCodes.Ldc_I4_0));
                resaves++;
            }
        }
        if (resaves == 0) throw new InvalidOperationException("ActionData.LoadActions reads no resaveActionData");
        var normalize = new List<Instruction>();
        foreach (FieldDefinition field in elements.Keys)
        {
            Instruction skip = Instruction.Create(OpCodes.Nop);
            normalize.Add(Instruction.Create(OpCodes.Ldarg_0));
            normalize.Add(Instruction.Create(OpCodes.Ldfld, field));
            normalize.Add(Instruction.Create(OpCodes.Brfalse, skip));
            normalize.Add(Instruction.Create(OpCodes.Ldarg_0));
            normalize.Add(Instruction.Create(OpCodes.Ldfld, field));
            normalize.Add(Instruction.Create(OpCodes.Ldlen));
            normalize.Add(Instruction.Create(OpCodes.Brtrue, skip));
            normalize.Add(Instruction.Create(OpCodes.Ldarg_0));
            normalize.Add(Instruction.Create(OpCodes.Ldsfld, empties[field]));
            normalize.Add(Instruction.Create(OpCodes.Stfld, field));
            normalize.Add(skip);
        }
        InsertBefore(loadActions.Body.GetILProcessor(), loadActions.Body.Instructions[0], normalize.ToArray());

        // Nothing else may use the fields (private: only nested types could).
        foreach (TypeDefinition type in AllTypes(module.Types))
        {
            if (type == data) continue;
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                if (method.Body.Instructions.Any(x => loadsList(x) != null))
                    throw new InvalidOperationException(type.FullName + "." + method.Name + " uses ActionData's lists");
        }
        Console.WriteLine("ActionData: " + elements.Count + " list fields serialized as arrays; " + readers.Count + " methods read them as arrays (" + sites +
                          " list calls), " + writers.Count + " throw (" + string.Join(", ", writers.Select(m => m.Name).ToArray()) + "); " + initializers + " initializers");
        return elements.Count + sites;
    }

    // A type of the core library as an instruction's operand: the module's TypeSystem types keep the
    // source's core library (netstandard in the game's assemblies), which only signatures may name.
    static TypeReference CorlibType(ModuleDefinition module, string space, string name, bool valueType)
    {
        AssemblyNameReference corlib = module.AssemblyReferences.FirstOrDefault(a => a.Name == "mscorlib");
        if (corlib == null) throw new InvalidOperationException(module.Name + " references no mscorlib");
        return new TypeReference(space, name, module, corlib, valueType);
    }

    static TypeReference Tokenable(ModuleDefinition module, TypeReference type)
    {
        if (type.IsPrimitive || type.MetadataType == MetadataType.String || type.MetadataType == MetadataType.Object)
            return CorlibType(module, type.Namespace, type.Name, type.IsValueType);
        return type;
    }

    static FieldDefinition[] SettableFields(TypeDefinition type)
    {
        return type.Fields.Where(f => f.IsPublic && !f.IsStatic && !f.IsLiteral && !f.IsInitOnly && !f.FieldType.ContainsGenericParameter).ToArray();
    }

    // The nearest base class with a CrowSetField of its own (FsmStateAction at the latest): IL2CPP
    // wants a base call named on the class that defines the method.
    static TypeReference DeclaringAncestor(ModuleDefinition module, TypeDefinition type)
    {
        for (TypeReference current = type.BaseType; current != null; )
        {
            TypeDefinition definition = current.Resolve();
            if (definition == null) throw new InvalidOperationException("no base class " + current.FullName + " for " + type.FullName);
            if (definition.FullName == "HutongGames.PlayMaker.FsmStateAction" ||
                (!definition.HasGenericParameters && IsAction(definition) && SettableFields(definition).Length > 0))
                return module.ImportReference(definition);
            current = definition.BaseType;
        }
        throw new InvalidOperationException(type.FullName + " is no FsmStateAction");
    }

    static MethodReference CrowSetFieldOf(ModuleDefinition module, TypeReference declaring)
    {
        var method = new MethodReference("CrowSetField", module.TypeSystem.Boolean, declaring) { HasThis = true };
        method.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        method.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        return method;
    }

    static bool IsAction(TypeDefinition type)
    {
        for (TypeReference current = type.BaseType; current != null; )
        {
            if (current.FullName == "HutongGames.PlayMaker.FsmStateAction") return true;
            TypeDefinition definition;
            try { definition = current.Resolve(); }
            catch (AssemblyResolutionException) { return false; }
            if (definition == null) return false;
            current = definition.BaseType;
        }
        return false;
    }

    // Load time: PlayMaker sets each action parameter it loads with FieldInfo.SetValue (ActionData.
    // LoadActionField), whose managed checks (the target's type against the field's declaring type,
    // the value through RuntimeType.CheckValue and the default binder) and icalls cost the PS Vita
    // about 48 us a field: 3 s of Station Square's load for 63,000 fields. Each action class gets
    // CrowSetField(name, value), a switch over the public instance fields it declares that stores the
    // value with a cast (castclass, or unbox.any for value types; a null for a value type, readonly
    // fields and generic classes are left to reflection), then asks its base class; FsmStateAction's
    // returns false. LoadActionField's SetValue calls go through ActionData.CrowSetValue, which tries
    // the action's CrowSetField and falls back to SetValue (custom parameter classes, what the switch
    // leaves out). A wrong value's cast throws InvalidCastException where CheckValue threw
    // ArgumentException: LoadActionField's caller catches both and logs the field.
    static int FastActionFields(ModuleDefinition module)
    {
        int count = 0;
        var stringEquals = new MethodReference("op_Equality", module.TypeSystem.Boolean, CorlibType(module, "System", "String", false));
        stringEquals.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        stringEquals.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        if (module.Assembly.Name.Name == "PlayMaker")
        {
            TypeDefinition action = GameType(module, "HutongGames.PlayMaker.FsmStateAction");
            TypeDefinition data = GameType(module, "HutongGames.PlayMaker.ActionData");
            if (action == null || data == null) return 0;
            var root = new MethodDefinition("CrowSetField", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                module.TypeSystem.Boolean);
            root.Parameters.Add(new ParameterDefinition("name", ParameterAttributes.None, module.TypeSystem.String));
            root.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Object));
            ILProcessor il = root.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Ret));
            action.Methods.Add(root);

            // static void CrowSetValue(FieldInfo field, object target, object value)
            var fieldInfo = CorlibType(module, "System.Reflection", "FieldInfo", false);
            var memberInfo = CorlibType(module, "System.Reflection", "MemberInfo", false);
            var getName = new MethodReference("get_Name", module.TypeSystem.String, memberInfo) { HasThis = true };
            var setValue = new MethodReference("SetValue", module.TypeSystem.Void, fieldInfo) { HasThis = true };
            setValue.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
            setValue.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
            var helper = new MethodDefinition("CrowSetValue", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
            helper.Parameters.Add(new ParameterDefinition("field", ParameterAttributes.None, fieldInfo));
            helper.Parameters.Add(new ParameterDefinition("target", ParameterAttributes.None, module.TypeSystem.Object));
            helper.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Object));
            var cast = new VariableDefinition(action);
            helper.Body.Variables.Add(cast);
            helper.Body.InitLocals = true;
            il = helper.Body.GetILProcessor();
            Instruction slow = il.Create(OpCodes.Ldarg_0), done = il.Create(OpCodes.Ret);
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Isinst, action));
            il.Append(il.Create(OpCodes.Stloc, cast));
            il.Append(il.Create(OpCodes.Ldloc, cast));
            il.Append(il.Create(OpCodes.Brfalse, slow));
            il.Append(il.Create(OpCodes.Ldloc, cast));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Callvirt, getName));
            il.Append(il.Create(OpCodes.Ldarg_2));
            il.Append(il.Create(OpCodes.Callvirt, root));
            il.Append(il.Create(OpCodes.Brtrue, done));
            il.Append(slow);
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Ldarg_2));
            il.Append(il.Create(OpCodes.Callvirt, setValue));
            il.Append(done);
            data.Methods.Add(helper);

            MethodDefinition load = data.Methods.Single(m => m.Name == "LoadActionField");
            foreach (Instruction call in load.Body.Instructions)
            {
                var called = call.Operand as MethodReference;
                if (call.OpCode != OpCodes.Callvirt || called == null || called.Name != "SetValue" || called.DeclaringType.FullName != "System.Reflection.FieldInfo" ||
                    called.Parameters.Count != 2) continue;
                call.OpCode = OpCodes.Call;
                call.Operand = helper;
                count++;
            }
            if (count == 0) throw new InvalidOperationException("ActionData.LoadActionField calls no FieldInfo.SetValue");
        }
        foreach (TypeDefinition type in AllTypes(module.Types).ToArray())
        {
            if (type.IsInterface || type.HasGenericParameters || !IsAction(type)) continue;
            FieldDefinition[] fields = SettableFields(type);
            if (fields.Length == 0) continue;
            var method = new MethodDefinition("CrowSetField", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig, module.TypeSystem.Boolean);
            method.Parameters.Add(new ParameterDefinition("name", ParameterAttributes.None, module.TypeSystem.String));
            method.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Object));
            ILProcessor il = method.Body.GetILProcessor();
            Instruction fallback = il.Create(OpCodes.Ldarg_0);
            foreach (FieldDefinition field in fields)
            {
                Instruction next = il.Create(OpCodes.Nop);
                il.Append(il.Create(OpCodes.Ldarg_1));
                il.Append(il.Create(OpCodes.Ldstr, field.Name));
                il.Append(il.Create(OpCodes.Call, stringEquals));
                il.Append(il.Create(OpCodes.Brfalse, next));
                if (field.FieldType.IsValueType)
                {
                    il.Append(il.Create(OpCodes.Ldarg_2));
                    il.Append(il.Create(OpCodes.Brfalse, fallback));
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldarg_2));
                    il.Append(il.Create(OpCodes.Unbox_Any, Tokenable(module, field.FieldType)));
                }
                else
                {
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldarg_2));
                    il.Append(il.Create(OpCodes.Castclass, Tokenable(module, field.FieldType)));
                }
                il.Append(il.Create(OpCodes.Stfld, field));
                il.Append(il.Create(OpCodes.Ldc_I4_1));
                il.Append(il.Create(OpCodes.Ret));
                il.Append(next);
            }
            il.Append(fallback);
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Ldarg_2));
            il.Append(il.Create(OpCodes.Call, CrowSetFieldOf(module, DeclaringAncestor(module, type))));
            il.Append(il.Create(OpCodes.Ret));
            type.Methods.Add(method);
            count++;
        }
        return count;
    }
}
