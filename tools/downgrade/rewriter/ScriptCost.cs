using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The main thread's per-frame script work that has no effect, skipped: PlayMaker's bookkeeping and
// the game's fade loop. CrowCompat ScriptCost (compat/ScriptCost.cs) says what each pass changes
// and why; ScriptCost.Fast (off with -crow-script-cost game) gates the skips.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    static FieldReference ScriptCostFast(ModuleDefinition module)
    {
        return CompatField(module, "ScriptCost", "Fast");
    }

    // FsmExecutionStack's Stack<Fsm> becomes a CrowCompat FastStack<Fsm>: the field's type and the
    // Stack members its methods call.
    static int PatchFsmExecutionStack(ModuleDefinition module)
    {
        TypeDefinition type = GameType(module, "HutongGames.PlayMaker.FsmExecutionStack");
        if (type == null) return 0;
        FieldDefinition field = type.Fields.Single(f => f.Name == "fsmExecutionStack");
        var stack = (GenericInstanceType)field.FieldType;
        TypeDefinition fastDefinition = compat["CrowCompat.FastStack`1"];
        var fast = new GenericInstanceType(module.ImportReference(fastDefinition));
        fast.GenericArguments.Add(stack.GenericArguments[0]);
        field.FieldType = fast;
        int count = 1;
        foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            foreach (Instruction instruction in method.Body.Instructions)
            {
                var called = instruction.Operand as MethodReference;
                if (called == null || called.DeclaringType.GetElementType().FullName != "System.Collections.Generic.Stack`1") continue;
                MethodDefinition replacement = fastDefinition.Methods.Single(m => m.Name == called.Name && m.Parameters.Count == called.Parameters.Count);
                var reference = new MethodReference(replacement.Name, replacement.ReturnType, fast)
                {
                    HasThis = replacement.HasThis,
                    ExplicitThis = replacement.ExplicitThis,
                    CallingConvention = replacement.CallingConvention,
                };
                foreach (ParameterDefinition parameter in replacement.Parameters) reference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
                instruction.Operand = module.ImportReference(reference);
                if (instruction.OpCode == OpCodes.Callvirt) instruction.OpCode = OpCodes.Call;  // sealed class: no virtual dispatch needed
                count++;
            }
        return count;
    }

    // Fsm.EnterState marks the FSM (a new [NonSerialized] field, crowStatesEntered); the loop in
    // UpdateStateChanges that sets every state's loop count to 0 runs only when the mark is set
    // (and clears it), or always with -crow-script-cost game. Only EnterState raises loop counts.
    static int PatchFsmLoopReset(ModuleDefinition module)
    {
        TypeDefinition fsm = GameType(module, "HutongGames.PlayMaker.Fsm");
        if (fsm == null) return 0;
        FieldDefinition mark = AddField(fsm, "crowStatesEntered", module.TypeSystem.Boolean);

        MethodDefinition enter = fsm.Methods.Single(m => m.Name == "EnterState" && m.Parameters.Count == 1);
        InsertBeforeRetarget(enter, enter.Body.Instructions[0],
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Stfld, mark));

        MethodDefinition changes = fsm.Methods.Single(m => m.Name == "UpdateStateChanges" && m.Parameters.Count == 0);
        var code = changes.Body.Instructions;
        int reset = code.IndexOf(code.Single(i => CalledName(i) == "ResetLoopCount"));
        Instruction back = code.Skip(reset).First(i => i.OpCode.FlowControl == FlowControl.Cond_Branch && code.IndexOf((Instruction)i.Operand) < reset);
        int body = code.IndexOf((Instruction)back.Operand);
        Instruction init = code[body - 3];
        if (init.OpCode != OpCodes.Ldc_I4_0 || !code[body - 2].OpCode.Name.StartsWith("stloc") || code[body - 1].OpCode.FlowControl != FlowControl.Branch)
            throw new System.Exception("Fsm.UpdateStateChanges: the loop count reset is not the expected for loop");
        Instruction after = back.Next;
        Instruction clear = Instruction.Create(OpCodes.Ldarg_0);
        InsertBeforeRetarget(changes, init,
            Instruction.Create(OpCodes.Ldsfld, ScriptCostFast(module)),
            Instruction.Create(OpCodes.Brfalse, clear),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, mark),
            Instruction.Create(OpCodes.Brfalse, after),
            clear,
            Instruction.Create(OpCodes.Ldc_I4_0),
            Instruction.Create(OpCodes.Stfld, mark));
        return 2;
    }

    // Fsm.UpdateDelayedEvents returns at once when delayedEvents, updateEvents and removeEvents are
    // all empty (it would clear the two empty lists and copy the empty one into them). Asking the
    // three lists costs more than the rest of an idle FSM's update, so a new [NonSerialized] field,
    // crowDelayedEvents, marks an FSM whose lists may hold events: every List<DelayedEvent>.Add in
    // Fsm sets it, and so does the DelayedEvents getter (the list's only way out); the lists are
    // asked only while it is set, and it is cleared when they are found empty.
    static int PatchFsmDelayedEvents(ModuleDefinition module)
    {
        TypeDefinition fsm = GameType(module, "HutongGames.PlayMaker.Fsm");
        if (fsm == null) return 0;
        FieldDefinition mark = AddField(fsm, "crowDelayedEvents", module.TypeSystem.Boolean);
        int count = 0;
        foreach (MethodDefinition method in fsm.Methods.Where(m => m.HasBody && !m.IsStatic))
        {
            ILProcessor il = method.Body.GetILProcessor();
            foreach (Instruction add in method.Body.Instructions.Where(i => CalledName(i) == "Add" &&
                         ((MethodReference)i.Operand).DeclaringType.FullName.Contains("HutongGames.PlayMaker.DelayedEvent")).ToArray())
            {
                InsertBefore(il, add.Next, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Stfld, mark));
                count++;
            }
        }
        MethodDefinition getter = fsm.Methods.Single(m => m.Name == "get_DelayedEvents");
        InsertBeforeRetarget(getter, getter.Body.Instructions[0],
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Stfld, mark));
        if (count < 2) throw new System.Exception("Fsm: expected the delayedEvents.Add calls, found " + count);

        MethodDefinition update = fsm.Methods.Single(m => m.Name == "UpdateDelayedEvents" && m.Parameters.Count == 0);
        MethodReference listCount = (MethodReference)update.Body.Instructions.First(i => CalledName(i) == "get_Count").Operand;
        Instruction first = update.Body.Instructions[0];
        Instruction ret = Instruction.Create(OpCodes.Ret);
        var added = new System.Collections.Generic.List<Instruction>
        {
            Instruction.Create(OpCodes.Ldsfld, ScriptCostFast(module)),
            Instruction.Create(OpCodes.Brfalse, first),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, mark),
            Instruction.Create(OpCodes.Brfalse, ret),
        };
        foreach (string name in new[] { "delayedEvents", "updateEvents", "removeEvents" })
        {
            added.Add(Instruction.Create(OpCodes.Ldarg_0));
            added.Add(Instruction.Create(OpCodes.Ldfld, fsm.Fields.Single(f => f.Name == name)));
            added.Add(Instruction.Create(OpCodes.Callvirt, listCount));
            added.Add(Instruction.Create(OpCodes.Brtrue, first));
        }
        added.Add(Instruction.Create(OpCodes.Ldarg_0));
        added.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        added.Add(Instruction.Create(OpCodes.Stfld, mark));
        added.Add(ret);
        InsertBefore(update.Body.GetILProcessor(), first, added.ToArray());  // branches to the old start stay there
        return count + 1;
    }

    // FullScreenColour.OnGUI returns after its useGUILayout = false while CrowCompat ScriptCost
    // knows of no fade action between OnEnter and OnExit (GuiIdle(this) also turns the component
    // off until one enters); the fade actions (the classes with
    // IFsmStateActionGUI) report both, in an OnEnter and OnExit of their own where they have none.
    static int PatchFullScreenColour(ModuleDefinition module)
    {
        TypeDefinition type = GameType(module, "com.sfbgames.crowcountry.FullScreenColour");
        if (type == null) return 0;
        MethodDefinition onGui = type.Methods.Single(m => m.Name == "OnGUI" && m.Parameters.Count == 0);
        Instruction layout = onGui.Body.Instructions.Single(i => CalledName(i) == "set_useGUILayout");
        Instruction next = layout.Next;
        InsertBeforeRetarget(onGui, next,
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, CompatMethod(module, "ScriptCost", "GuiIdle")),
            Instruction.Create(OpCodes.Brfalse, next),
            Instruction.Create(OpCodes.Ret));
        int count = 1;
        foreach (TypeDefinition action in AllTypes(module.Types).Where(t => t.IsClass && !t.IsInterface &&
                     t.Interfaces.Any(i => i.InterfaceType.FullName == "com.sfbgames.playmaker.IFsmStateActionGUI")))
        {
            ReportFromOverride(module, action, "OnEnter", "GuiEnter");
            ReportFromOverride(module, action, "OnExit", "GuiExit");
            ImplementGuiAction(module, action);
            count++;
        }
        if (count != 4) throw new System.Exception("FullScreenColour: expected 3 fade actions with IFsmStateActionGUI, found " + (count - 1));
        return count;
    }

    // The action implements CrowCompat.IGuiAction: CrowGuiActive() returns FsmStateAction.Active.
    static void ImplementGuiAction(ModuleDefinition module, TypeDefinition action)
    {
        TypeDefinition contract = compat["CrowCompat.IGuiAction"];
        action.Interfaces.Add(new InterfaceImplementation(module.ImportReference(contract)));
        MethodDefinition getActive = BaseMethod(action, m => m.Name == "get_Active" && m.Parameters.Count == 0);
        var method = new MethodDefinition("CrowGuiActive",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
            module.TypeSystem.Boolean);
        ILProcessor il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, module.ImportReference(getActive));
        il.Emit(OpCodes.Ret);
        action.Methods.Add(method);
    }

    // Starts the action's override of FsmStateAction.<name>() with CrowCompat.ScriptCost.<report>(this);
    // adds an override that calls the base method first where the class has none.
    static void ReportFromOverride(ModuleDefinition module, TypeDefinition action, string name, string report)
    {
        MethodDefinition method = action.Methods.SingleOrDefault(m => m.Name == name && m.Parameters.Count == 0 && !m.IsStatic);
        if (method == null)
        {
            MethodDefinition baseMethod = BaseMethod(action, m => m.Name == name && m.Parameters.Count == 0 && m.IsVirtual);
            method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual, module.TypeSystem.Void);
            ILProcessor build = method.Body.GetILProcessor();
            build.Emit(OpCodes.Ldarg_0);
            build.Emit(OpCodes.Call, module.ImportReference(baseMethod));
            build.Emit(OpCodes.Ret);
            action.Methods.Add(method);
        }
        var added = new System.Collections.Generic.List<Instruction> { Instruction.Create(OpCodes.Ldarg_0) };
        MethodDefinition reportMethod = compat["CrowCompat.ScriptCost"].Methods.Single(m => m.Name == report);
        if (reportMethod.Parameters.Count == 2)  // GuiEnter(action, FsmStateAction.Owner)
        {
            MethodDefinition owner = BaseMethod(action, m => m.Name == "get_Owner" && m.Parameters.Count == 0);
            added.Add(Instruction.Create(OpCodes.Ldarg_0));
            added.Add(Instruction.Create(OpCodes.Call, module.ImportReference(owner)));
        }
        added.Add(Instruction.Create(OpCodes.Call, module.ImportReference(reportMethod)));
        InsertBeforeRetarget(method, method.Body.Instructions[0], added.ToArray());
    }
}
