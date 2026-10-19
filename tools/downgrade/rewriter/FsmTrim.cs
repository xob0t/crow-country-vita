using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// PlayMaker's FSM updates that do nothing, skipped; CrowCompat FsmTrim (compat/FsmTrim.cs) says when
// and why. FsmTrimHooks:
//   Fsm gets int CrowIdle(int mask) (mask 2 Update, 4 FixedUpdate, 8 LateUpdate): 0 when the update
//     must run, 1 when it would do nothing (the active state finished, no action running), 2 when it
//     would only add the frame time to StateTime (Update) or nothing (the others): every running
//     action's FsmStateAction.crowHooks (a new field, filled from FsmTrim.Hooks the first time)
//     leaves the mask out, and FsmState.CheckAllActionsFinished would return without a change.
//     The fields it reads (Fsm's, FsmState's, FsmStateAction.blocksFinish, PlayMakerFSM.fsm) become
//     internal, for Mono's access checks.
//   Fsm.Update, FixedUpdate and LateUpdate start (FsmGate's hooks, rewriter/Cuts.cs, go before this
//     later) with
//       FsmTrim.<kind>Calls++;
//       if (FsmTrim.<kind> && (t = CrowIdle(mask)) != 0) {
//           if (FsmTrim.Check) FsmTrim.CheckBegin(this, kind, t);
//           else { (Update, t == 2) activeState.StateTime += FsmGate.DeltaTime(); count; return; } }
//     and call FsmTrim.CheckEnd(this) before each return while FsmTrim.Checking.
//   PlayMakerFixedUpdate.FixedUpdate and PlayMakerLateUpdate.LateUpdate: each target the static
//     Fsm.CrowProxyIdle(target, mask) passes (its Fsm's owner is the target, CrowIdle != 0) is
//     skipped (FsmTrim.<kind>Proxy++) while the switch is on and Check off.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    static FieldReference TrimField(ModuleDefinition module, string name)
    {
        return module.ImportReference(compat["CrowCompat.FsmTrim"].Fields.Single(f => f.Name == name));
    }

    static FieldDefinition InternalField(TypeDefinition type, string name)
    {
        FieldDefinition field = type.Fields.Single(f => f.Name == name);
        field.Attributes = (field.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Assembly;
        return field;
    }

    // ldsfld counter; ldc.i4.1; add; stsfld counter
    static Instruction[] Increment(FieldReference counter)
    {
        return new[]
        {
            Instruction.Create(OpCodes.Ldsfld, counter), Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Add),
            Instruction.Create(OpCodes.Stsfld, counter),
        };
    }

    static int FsmTrimHooks(ModuleDefinition module)
    {
        TypeDefinition fsm = GameType(module, "HutongGames.PlayMaker.Fsm");
        if (fsm == null) return 0;
        TypeDefinition state = module.GetType("HutongGames.PlayMaker.FsmState");
        TypeDefinition action = module.GetType("HutongGames.PlayMaker.FsmStateAction");
        TypeDefinition component = module.GetType("PlayMakerFSM");
        var hooks = new FieldDefinition("crowHooks", FieldAttributes.Assembly | FieldAttributes.NotSerialized, module.TypeSystem.Int32);
        action.Fields.Add(hooks);

        // List<FsmStateAction>'s Count and indexer, as FsmState.OnUpdate calls them.
        MethodDefinition onUpdate = state.Methods.Single(m => m.Name == "OnUpdate" && m.Parameters.Count == 0);
        var count = (MethodReference)onUpdate.Body.Instructions.First(i => CalledName(i) == "get_Count").Operand;
        var item = (MethodReference)onUpdate.Body.Instructions.First(i => CalledName(i) == "get_Item").Operand;

        MethodDefinition idle = AddCrowIdle(module, fsm, state, action, hooks, count, item);
        MethodDefinition proxyIdle = AddCrowProxyIdle(module, fsm, component, idle);
        int sites = 2;

        FieldReference checking = TrimField(module, "Checking");
        MethodReference checkBegin = CompatMethod(module, "FsmTrim", "CheckBegin");
        MethodReference checkEnd = CompatMethod(module, "FsmTrim", "CheckEnd");
        string[] kinds = { "Update", "Fixed", "Late" };
        for (int kind = 0; kind < 3; kind++)
        {
            MethodDefinition method = fsm.Methods.Single(m => m.Name == (kind == 0 ? "Update" : kinds[kind] + "Update") && m.Parameters.Count == 0 && m.HasBody);
            foreach (Instruction ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
                InsertBeforeRetarget(method, ret,
                    Instruction.Create(OpCodes.Ldsfld, checking),
                    Instruction.Create(OpCodes.Brfalse, ret),
                    Instruction.Create(OpCodes.Ldarg_0),
                    Instruction.Create(OpCodes.Call, checkEnd));
            var tier = new VariableDefinition(module.TypeSystem.Int32);
            method.Body.Variables.Add(tier);
            Instruction first = method.Body.Instructions[0];
            Instruction skip = Instruction.Create(OpCodes.Ldloc, tier);
            var code = new System.Collections.Generic.List<Instruction>();
            code.AddRange(Increment(TrimField(module, kinds[kind] + "Calls")));
            code.Add(Instruction.Create(OpCodes.Ldsfld, TrimField(module, kinds[kind])));
            code.Add(Instruction.Create(OpCodes.Brfalse, first));
            code.Add(Instruction.Create(OpCodes.Ldarg_0));
            code.Add(Instruction.Create(OpCodes.Ldc_I4, 2 << kind));
            code.Add(Instruction.Create(OpCodes.Call, idle));
            code.Add(Instruction.Create(OpCodes.Stloc, tier));
            code.Add(Instruction.Create(OpCodes.Ldloc, tier));
            code.Add(Instruction.Create(OpCodes.Brfalse, first));
            code.Add(Instruction.Create(OpCodes.Ldsfld, TrimField(module, "Check")));
            code.Add(Instruction.Create(OpCodes.Brfalse, skip));
            code.Add(Instruction.Create(OpCodes.Ldarg_0));
            code.Add(Instruction.Create(OpCodes.Ldc_I4, kind));
            code.Add(Instruction.Create(OpCodes.Ldloc, tier));
            code.Add(Instruction.Create(OpCodes.Call, checkBegin));
            code.Add(Instruction.Create(OpCodes.Br, first));
            code.Add(skip);
            if (kind == 0)
            {
                // tier 1: nothing; tier 2: activeState.StateTime += FsmGate.DeltaTime(), as FsmState.OnUpdate
                FieldDefinition activeState = fsm.Fields.Single(f => f.Name == "activeState");
                FieldDefinition stateTime = InternalField(state, "<StateTime>k__BackingField");
                Instruction timed = Instruction.Create(OpCodes.Ldarg_0);
                code.Add(Instruction.Create(OpCodes.Ldc_I4_1));
                code.Add(Instruction.Create(OpCodes.Bne_Un, timed));
                code.AddRange(Increment(TrimField(module, "UpdateIdle")));
                code.Add(Instruction.Create(OpCodes.Ret));
                code.Add(timed);
                code.Add(Instruction.Create(OpCodes.Ldfld, activeState));
                code.Add(Instruction.Create(OpCodes.Dup));
                code.Add(Instruction.Create(OpCodes.Ldfld, stateTime));
                code.Add(Instruction.Create(OpCodes.Call, CompatMethod(module, "FsmGate", "DeltaTime")));
                code.Add(Instruction.Create(OpCodes.Add));
                code.Add(Instruction.Create(OpCodes.Stfld, stateTime));
                code.AddRange(Increment(TrimField(module, "UpdateTimed")));
                code.Add(Instruction.Create(OpCodes.Ret));
            }
            else
            {
                code.Add(Instruction.Create(OpCodes.Pop));
                code.AddRange(Increment(TrimField(module, kinds[kind] + "Idle")));
                code.Add(Instruction.Create(OpCodes.Ret));
            }
            InsertBefore(method.Body.GetILProcessor(), first, code.ToArray());
            sites++;
        }

        // The proxies: each target the test passes is left out, as Fsm.FixedUpdate / LateUpdate would do nothing.
        foreach (string name in new[] { "PlayMakerFixedUpdate", "PlayMakerLateUpdate" })
        {
            TypeDefinition proxy = module.GetType(name);
            bool late = name == "PlayMakerLateUpdate";
            MethodDefinition method = proxy.Methods.Single(m => m.Name == (late ? "LateUpdate" : "FixedUpdate") && m.Parameters.Count == 0);
            var code = method.Body.Instructions;
            Instruction get = code.Single(i => CalledName(i) == "get_Item");
            Instruction store = get.Next;
            Instruction load = store.Next;
            Instruction test = code.First(i => code.IndexOf(i) > code.IndexOf(load) && CalledName(i) == "op_Equality");
            Instruction skipTarget = test.Next.Operand as Instruction;
            if (!store.OpCode.Name.StartsWith("stloc") || !load.OpCode.Name.StartsWith("ldloc") || test.Next.OpCode.FlowControl != FlowControl.Cond_Branch ||
                skipTarget == null)
                throw new System.Exception(name + ": the targets loop is not the expected one");
            var added = new System.Collections.Generic.List<Instruction>
            {
                Instruction.Create(OpCodes.Ldsfld, TrimField(module, late ? "Late" : "Fixed")),
                Instruction.Create(OpCodes.Brfalse, load),
                Instruction.Create(OpCodes.Ldsfld, TrimField(module, "Check")),
                Instruction.Create(OpCodes.Brtrue, load),
                load.Operand == null ? Instruction.Create(load.OpCode) : Instruction.Create(load.OpCode, (VariableDefinition)load.Operand),
                Instruction.Create(OpCodes.Ldc_I4, late ? 8 : 4),
                Instruction.Create(OpCodes.Call, proxyIdle),
                Instruction.Create(OpCodes.Brfalse, load),
            };
            added.AddRange(Increment(TrimField(module, late ? "LateProxy" : "FixedProxy")));
            added.Add(Instruction.Create(OpCodes.Br, skipTarget));
            InsertBefore(method.Body.GetILProcessor(), load, added.ToArray());
            sites++;
        }
        return sites;
    }

    // int Fsm.CrowIdle(int mask): see the comment at the top.
    static MethodDefinition AddCrowIdle(ModuleDefinition module, TypeDefinition fsm, TypeDefinition state, TypeDefinition action,
                                        FieldDefinition hooks, MethodReference count, MethodReference item)
    {
        FieldDefinition entered = fsm.Fields.Single(f => f.Name == "activeStateEntered");
        FieldDefinition activeState = fsm.Fields.Single(f => f.Name == "activeState");
        FieldDefinition switchTo = fsm.Fields.Single(f => f.Name == "switchToState");
        FieldDefinition eventTarget = fsm.Fields.Single(f => f.Name == "<EventTarget>k__BackingField");
        FieldDefinition statesEntered = fsm.Fields.Single(f => f.Name == "crowStatesEntered");
        FieldDefinition delayed = fsm.Fields.Single(f => f.Name == "crowDelayedEvents");
        FieldDefinition stateFsm = InternalField(state, "fsm");
        FieldDefinition finished = InternalField(state, "finished");
        FieldDefinition active = InternalField(state, "active");
        FieldDefinition running = InternalField(state, "activeActions");
        FieldDefinition done = InternalField(state, "_finishedActions");
        FieldDefinition blocks = InternalField(action, "blocksFinish");

        var method = new MethodDefinition("CrowIdle", MethodAttributes.Assembly | MethodAttributes.HideBySig, module.TypeSystem.Int32);
        method.Parameters.Add(new ParameterDefinition("mask", ParameterAttributes.None, module.TypeSystem.Int32));
        method.Body.InitLocals = true;
        var s = new VariableDefinition(state);
        var list = new VariableDefinition(count.DeclaringType);
        var n = new VariableDefinition(module.TypeSystem.Int32);
        var i = new VariableDefinition(module.TypeSystem.Int32);
        var x = new VariableDefinition(action);
        var bits = new VariableDefinition(module.TypeSystem.Int32);
        var blocked = new VariableDefinition(module.TypeSystem.Boolean);
        foreach (VariableDefinition variable in new[] { s, list, n, i, x, bits, blocked }) method.Body.Variables.Add(variable);
        ILProcessor il = method.Body.GetILProcessor();
        Instruction no = Instruction.Create(OpCodes.Ldc_I4_0);
        Instruction afterDelayed = Instruction.Create(OpCodes.Ldc_I4_0);
        Instruction counted = Instruction.Create(OpCodes.Ldloc, n);
        Instruction timed = Instruction.Create(OpCodes.Ldc_I4_0);
        Instruction loop = Instruction.Create(OpCodes.Ldloc, list);
        Instruction have = Instruction.Create(OpCodes.Ldloc, bits);
        Instruction next = Instruction.Create(OpCodes.Ldloc, i);
        Instruction condition = Instruction.Create(OpCodes.Ldloc, i);
        Instruction yes = Instruction.Create(OpCodes.Ldc_I4_2);

        // the FSM: its state entered and its own, no switch pending, EventTarget null, no state entered
        // since the loop counts' reset, (Update) no delayed event
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, entered);
        il.Emit(OpCodes.Brfalse, no);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, activeState);
        il.Emit(OpCodes.Stloc, s);
        il.Emit(OpCodes.Ldloc, s);
        il.Emit(OpCodes.Brfalse, no);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, switchTo);
        il.Emit(OpCodes.Brtrue, no);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, eventTarget);
        il.Emit(OpCodes.Brtrue, no);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, statesEntered);
        il.Emit(OpCodes.Brtrue, no);
        il.Emit(OpCodes.Ldloc, s);
        il.Emit(OpCodes.Ldfld, stateFsm);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Bne_Un, no);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Bne_Un, afterDelayed);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, delayed);
        il.Emit(OpCodes.Brtrue, no);
        // n = activeActions == null ? 0 : activeActions.Count; finished with none running: idle
        il.Append(afterDelayed);
        il.Emit(OpCodes.Stloc, n);
        il.Emit(OpCodes.Ldloc, s);
        il.Emit(OpCodes.Ldfld, running);
        il.Emit(OpCodes.Stloc, list);
        il.Emit(OpCodes.Ldloc, list);
        il.Emit(OpCodes.Brfalse, counted);
        il.Emit(OpCodes.Ldloc, list);
        il.Emit(OpCodes.Callvirt, count);
        il.Emit(OpCodes.Stloc, n);
        il.Append(counted);
        il.Emit(OpCodes.Brtrue, timed);
        il.Emit(OpCodes.Ldloc, s);
        il.Emit(OpCodes.Ldfld, finished);
        il.Emit(OpCodes.Brfalse, timed);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        // every running action leaves the update alone (crowHooks & mask == 0); does one block the finish?
        il.Append(timed);
        il.Emit(OpCodes.Stloc, blocked);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, i);
        il.Emit(OpCodes.Br, condition);
        il.Append(loop);
        il.Emit(OpCodes.Ldloc, i);
        il.Emit(OpCodes.Callvirt, item);
        il.Emit(OpCodes.Stloc, x);
        il.Emit(OpCodes.Ldloc, x);
        il.Emit(OpCodes.Brfalse, no);
        il.Emit(OpCodes.Ldloc, x);
        il.Emit(OpCodes.Ldfld, hooks);
        il.Emit(OpCodes.Stloc, bits);
        il.Emit(OpCodes.Ldloc, bits);
        il.Emit(OpCodes.Brtrue, have);
        il.Emit(OpCodes.Ldloc, x);
        il.Emit(OpCodes.Call, CompatMethod(module, "FsmTrim", "Hooks"));
        il.Emit(OpCodes.Stloc, bits);
        il.Emit(OpCodes.Ldloc, x);
        il.Emit(OpCodes.Ldloc, bits);
        il.Emit(OpCodes.Stfld, hooks);
        il.Append(have);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Brtrue, no);
        il.Emit(OpCodes.Ldloc, x);
        il.Emit(OpCodes.Ldfld, blocks);
        il.Emit(OpCodes.Brfalse, next);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, blocked);
        il.Append(next);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, i);
        il.Append(condition);
        il.Emit(OpCodes.Ldloc, n);
        il.Emit(OpCodes.Blt, loop);
        // CheckAllActionsFinished returns without a change: the state finished, isn't active, or no
        // action waits in its finished list and a running one blocks the finish
        il.Emit(OpCodes.Ldloc, s);
        il.Emit(OpCodes.Ldfld, finished);
        il.Emit(OpCodes.Brtrue, yes);
        il.Emit(OpCodes.Ldloc, s);
        il.Emit(OpCodes.Ldfld, active);
        il.Emit(OpCodes.Brfalse, yes);
        il.Emit(OpCodes.Ldloc, blocked);
        il.Emit(OpCodes.Brfalse, no);
        il.Emit(OpCodes.Ldloc, s);
        il.Emit(OpCodes.Ldfld, done);
        il.Emit(OpCodes.Stloc, list);
        il.Emit(OpCodes.Ldloc, list);
        il.Emit(OpCodes.Brfalse, yes);
        il.Emit(OpCodes.Ldloc, list);
        il.Emit(OpCodes.Callvirt, count);
        il.Emit(OpCodes.Brtrue, no);
        il.Append(yes);
        il.Emit(OpCodes.Ret);
        il.Append(no);
        il.Emit(OpCodes.Ret);
        fsm.Methods.Add(method);
        return method;
    }

    // static bool Fsm.CrowProxyIdle(PlayMakerFSM target, int mask): the target's Fsm is owned by the
    // target (PlayMakerFSM.Fsm's getter would set it) and CrowIdle(mask) != 0.
    static MethodDefinition AddCrowProxyIdle(ModuleDefinition module, TypeDefinition fsm, TypeDefinition component, MethodDefinition idle)
    {
        FieldDefinition componentFsm = InternalField(component, "fsm");
        FieldDefinition owner = fsm.Fields.Single(f => f.Name == "owner");
        var method = new MethodDefinition("CrowProxyIdle", MethodAttributes.Assembly | MethodAttributes.HideBySig | MethodAttributes.Static,
                                          module.TypeSystem.Boolean);
        method.Parameters.Add(new ParameterDefinition("target", ParameterAttributes.None, component));
        method.Parameters.Add(new ParameterDefinition("mask", ParameterAttributes.None, module.TypeSystem.Int32));
        var held = new VariableDefinition(fsm);
        method.Body.Variables.Add(held);
        method.Body.InitLocals = true;
        ILProcessor il = method.Body.GetILProcessor();
        Instruction no = Instruction.Create(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, no);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, componentFsm);
        il.Emit(OpCodes.Stloc, held);
        il.Emit(OpCodes.Ldloc, held);
        il.Emit(OpCodes.Brfalse, no);
        il.Emit(OpCodes.Ldloc, held);
        il.Emit(OpCodes.Ldfld, owner);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Bne_Un, no);
        il.Emit(OpCodes.Ldloc, held);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, idle);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Cgt_Un);
        il.Emit(OpCodes.Ret);
        il.Append(no);
        il.Emit(OpCodes.Ret);
        fsm.Methods.Add(method);
        return method;
    }
}
