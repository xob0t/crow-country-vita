using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

// The probe's flags and the player's log on the PS Vita, where a player gets no command line and
// Unity's log goes only to a development kit's console. Flags come from the command line, and on
// the PS Vita also from ux0:data/crow/args.txt (vita.py args); the player's log messages go to
// ux0:data/crow/log.txt (vita.py log): errors and exceptions with their stack traces, a message
// repeated back to back as one line with its count, and nothing more once the file reaches
// MaxBytes, so an error every frame cannot fill the card.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class ProbeFiles
    {
        public const string Folder = "ux0:data/crow";
        const long MaxBytes = 8 << 20;

        static string[] args;
        static StreamWriter log;
        static string last;
        static int repeats;
        static long written;
        static readonly object gate = new object();

        static bool Vita { get { return Application.platform == RuntimePlatform.PSP2; } }

        // The command line, and on the PS Vita the words of args.txt after it ("quoted words" kept whole).
        public static string[] Args
        {
            get
            {
                if (args != null) return args;
                string[] line = Environment.GetCommandLineArgs();
                string file = Vita ? Path.Combine(Folder, "args.txt") : null;
                if (file == null || !File.Exists(file)) return args = line;
                string text = File.ReadAllText(file);
                var words = System.Text.RegularExpressions.Regex.Matches(text, "\"([^\"]*)\"|(\\S+)").Cast<System.Text.RegularExpressions.Match>()
                    .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                return args = line.Concat(words).ToArray();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            if (!Vita) return;
            try
            {
                Directory.CreateDirectory(Folder);
                log = new StreamWriter(Path.Combine(Folder, "log.txt"), false, new UTF8Encoding(false));
                log.WriteLine("Crow Country " + Application.version + ", Unity " + Application.unityVersion + ", started " + Saves.Now().ToString("s"));
                log.WriteLine("args: " + string.Join(" ", Args.Skip(1).ToArray()));
                log.Flush();
                Application.logMessageReceivedThreaded += Write;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("CrowCompat: no log file in " + Folder + ": " + exception.Message);
            }
            Audio.Configure();  // the mixer's settings before anything plays
        }

        static void Write(string message, string stack, LogType type)
        {
            lock (gate)
            {
                if (log == null) return;
                string entry = type == LogType.Log || type == LogType.Warning ? message : type + ": " + message + "\n" + stack;
                if (entry == last)
                {
                    repeats++;
                    return;
                }
                string line = (repeats > 0 ? "  (the line above " + repeats + " more times)\n" : "") +
                              Time.realtimeSinceStartup.ToString("F2") + " " + entry.TrimEnd();
                last = entry;
                repeats = 0;
                written += line.Length + 1;
                if (written > MaxBytes)
                {
                    log.WriteLine("(log full)");
                    log.Dispose();
                    log = null;
                    return;
                }
                log.WriteLine(line);
                log.Flush();
            }
        }
    }
}
