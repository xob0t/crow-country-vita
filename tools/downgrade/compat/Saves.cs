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
        // written once in the saves folder plus a stopwatch since. The console's time zone is out of
        // reach, so Now is UTC too (with Kind Utc: CrowCountrySave's ToFileTimeUtc keeps it as it is).
        // Other players read the system clock as before.
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
        static bool clockFailed;

        static DateTime VitaUtcNow()
        {
            lock (clockGate)
            {
                if (sinceStart == null && !clockFailed)
                {
                    try
                    {
                        Directory.CreateDirectory(VitaFolder);
                        string file = VitaFolder + "/clock";
                        File.WriteAllBytes(file, new byte[1]);
                        start = DateTime.SpecifyKind(File.GetLastWriteTimeUtc(file), DateTimeKind.Utc);
                        sinceStart = System.Diagnostics.Stopwatch.StartNew();
                    }
                    catch (Exception e)
                    {
                        clockFailed = true;
                        Debug.Log("CROW_CLOCK no file time for the date (" + e.GetType().Name + ": " + e.Message + ")");
                    }
                }
                return sinceStart == null ? DateTime.UtcNow : start + sinceStart.Elapsed;
            }
        }
#endif
    }
}
