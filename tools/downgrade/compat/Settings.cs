using System;
using UnityEngine;

// The game's console behaviour, which its data still has, on the PS Vita. Its build for Windows says
// "desktop" in two places that the console builds answer differently:
//   - The PlayMaker global int "Control Mode Console" (0 in PlayMakerGlobals, not saved). Above 0
//     the game is a console game: the Master Control Code FSM sets "Control Mode" to it (1
//     PlayStation, 2 Xbox, 3 keyboard) and stops looking for a keyboard or another pad, so prompts
//     and the Controls page stay the pad's; the title menu hides Quit and its cursor skips it; the
//     Graphics page hides Screen (fullscreen or windowed) and its cursor skips it. Nothing in the
//     data sets it: the console builds must have done it in code. PatchConsoleMode
//     (rewriter/Patches.cs) sets it in the first PlayMakerFSM.Awake, after PlayMaker loads its
//     globals and before any FSM starts.
//   - PlatformSpecificAnimation (926 in 16 areas, all on the infected crows' skin blobs' random
//     scale animations) keeps its animation scripts on where onWin says so, and untags their renderers
//     from MeshCombine so they stay separate meshes; the Switch (onNS, false on all of them) and
//     the PS4 leave the scripts off and the meshes to be combined. PatchPlatformAnimation sends
//     the onWin read to PlatformAnimation, which answers onNS on the PS Vita.
// Flags, on any player:
//   -crow-console <0-3>              Control Mode Console (default 1 on the PS Vita, else the game's 0)
//   -crow-platform-anim win|switch   which flag PlatformSpecificAnimation follows (default switch on
//                                    the PS Vita, else win)
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Settings
    {
        static int? consoleMode;
        static bool? switchAnimation;
        static bool consoleModeSet;

        static bool Vita { get { return Application.platform == RuntimePlatform.PSP2; } }

        // Control Mode Console's value: 0 leaves the game's (desktop) behaviour.
        public static int ConsoleControlMode()
        {
            if (consoleMode == null)
            {
                string[] args = ProbeFiles.Args;
                int index = Array.IndexOf(args, "-crow-console");
                int value;
                consoleMode = index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out value) ? value : Vita ? 1 : 0;
            }
            return consoleMode.Value;
        }

        // True once, in the first PlayMakerFSM.Awake, when Control Mode Console is to be set.
        public static bool ConsoleModePending()
        {
            if (consoleModeSet) return false;
            consoleModeSet = true;
            if (ConsoleControlMode() == 0) return false;
            Debug.Log("CROW_SETTINGS Control Mode Console " + ConsoleControlMode());
            return true;
        }

        // PlatformSpecificAnimation.SetToPlatform's onWin, with the component's onNS.
        public static bool PlatformAnimation(bool onWin, bool onNS)
        {
            if (switchAnimation == null)
            {
                string[] args = ProbeFiles.Args;
                int index = Array.IndexOf(args, "-crow-platform-anim");
                string value = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
                switchAnimation = value != null ? value == "switch" : Vita;
                if (switchAnimation.Value) Debug.Log("CROW_SETTINGS platform animations as on the Switch");
            }
            return switchAnimation.Value ? onNS : onWin;
        }
    }
}
