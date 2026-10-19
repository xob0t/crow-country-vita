using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Decoration off screen (compat/Decor.cs says what runs slower and why that is safe):
//   DecorTweenHooks: DOTween's Tween gets an int field, crowDecor (0: not looked at yet; CrowCompat's
//     DecorTweens keeps its verdict on the tween's target there), which Tween.Reset clears (a recycled
//     tween is looked at again), and TweenManager.Update's read of tween.timeScale goes through a new
//     static Tween.CrowScale(tween) = DecorTweens.Scale(timeScale, ref crowDecor, target, updateType),
//     or tween.timeScale straight away while DecorTweens.Active is off or crowDecor is -1 (not
//     decoration).
//   DecorAnimationScripts: the game's AbstractAnimationScript (RotationAnimationScript,
//     ScaleAnimationScript, PositionAnimationScript) gets float crowPending and int crowEntry; its
//     Update starts with `if (!DecorScripts.Step(this, ref crowPending, ref crowEntry)) return;` and
//     reads its frame time from DecorScripts.Delta() instead of Time.deltaTime (the frame's time plus
//     the frames' it skipped); OnEnable clears crowPending.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    static int DecorTweenHooks(ModuleDefinition module)
    {
        TypeDefinition tween = GameType(module, "DG.Tweening.Tween");
        TypeDefinition manager = module.GetType("DG.Tweening.Core.TweenManager");
        if (tween == null || manager == null) return 0;
        FieldDefinition timeScale = tween.Fields.Single(f => f.Name == "timeScale");
        FieldDefinition state = AddField(tween, "crowDecor", module.TypeSystem.Int32);
        state.Attributes = FieldAttributes.Assembly | FieldAttributes.NotSerialized;
        FieldDefinition targetField = tween.Fields.Single(f => f.Name == "target");
        FieldDefinition updateType = tween.Fields.Single(f => f.Name == "updateType");
        // static float CrowScale(Tween tween) => DecorTweens.Scale(tween.timeScale, ref tween.crowDecor, tween.target, (int)tween.updateType)
        var scale = new MethodDefinition("CrowScale", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Single);
        scale.Parameters.Add(new ParameterDefinition("tween", ParameterAttributes.None, tween));
        ILProcessor il = scale.Body.GetILProcessor();
        Instruction plain = Instruction.Create(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, CompatField(module, "DecorTweens", "Active"));
        il.Emit(OpCodes.Brfalse, plain);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, state);
        il.Emit(OpCodes.Ldc_I4_M1);
        il.Emit(OpCodes.Beq, plain);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, timeScale);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldflda, state);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, targetField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, updateType);
        il.Emit(OpCodes.Call, CompatMethod(module, "DecorTweens", "Scale"));
        il.Emit(OpCodes.Ret);
        il.Append(plain);
        il.Emit(OpCodes.Ldfld, timeScale);
        il.Emit(OpCodes.Ret);
        tween.Methods.Add(scale);
        int count = 0;
        MethodDefinition update = manager.Methods.Single(m => m.Name == "Update" && m.Parameters.Count == 3 && m.HasBody);
        foreach (Instruction instruction in update.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference &&
                     ((FieldReference)i.Operand).Name == "timeScale" && ((FieldReference)i.Operand).DeclaringType.FullName == "DG.Tweening.Tween").ToArray())
        {
            instruction.OpCode = OpCodes.Call;
            instruction.Operand = scale;
            count++;
        }
        if (count != 1) throw new System.Exception("TweenManager.Update: expected one read of Tween.timeScale, found " + count);
        MethodDefinition reset = tween.Methods.Single(m => m.Name == "Reset" && m.Parameters.Count == 0 && m.HasBody);
        Instruction first = reset.Body.Instructions[0];
        InsertBeforeRetarget(reset, first, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Stfld, state));
        return count + 1;
    }

    static int DecorAnimationScripts(ModuleDefinition module)
    {
        TypeDefinition script = GameType(module, "Code.com.sfbgames.general.animation.AbstractAnimationScript");
        if (script == null) return 0;
        FieldDefinition pending = AddField(script, "crowPending", module.TypeSystem.Single);
        FieldDefinition entry = AddField(script, "crowEntry", module.TypeSystem.Int32);
        MethodDefinition update = script.Methods.Single(m => m.Name == "Update" && m.Parameters.Count == 0 && m.HasBody);
        int count = 0;
        foreach (Instruction call in update.Body.Instructions.Where(i => Calls(i, "UnityEngine.Time", "get_deltaTime")).ToArray())
        {
            call.Operand = CompatMethod(module, "DecorScripts", "Delta");
            count++;
        }
        if (count != 1) throw new System.Exception("AbstractAnimationScript.Update: expected one Time.deltaTime, found " + count);
        Instruction first = update.Body.Instructions[0];
        InsertBeforeRetarget(update, first,
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldflda, pending),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldflda, entry),
            Instruction.Create(OpCodes.Call, CompatMethod(module, "DecorScripts", "Step")),
            Instruction.Create(OpCodes.Brtrue, first),
            Instruction.Create(OpCodes.Ret));
        MethodDefinition enable = script.Methods.Single(m => m.Name == "OnEnable" && m.Parameters.Count == 0 && m.HasBody);
        InsertBeforeRetarget(enable, enable.Body.Instructions[0],
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldc_R4, 0f), Instruction.Create(OpCodes.Stfld, pending));
        return count + 1;
    }
}
