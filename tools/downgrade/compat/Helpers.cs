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

// What every module uses: the flags (CrowFlags), hierarchy paths for the logs (Hierarchy), caches pruned of
// destroyed objects (Destroyed), and members read by name (ByName).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    // The probe's flags as the modules read them from ProbeFiles.Args: a flag's value is the word
    // after it, numbers in the invariant culture.
    static class CrowFlags
    {
        // The PS Vita's player, where many flags default to on.
        internal static bool Vita { get { return Application.platform == RuntimePlatform.PSP2; } }

        internal static bool Has(string[] args, string flag)
        {
            return Array.IndexOf(args, flag) >= 0;
        }

        // The word after the flag, else this player's default: vita on the PS Vita, other on the others (null:
        // off there).
        internal static string Value(string[] args, string flag, string vita, string other = null)
        {
            return Value(args, flag) ?? (Vita ? vita : other);
        }

        // A flag that is on by default on the PS Vita and off elsewhere: when given, whether its word is on
        // (-crow-map-pan on|game, -crow-map-arrows canvas|game).
        internal static bool On(string[] args, string flag, string on = "on")
        {
            string value = Value(args, flag);
            return value != null ? value == on : Vita;
        }

        // The n-th word after the flag (1: the word after it, as Value), or null (-crow-ab <s> <path> <changes>).
        internal static string Word(string[] args, string flag, int n)
        {
            int index = Array.IndexOf(args, flag);
            return index >= 0 && index + n < args.Length ? args[index + n] : null;
        }

        // The n-th word after the flag as a number, or fallback (-crow-scene-then <name> <s>).
        internal static float NumberAt(string[] args, string flag, int n, float fallback)
        {
            string text = Word(args, flag, n);
            float value;
            return text != null && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
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

    // The game's types CrowCompat reaches by reflection (it references neither PlayMaker, MeshCombineStudio nor
    // DOTween), looked up once; null where the assembly is missing.
    static class GameTypes
    {
        internal static readonly Type PlayMakerFsm = Type.GetType("PlayMakerFSM, PlayMaker");
        internal static readonly Type MeshCombiner = Type.GetType("MeshCombineStudio.MeshCombiner, MeshCombineStudio.Runtime");
        internal static readonly Type DOTween = Type.GetType("DG.Tweening.DOTween, DOTween");
        internal static readonly Type Tween = Type.GetType("DG.Tweening.Tween, DOTween");
        static readonly System.Reflection.MethodInfo playing = DOTween == null ? null : DOTween.GetMethod("PlayingTweens");

        // DOTween.PlayingTweens(): the tweens playing now (a new list), or null without DOTween.
        internal static System.Collections.IList PlayingTweens()
        {
            return playing == null ? null : playing.Invoke(null, new object[] { null }) as System.Collections.IList;
        }
    }

    // Hierarchy paths for the logs.
    static class Hierarchy
    {
        // A transform's name after up to depth - 1 of its parents' ("parent/child" at 2), spaces as underscores (a
        // log word).
        internal static string Path(Transform transform, int depth)
        {
            string name = transform.name;
            for (Transform parent = transform.parent; parent != null && depth > 1; parent = parent.parent, depth--) name = parent.name + "/" + name;
            return name.Replace(' ', '_');
        }

        // Its path from root (included) down, spaces as underscores.
        internal static string Path(Transform transform, Transform root)
        {
            var path = new StringBuilder(transform.name.Replace(' ', '_'));
            for (Transform p = transform.parent; p != null && p != root.parent; p = p.parent) path.Insert(0, p.name.Replace(' ', '_') + "/");
            return path.ToString();
        }

        // Its whole path from its scene's root, the names as they are (-crow-find's patterns match those).
        internal static string Full(Transform transform)
        {
            var path = new StringBuilder(transform.name);
            for (Transform t = transform.parent; t != null; t = t.parent) path.Insert(0, t.name + "/");
            return path.ToString();
        }
    }

    // Caches keyed by engine objects: the keys of objects since destroyed (an unloaded area's) removed, so the cache
    // doesn't keep their managed wrappers alive. Called at CrowEvents.Unloaded or a scene load.
    static class Destroyed
    {
        internal static void Forget<TKey, TValue>(Dictionary<TKey, TValue> map) where TKey : UnityEngine.Object
        {
            List<TKey> gone = null;
            foreach (TKey key in map.Keys)
                if (key == null) (gone ?? (gone = new List<TKey>())).Add(key);
            if (gone != null) foreach (TKey key in gone) map.Remove(key);
        }
    }

    // Members of objects CrowCompat doesn't reference (Rewired's, PlayMaker's) read by name, through reflection.
    // One copy for the pad reader (Pad.cs) and RewiredProbe (Cuts.cs). InputProbe.cs (-crow-input) keeps its own:
    // it also reads non-public members, and writes the error in its log line in place of a value.
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

        // PlayMaker's variable of that name (FsmVariables.FindVariable) in an FSM's FsmVariables, or with variables null
        // in the globals (FsmVariables.GlobalVariables): its NamedVariable, null when there is none; without PlayMaker
        // it throws.
        internal static object Variable(object variables, string name)
        {
            Type type = Type.GetType("HutongGames.PlayMaker.FsmVariables, PlayMaker");
            if (variables == null) variables = type.GetProperty("GlobalVariables").GetValue(null, null);
            return type.GetMethod("FindVariable", new[] { typeof(string) }).Invoke(variables, new object[] { name });
        }

        // PlayMaker's global bool of that name: its FsmBool, null when the game has no such global (a variable's name
        // is unique among all its types'); without PlayMaker it throws.
        internal static object GlobalBool(string name)
        {
            object variable = Variable(null, name);
            return variable != null && variable.GetType().Name == "FsmBool" ? variable : null;
        }
    }
}
