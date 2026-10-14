using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Single game methods that call CrowCompat rules: the camera's post-processing chain, Rewired's platform.
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

    // Rewired's InputManager (compiled into the game's firstpass assembly) sets its platform from
    // the build target's defines, so the game's says Windows (Platform 1) and Mono. On the PS Vita
    // Rewired then matched the pad against the Windows entries of its controller database, found
    // nothing, and left it unmapped: no button worked, and the game kept its keyboard glyphs. Each
    // constant stored to platform or scriptingBackend goes through CrowCompat RewiredPlatform, which
    // answers PSVita and IL2CPP there; the database's PS Vita entries match the pad ("PS Vita").
    static int PatchRewiredPlatform(ModuleDefinition module)
    {
        TypeDefinition type = GameType(module, "Rewired.InputManager");
        if (type == null) return 0;
        MethodDefinition detect = type.Methods.Single(m => m.Name == "DetectPlatform");
        ILProcessor il = detect.Body.GetILProcessor();
        int count = 0;
        foreach (Instruction store in detect.Body.Instructions.ToArray())
        {
            var field = store.Operand as FieldReference;
            if (store.OpCode != OpCodes.Stfld || field == null || (field.Name != "platform" && field.Name != "scriptingBackend")) continue;
            string name = field.Name == "platform" ? "Platform" : "ScriptingBackend";
            il.InsertBefore(store, Instruction.Create(OpCodes.Call, CompatMethod(module, "RewiredPlatform", name)));
            count++;
        }
        return count;
    }

    // KeyboardOrControllerCheck.isKeyboard starts true, and the game shows keyboard prompts (D Confirm,
    // S Cancel) until a pad button goes down. Its static constructor's store goes through CrowCompat
    // RewiredPlatform.StartsWithKeyboard, which says false on the PS Vita.
    static int PatchKeyboardOrController(ModuleDefinition module)
    {
        TypeDefinition type = GameType(module, "com.sfbgames.playmaker.KeyboardOrControllerCheck");
        if (type == null) return 0;
        MethodDefinition cctor = type.Methods.SingleOrDefault(m => m.IsConstructor && m.IsStatic);
        if (cctor == null) return 0;
        ILProcessor il = cctor.Body.GetILProcessor();
        int count = 0;
        foreach (Instruction store in cctor.Body.Instructions.ToArray())
        {
            var field = store.Operand as FieldReference;
            if (store.OpCode != OpCodes.Stsfld || field == null || field.Name != "isKeyboard") continue;
            il.InsertBefore(store, Instruction.Create(OpCodes.Call, CompatMethod(module, "RewiredPlatform", "StartsWithKeyboard")));
            count++;
        }
        return count;
    }

    // MeshCombineStudio's GarbageCollectMesh is [ExecuteInEditMode] and destroys its combined mesh in
    // OnDestroy with Object.Destroy, which the editor refuses outside play mode ("Destroy may not be
    // called from edit mode!", and nothing is destroyed). The build closes every scene it writes, so
    // each build logged that once per component: 2,986 of the build's errors, all of them, in 54 area
    // scenes, which hid any real one. OnDestroy now returns at once in edit mode; players are unchanged.
    static int PatchGarbageCollectMesh(ModuleDefinition module)
    {
        TypeDefinition type = GameType(module, "MeshCombineStudio.GarbageCollectMesh");
        if (type == null) return 0;
        MethodDefinition onDestroy = type.Methods.Single(m => m.Name == "OnDestroy" && m.Parameters.Count == 0);
        var code = onDestroy.Body.Instructions;
        MethodReference isPlaying = module.ImportReference(target["UnityEngine.Application"].Methods.Single(m => m.Name == "get_isPlaying"));
        ILProcessor il = onDestroy.Body.GetILProcessor();
        Instruction first = code[0];
        il.InsertBefore(first, Instruction.Create(OpCodes.Call, isPlaying));
        il.InsertBefore(first, Instruction.Create(OpCodes.Brfalse, code.Last(i => i.OpCode == OpCodes.Ret)));
        return 1;
    }

    // The game's console mode (see CrowCompat Settings): PlayMakerFSM.Awake loads PlayMaker's globals
    // (PlayMakerGlobals.Initialize) before the FSM initializes; after that call, the first Awake sets
    //   FsmVariables.GlobalVariables.GetFsmInt("Control Mode Console").Value = Settings.ConsoleControlMode()
    // when Settings.ConsoleModePending() says so (once, and only where the mode is not 0).
    static int PatchConsoleMode(ModuleDefinition module)
    {
        TypeDefinition type = GameType(module, "PlayMakerFSM");
        if (type == null) return 0;
        MethodDefinition awake = type.Methods.Single(m => m.Name == "Awake" && m.Parameters.Count == 0);
        Instruction initialize = awake.Body.Instructions.Single(i => i.OpCode == OpCodes.Call &&
            ((MethodReference)i.Operand).DeclaringType.FullName == "PlayMakerGlobals" && ((MethodReference)i.Operand).Name == "Initialize");
        TypeDefinition variables = module.GetType("HutongGames.PlayMaker.FsmVariables");
        TypeDefinition fsmInt = module.GetType("HutongGames.PlayMaker.FsmInt");
        MethodDefinition globals = variables.Methods.Single(m => m.Name == "get_GlobalVariables");
        MethodDefinition getInt = variables.Methods.Single(m => m.Name == "GetFsmInt" && m.Parameters.Count == 1);
        MethodDefinition setValue = fsmInt.Methods.Single(m => m.Name == "set_Value" && m.Parameters.Count == 1);
        Instruction next = initialize.Next;
        ILProcessor il = awake.Body.GetILProcessor();
        foreach (Instruction instruction in new[]
        {
            Instruction.Create(OpCodes.Call, CompatMethod(module, "Settings", "ConsoleModePending")),
            Instruction.Create(OpCodes.Brfalse, next),
            Instruction.Create(OpCodes.Call, globals),
            Instruction.Create(OpCodes.Ldstr, "Control Mode Console"),
            Instruction.Create(OpCodes.Callvirt, getInt),
            Instruction.Create(OpCodes.Call, CompatMethod(module, "Settings", "ConsoleControlMode")),
            Instruction.Create(OpCodes.Callvirt, setValue),
        })
            il.InsertBefore(next, instruction);
        return 1;
    }

    // PlatformSpecificAnimation.SetToPlatform turns its animations on where onWin says so; the load of
    // onWin goes through CrowCompat Settings.PlatformAnimation(onWin, onNS), the Switch's choice on
    // the PS Vita.
    static int PatchPlatformAnimation(ModuleDefinition module)
    {
        TypeDefinition type = GameType(module, "com.sfbgames.playmaker.PlatformSpecificAnimation");
        if (type == null) return 0;
        MethodDefinition set = type.Methods.Single(m => m.Name == "SetToPlatform" && m.Parameters.Count == 0);
        FieldDefinition onNS = type.Fields.Single(f => f.Name == "onNS");
        ILProcessor il = set.Body.GetILProcessor();
        int count = 0;
        foreach (Instruction load in set.Body.Instructions.ToArray())
        {
            var field = load.Operand as FieldReference;
            if (load.OpCode != OpCodes.Ldfld || field == null || field.Name != "onWin") continue;
            Instruction after = load.Next;
            il.InsertBefore(after, Instruction.Create(OpCodes.Ldarg_0));
            il.InsertBefore(after, Instruction.Create(OpCodes.Ldfld, onNS));
            il.InsertBefore(after, Instruction.Create(OpCodes.Call, CompatMethod(module, "Settings", "PlatformAnimation")));
            count++;
        }
        return count;
    }

    // MaraLoader.InstantiateMara clones the player prefab (Object.Instantiate(original, parent)); the
    // call goes to CrowCompat MaraScene.Instantiate, which hands over the CrowMara scene's instance
    // where the build has that scene (compat/Loading.cs), and clones as before where it hasn't.
    static int PatchMaraLoader(ModuleDefinition module)
    {
        TypeDefinition type = GameType(module, "Code.com.sfbgames.crowcountry.MaraLoader");
        if (type == null) return 0;
        MethodDefinition instantiate = type.Methods.Single(m => m.Name == "InstantiateMara" && m.Parameters.Count == 0);
        int count = 0;
        foreach (Instruction call in instantiate.Body.Instructions)
        {
            var callee = call.Operand as MethodReference;
            if (call.OpCode != OpCodes.Call || callee == null || callee.Name != "Instantiate" ||
                callee.DeclaringType.FullName != "UnityEngine.Object" || callee.Parameters.Count != 2) continue;
            call.Operand = CompatMethod(module, "MaraScene", "Instantiate");
            count++;
        }
        return count;
    }

    // PlayMaker's LoadScene action loads with SceneManager.LoadScene(name, mode); the call goes to
    // CrowCompat ScenePrefetch.LoadScene, which activates the title where it was read ahead
    // during the logos (compat/Loading.cs), and loads as before otherwise.
    static int PatchLoadSceneAction(ModuleDefinition module)
    {
        TypeDefinition type = GameType(module, "HutongGames.PlayMaker.Actions.LoadScene");
        if (type == null) return 0;
        MethodDefinition load = type.Methods.Single(m => m.Name == "DoLoadSceneSync" && m.Parameters.Count == 0);
        int count = 0;
        foreach (Instruction call in load.Body.Instructions)
        {
            var callee = call.Operand as MethodReference;
            if (call.OpCode != OpCodes.Call || callee == null || callee.Name != "LoadScene" ||
                callee.DeclaringType.FullName != "UnityEngine.SceneManagement.SceneManager" || callee.Parameters.Count != 2 ||
                callee.Parameters[0].ParameterType.FullName != "System.String") continue;
            call.Operand = CompatMethod(module, "ScenePrefetch", "LoadScene");
            count++;
        }
        return count;
    }

    // typeof(T) in PlayMaker (ldtoken T; Type.GetTypeFromHandle) becomes CrowCompat TypeOf<T>.Type, a
    // static field read. IL2CPP's GetTypeFromHandle takes a lock and searches a hash table on every
    // call, and ActionData.LoadActionField, which sets each parameter of each action as an FSM is
    // read, tests the field's type against up to 30 typeof()s: at the PS Vita's speed these lookups
    // were about 30% of PlayMaker's FSM loading (Fsm.InitData) on the loading thread.
    static int CacheTypeOf(ModuleDefinition module)
    {
        if (module.Assembly.Name.Name != "PlayMaker") return 0;
        TypeDefinition typeOf = compat["CrowCompat.TypeOf`1"];
        FieldDefinition typeField = typeOf.Fields.Single(f => f.Name == "Type");
        TypeReference openTypeOf = module.ImportReference(typeOf);
        TypeReference fieldType = module.ImportReference(typeField.FieldType);
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                var code = method.Body.Instructions;
                for (int i = 0; i + 1 < code.Count; i++)
                {
                    var token = code[i].Operand as TypeReference;
                    var call = code[i + 1].Operand as MethodReference;
                    if (code[i].OpCode != OpCodes.Ldtoken || token == null || code[i + 1].OpCode != OpCodes.Call || call == null ||
                        call.Name != "GetTypeFromHandle" || call.DeclaringType.FullName != "System.Type") continue;
                    if (token.ContainsGenericParameter || token.IsByReference || token.IsPointer) continue;
                    var instance = new GenericInstanceType(openTypeOf);
                    instance.GenericArguments.Add(token);
                    code[i].OpCode = OpCodes.Ldsfld;
                    code[i].Operand = new FieldReference(typeField.Name, fieldType, instance);
                    code[i + 1].OpCode = OpCodes.Nop;
                    code[i + 1].Operand = null;
                    count++;
                }
            }
        return count;
    }
}
