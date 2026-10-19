using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

// The PS Vita's CPU, GPU, bus and GPU crossbar clocks. The player's start applies the project's power mode (C),
// which sets the ARM cores to 333 MHz, whatever was set before (an overclocking plugin's 500, say). The CrowClock
// plugin (Media/Plugins/CrowClock.suprx, tools/downgrade/native/clock) calls the system's
// scePowerSet*ClockFrequency, which the SDK's stubs lack. A background thread checks the clocks
// every 2 s and sets them again when they are off target (after a suspend, or a power mode change).
//   -crow-cpu-mhz <n>   the ARM clock to keep, in MHz (default 444, the highest an application may
//                       set: 500 fails with 0x802B0000 even with PSVshell, and falls back to 444;
//                       0 leaves the power mode's 333). Only raised: a higher clock (PSVshell's 500) is left.
//   -crow-gpu-mhz <n>   the GPU core clock to keep: 41, 55, 111, 166 or 222 MHz, the values the system
//                       takes from an application (scePowerSetGpuClockFrequency, NID 0x717DB06C in
//                       vita-headers' db). Default 222: the power mode leaves 166, and gameplay is
//                       GPU-bound there (Roadside 63.5 -> 49.5 ms a frame at 222, the picture the
//                       same); 0 leaves the power mode's.
//   -crow-bus-mhz <n>   the bus clock to keep, at most 222 MHz (scePowerSetBusClockFrequency, 0xB8D7B3FB);
//                       default 0, left as it is.
//   -crow-xbar-mhz <n>  the GPU crossbar clock to keep, 111 or 166 MHz (scePowerSetGpuXbarClockFrequency,
//                       0xA7739DBE); default 0, left as it is.
// -crow-cycle, -crow-fight-cycle and -crow-ab take gpu=<mhz> (Perf.cs), cpumhz=<mhz> (the ARM clock set to
// exactly that, lower included, at most 444), busmhz=<mhz> and xbarmhz=<mhz> (0 stops keeping).
// The CLOCK log lines give the clocks at start and before and after each change.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Clock
    {
        const int DefaultMhz = 444;
        const int DefaultGpuMhz = 222;
        const int MaxBusMhz = 222;
        static readonly int[] GpuSteps = { 41, 55, 111, 166, 222 };
        static readonly int[] XbarSteps = { 111, 166 };
        // The clocks set to a fixed value: GPU, bus, crossbar.
        const int Gpu = 0, Bus = 1, Xbar = 2;
        static readonly string[] Names = { "GPU", "bus", "crossbar" };
        static readonly int[] fixedTarget = new int[3];
        static readonly bool[] keepFixed = new bool[3];
        static int target;
        static bool keepArm, armExact;
        static readonly object gate = new object();
        static Thread keeper;

        [DllImport("CrowClock")] static extern int CrowClockSetArm(int mhz);
        [DllImport("CrowClock")] static extern int CrowClockGetArm();
        [DllImport("CrowClock")] static extern int CrowClockSetGpu(int mhz);
        [DllImport("CrowClock")] static extern int CrowClockGetGpu();
        [DllImport("CrowClock")] static extern int CrowClockSetBus(int mhz);
        [DllImport("CrowClock")] static extern int CrowClockGetBus();
        [DllImport("CrowClock")] static extern int CrowClockSetXbar(int mhz);
        [DllImport("CrowClock")] static extern int CrowClockGetXbar();

        // From Diag.Install (a RuntimeInitializeOnLoadMethod: the build lists those, so quickcode can't add one).
        internal static void Install()
        {
            if (Application.platform != RuntimePlatform.PSP2) return;
            string[] args = ProbeFiles.Args;
            target = Flag(args, "-crow-cpu-mhz", DefaultMhz);
            fixedTarget[Gpu] = Flag(args, "-crow-gpu-mhz", DefaultGpuMhz);
            fixedTarget[Bus] = Flag(args, "-crow-bus-mhz", 0);
            fixedTarget[Xbar] = Flag(args, "-crow-xbar-mhz", 0);
            for (int which = Gpu; which <= Xbar; which++)
                if (fixedTarget[which] > 0 && !Allowed(which, fixedTarget[which]))
                {
                    Debug.Log("CLOCK: " + Names[which] + " " + fixedTarget[which] + " MHz is not allowed (" + Steps(which) + ")");
                    fixedTarget[which] = 0;
                }
            try { Debug.Log("CLOCK start: bus " + CrowClockGetBus() + " MHz, crossbar " + CrowClockGetXbar() + " MHz"); }
            catch (Exception e) { Debug.Log("CLOCK: no bus clock in the CrowClock plugin (" + e.GetType().Name + ")"); }
            lock (gate)
            {
                keepArm = target > 0 && Apply("start");
                for (int which = Gpu; which <= Xbar; which++)
                    keepFixed[which] = fixedTarget[which] > 0 && ApplyFixed(which, "start");
            }
            EnsureKeeper();
        }

        static int Flag(string[] args, string flag, int fallback)
        {
            int index = Array.IndexOf(args, flag);
            int value;
            return index >= 0 && index + 1 < args.Length &&
                   int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        static bool Allowed(int which, int mhz)
        {
            if (which == Gpu) return Array.IndexOf(GpuSteps, mhz) >= 0;
            if (which == Xbar) return Array.IndexOf(XbarSteps, mhz) >= 0;
            return mhz > 0 && mhz <= MaxBusMhz;
        }

        static string Steps(int which)
        {
            return which == Gpu ? "41, 55, 111, 166 or 222" : which == Xbar ? "111 or 166" : "at most 222";
        }

        static void EnsureKeeper()
        {
            lock (gate)
            {
                if (keeper != null || !(keepArm || keepFixed[Gpu] || keepFixed[Bus] || keepFixed[Xbar])) return;
                keeper = new Thread(Keep) { IsBackground = true, Name = "CrowClock" };
                keeper.Start();
            }
        }

        static void Keep()
        {
            while (true)
            {
                Thread.Sleep(2000);
                lock (gate)
                {
                    if (keepArm) keepArm = Apply(null);
                    for (int which = Gpu; which <= Xbar; which++)
                        if (keepFixed[which]) keepFixed[which] = ApplyFixed(which, null);
                }
            }
        }

        static int GetFixed(int which)
        {
            return which == Gpu ? CrowClockGetGpu() : which == Bus ? CrowClockGetBus() : CrowClockGetXbar();
        }

        static int SetFixed(int which, int mhz)
        {
            return which == Gpu ? CrowClockSetGpu(mhz) : which == Bus ? CrowClockSetBus(mhz) : CrowClockSetXbar(mhz);
        }

        // A fixed clock at its target (set when different, logged when it changes); false when the
        // plugin lacks the function or the system refuses the value.
        static bool ApplyFixed(int which, string why)
        {
            int goal = fixedTarget[which];
            try
            {
                int before = GetFixed(which);
                if (before == goal)
                {
                    if (why != null) Debug.Log("CLOCK " + why + ": " + Names[which] + " " + before + " MHz, target " + goal);
                    return true;
                }
                int result = SetFixed(which, goal);
                int after = GetFixed(which);
                Debug.Log("CLOCK " + (why ?? "reset") + ": " + Names[which] + " " + before + " -> " + after + " MHz (target " + goal
                    + (result != 0 ? ", error 0x" + result.ToString("X8") : "") + ")");
                return result == 0 && after == goal;
            }
            catch (Exception e)
            {
                Debug.Log("CLOCK: no " + Names[which] + " clock in the CrowClock plugin (" + e.GetType().Name + ": " + e.Message + ")");
                return false;
            }
        }

        // For the probe's heartbeat-style lines: "arm/gpu/bus/xbar" in MHz, or null without the plugin.
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
            string clocks = CrowClockGetArm() + "/" + CrowClockGetGpu();
            try { clocks += "/" + CrowClockGetBus() + "/" + CrowClockGetXbar(); }
            catch (Exception) { }
            return clocks;
        }

        // -crow-cycle's gpu=<mhz>: one change of the GPU clock, kept from then on.
        internal static void SetGpu(int mhz)
        {
            SetKept(Gpu, mhz);
        }

        static void SetKept(int which, int mhz)
        {
            if (Application.platform != RuntimePlatform.PSP2) return;
            lock (gate)
            {
                if (mhz == 0)
                {
                    keepFixed[which] = false;
                    Debug.Log("CLOCK change: " + Names[which] + " no longer kept");
                    return;
                }
                if (!Allowed(which, mhz))
                {
                    Debug.Log("CLOCK: " + Names[which] + " " + mhz + " MHz is not allowed (" + Steps(which) + ")");
                    return;
                }
                fixedTarget[which] = mhz;
                keepFixed[which] = ApplyFixed(which, "change");
            }
            EnsureKeeper();
        }

        // The cycles' clock keys (Quality.Change): cpumhz=, busmhz=, xbarmhz=; false for other keys.
        internal static bool Change(string key, string value)
        {
            if (key != "cpumhz" && key != "busmhz" && key != "xbarmhz") return false;
            int mhz;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out mhz) || mhz < 0)
            {
                Debug.Log("CLOCK: " + key + " takes a number of MHz, not " + value);
                return true;
            }
            if (key == "busmhz") SetKept(Bus, mhz);
            else if (key == "xbarmhz") SetKept(Xbar, mhz);
            else if (Application.platform == RuntimePlatform.PSP2)
            {
                if (mhz > DefaultMhz)
                {
                    Debug.Log("CLOCK: cpumhz is at most " + DefaultMhz + ", not " + mhz);
                    return true;
                }
                lock (gate)
                {
                    target = mhz;
                    armExact = true;
                    keepArm = mhz > 0 && Apply("change");
                }
                EnsureKeeper();
            }
            return true;
        }

        // Sets the clock when it is below the target (or, after cpumhz=, when it is anything else); false
        // when the plugin is missing or the system doesn't take the clock.
        static bool Apply(string why)
        {
            try
            {
                int before = CrowClockGetArm();
                if (before == target || (before > target && !armExact))
                {
                    if (why != null) Debug.Log("CLOCK " + why + ": ARM " + before + " MHz, target " + target);
                    return true;
                }
                int result = CrowClockSetArm(target);
                int after = CrowClockGetArm();
                Debug.Log("CLOCK " + (why ?? "reset") + ": ARM " + before + " -> " + after + " MHz (target " + target
                    + (result != 0 ? ", error 0x" + result.ToString("X8") : "") + ")");
                // A clock set exactly stops being kept when the system (or another plugin) holds another.
                if (armExact) return result == 0 && after == target;
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
