using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Load time: PlayMaker's load-time methods timed by CrowCompat LoadStats (compat/Loading.cs), which
// sums each one's time per thread when -crow-load-stats is given (Start returns 0 otherwise, and Stop
// then does nothing): a timestamp in a new local at the method's start, LoadStats.Stop(start, slot)
// before each return.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    // Type, method and LoadStats slot (LoadStats.Names has them in this order).
    static readonly string[][] TimedLoadMethods =
    {
        new[] { "PlayMakerFSM", "OnAfterDeserialize" },
        new[] { "PlayMakerFSM", "Awake" },
        new[] { "PlayMakerFSM", "OnEnable" },
        new[] { "PlayMakerFSM", "Start" },
        new[] { "HutongGames.PlayMaker.Fsm", "InitData" },
        new[] { "HutongGames.PlayMaker.ActionData", "LoadActions" },
        new[] { "HutongGames.PlayMaker.Fsm", "Init" },
        new[] { "HutongGames.PlayMaker.Fsm", "Preprocess" },
        new[] { "HutongGames.PlayMaker.ActionData", "CreateAction" },
        new[] { "HutongGames.PlayMaker.ActionData", "LoadActionField" },
    };

    static int TimeLoadMethods(ModuleDefinition module)
    {
        if (module.Assembly.Name.Name != "PlayMaker") return 0;
        TypeDefinition stats = compat["CrowCompat.LoadStats"];
        MethodReference start = module.ImportReference(stats.Methods.Single(m => m.Name == "Start"));
        MethodReference stop = module.ImportReference(stats.Methods.Single(m => m.Name == "Stop"));
        int count = 0;
        for (int slot = 0; slot < TimedLoadMethods.Length; slot++)
        {
            TypeDefinition type = GameType(module, TimedLoadMethods[slot][0]);
            if (type == null) continue;
            foreach (MethodDefinition method in type.Methods.Where(m => m.Name == TimedLoadMethods[slot][1] && m.HasBody).ToArray())
            {
                var stamp = new VariableDefinition(module.TypeSystem.Int64);
                method.Body.Variables.Add(stamp);
                method.Body.InitLocals = true;
                ILProcessor il = method.Body.GetILProcessor();
                InsertBefore(il, method.Body.Instructions[0], Instruction.Create(OpCodes.Call, start), Instruction.Create(OpCodes.Stloc, stamp));
                foreach (Instruction ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
                    InsertBeforeRetarget(method, ret, Instruction.Create(OpCodes.Ldloc, stamp), Instruction.Create(OpCodes.Ldc_I4, slot),
                        Instruction.Create(OpCodes.Call, stop));
                count++;
            }
        }
        return count;
    }
}
