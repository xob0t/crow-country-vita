using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

// The PS Vita's CPU clock. The player's start applies the project's power mode (C), which sets the
// ARM cores to 333 MHz, whatever was set before (an overclocking plugin's 500, say). The CrowClock
// plugin (Media/Plugins/CrowClock.suprx, tools/downgrade/native/clock) calls the system's
// scePowerSetArmClockFrequency, which the SDK's stubs lack. A background thread checks the clock
// every 2 s and sets it again when it is below the target (after a suspend, or a power mode change).
//   -crow-cpu-mhz <n>   the ARM clock to keep, in MHz (default 444, the highest an application may
//                       set: 500 fails with 0x802B0000 even with PSVshell, and falls back to 444;
//                       0 leaves the power mode's 333).
// The CLOCK log lines give the clock before and after each change.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Clock
    {
        const int DefaultMhz = 444;
        static int target;

        [DllImport("CrowClock")] static extern int CrowClockSetArm(int mhz);
        [DllImport("CrowClock")] static extern int CrowClockGetArm();

        // From Diag.Install (a RuntimeInitializeOnLoadMethod: the build lists those, so quickcode can't add one).
        internal static void Install()
        {
            if (Application.platform != RuntimePlatform.PSP2) return;
            string[] args = ProbeFiles.Args;
            target = DefaultMhz;
            int index = Array.IndexOf(args, "-crow-cpu-mhz");
            int value;
            if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                target = value;
            if (target <= 0) return;
            if (!Apply("start")) return;
            var thread = new Thread(Keep) { IsBackground = true, Name = "CrowClock" };
            thread.Start();
        }

        static void Keep()
        {
            while (true)
            {
                Thread.Sleep(2000);
                if (!Apply(null)) return;
            }
        }

        // Sets the clock when it is below the target; false when the plugin is missing or the
        // system doesn't raise the clock.
        static bool Apply(string why)
        {
            try
            {
                int before = CrowClockGetArm();
                if (before >= target)
                {
                    if (why != null) Debug.Log("CLOCK " + why + ": ARM " + before + " MHz, target " + target);
                    return true;
                }
                int result = CrowClockSetArm(target);
                int after = CrowClockGetArm();
                Debug.Log("CLOCK " + (why ?? "reset") + ": ARM " + before + " -> " + after + " MHz (target " + target
                    + (result != 0 ? ", error 0x" + result.ToString("X8") : "") + ")");
                // A target the system refuses would log every 2 s: keep what it allows, or stop.
                if (after <= before)
                {
                    // An application can set at most 444 (0x802B0000 above it); 500 needs a kernel plugin.
                    if (target <= DefaultMhz) return false;
                    target = DefaultMhz;
                    return Apply(why);
                }
                if (after < target) target = after;
                return true;
            }
            catch (Exception e)
            {
                Debug.Log("CLOCK: no CrowClock plugin (" + e.GetType().Name + ": " + e.Message + ")");
                return false;
            }
        }
    }
}
