using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Profiling builds only (CROW_ACTION_PROFILE=1 when the assemblies are rewritten, e.g. for a quickcode;
// the pass loop skips the "profile" group otherwise): each PlayMaker action call in FsmState's state
// loops (OnEnter in ActivateActions, OnUpdate, OnFixedUpdate, OnLateUpdate) between CrowCompat
// ActionProfile.Begin(action, kind, state) and End() (ProfileActions), and the body of every
// MonoBehaviour's Update, FixedUpdate, LateUpdate and OnCollision/OnTrigger Enter/Stay/Exit between
// Begin(this, kind, this) and End() (ProfileBehaviours), which sum the time per action and behaviour
// type, per state and per behaviour when the player gets -crow-action-profile (compat/ActionProfile.cs).
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
                // The action is on the stack: Begin gets a copy, and the state.
                il.InsertBefore(call, Instruction.Create(OpCodes.Dup));
                il.InsertBefore(call, Instruction.Create(OpCodes.Ldc_I4, kind));
                il.InsertBefore(call, Instruction.Create(method.IsStatic ? OpCodes.Ldnull : OpCodes.Ldarg_0));
                il.InsertBefore(call, Instruction.Create(OpCodes.Call, begin));
                il.InsertAfter(call, Instruction.Create(OpCodes.Call, end));
                count++;
            }
        }
        return count;
    }

    // Behaviour callbacks and their kinds (ActionProfile's 4-7).
    static readonly string[] ProfiledCallbacks = { "Update", "FixedUpdate", "LateUpdate" };
    static readonly string[] CollisionCallbacks =
        { "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit", "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit" };

    static int ProfileBehaviours(ModuleDefinition module)
    {
        MethodReference begin = null, end = null;
        int count = 0;
        foreach (TypeDefinition type in module.GetTypes())
        {
            if (!type.IsClass || type.IsValueType || !DerivesFrom(type, "UnityEngine.MonoBehaviour")) continue;
            foreach (MethodDefinition method in type.Methods)
            {
                if (!method.HasBody || method.IsStatic || method.IsAbstract || method.ReturnType.FullName != "System.Void") continue;
                int kind = Array.IndexOf(ProfiledCallbacks, method.Name);
                if (kind >= 0 && method.Parameters.Count != 0) continue;
                if (kind < 0)
                {
                    if (Array.IndexOf(CollisionCallbacks, method.Name) < 0 || method.Parameters.Count > 1) continue;
                    kind = 3;
                }
                if (begin == null)
                {
                    begin = CompatMethod(module, "ActionProfile", "Begin");
                    end = CompatMethod(module, "ActionProfile", "End");
                }
                ILProcessor il = method.Body.GetILProcessor();
                // Each ret becomes the End call (branches to it then run End too), followed by a ret.
                foreach (Instruction ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
                {
                    ret.OpCode = OpCodes.Call;
                    ret.Operand = end;
                    il.InsertAfter(ret, Instruction.Create(OpCodes.Ret));
                }
                Instruction first = method.Body.Instructions[0];
                il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, Instruction.Create(OpCodes.Ldc_I4, 4 + kind));
                il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, Instruction.Create(OpCodes.Call, begin));
                count++;
            }
        }
        return count;
    }
}
