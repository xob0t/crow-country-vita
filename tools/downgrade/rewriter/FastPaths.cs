using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// PlayMaker's Set Property and Get Property through CrowCompat FastProperty (compat/FastPaths.cs says
// why, and what stays reflection's). FsmProperty gets a [NonSerialized] object field, crowAccessor,
// and two static helpers: CrowSetMember(members, target, value, ref crowAccessor) asks
// FastProperty.Set and calls ReflectionUtils.SetMemberValue(members, target, value) when it declines;
// CrowGetMember(members, target, ref crowAccessor) the same with FastProperty.Get and GetMemberValue.
// Every such ReflectionUtils call in FsmProperty.SetValue and GetValue goes through them: the call's
// arguments stay on the stack, the field's address is pushed after them. TypedPropertySets then takes
// the box out of SetValue's value-type sets (CrowSetMemberFloat and the like, FastProperty.SetFloat...).
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
// Object names: every UnityEngine.Object.name read and write in the game's assemblies becomes a call of
// CrowCompat Names.Get and Names.Set, which keep the names read (compat/FastPaths.cs). Calls through a
// constrained. prefix (generic code) are left as they are.
partial class DowngradeAssemblies
{
    static int RouteObjectNames(ModuleDefinition module)
    {
        TypeDefinition names = compat["CrowCompat.Names"];
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    var called = instruction.Operand as MethodReference;
                    if (called == null || !called.HasThis || (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)) continue;
                    if (called.DeclaringType.FullName != "UnityEngine.Object" || (called.Name != "get_name" && called.Name != "set_name")) continue;
                    if (instruction.Previous != null && instruction.Previous.OpCode == OpCodes.Constrained) continue;
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = module.ImportReference(names.Methods.Single(m => m.Name == (called.Name == "get_name" ? "Get" : "Set")));
                    Count("object names " + called.Name, 1);
                    count++;
                }
        return count;
    }

    static int FastPropertyAccess(ModuleDefinition module)
    {
        TypeDefinition property = GameType(module, "HutongGames.PlayMaker.FsmProperty");
        if (property == null) return 0;
        var cache = new FieldDefinition("crowAccessor", FieldAttributes.Private | FieldAttributes.NotSerialized, module.TypeSystem.Object);
        property.Fields.Add(cache);
        var byRef = new ByReferenceType(module.TypeSystem.Object);
        MethodDefinition setHelper = null, getHelper = null;
        int count = 0;
        foreach (MethodDefinition method in property.Methods.Where(m => (m.Name == "SetValue" || m.Name == "GetValue") && m.Parameters.Count == 0 && m.HasBody).ToArray())
        {
            ILProcessor il = method.Body.GetILProcessor();
            foreach (Instruction call in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Call).ToArray())
            {
                var called = (MethodReference)call.Operand;
                if (called.DeclaringType.FullName != "HutongGames.PlayMaker.ReflectionUtils" || called.HasThis ||
                    called.Parameters.Count == 0 || called.Parameters[0].ParameterType.FullName != "System.Reflection.MemberInfo[]")
                    continue;
                if (method.Body.Instructions.Any(i => i.Operand == call || (i.Operand as Instruction[] ?? new Instruction[0]).Contains(call)))
                    throw new System.Exception("FsmProperty." + method.Name + ": a branch targets a ReflectionUtils call");
                if (called.Name == "SetMemberValue" && called.Parameters.Count == 3)
                {
                    if (setHelper == null) setHelper = SetMemberHelper(module, property, called, byRef);
                    call.Operand = setHelper;
                }
                else if (called.Name == "GetMemberValue" && called.Parameters.Count == 2)
                {
                    if (getHelper == null) getHelper = GetMemberHelper(module, property, called, byRef);
                    call.Operand = getHelper;
                }
                else continue;
                InsertBefore(il, call, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldflda, cache));
                count++;
            }
        }
        if (setHelper == null || getHelper == null)
            throw new System.Exception("FsmProperty: expected ReflectionUtils.SetMemberValue and GetMemberValue calls in SetValue and GetValue, found " + count);
        return count;
    }

    // static void CrowSetMember(MemberInfo[] members, object target, object value, ref object cache)
    static MethodDefinition SetMemberHelper(ModuleDefinition module, TypeDefinition owner, MethodReference reflection, TypeReference byRef)
    {
        var helper = new MethodDefinition("CrowSetMember", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
        helper.Parameters.Add(new ParameterDefinition("members", ParameterAttributes.None, reflection.Parameters[0].ParameterType));
        helper.Parameters.Add(new ParameterDefinition("target", ParameterAttributes.None, module.TypeSystem.Object));
        helper.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Object));
        helper.Parameters.Add(new ParameterDefinition("cache", ParameterAttributes.None, byRef));
        ILProcessor il = helper.Body.GetILProcessor();
        Instruction done = Instruction.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Call, CompatMethod(module, "FastProperty", "Set"));
        il.Emit(OpCodes.Brtrue, done);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, reflection);
        il.Append(done);
        owner.Methods.Add(helper);
        return helper;
    }

    // static object CrowGetMember(MemberInfo[] members, object target, ref object cache)
    static MethodDefinition GetMemberHelper(ModuleDefinition module, TypeDefinition owner, MethodReference reflection, TypeReference byRef)
    {
        var helper = new MethodDefinition("CrowGetMember", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Object);
        helper.Parameters.Add(new ParameterDefinition("members", ParameterAttributes.None, reflection.Parameters[0].ParameterType));
        helper.Parameters.Add(new ParameterDefinition("target", ParameterAttributes.None, module.TypeSystem.Object));
        helper.Parameters.Add(new ParameterDefinition("cache", ParameterAttributes.None, byRef));
        var value = new VariableDefinition(module.TypeSystem.Object);
        helper.Body.Variables.Add(value);
        helper.Body.InitLocals = true;
        ILProcessor il = helper.Body.GetILProcessor();
        Instruction slow = Instruction.Create(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldloca, value);
        il.Emit(OpCodes.Call, CompatMethod(module, "FastProperty", "Get"));
        il.Emit(OpCodes.Brfalse, slow);
        il.Emit(OpCodes.Ldloc, value);
        il.Emit(OpCodes.Ret);
        il.Append(slow);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, reflection);
        il.Emit(OpCodes.Ret);
        owner.Methods.Add(helper);
        return helper;
    }

    // After FastPropertyAccess: SetValue boxed each value-type parameter (box T; ... CrowSetMember). The box
    // goes, and the call goes to CrowSetMember<T>(members, target, T value, ref cache), which asks
    // FastProperty.Set<T> (unboxed) and otherwise boxes the value and calls CrowSetMember as before.
    static readonly string[][] TypedSets =
    {
        new[] { "System.Single", "SetFloat" }, new[] { "System.Int32", "SetInt" }, new[] { "System.Boolean", "SetBool" },
        new[] { "UnityEngine.Vector2", "SetVector2" }, new[] { "UnityEngine.Vector3", "SetVector3" },
        new[] { "UnityEngine.Quaternion", "SetQuaternion" }, new[] { "UnityEngine.Color", "SetColor" }, new[] { "UnityEngine.Rect", "SetRect" },
    };

    static int TypedPropertySets(ModuleDefinition module)
    {
        TypeDefinition property = module.GetType("HutongGames.PlayMaker.FsmProperty");
        if (property == null) return 0;
        MethodDefinition boxed = property.Methods.Single(m => m.Name == "CrowSetMember");
        MethodDefinition setValue = property.Methods.Single(m => m.Name == "SetValue" && m.Parameters.Count == 0 && m.HasBody);
        var helpers = new System.Collections.Generic.Dictionary<string, MethodDefinition>();
        int count = 0;
        foreach (Instruction call in setValue.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand == boxed).ToArray())
        {
            // box T; ldarg.0; ldflda crowAccessor; call CrowSetMember
            Instruction box = call.Previous.Previous.Previous;
            if (box == null || box.OpCode != OpCodes.Box || call.Previous.Previous.OpCode != OpCodes.Ldarg_0 || call.Previous.OpCode != OpCodes.Ldflda) continue;
            var valueType = (TypeReference)box.Operand;
            string[] typed = TypedSets.FirstOrDefault(t => t[0] == valueType.FullName);
            if (typed == null) continue;
            MethodDefinition helper;
            if (!helpers.TryGetValue(typed[0], out helper))
            {
                helper = TypedSetHelper(module, property, boxed, valueType, typed[1]);
                helpers[typed[0]] = helper;
            }
            box.OpCode = OpCodes.Nop;
            box.Operand = null;
            call.Operand = helper;
            Count("typed property sets", 1);
            count++;
        }
        return count;
    }

    // static void CrowSetMember<T>(MemberInfo[] members, object target, T value, ref object cache)
    static MethodDefinition TypedSetHelper(ModuleDefinition module, TypeDefinition owner, MethodDefinition boxed, TypeReference valueType, string fast)
    {
        var helper = new MethodDefinition("CrowSetMember" + fast.Substring(3), MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
        helper.Parameters.Add(new ParameterDefinition("members", ParameterAttributes.None, boxed.Parameters[0].ParameterType));
        helper.Parameters.Add(new ParameterDefinition("target", ParameterAttributes.None, module.TypeSystem.Object));
        helper.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, valueType));
        helper.Parameters.Add(new ParameterDefinition("cache", ParameterAttributes.None, boxed.Parameters[3].ParameterType));
        ILProcessor il = helper.Body.GetILProcessor();
        Instruction done = Instruction.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Call, CompatMethod(module, "FastProperty", fast));
        il.Emit(OpCodes.Brtrue, done);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Box, valueType);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Call, boxed);
        il.Append(done);
        owner.Methods.Add(helper);
        return helper;
    }
}
