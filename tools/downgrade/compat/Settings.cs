using System;
using System.Globalization;
using System.Threading;
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
//   -crow-culture invariant|system|<name>  the culture numbers and dates are written and read in (default
//                                    invariant on the PS Vita, else system; a name such as ru-RU, for tests)
// The culture: IL2CPP takes the current culture from the PS Vita's system language (libil2cpp
// os/PSP2/Locale.cpp: "en-us", "ru", "de", "fr" and so on), so with a language that writes a decimal
// comma every number the game, PlayMaker, Unity and CrowCompat turned into text without a culture of
// their own came out "71,33" (the log's times and its CROW_ lines, which the tools read; 25 Oct). The
// saves don't depend on it (BinaryFormatter: numbers as bytes, the date as a file time), and the
// game's one float read from text (com.sfbgames.playmaker.ConvertStringToFloat) is invariant already.
// The PS Vita's default is the invariant culture: numbers as the console set to English (US) wrote
// them, on any language. The Load Game slots' date (ToShortDateString) is then MM/dd/yyyy (10/05/2026;
// en-US wrote 10/5/2026, Russian 05.10.2026). system keeps the language's culture, as desktop players
// keep the system's. The game's other text with numbers is ints only (its FSMs' FormatString and
// ConvertIntToString, the slot number), which every culture writes alike, and nothing in it sets a
// thread's culture (I2's AutoChangeCultureInfo is in no scene). Set from ProbeFiles.Install
// (BeforeSceneLoad, before the first scene): DefaultThreadCurrentCulture and the main thread's own.
// A thread that sets none reads DefaultThreadCurrentCulture at every use (Thread.CurrentCulture
// keeps nothing until it is set), so threads started before (the log's writer, audio, the title's
// prefetch) follow it too.
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
                consoleMode = CrowFlags.Int(ProbeFiles.Args, "-crow-console", Vita ? 1 : 0);
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
                string value = CrowFlags.Value(ProbeFiles.Args, "-crow-platform-anim");
                switchAnimation = value != null ? value == "switch" : Vita;
                if (switchAnimation.Value) Debug.Log("CROW_SETTINGS platform animations as on the Switch");
            }
            return switchAnimation.Value ? onNS : onWin;
        }

        // -crow-culture, from ProbeFiles.Install: the line to log (null on desktop players left at the system's). Never throws:
        // a name the runtime doesn't know (CultureNotFoundException) leaves the system's, with a "failed" line.
        internal static string Culture()
        {
            string value = "?", system = "?";
            try
            {
                system = CultureInfo.CurrentCulture.Name;
                if (system.Length == 0) system = "invariant";
                string given = CrowFlags.Value(ProbeFiles.Args, "-crow-culture");
                value = given ?? (Vita ? "invariant" : "system");
                if (value == "system") return given != null || Vita ? "CROW_SETTINGS culture system (" + system + "; 1.5 is " + 1.5f + ")" : null;
                CultureInfo culture = value == "invariant" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(value);
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                Thread.CurrentThread.CurrentCulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
                return "CROW_SETTINGS culture " + value + " (the system's: " + system + "; 1.5 is " + 1.5f + ")";
            }
            catch (Exception e)  // nothing was set: GetCultureInfo throws before the setters
            {
                return "CROW_SETTINGS culture " + value + " failed (" + e.GetType().Name + ": " + e.Message.Replace("\r", "").Replace('\n', ' ') + "), the system's (" + system + ") kept";
            }
        }
    }
}
