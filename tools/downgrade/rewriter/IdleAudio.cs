using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Idle AudioSources off: the calls CrowCompat IdleAudio (compat/IdleAudio.cs) needs to see. Every
// AudioSource.Play, PlayDelayed, PlayScheduled, PlayOneShot and UnPause call becomes a call of
// IdleAudio's static method of the same name with the source as its first argument (it enables the
// source again if IdleAudio turned it off, then plays), and every Behaviour.enabled read and write
// goes through IdleAudio.GetEnabled and SetEnabled (a source IdleAudio turned off still reads as
// enabled; a write makes it the game's again). Calls through a constrained. prefix (generic code)
// are left as they are.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    static readonly HashSet<string> AudioPlayCalls = new HashSet<string> { "Play", "PlayDelayed", "PlayScheduled", "PlayOneShot", "UnPause" };

    static int RouteAudioSourcePlays(ModuleDefinition module)
    {
        TypeDefinition idle = compat["CrowCompat.IdleAudio"];
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    var called = instruction.Operand as MethodReference;
                    if (called == null || !called.HasThis || (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)) continue;
                    if (instruction.Previous != null && instruction.Previous.OpCode == OpCodes.Constrained) continue;
                    string owner = called.DeclaringType.FullName;
                    MethodDefinition target = null;
                    if (owner == "UnityEngine.AudioSource" && AudioPlayCalls.Contains(called.Name))
                        target = idle.Methods.SingleOrDefault(m => m.IsStatic && m.IsPublic && m.Name == called.Name &&
                            m.Parameters.Count == called.Parameters.Count + 1 &&
                            m.Parameters.Skip(1).Select(p => p.ParameterType.FullName).SequenceEqual(called.Parameters.Select(p => p.ParameterType.FullName)));
                    else if (owner == "UnityEngine.Behaviour" && (called.Name == "get_enabled" || called.Name == "set_enabled"))
                        target = idle.Methods.Single(m => m.Name == (called.Name == "get_enabled" ? "GetEnabled" : "SetEnabled"));
                    else continue;
                    if (target == null) throw new System.Exception("IdleAudio has no " + called.Name + " for " + called.FullName);
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = module.ImportReference(target);
                    Count("idle audio " + (owner == "UnityEngine.AudioSource" ? "AudioSource." + called.Name : "Behaviour.enabled"), 1);
                    count++;
                }
        return count;
    }
}
