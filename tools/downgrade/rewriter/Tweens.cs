using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// DOTween's update, timed and counted (compat/Tweens.cs, TweenProbe; nothing in what DOTween does changes):
//   TweenProbeHooks: TweenManager.Update(UpdateType, float, float) starts with TweenProbe.Begin and ends with
//     TweenProbe.End, and its one Tween.DoGoto goes through the new static Tween.CrowGoto(t, position, loops, mode):
//     TweenProbe.Gotos++, then DoGoto. Tween.OnTweenCallback (TweenCallback, Tween) starts with TweenProbe.CallBegin
//     and ends with TweenProbe.CallEnd.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    static int TweenProbeHooks(ModuleDefinition module)
    {
        TypeDefinition tween = GameType(module, "DG.Tweening.Tween");
        TypeDefinition manager = GameType(module, "DG.Tweening.Core.TweenManager");
        if (tween == null || manager == null) return 0;
        MethodDefinition doGoto = tween.Methods.Single(m => m.Name == "DoGoto" && m.Parameters.Count == 4);

        // static bool CrowGoto(Tween t, float to, int loops, UpdateMode mode)
        var go = new MethodDefinition("CrowGoto", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Boolean);
        foreach (ParameterDefinition parameter in doGoto.Parameters) go.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
        ILProcessor il = go.Body.GetILProcessor();
        FieldReference gotos = CompatField(module, "TweenProbe", "Gotos");
        il.Emit(OpCodes.Ldsfld, gotos);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stsfld, gotos);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Call, doGoto);
        il.Emit(OpCodes.Ret);
        tween.Methods.Add(go);

        MethodDefinition update = manager.Methods.Single(m => m.Name == "Update" && m.Parameters.Count == 3 && m.HasBody);
        Instruction[] calls = update.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference && ((MethodReference)i.Operand).Resolve() == doGoto).ToArray();
        if (calls.Length != 1) throw new System.Exception("TweenManager.Update: expected one Tween.DoGoto, found " + calls.Length);
        calls[0].Operand = go;
        Wrap(update, CompatMethod(module, "TweenProbe", "Begin"), CompatMethod(module, "TweenProbe", "End"));
        Wrap(tween.Methods.Single(m => m.Name == "OnTweenCallback" && !m.HasGenericParameters && m.Parameters.Count == 2),
             CompatMethod(module, "TweenProbe", "CallBegin"), CompatMethod(module, "TweenProbe", "CallEnd"));
        return 3;
    }

    // A call of begin at the method's start and of end before each ret.
    static void Wrap(MethodDefinition method, MethodReference begin, MethodReference end)
    {
        InsertBeforeRetarget(method, method.Body.Instructions[0], Instruction.Create(OpCodes.Call, begin));
        foreach (Instruction ret in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
            InsertBeforeRetarget(method, ret, Instruction.Create(OpCodes.Call, end));
    }
}
