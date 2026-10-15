using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Allocation in play: PlayMaker's DOTween actions' tweens recyclable when CrowCompat TweenRecycle.On
// is set (compat/Alloc.cs says why that is safe there). Each action's OnEnter calls
// tween.SetRecyclable(recyclable.Value); the value becomes recyclable.Value | TweenRecycle.On.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    static int RecyclePlayMakerTweens(ModuleDefinition module)
    {
        FieldReference on = module.ImportReference(compat["CrowCompat.TweenRecycle"].Fields.Single(f => f.Name == "On"));
        int count = 0;
        foreach (TypeDefinition type in module.Types.Where(t => t.Namespace == "HutongGames.PlayMaker.Actions" && t.Name.StartsWith("DOTween")))
        {
            MethodDefinition enter = type.Methods.SingleOrDefault(m => m.Name == "OnEnter" && m.Parameters.Count == 0 && m.HasBody);
            if (enter == null) continue;
            ILProcessor il = enter.Body.GetILProcessor();
            foreach (Instruction call in enter.Body.Instructions.Where(i => Calls(i, "DG.Tweening.TweenSettingsExtensions", "SetRecyclable") &&
                         ((MethodReference)i.Operand).Parameters.Count == 2).ToArray())
            {
                InsertBefore(il, call, Instruction.Create(OpCodes.Ldsfld, on), Instruction.Create(OpCodes.Or));
                count++;
            }
        }
        return count;
    }
}
