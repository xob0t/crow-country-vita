using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The enemies' inactive parts taken out of their moving hierarchies (CrowCompat EnemySplit,
// compat/EnemySplit.cs) go back in place before the game's code touches them: in the game's assemblies,
// every GameObject.SetActive call and every Transform parent, world position, rotation and Euler angle
// read and write becomes a call of EnemySplit's static of the same meaning, with the object first.
// Calls through a constrained. prefix (generic code) are left as they are.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    static readonly Dictionary<string, string> EnemySplitCalls = new Dictionary<string, string>
    {
        { "UnityEngine.GameObject::SetActive(System.Boolean)", "SetActive" },
        { "UnityEngine.Transform::SetParent(UnityEngine.Transform)", "SetParent" },
        { "UnityEngine.Transform::SetParent(UnityEngine.Transform,System.Boolean)", "SetParentStays" },
        { "UnityEngine.Transform::get_parent()", "GetParent" },
        { "UnityEngine.Transform::set_parent(UnityEngine.Transform)", "SetParentProperty" },
        { "UnityEngine.Transform::get_position()", "GetPosition" },
        { "UnityEngine.Transform::set_position(UnityEngine.Vector3)", "SetPosition" },
        { "UnityEngine.Transform::get_rotation()", "GetRotation" },
        { "UnityEngine.Transform::set_rotation(UnityEngine.Quaternion)", "SetRotation" },
        { "UnityEngine.Transform::get_eulerAngles()", "GetEulerAngles" },
        { "UnityEngine.Transform::set_eulerAngles(UnityEngine.Vector3)", "SetEulerAngles" },
        { "UnityEngine.Transform::SetPositionAndRotation(UnityEngine.Vector3,UnityEngine.Quaternion)", "SetPositionAndRotation" },
    };

    static int RouteEnemySplit(ModuleDefinition module)
    {
        TypeDefinition split = compat["CrowCompat.EnemySplit"];
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    var called = instruction.Operand as MethodReference;
                    if (called == null || !called.HasThis || (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)) continue;
                    if (instruction.Previous != null && instruction.Previous.OpCode == OpCodes.Constrained) continue;
                    string key = called.DeclaringType.FullName + "::" + called.Name + "(" +
                                 string.Join(",", called.Parameters.Select(p => p.ParameterType.FullName)) + ")";
                    string name;
                    if (!EnemySplitCalls.TryGetValue(key, out name)) continue;
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = module.ImportReference(split.Methods.Single(m => m.IsStatic && m.IsPublic && m.Name == name));
                    Count("enemy split " + name, 1);
                    count++;
                }
        return count;
    }
}
