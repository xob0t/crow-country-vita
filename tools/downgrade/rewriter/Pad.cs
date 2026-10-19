using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The PS Vita's pad reader (compat/Pad.cs says what it serves and when):
//   PadQueries: in the game's assemblies, every call of Rewired.Player's button and axis queries (GetButton,
//     GetButtonDown, GetButtonUp, GetNegativeButton, GetNegativeButtonDown, GetNegativeButtonUp, GetAxis and
//     GetAxisRaw, by name or id; GetAnyButton and the like, no argument) goes to a static helper of a class
//     the module gets, CrowPadCalls, one per query, such as
//       static bool GetButtonDownByName(Player player, string action)
//       { bool result; if (Pad.Button(player, action, 1, out result)) return result; return player.GetButtonDown(action); }
//     Player's other queries (timed, double and repeated presses, previous values, 2D axes) stay Rewired's
//     and are listed; no FSM uses them.
//   PadMaps: after every call in the game's assemblies that changes a player's maps (Player.ControllerHelper.
//     MapHelper's Load*, Remove*, Add*, Clear* and Set*; ControllerMap.set_enabled), Pad.MapsChanged().
//   PadUpdate: Rewired's InputManager_Base.Update starts with
//       if (Pad.Update(this, ReInput.rewiredInputManager)) return;
//     and calls Pad.AfterRewired(this, ReInput.rewiredInputManager) before each return; FixedUpdate and
//     LateUpdate start with if (Pad.Serving) return; all ahead of CutsRewired's RewiredProbe.Begin, so a
//     skipped update isn't timed.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    static readonly string[] PadButtonQueries = { "GetButton", "GetButtonDown", "GetButtonUp", "GetNegativeButton", "GetNegativeButtonDown", "GetNegativeButtonUp" };
    static readonly string[] PadAxisQueries = { "GetAxis", "GetAxisRaw" };
    static readonly string[] PadAnyQueries = { "GetAnyButton", "GetAnyButtonDown", "GetAnyButtonUp", "GetAnyNegativeButton", "GetAnyNegativeButtonDown", "GetAnyNegativeButtonUp" };

    static int PadQueries(ModuleDefinition module)
    {
        TypeDefinition calls = null;
        var helpers = new Dictionary<string, MethodDefinition>();
        var left = new SortedSet<string>();
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types).ToArray())
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    var called = instruction.Operand as MethodReference;
                    if (called == null || !called.HasThis || (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)) continue;
                    if (called.DeclaringType.FullName != "Rewired.Player" || !called.Name.StartsWith("Get")) continue;
                    string argument = called.Parameters.Count == 1 ? called.Parameters[0].ParameterType.FullName : called.Parameters.Count == 0 ? "" : null;
                    string target = null;
                    int kind = Array.IndexOf(PadButtonQueries, called.Name);
                    if (kind >= 0 && argument == "System.String") target = "Button";
                    else if (kind >= 0 && argument == "System.Int32") target = "ButtonId";
                    else if ((kind = Array.IndexOf(PadAxisQueries, called.Name)) >= 0 && argument == "System.String") target = "Axis";
                    else if (kind >= 0 && argument == "System.Int32") target = "AxisId";
                    else if ((kind = Array.IndexOf(PadAnyQueries, called.Name)) >= 0 && argument == "") target = "Any";
                    if (target == null)
                    {
                        left.Add(called.Name + "(" + string.Join(",", called.Parameters.Select(p => p.ParameterType.Name)) + ")");
                        continue;
                    }
                    if (target.StartsWith("Axis")) kind += 6;
                    MethodDefinition helper;
                    if (!helpers.TryGetValue(called.FullName, out helper))
                    {
                        if (calls == null)
                        {
                            // System.Object as the module's own classes name it (retargeted; TypeSystem's may be netstandard's).
                            TypeReference objectType = AllTypes(module.Types).Select(t => t.BaseType).First(b => b != null && b.FullName == "System.Object");
                            calls = new TypeDefinition("", "CrowPadCalls", TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed |
                                TypeAttributes.BeforeFieldInit, objectType);
                            module.Types.Add(calls);
                        }
                        helpers[called.FullName] = helper = PadHelper(module, calls, called, target, kind);
                    }
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = helper;
                    Count("pad query " + called.Name, 1);
                    count++;
                }
        if (left.Count > 0) Console.WriteLine("Pad: Rewired.Player queries left to Rewired in " + module.Name + ": " + string.Join(", ", left));
        return count;
    }

    // static <bool|float> <query>By<Name|Id>(Player player[, <string|int> action]): Pad.<target> first, else the query.
    static MethodDefinition PadHelper(ModuleDefinition module, TypeDefinition owner, MethodReference query, string target, int kind)
    {
        bool argument = query.Parameters.Count == 1;
        string suffix = !argument ? "" : query.Parameters[0].ParameterType.FullName == "System.String" ? "ByName" : "ById";
        var helper = new MethodDefinition(query.Name + suffix, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, query.ReturnType);
        helper.Parameters.Add(new ParameterDefinition("player", ParameterAttributes.None, query.DeclaringType));
        if (argument) helper.Parameters.Add(new ParameterDefinition("action", ParameterAttributes.None, query.Parameters[0].ParameterType));
        var result = new VariableDefinition(query.ReturnType);
        helper.Body.Variables.Add(result);
        helper.Body.InitLocals = true;
        ILProcessor il = helper.Body.GetILProcessor();
        Instruction rewired = Instruction.Create(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        if (argument) il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4, kind);
        il.Emit(OpCodes.Ldloca, result);
        il.Emit(OpCodes.Call, CompatMethod(module, "Pad", target));
        il.Emit(OpCodes.Brfalse, rewired);
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ret);
        il.Append(rewired);
        if (argument) il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, query);
        il.Emit(OpCodes.Ret);
        owner.Methods.Add(helper);
        return helper;
    }

    static readonly string[] PadMapChanges = { "Load", "Remove", "Add", "Clear", "Set" };

    static int PadMaps(ModuleDefinition module)
    {
        var sites = new List<KeyValuePair<MethodDefinition, Instruction>>();
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    var called = instruction.Operand as MethodReference;
                    if (called == null || (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)) continue;
                    string owner = called.DeclaringType.FullName;
                    if ((owner == "Rewired.Player/ControllerHelper/MapHelper" && PadMapChanges.Any(prefix => called.Name.StartsWith(prefix))) ||
                        (owner == "Rewired.ControllerMap" && called.Name == "set_enabled"))
                    {
                        sites.Add(new KeyValuePair<MethodDefinition, Instruction>(method, instruction));
                        Count("pad maps " + called.Name, 1);
                    }
                }
        foreach (KeyValuePair<MethodDefinition, Instruction> site in sites)
            InsertAfter(site.Key.Body.GetILProcessor(), site.Value, Instruction.Create(OpCodes.Call, CompatMethod(module, "Pad", "MapsChanged")));
        return sites.Count;
    }

    static int PadUpdate(ModuleDefinition module)
    {
        TypeDefinition manager = GameType(module, "Rewired.InputManager_Base");
        if (manager == null) return 0;
        MethodReference active = module.GetType("Rewired.ReInput").Properties.Single(p => p.Name == "rewiredInputManager").GetMethod;
        MethodDefinition update = manager.Methods.Single(m => m.Name == "Update" && m.Parameters.Count == 0 && m.HasBody);
        BeforeRets(update, () => new[]
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, active),
            Instruction.Create(OpCodes.Call, CompatMethod(module, "Pad", "AfterRewired")),
        });
        Instruction first = update.Body.Instructions[0];
        InsertBefore(update.Body.GetILProcessor(), first,
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, active),
            Instruction.Create(OpCodes.Call, CompatMethod(module, "Pad", "Update")),
            Instruction.Create(OpCodes.Brfalse, first),
            Instruction.Create(OpCodes.Ret));
        FieldReference serving = CompatField(module, "Pad", "Serving");
        foreach (string name in new[] { "FixedUpdate", "LateUpdate" })
        {
            MethodDefinition method = manager.Methods.Single(m => m.Name == name && m.Parameters.Count == 0 && m.HasBody);
            Instruction start = method.Body.Instructions[0];
            InsertBefore(method.Body.GetILProcessor(), start,
                Instruction.Create(OpCodes.Ldsfld, serving),
                Instruction.Create(OpCodes.Brfalse, start),
                Instruction.Create(OpCodes.Ret));
        }
        return 3;
    }
}
