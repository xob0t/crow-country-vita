using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

// The PS Vita's CPU and GPU clocks. The player's start applies the project's power mode (C), which sets the
// ARM cores to 333 MHz, whatever was set before (an overclocking plugin's 500, say). The CrowClock
// plugin (Media/Plugins/CrowClock.suprx, tools/downgrade/native/clock) calls the system's
// scePowerSetArmClockFrequency, which the SDK's stubs lack. A background thread checks the clock
// every 2 s and sets it again when it is below the target (after a suspend, or a power mode change).
//   -crow-cpu-mhz <n>   the ARM clock to keep, in MHz (default 444, the highest an application may
//                       set: 500 fails with 0x802B0000 even with PSVshell, and falls back to 444;
//                       0 leaves the power mode's 333).
//   -crow-gpu-mhz <n>   the GPU core clock to keep: 41, 55, 111, 166 or 222 MHz, the values the system
//                       takes from an application (scePowerSetGpuClockFrequency, NID 0x717DB06C in
//                       vita-headers' db). Default 222: the power mode leaves 166, and gameplay is
//                       GPU-bound there (Roadside 63.5 -> 49.5 ms a frame at 222, the picture the
//                       same); 0 leaves the power mode's.
// The CLOCK log lines give the clock before and after each change.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Clock
    {
        const int DefaultMhz = 444;
        const int DefaultGpuMhz = 222;
        static readonly int[] GpuSteps = { 41, 55, 111, 166, 222 };
        static int target, gpuTarget;

        [DllImport("CrowClock")] static extern int CrowClockSetArm(int mhz);
        [DllImport("CrowClock")] static extern int CrowClockGetArm();
        [DllImport("CrowClock")] static extern int CrowClockSetGpu(int mhz);
        [DllImport("CrowClock")] static extern int CrowClockGetGpu();

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
            gpuTarget = DefaultGpuMhz;
            index = Array.IndexOf(args, "-crow-gpu-mhz");
            if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                gpuTarget = value;
            if (gpuTarget > 0 && Array.IndexOf(GpuSteps, gpuTarget) < 0)
            {
                Debug.Log("CLOCK: -crow-gpu-mhz takes 41, 55, 111, 166 or 222, not " + gpuTarget);
                gpuTarget = 0;
            }
            bool arm = target > 0 && Apply("start");
            bool gpu = gpuTarget > 0 && ApplyGpu("start");
            if (!arm && !gpu) return;
            var thread = new Thread(() => Keep(arm, gpu)) { IsBackground = true, Name = "CrowClock" };
            thread.Start();
        }

        static void Keep(bool arm, bool gpu)
        {
            while (arm || gpu)
            {
                Thread.Sleep(2000);
                if (arm) arm = Apply(null);
                if (gpu) gpu = ApplyGpu(null);
            }
        }

        // The GPU clock, at the target (set when different, logged when it changes); false when the
        // plugin lacks the function or the system refuses the value.
        static bool ApplyGpu(string why)
        {
            try
            {
                int before = CrowClockGetGpu();
                if (before == gpuTarget)
                {
                    if (why != null) Debug.Log("CLOCK " + why + ": GPU " + before + " MHz, target " + gpuTarget);
                    return true;
                }
                int result = CrowClockSetGpu(gpuTarget);
                int after = CrowClockGetGpu();
                Debug.Log("CLOCK " + (why ?? "reset") + ": GPU " + before + " -> " + after + " MHz (target " + gpuTarget
                    + (result != 0 ? ", error 0x" + result.ToString("X8") : "") + ")");
                return result == 0 && after == gpuTarget;
            }
            catch (Exception e)
            {
                Debug.Log("CLOCK: no GPU clock in the CrowClock plugin (" + e.GetType().Name + ": " + e.Message + ")");
                return false;
            }
        }

        // For the probe's heartbeat-style lines: "arm/gpu" in MHz, or null without the plugin.
        internal static string Describe()
        {
            if (Application.platform != RuntimePlatform.PSP2) return null;
            try { return DescribeNative(); }
            catch (Exception) { return null; }
        }

        // Apart, as Mono looks the plugin up when it compiles a caller (see Audio.cs's Threads.SelfNative).
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string DescribeNative()
        {
            return CrowClockGetArm() + "/" + CrowClockGetGpu();
        }

        // -crow-cycle's gpu=<mhz>: one change of the GPU clock, kept from then on.
        internal static void SetGpu(int mhz)
        {
            if (Application.platform != RuntimePlatform.PSP2 || Array.IndexOf(GpuSteps, mhz) < 0) return;
            bool keeping = gpuTarget > 0;
            gpuTarget = mhz;
            if (ApplyGpu("change") && !keeping)
                new Thread(() => Keep(false, true)) { IsBackground = true, Name = "CrowGpuClock" }.Start();
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
