using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

// The probe's flags and the player's log on the PS Vita, where a player gets no command line and
// Unity's log goes only to a development kit's console. Flags come from the command line, and on
// the PS Vita also from ux0:data/crow/args.txt (vita.py args); the player's log messages go to
// ux0:data/crow/log.txt (vita.py log; the two runs before in log.prev.txt and log.prev2.txt): errors and exceptions with their stack traces, a message
// repeated back to back as one line with its count, and nothing more once the file reaches
// MaxBytes, so an error every frame cannot fill the card. A thread of its own writes the lines: a
// write to the memory card now and then takes seconds (two lines in a row took 2 s each, a 4 s frame
// in Station Square), and the main thread, which logs, waited for it. A write that takes 150 ms or
// more adds a CROW_LOG_SLOW line. -crow-log-sync writes on the logging thread, as before.
// The writer runs at the main thread's priority, on any CPU, catches and logs its own
// failures (CROW_LOG_ERROR, and it opens the file again), and notes lines that waited a second or
// more (CROW_LOG_LATE); Flush writes what is queued from the calling thread, for the stall dump's crash.
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
        static readonly Queue<KeyValuePair<long, string>> pending = new Queue<KeyValuePair<long, string>>();  // (queued at, line)
        static readonly object fileGate = new object();  // held while lines are written
        static StreamWriter output;  // the threaded writer's file (fileGate)
        static string path;
        static bool sync;
        static int mainPriority;
        const double SlowMs = 150.0, LateMs = 1000.0;
        [System.Runtime.InteropServices.DllImport("CrowClock")] static extern int CrowThreadSelf();
        [System.Runtime.InteropServices.DllImport("CrowClock")] static extern int CrowThreadQuery(int uid, int[] info, byte[] name);
        [System.Runtime.InteropServices.DllImport("CrowClock")] static extern int CrowThreadSet(int uid, int priority, int affinity);

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
                KeepPrevious();
                path = Path.Combine(Folder, "log.txt");
                log = new StreamWriter(path, false, new UTF8Encoding(false));
                log.WriteLine("Crow Country " + Application.version + ", Unity " + Application.unityVersion + ", started " + Saves.Now().ToString("s"));
                log.WriteLine("args: " + string.Join(" ", Args.Skip(1).ToArray()));
                if (keepFailed != null) log.WriteLine("CROW_LOG the logs before weren't all kept: " + keepFailed);
                log.Flush();
                sync = Args.Contains("-crow-log-sync");
                if (!sync)
                {
                    mainPriority = MainPriority();
                    output = log;
                    new Thread(WriteLines) { IsBackground = true, Name = "CrowLog" }.Start();
                }
                Application.logMessageReceivedThreaded += Write;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("CrowCompat: no log file in " + Folder + ": " + exception.Message);
            }
            Audio.Configure();  // the mixer's settings before anything plays
        }

        // The last runs' logs: log.txt becomes log.prev.txt and that log.prev2.txt (vita.py log --prev
        // [2]), so the log of a run that crashed survives the next launch or two. A failure is noted in
        // the new log (keepFailed).
        static string keepFailed;

        static void KeepPrevious()
        {
            string[] names = { "log.txt", "log.prev.txt", "log.prev2.txt" };
            for (int i = names.Length - 1; i > 0; i--)
            {
                try
                {
                    string from = Path.Combine(Folder, names[i - 1]), to = Path.Combine(Folder, names[i]);
                    if (!File.Exists(from)) continue;
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);
                }
                catch (Exception exception)
                {
                    keepFailed = names[i - 1] + " -> " + names[i] + ": " + exception.Message;
                }
            }
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
                if (written > MaxBytes) line = null;  // the last: "(log full)"
                if (sync)
                {
                    Put(log, line);
                    if (line == null) log = null;
                    return;
                }
                if (line == null) log = null;
                pending.Enqueue(new KeyValuePair<long, string>(Stopwatch.GetTimestamp(), line));
                Monitor.Pulse(gate);
            }
        }

        // The writer thread: takes the lines logged so far and writes them outside the lock, so a slow
        // card holds up only this thread.
        static void WriteLines()
        {
            var lines = new List<KeyValuePair<long, string>>();
            lines.Add(new KeyValuePair<long, string>(Stopwatch.GetTimestamp(), "CROW_LOG writer " + Boost()));
            while (true)
            {
                lock (gate)
                {
                    while (pending.Count == 0 && lines.Count == 0) Monitor.Wait(gate);
                    lines.AddRange(pending);
                    pending.Clear();
                }
                if (!WriteAll(lines, Timeout.Infinite)) return;
                lines.Clear();
            }
        }

        // Queued lines, written under fileGate: first a CROW_LOG_LATE line when the oldest waited LateMs
        // or more. False once the log is closed, or when fileGate wasn't had within ms.
        static bool WriteAll(List<KeyValuePair<long, string>> lines, int ms)
        {
            if (lines.Count == 0) return true;
            if (!Monitor.TryEnter(fileGate, ms)) return false;
            try
            {
                if (output == null) return false;
                double waited = (Stopwatch.GetTimestamp() - lines[0].Key) * 1000.0 / Stopwatch.Frequency;
                if (waited >= LateMs && !Safe("CROW_LOG_LATE " + lines.Count + " lines waited up to " + waited.ToString("F0") + " ms")) return false;
                foreach (KeyValuePair<long, string> line in lines)
                    if (!Safe(line.Value)) return false;
                return true;
            }
            finally
            {
                Monitor.Exit(fileGate);
            }
        }

        // Put that never throws: after a failure the file is opened again for appending, with a
        // CROW_LOG_ERROR line, and the line tried again (three times). False once the log is closed.
        static bool Safe(string line)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Put(output, line)) return true;
                    output = null;
                    return false;
                }
                catch (Exception e)
                {
                    try
                    {
                        output.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                    try
                    {
                        output = new StreamWriter(path, true, new UTF8Encoding(false));
                        output.WriteLine("CROW_LOG_ERROR " + e.GetType().Name + ": " + e.Message);
                        output.Flush();
                    }
                    catch (Exception)
                    {
                        Thread.Sleep(200);
                    }
                }
            }
            return output != null;  // the line is lost; the log goes on
        }

        // Writes what is queued from the calling thread, waiting at most ms for a write in progress
        // (Diag's stall dump, before it crashes the player).
        public static void Flush(int ms)
        {
            if (sync || path == null) return;
            var lines = new List<KeyValuePair<long, string>>();
            lock (gate)
            {
                lines.AddRange(pending);
                pending.Clear();
            }
            WriteAll(lines, ms);
        }

        // The writer at the main thread's priority (64, the highest a user thread has; it starts at 159,
        // below every busy thread of a load), on any of the three user CPUs; what it was and is.
        static string Boost()
        {
            try
            {
                int self = CrowThreadSelf();
                var info = new int[10];
                var name = new byte[32];
                CrowThreadQuery(self, info, name);
                string was = "priority " + info[1] + " cpus 0x" + info[2].ToString("X");
                int result = CrowThreadSet(self, mainPriority >= 64 ? mainPriority : 64, 0x70000);
                CrowThreadQuery(self, info, name);
                return "was " + was + "; main thread priority " + mainPriority + "; now priority " + info[1] + " cpus 0x" + info[2].ToString("X") +
                       (result < 0 ? " (set failed 0x" + result.ToString("X8") + ")" : "");
            }
            catch (Exception e)
            {
                return "not boosted: " + e.GetType().Name;
            }
        }

        // The calling (main) thread's priority, or 0.
        static int MainPriority()
        {
            try
            {
                var info = new int[10];
                return CrowThreadQuery(CrowThreadSelf(), info, new byte[32]) == 0 ? info[1] : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // One line, written and flushed; null closes the file with "(log full)". False once closed.
        static bool Put(StreamWriter file, string line)
        {
            if (line == null)
            {
                file.WriteLine("(log full)");
                file.Dispose();
                return false;
            }
            long start = Stopwatch.GetTimestamp();
            file.WriteLine(line);
            file.Flush();
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (ms >= SlowMs)
            {
                file.WriteLine(Time.realtimeSinceStartup.ToString("F2") + " CROW_LOG_SLOW the card took " + ms.ToString("F0") + " ms to write a line" +
                               (sync ? " (the logging thread waited)" : ""));
                file.Flush();
            }
            return true;
        }
    }

    // The probe's flags as the modules read them from ProbeFiles.Args: a flag's value is the word
    // after it, numbers in the invariant culture.
    static class CrowFlags
    {
        internal static bool Has(string[] args, string flag)
        {
            return Array.IndexOf(args, flag) >= 0;
        }

        // The word after the flag, or null.
        internal static string Value(string[] args, string flag)
        {
            int index = Array.IndexOf(args, flag);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        // The word after the flag, or null when there is none or it is another -crow-* flag (for flags
        // whose value may be left out: -crow-audio [s]).
        internal static string Optional(string[] args, string flag)
        {
            string value = Value(args, flag);
            return value != null && !value.StartsWith("-crow-") ? value : null;
        }

        internal static float Number(string[] args, string flag, float fallback)
        {
            string text = Value(args, flag);
            float value;
            return text != null && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        internal static int Int(string[] args, string flag, int fallback)
        {
            string text = Value(args, flag);
            int value;
            return text != null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }
    }

    // Members of objects CrowCompat doesn't reference (Rewired's, PlayMaker's) read by name, through reflection.
    // One copy since 21 Oct: the pad reader's (Pad.cs) and RewiredProbe's (Cuts.cs) had drifted apart (one passed
    // over indexers, the other took a null target). Diag's -crow-input reader keeps its own: it also reads
    // non-public members, and writes the error in its log line in place of a value.
    static class ByName
    {
        const System.Reflection.BindingFlags Public = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

        // The most derived public instance property of that name that takes no index (Rewired's types redeclare some).
        internal static System.Reflection.PropertyInfo Property(object target, string name)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                System.Reflection.PropertyInfo property = type.GetProperty(name, Public | System.Reflection.BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0) return property;
            }
            return null;
        }

        // That property's value, else the public field's; no such member throws (the pad reader's tables need each one).
        internal static object Get(object target, string name)
        {
            System.Reflection.PropertyInfo property = Property(target, name);
            if (property != null) return property.GetValue(target, null);
            System.Reflection.FieldInfo field = target.GetType().GetField(name, Public);
            if (field == null) throw new MissingMemberException(target.GetType().FullName, name);
            return field.GetValue(target);
        }

        // The same, but null for a null target or no such member (the probes' reports).
        internal static object GetOrNull(object target, string name)
        {
            if (target == null) return null;
            System.Reflection.PropertyInfo property = Property(target, name);
            if (property != null) return property.GetValue(target, null);
            System.Reflection.FieldInfo field = target.GetType().GetField(name, Public);
            return field == null ? null : field.GetValue(target);
        }

        // PlayMaker's global bool of that name (FsmVariables.GlobalVariables.FindFsmBool): its FsmBool, null when the
        // game has no such global; without PlayMaker it throws.
        internal static object GlobalBool(string name)
        {
            Type variables = Type.GetType("HutongGames.PlayMaker.FsmVariables, PlayMaker");
            object globals = variables.GetProperty("GlobalVariables").GetValue(null, null);
            return variables.GetMethod("FindFsmBool", new[] { typeof(string) }).Invoke(globals, new object[] { name });
        }
    }
}
