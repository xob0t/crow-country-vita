using System.IO;
using UnityEngine;

// Where the game keeps files. CrowCountrySave writes its slots to "Saves/", relative to the working
// directory: the install folder on desktop players, read-only app data on the PS Vita
// ("Access to the path "Saves" is denied"). SmoothSave, I2 and PlayMaker's screenshot actions use
// Application.persistentDataPath, which the PS Vita player leaves empty. The rewriter
// (Redirect.cs) sends CrowCountrySave's "Saves" literals and every persistentDataPath read here.
// Desktop players keep the game's own paths. Part of CrowCompat; see Compat2018.cs.
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
    }
}
