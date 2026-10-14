using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Type layouts that the newer runtimes the game was built for accept and the target's Mono (2018)
// rejects with a TypeLoadException when it sets up the type's vtable. The editor and the IL2CPP
// build don't load the types, so only the player finds them, and only when it first uses one.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    // A class that lists an interface its base class already implements, and implements none of the
    // interface's methods itself, gets its methods from the base class's implementations, explicit
    // ones (MethodImpl) included. Mono 2018 does not look at the base class's explicit
    // implementations then: "no implementation for interface method ... in class ..." (Rewired_Core,
    // whose obfuscated classes do this; Rewired failed to start, so the game had no input). The
    // class's own listing changes nothing then, so it goes. A class that implements any of the
    // interface's methods itself, explicitly or by a virtual method of the same name, keeps it.
    static int ReimplementedInterfaces(ModuleDefinition module)
    {
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
        {
            if (type.IsInterface || type.BaseType == null) continue;
            foreach (InterfaceImplementation listed in type.Interfaces.ToArray())
            {
                TypeDefinition contract;
                try { contract = listed.InterfaceType.Resolve(); }
                catch (AssemblyResolutionException) { continue; }
                if (contract == null) continue;
                string name = listed.InterfaceType.FullName;
                bool own = type.Methods.Any(m => m.Overrides.Any(o => o.DeclaringType.FullName == name)) ||
                           contract.Methods.Any(im => type.Methods.Any(m => m.IsVirtual && m.Name == im.Name));
                if (!BaseImplements(type.BaseType, name)) continue;
                if (!own)
                {
                    type.Interfaces.Remove(listed);
                    count++;
                    continue;
                }
                count += ForwardExplicitImplementations(module, type, contract, listed.InterfaceType);
            }
        }
        return count;
    }

    // For a class that keeps its listing of an interface, the interface's methods it doesn't implement
    // itself and a base class implements explicitly get an explicit implementation in the class that
    // calls the base class's (made assembly-visible: an explicit implementation is private).
    static int ForwardExplicitImplementations(ModuleDefinition module, TypeDefinition type, TypeDefinition contract, TypeReference contractReference)
    {
        int count = 0;
        foreach (MethodDefinition method in contract.Methods.Where(m => !m.IsStatic))
        {
            if (type.Methods.Any(m => m.Overrides.Any(o => o.Name == method.Name && o.DeclaringType.FullName == contractReference.FullName)) ||
                type.Methods.Any(m => m.IsVirtual && m.IsPublic && SameSignature(m, method)))
                continue;
            MethodDefinition inherited = BaseExplicitImplementation(type.BaseType, method, contractReference.FullName);
            if (inherited == null || inherited.Module != module) continue;
            if (inherited.IsPrivate) inherited.IsAssembly = true;
            var forward = new MethodDefinition(contract.FullName + "." + method.Name,
                MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                inherited.ReturnType);
            foreach (ParameterDefinition parameter in inherited.Parameters)
                forward.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
            ILProcessor il = forward.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            foreach (ParameterDefinition parameter in forward.Parameters) il.Emit(OpCodes.Ldarg, parameter);
            il.Emit(OpCodes.Call, module.ImportReference(inherited));
            il.Emit(OpCodes.Ret);
            forward.Overrides.Add(module.ImportReference(method));
            type.Methods.Add(forward);
            count++;
        }
        return count;
    }

    static bool SameSignature(MethodDefinition a, MethodDefinition b)
    {
        return a.Name == b.Name && a.ReturnType.FullName == b.ReturnType.FullName && a.Parameters.Count == b.Parameters.Count &&
               a.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(b.Parameters.Select(p => p.ParameterType.FullName));
    }

    // The nearest base class method that explicitly implements an interface method.
    static MethodDefinition BaseExplicitImplementation(TypeReference baseType, MethodDefinition method, string interfaceName)
    {
        for (TypeReference current = baseType; current != null;)
        {
            TypeDefinition definition;
            try { definition = current.Resolve(); }
            catch (AssemblyResolutionException) { return null; }
            if (definition == null) return null;
            MethodDefinition found = definition.Methods.FirstOrDefault(m =>
                m.Overrides.Any(o => o.Name == method.Name && o.DeclaringType.FullName == interfaceName));
            if (found != null) return found;
            current = definition.BaseType;
        }
        return null;
    }

    static bool BaseImplements(TypeReference baseType, string interfaceName)
    {
        for (TypeReference current = baseType; current != null;)
        {
            TypeDefinition definition;
            try { definition = current.Resolve(); }
            catch (AssemblyResolutionException) { return false; }
            if (definition == null) return false;
            if (definition.Interfaces.Any(i => i.InterfaceType.FullName == interfaceName || Extends(i.InterfaceType, interfaceName))) return true;
            current = definition.BaseType;
        }
        return false;
    }

    static bool Extends(TypeReference contract, string interfaceName)
    {
        TypeDefinition definition;
        try { definition = contract.Resolve(); }
        catch (AssemblyResolutionException) { return false; }
        return definition != null && definition.Interfaces.Any(i => i.InterfaceType.FullName == interfaceName || Extends(i.InterfaceType, interfaceName));
    }

    // A constrained call on a struct reached through a void* ("((Guid*)ptr)->ToString()": call
    // IntPtr::op_Explicit returning void*, then constrained. Guid). The target's IL2CPP types the value
    // as System.Void and stops ("Attempting to constrain a value of type 'System.Void'"); the one such
    // method, in Rewired_Windows, writes a device dump to the console. A void method like that gets
    // an empty body; any other is reported (UNRESOLVED).
    static int EmptyPointerConstrainedCalls(ModuleDefinition module)
    {
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
        foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
        {
            bool found = method.Body.Instructions.Any(i => i.OpCode == OpCodes.Constrained && i.Previous != null &&
                i.Previous.Operand is MethodReference && ((MethodReference)i.Previous.Operand).ReturnType.FullName == "System.Void*");
            if (!found) continue;
            if (method.ReturnType.FullName != "System.Void")
            {
                problems.Add(module.Name + ": constrained call through void* in " + method.FullName);
                continue;
            }
            method.Body = new MethodBody(method);
            method.Body.GetILProcessor().Emit(OpCodes.Ret);
            count++;
        }
        return count;
    }

    // Default interface methods (C# 8): an interface method with a body. Mono 2018 has none. The
    // game's one, IFsmStateActionGUI.OnGUI, is empty, and its implementers (PlayMaker actions) all
    // inherit FsmStateAction.OnGUI, so it becomes abstract. One with a body, or an implementer that
    // would be left without the method, is reported instead (UNRESOLVED), for a rule of its own.
    static int DefaultInterfaceMethods(ModuleDefinition module)
    {
        int count = 0;
        var types = AllTypes(module.Types).ToArray();
        foreach (TypeDefinition contract in types.Where(t => t.IsInterface))
        foreach (MethodDefinition method in contract.Methods.Where(m => m.HasBody && !m.IsStatic).ToArray())
        {
            bool empty = method.Body.Instructions.All(i => i.OpCode == OpCodes.Nop || i.OpCode == OpCodes.Ret) && method.ReturnType.FullName == "System.Void";
            var missing = types.Where(t => !t.IsInterface && t.Interfaces.Any(i => i.InterfaceType.FullName == contract.FullName) &&
                                           !Implements(t, method)).Select(t => t.FullName).ToArray();
            if (!empty || missing.Length > 0)
            {
                problems.Add(module.Name + ": default interface method " + method.FullName +
                             (empty ? " not implemented by " + string.Join(", ", missing) : " has a body"));
                continue;
            }
            method.Body = null;
            method.IsAbstract = true;
            method.IsVirtual = true;
            method.IsNewSlot = true;
            count++;
        }
        return count;
    }

    // Whether a class or its bases have a public virtual method of the interface method's name and
    // parameters, or an explicit implementation of it.
    static bool Implements(TypeDefinition type, MethodDefinition method)
    {
        for (TypeDefinition current = type; current != null;)
        {
            if (current.Methods.Any(m => m.Overrides.Any(o => o.Name == method.Name && o.DeclaringType.FullName == method.DeclaringType.FullName)))
                return true;
            if (current.Methods.Any(m => m.IsVirtual && m.IsPublic && m.Name == method.Name && m.Parameters.Count == method.Parameters.Count &&
                                         m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(method.Parameters.Select(p => p.ParameterType.FullName))))
                return true;
            try { current = current.BaseType == null ? null : current.BaseType.Resolve(); }
            catch (AssemblyResolutionException) { return false; }
        }
        return false;
    }
}
