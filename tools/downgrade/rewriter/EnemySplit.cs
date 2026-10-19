using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The enemies' inactive parts taken out of their moving hierarchies (CrowCompat EnemySplit,
// compat/EnemySplit.cs) go back in place before the game's code touches them: in the game's assemblies,
// every GameObject.SetActive call and every Transform parent, world position, rotation and Euler angle
// read and write becomes a call of EnemySplit's static of the same meaning, with the object first.
// The overlay canvases' parked switched-off subtrees (compat/UiPark.cs) go back the same way, and also
// before a call that sees their old parents' children or descendants (GetChild, childCount, foreach,
// Find, sibling indices, GetComponent(s)InChildren, GetComponent(s)InParent, IsChildOf; generic forms
// included). Calls through a constrained. prefix (generic code) are left as they are.
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
        // UiPark: children and parents
        { "UnityEngine.Transform::GetChild(System.Int32)", "GetChild" },
        { "UnityEngine.Transform::get_childCount()", "GetChildCount" },
        { "UnityEngine.Transform::GetEnumerator()", "GetEnumerator" },
        { "UnityEngine.Transform::Find(System.String)", "Find" },
        { "UnityEngine.Transform::GetSiblingIndex()", "GetSiblingIndex" },
        { "UnityEngine.Transform::SetSiblingIndex(System.Int32)", "SetSiblingIndex" },
        { "UnityEngine.Transform::SetAsFirstSibling()", "SetAsFirstSibling" },
        { "UnityEngine.Transform::SetAsLastSibling()", "SetAsLastSibling" },
        { "UnityEngine.Transform::IsChildOf(UnityEngine.Transform)", "IsChildOf" },
        { "UnityEngine.Component::GetComponentsInChildren(System.Type,System.Boolean)", "ComponentsInChildren" },
        { "UnityEngine.GameObject::GetComponentsInChildren(System.Type,System.Boolean)", "ComponentsInChildren" },
        { "UnityEngine.Component::GetComponentInChildren(System.Type,System.Boolean)", "ComponentInChildren" },
        { "UnityEngine.GameObject::GetComponentInChildren(System.Type,System.Boolean)", "ComponentInChildren" },
        { "UnityEngine.Component::GetComponentsInChildren<T>(System.Boolean)", "ComponentsInChildrenT" },
        { "UnityEngine.GameObject::GetComponentsInChildren<T>(System.Boolean)", "ComponentsInChildrenT" },
        { "UnityEngine.Component::GetComponentsInChildren<T>(System.Boolean,System.Collections.Generic.List`1<T>)", "ComponentsInChildrenList" },
        { "UnityEngine.GameObject::GetComponentsInChildren<T>(System.Boolean,System.Collections.Generic.List`1<T>)", "ComponentsInChildrenList" },
        { "UnityEngine.Component::GetComponentInChildren<T>(System.Boolean)", "ComponentInChildrenT" },
        { "UnityEngine.GameObject::GetComponentInChildren<T>(System.Boolean)", "ComponentInChildrenT" },
        { "UnityEngine.Component::GetComponentInParent(System.Type)", "ComponentInParent" },
        { "UnityEngine.GameObject::GetComponentInParent(System.Type)", "ComponentInParent" },
        { "UnityEngine.Component::GetComponentInParent<T>()", "ComponentInParentT" },
        { "UnityEngine.GameObject::GetComponentInParent<T>()", "ComponentInParentT" },
        { "UnityEngine.Component::GetComponentsInParent(System.Type,System.Boolean)", "ComponentsInParent" },
        { "UnityEngine.GameObject::GetComponentsInParent(System.Type,System.Boolean)", "ComponentsInParent" },
        { "UnityEngine.Component::GetComponentsInParent<T>(System.Boolean)", "ComponentsInParentT" },
        { "UnityEngine.GameObject::GetComponentsInParent<T>(System.Boolean)", "ComponentsInParentT" },
        { "UnityEngine.Component::GetComponentsInParent<T>()", "ComponentsInParentT0" },
        { "UnityEngine.GameObject::GetComponentsInParent<T>()", "ComponentsInParentT0" },
    };

    // A call's key: Type::Name, <T> for a generic method, and its parameter types with the method's own
    // generic parameter as T.
    static string EnemySplitKey(MethodReference called)
    {
        var generic = called as GenericInstanceMethod;
        MethodReference element = generic != null ? generic.ElementMethod : called;
        return called.DeclaringType.FullName + "::" + called.Name + (generic != null ? "<T>" : "") + "(" +
               string.Join(",", element.Parameters.Select(p => p.ParameterType.FullName.Replace("!!0", "T"))) + ")";
    }

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
                    string name;
                    if (!EnemySplitCalls.TryGetValue(EnemySplitKey(called), out name)) continue;
                    var generic = called as GenericInstanceMethod;
                    string self = called.DeclaringType.FullName;
                    int arguments = (generic != null ? generic.ElementMethod : called).Parameters.Count + 1;
                    MethodDefinition target = split.Methods.Single(m => m.IsStatic && m.IsPublic && m.Name == name &&
                        m.Parameters.Count == arguments && m.Parameters[0].ParameterType.FullName == self &&
                        m.HasGenericParameters == (generic != null));
                    MethodReference imported = module.ImportReference(target);
                    if (generic != null)
                    {
                        var instance = new GenericInstanceMethod(imported);
                        foreach (TypeReference argument in generic.GenericArguments) instance.GenericArguments.Add(argument);
                        imported = instance;
                    }
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = imported;
                    Count("enemy split " + name, 1);
                    count++;
                }
        return count;
    }
}
