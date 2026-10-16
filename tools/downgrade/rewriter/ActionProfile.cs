using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Profiling builds only (CROW_ACTION_PROFILE=1 when the assemblies are rewritten, e.g. for a quickcode;
// the pass loop skips the "profile" group otherwise): each PlayMaker action call in FsmState's state
// loops (OnEnter in ActivateActions, OnUpdate, OnFixedUpdate, OnLateUpdate) between CrowCompat
// ActionProfile.Begin(action, kind) and End(), which sum the time per action type when the player gets
// -crow-action-profile (compat/ActionProfile.cs).
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    static readonly string[] ProfiledCalls = { "OnUpdate", "OnFixedUpdate", "OnLateUpdate", "OnEnter" };

    static bool Profiling { get { return Environment.GetEnvironmentVariable("CROW_ACTION_PROFILE") == "1"; } }

    static int ProfileActions(ModuleDefinition module)
    {
        if (module.Assembly.Name.Name != "PlayMaker") return 0;
        TypeDefinition state = GameType(module, "HutongGames.PlayMaker.FsmState");
        if (state == null) return 0;
        MethodReference begin = CompatMethod(module, "ActionProfile", "Begin");
        MethodReference end = CompatMethod(module, "ActionProfile", "End");
        int count = 0;
        foreach (MethodDefinition method in state.Methods.Where(m => m.HasBody &&
                     (m.Name == "OnUpdate" || m.Name == "OnFixedUpdate" || m.Name == "OnLateUpdate" || m.Name == "ActivateActions")))
        {
            ILProcessor il = method.Body.GetILProcessor();
            foreach (Instruction call in method.Body.Instructions.ToArray())
            {
                var target = call.Operand as MethodReference;
                if (call.OpCode != OpCodes.Callvirt || target == null || target.DeclaringType.FullName != "HutongGames.PlayMaker.FsmStateAction") continue;
                int kind = Array.IndexOf(ProfiledCalls, target.Name);
                if (kind < 0) continue;
                // The action is on the stack: Begin gets a copy.
                il.InsertBefore(call, Instruction.Create(OpCodes.Dup));
                il.InsertBefore(call, Instruction.Create(OpCodes.Ldc_I4, kind));
                il.InsertBefore(call, Instruction.Create(OpCodes.Call, begin));
                il.InsertAfter(call, Instruction.Create(OpCodes.Call, end));
                count++;
            }
        }
        return count;
    }
}
