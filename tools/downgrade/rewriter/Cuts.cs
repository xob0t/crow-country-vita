using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The fights' cuts (compat/Cuts.cs says what each does and its switch):
//   FsmGateHooks: PlayMaker's Fsm gets an int field, crowGate (0: not looked at; -1: never gated; else
//     CrowCompat FsmGate's entry); Fsm.Update starts with
//       if (FsmGate.Active && crowGate != -1 && !FsmGate.Update(ref crowGate, Owner, ActiveState)) return;
//     and calls FsmGate.After() before each return; Fsm.FixedUpdate and Fsm.LateUpdate start with
//       if (FsmGate.Active && crowGate > 0 && FsmGate.Skip(crowGate)) return;
//   FsmDeltaTime: Time.deltaTime in PlayMaker's code (HutongGames.PlayMaker*, com.sfbgames.playmaker:
//     the actions, FsmState, delayed events) reads FsmGate.DeltaTime(), which is Time.deltaTime but inside
//     a rate-limited FSM's update, where it is the time since its last one.
//   CutsLightFlicker: the game's LightFlicker gets float crowPending; Update starts with
//     if (!Cuts.FlickerStep(this, ref crowPending)) return; and reads Cuts.FlickerDelta() for Time.deltaTime.
//   CutsParticleBursts: ParticleSystemEmit's ParticleSystem.Emit(int) goes through Cuts.Emit.
//   CutsSamePose: SetPosition's and SetRotation's Transform writes go through Cuts.SetPosition and friends.
//   CutsRewired: Rewired's UnityInputHelper.GetJoystickAxis(Raw)ValueByJoystickId return 0 for an axis
//     index at or past Cuts.RewiredAxes or a Unity joystick id past Cuts.RewiredMaxJoystick, and
//     GetJoystickButtonValueByJoystickId false for such a joystick; ThreadSafeUnityInput.Mouse.Update returns at once while
//     Cuts.RewiredNoMouse; InputManager_Base's Update, FixedUpdate and LateUpdate start with
//     RewiredProbe.Begin() and call RewiredProbe.End() before each return (their time, for -crow-cuts-log).
//   CutsRewiredSites (probing builds only: CROW_REWIRED_SITES=1): after every call in ReInput's three update
//     methods and in the Rewired_Core methods they call, RewiredProbe.Mark(site, "<caller>><callee>"), which
//     books the time since the last mark to the site (-crow-cuts-log: the costliest sites).
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    static int CutsRewiredSites(ModuleDefinition module)
    {
        if (System.Environment.GetEnvironmentVariable("CROW_REWIRED_SITES") != "1") return 0;
        TypeDefinition input = module.GetType("Rewired.ReInput");
        if (input == null) return 0;
        var roots = input.Methods.Where(m => m.HasBody && (m.Name == "DDMCXcvwbqGIHLxaOdfxBmmekcEj" || m.Name == "DDhdniZCeehTddXfycZkIQXlgjzHA" ||
                                                          m.Name == "vImavScxcQKaPpkIMLZCjlRbnIUR")).ToList();
        // and, a level deeper, the costliest of what they call (the PS Vita's first probe): the input source's
        // update and the players' updates
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                if ((type.Name == "PlatformInputManager" && method.Name == "Update") ||
                    (type.Name == "vmnshPWJBtuXCwizMdMOJnwKismG" && (method.Name == "zFJQNuSBgLLQMvgmDkKhbtHYyWQC" || method.Name == "ErNIGrnQQpuNIHUKezBmIEhNdrte" ||
                                                                       method.Name == "sPIugBgROohCzCYZQLAcxtVZspWg")))
                    roots.Add(method);
        var methods = new System.Collections.Generic.List<MethodDefinition>(roots);
        foreach (MethodDefinition root in roots)
            foreach (Instruction call in root.Body.Instructions.Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt))
            {
                MethodDefinition callee = ((MethodReference)call.Operand).Resolve();
                if (callee != null && callee.HasBody && callee.Module == module && !methods.Contains(callee)) methods.Add(callee);
            }
        MethodReference mark = CompatMethod(module, "RewiredProbe", "Mark");
        int site = 0;
        foreach (MethodDefinition method in methods)
        {
            ILProcessor il = method.Body.GetILProcessor();
            foreach (Instruction call in method.Body.Instructions.Where(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                         (i.Previous == null || i.Previous.OpCode != OpCodes.Tail)).ToArray())
            {
                var called = (MethodReference)call.Operand;
                string name = method.Name.Substring(0, System.Math.Min(6, method.Name.Length)) + ">" + called.DeclaringType.Name.Substring(0, System.Math.Min(12, called.DeclaringType.Name.Length)) +
                              "." + called.Name.Substring(0, System.Math.Min(14, called.Name.Length));
                InsertAfter(il, call, Instruction.Create(OpCodes.Ldc_I4, site++), Instruction.Create(OpCodes.Ldstr, name), Instruction.Create(OpCodes.Call, mark));
                if (site >= 4000) return site;
            }
        }
        return site;
    }

    static int FsmGateHooks(ModuleDefinition module)
    {
        TypeDefinition fsm = GameType(module, "HutongGames.PlayMaker.Fsm");
        if (fsm == null) return 0;
        FieldDefinition gate = AddField(fsm, "crowGate", module.TypeSystem.Int32);
        FieldReference active = module.ImportReference(compat["CrowCompat.FsmGate"].Fields.Single(f => f.Name == "Active"));
        MethodDefinition owner = fsm.Methods.Single(m => m.Name == "get_Owner" && m.Parameters.Count == 0);
        MethodDefinition state = fsm.Methods.Single(m => m.Name == "get_ActiveState" && m.Parameters.Count == 0);

        MethodDefinition update = fsm.Methods.Single(m => m.Name == "Update" && m.Parameters.Count == 0 && m.HasBody);
        int rets = BeforeRets(update, () => new[] { Instruction.Create(OpCodes.Call, CompatMethod(module, "FsmGate", "After")) });
        Instruction first = update.Body.Instructions[0];
        InsertBefore(update.Body.GetILProcessor(), first,
            Instruction.Create(OpCodes.Ldsfld, active),
            Instruction.Create(OpCodes.Brfalse, first),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, gate),
            Instruction.Create(OpCodes.Ldc_I4_M1),
            Instruction.Create(OpCodes.Beq, first),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldflda, gate),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, owner),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, state),
            Instruction.Create(OpCodes.Call, CompatMethod(module, "FsmGate", "Update")),
            Instruction.Create(OpCodes.Brtrue, first),
            Instruction.Create(OpCodes.Ret));
        int count = 1 + rets;
        foreach (string name in new[] { "FixedUpdate", "LateUpdate" })
        {
            MethodDefinition method = fsm.Methods.Single(m => m.Name == name && m.Parameters.Count == 0 && m.HasBody);
            Instruction start = method.Body.Instructions[0];
            InsertBefore(method.Body.GetILProcessor(), start,
                Instruction.Create(OpCodes.Ldsfld, active),
                Instruction.Create(OpCodes.Brfalse, start),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, gate),
                Instruction.Create(OpCodes.Ldc_I4_0),
                Instruction.Create(OpCodes.Ble, start),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, gate),
                Instruction.Create(OpCodes.Call, CompatMethod(module, "FsmGate", "Skip")),
                Instruction.Create(OpCodes.Brfalse, start),
                Instruction.Create(OpCodes.Ret));
            count++;
        }
        return count;
    }

    static int FsmDeltaTime(ModuleDefinition module)
    {
        int count = 0;
        MethodReference delta = null;
        foreach (TypeDefinition type in AllTypes(module.Types))
        {
            string space = OuterType(type).Namespace ?? "";
            if (!space.StartsWith("HutongGames.PlayMaker", System.StringComparison.Ordinal) && space != "com.sfbgames.playmaker") continue;
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                foreach (Instruction call in method.Body.Instructions.Where(i => Calls(i, "UnityEngine.Time", "get_deltaTime")))
                {
                    if (delta == null) delta = CompatMethod(module, "FsmGate", "DeltaTime");
                    call.OpCode = OpCodes.Call;
                    call.Operand = delta;
                    count++;
                }
        }
        return count;
    }

    static int CutsLightFlicker(ModuleDefinition module)
    {
        TypeDefinition type = GameType(module, "com.sfbgames.crowcountry.LightFlicker");
        if (type == null) return 0;
        FieldDefinition pending = AddField(type, "crowPending", module.TypeSystem.Single);
        MethodDefinition update = type.Methods.Single(m => m.Name == "Update" && m.Parameters.Count == 0 && m.HasBody);
        int count = 0;
        foreach (Instruction call in update.Body.Instructions.Where(i => Calls(i, "UnityEngine.Time", "get_deltaTime")).ToArray())
        {
            call.Operand = CompatMethod(module, "Cuts", "FlickerDelta");
            count++;
        }
        if (count != 1) throw new System.Exception("LightFlicker.Update: expected one Time.deltaTime, found " + count);
        Instruction first = update.Body.Instructions[0];
        InsertBeforeRetarget(update, first,
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldflda, pending),
            Instruction.Create(OpCodes.Call, CompatMethod(module, "Cuts", "FlickerStep")),
            Instruction.Create(OpCodes.Brtrue, first),
            Instruction.Create(OpCodes.Ret));
        return count + 1;
    }

    static int CutsParticleBursts(ModuleDefinition module)
    {
        TypeDefinition type = GameType(module, "HutongGames.PlayMaker.Actions.ParticleSystemEmit");
        if (type == null) return 0;
        int count = 0;
        foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            foreach (Instruction call in method.Body.Instructions.Where(i => Calls(i, "UnityEngine.ParticleSystem", "Emit") &&
                         ((MethodReference)i.Operand).Parameters.Count == 1 && ((MethodReference)i.Operand).Parameters[0].ParameterType.FullName == "System.Int32"))
            {
                call.OpCode = OpCodes.Call;
                call.Operand = CompatMethod(module, "Cuts", "Emit");
                count++;
            }
        if (count == 0) throw new System.Exception("ParticleSystemEmit: no ParticleSystem.Emit(int)");
        return count;
    }

    static int CutsSamePose(ModuleDefinition module)
    {
        var rules = new[]
        {
            new[] { "HutongGames.PlayMaker.Actions.SetPosition", "set_position", "SetPosition" },
            new[] { "HutongGames.PlayMaker.Actions.SetPosition", "set_localPosition", "SetLocalPosition" },
            new[] { "HutongGames.PlayMaker.Actions.SetRotation", "set_eulerAngles", "SetEulerAngles" },
            new[] { "HutongGames.PlayMaker.Actions.SetRotation", "set_localEulerAngles", "SetLocalEulerAngles" },
        };
        int count = 0;
        foreach (string[] rule in rules)
        {
            TypeDefinition type = module.GetType(rule[0]);
            if (type == null) continue;
            int sites = 0;
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                foreach (Instruction call in method.Body.Instructions.Where(i => Calls(i, "UnityEngine.Transform", rule[1])))
                {
                    call.OpCode = OpCodes.Call;
                    call.Operand = CompatMethod(module, "Cuts", rule[2]);
                    sites++;
                }
            Count("samepose " + rule[0] + "." + rule[1], sites);
            count += sites;
        }
        return count;
    }

    static int CutsRewired(ModuleDefinition module)
    {
        TypeDefinition helper = GameType(module, "Rewired.UnityInputHelper");
        TypeDefinition input = GameType(module, "Rewired.ThreadSafeUnityInput");
        if (helper == null || input == null) return 0;
        FieldReference axes = module.ImportReference(compat["CrowCompat.Cuts"].Fields.Single(f => f.Name == "RewiredAxes"));
        FieldReference noMouse = module.ImportReference(compat["CrowCompat.Cuts"].Fields.Single(f => f.Name == "RewiredNoMouse"));
        int count = 0;
        FieldReference maxJoystick = module.ImportReference(compat["CrowCompat.Cuts"].Fields.Single(f => f.Name == "RewiredMaxJoystick"));
        foreach (string name in new[] { "GetJoystickAxisValueByJoystickId", "GetJoystickAxisRawValueByJoystickId" })
        {
            MethodDefinition method = helper.Methods.Single(m => m.Name == name && m.Parameters.Count == 2 && m.HasBody);
            Instruction first = method.Body.Instructions[0];
            Instruction zero = Instruction.Create(OpCodes.Ldc_R4, 0f);
            InsertBefore(method.Body.GetILProcessor(), first,
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldsfld, maxJoystick),
                Instruction.Create(OpCodes.Bgt, zero),
                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Ldsfld, axes),
                Instruction.Create(OpCodes.Blt, first),
                zero,
                Instruction.Create(OpCodes.Ret));
            count++;
        }
        MethodDefinition button = helper.Methods.Single(m => m.Name == "GetJoystickButtonValueByJoystickId" && m.Parameters.Count == 2 && m.HasBody);
        Instruction buttonFirst = button.Body.Instructions[0];
        InsertBefore(button.Body.GetILProcessor(), buttonFirst,
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldsfld, maxJoystick),
            Instruction.Create(OpCodes.Ble, buttonFirst),
            Instruction.Create(OpCodes.Ldc_I4_0),
            Instruction.Create(OpCodes.Ret));
        count++;
        TypeDefinition manager = GameType(module, "Rewired.InputManager_Base");
        if (manager == null) return 0;
        foreach (string name in new[] { "Update", "FixedUpdate", "LateUpdate" })
        {
            MethodDefinition method = manager.Methods.Single(m => m.Name == name && m.Parameters.Count == 0 && m.HasBody);
            BeforeRets(method, () => new[] { Instruction.Create(OpCodes.Call, CompatMethod(module, "RewiredProbe", "End")) });
            InsertBeforeRetarget(method, method.Body.Instructions[0], Instruction.Create(OpCodes.Call, CompatMethod(module, "RewiredProbe", "Begin")));
            count++;
        }
        TypeDefinition mouse = input.NestedTypes.Single(t => t.Name == "Mouse");
        MethodDefinition update = mouse.Methods.Single(m => m.Name == "Update" && m.Parameters.Count == 0 && m.HasBody);
        Instruction start = update.Body.Instructions[0];
        InsertBefore(update.Body.GetILProcessor(), start,
            Instruction.Create(OpCodes.Ldsfld, noMouse),
            Instruction.Create(OpCodes.Brfalse, start),
            Instruction.Create(OpCodes.Ret));
        return count + 1;
    }
}
