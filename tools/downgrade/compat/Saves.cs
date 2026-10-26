using System;
using System.IO;
using UnityEngine;

// Where the game keeps files. CrowCountrySave writes its slots to "Saves/", relative to the working
// directory: the install folder on desktop players, read-only app data on the PS Vita
// ("Access to the path "Saves" is denied"). SmoothSave, I2 and PlayMaker's screenshot actions use
// Application.persistentDataPath, which the PS Vita player leaves empty. The rewriter
// (Redirect.cs) sends CrowCountrySave's "Saves" literals and every persistentDataPath read here.
// Desktop players keep the game's own paths. Also the clock save slots are dated by (Now, UtcNow).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class Saves
    {
        const string VitaFolder = "ux0:data/CrowCountry";

        static bool Desktop
        {
            get
            {
                switch (Application.platform)
                {
                    case RuntimePlatform.WindowsPlayer:
                    case RuntimePlatform.OSXPlayer:
                    case RuntimePlatform.LinuxPlayer:
                    case RuntimePlatform.WindowsEditor:
                    case RuntimePlatform.OSXEditor:
                    case RuntimePlatform.LinuxEditor:
                        return true;
                    default:
                        return false;
                }
            }
        }

        // The "Saves" literal.
        public static string Folder()
        {
            if (Desktop) return "Saves";
            string folder = PersistentDataPath() + "/Saves";
            Directory.CreateDirectory(folder);
            return folder;
        }

        // The "Saves/" literal.
        public static string Prefix()
        {
            return Folder() + "/";
        }

        public static string PersistentDataPath()
        {
            string path = Application.persistentDataPath;
            if (Desktop || !string.IsNullOrEmpty(path)) return path;
            Directory.CreateDirectory(VitaFolder);
            return VitaFolder;
        }

        // DateTime.Now and DateTime.UtcNow: the rewriter sends every read in the game's assemblies here.
        // The PS Vita's IL2CPP leaves GetSystemTimeAsFileTime unimplemented (libil2cpp os/PSP2/Time.cpp),
        // so both said 1 January 1601 there, and every save slot showed "1/1/1601". A file's
        // modification time is right on the console (sceIoGetstat, UTC), so the clock is a file
        // written in the saves folder plus a stopwatch since. The console's time zone is out of
        // reach, so Now is UTC too (with Kind Utc: CrowCountrySave's ToFileTimeUtc keeps it as it is).
        // Other players read the system clock as before.
        // The stopwatch is the process's time (IL2CPP's PSP2 GetTicks100NanosecondsMonotonic is
        // sceKernelGetProcessTime), which stands still while the console sleeps: on it alone a save after
        // a night's sleep would be dated the evening before. The file is written again on each
        // call that isn't part of a burst (calls are rare: the log's start, each save, the Driving
        // Intro's year), and when the file's time is ahead of the clock by over 2 s the clock moves on
        // to it. It only moves forward, so the slots' times keep their order (Continue takes the
        // latest). The re-read doesn't wait on the process's time, which the sleep doesn't add to (a save
        // soon after waking would keep the old date).
        public static DateTime Now()
        {
#if CROW_VITA
            return VitaUtcNow();
#else
            return DateTime.Now;
#endif
        }

        public static DateTime UtcNow()
        {
#if CROW_VITA
            return VitaUtcNow();
#else
            return DateTime.UtcNow;
#endif
        }

#if CROW_VITA
        static readonly object clockGate = new object();
        static System.Diagnostics.Stopwatch sinceStart;
        static DateTime start;
        static TimeSpan lastRead, lastCall;
        static bool clockFailed, rereadFailed;
        // A call within Burst of the process's time after the one before is part of a burst (a save
        // asks Now and UtcNow together; a caller that asks every frame): it reads the file only
        // ReadEvery after the last read, so such a caller writes the card at most every 10 s. A call
        // after a pause, the sleep's included (the player has to wake the console and get to a save),
        // reads it at once.
        static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(10), Burst = TimeSpan.FromSeconds(1), Slack = TimeSpan.FromSeconds(2);

        // The console's clock (UTC), from a file's modification time.
        static DateTime FileTime()
        {
            Directory.CreateDirectory(VitaFolder);
            string file = VitaFolder + "/clock";
            File.WriteAllBytes(file, new byte[1]);
            return DateTime.SpecifyKind(File.GetLastWriteTimeUtc(file), DateTimeKind.Utc);
        }

        static DateTime VitaUtcNow()
        {
            lock (clockGate)
            {
                if (sinceStart == null && !clockFailed)
                {
                    try
                    {
                        start = FileTime();
                        sinceStart = System.Diagnostics.Stopwatch.StartNew();
                        lastRead = lastCall = TimeSpan.Zero;
                    }
                    catch (Exception e)
                    {
                        clockFailed = true;
                        Debug.Log("CROW_CLOCK no file time for the date (" + e.GetType().Name + ": " + e.Message + ")");
                    }
                }
                if (sinceStart == null) return DateTime.UtcNow;
                TimeSpan elapsed = sinceStart.Elapsed;
                bool apart = elapsed - lastCall >= Burst;
                lastCall = elapsed;
                if (apart || elapsed - lastRead >= ReadEvery)
                {
                    lastRead = elapsed;
                    try
                    {
                        TimeSpan behind = FileTime() - (start + elapsed);
                        if (behind > Slack)
                        {
                            start += behind;  // the console slept: the stopwatch left that time out
                            Debug.Log("CROW_CLOCK " + behind.TotalSeconds.ToString("F0") + " s the stopwatch missed (the console slept): clock moved on");
                        }
                    }
                    catch (Exception e)
                    {
                        // The stopwatch's time stands; the next call apart from this one, or after ReadEvery, tries again.
                        if (!rereadFailed) Debug.Log("CROW_CLOCK file time failed (" + e.GetType().Name + ": " + e.Message + "): the stopwatch's time");
                        rereadFailed = true;
                    }
                }
                return start + sinceStart.Elapsed;
            }
        }
#endif
    }
}
