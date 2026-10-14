using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Single game methods that call CrowCompat rules: the camera's post-processing chain.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    // CrowCountryCamEffect.Awake makes the chain's textures and materials; CrowCompat CamEffect resizes
    // them on the PS Vita at its end.
    static int PatchCamEffect(ModuleDefinition module)
    {
        TypeDefinition type = GameType(module, "com.sfbgames.crowcountry.CrowCountryCamEffect");
        if (type == null) return 0;
        MethodDefinition awake = type.Methods.Single(m => m.Name == "Awake" && m.Parameters.Count == 0);
        MethodReference after = CompatMethod(module, "CamEffect", "AfterAwake");
        return BeforeRets(awake, () => new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, after) });
    }
}
