using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// What the editor scripts here share: the build switches, the scenes the scene steps leave alone, marking
// meshes unreadable, walking a behaviour's serialized references, and reflection on the plugins' fields.
static class BuildUtil
{
    // The scenes that are no area: the logos, the title and the player's scene (DowngradeBuild.SaveMaraScene).
    internal static readonly string[] SkippedScenes = { "Logos", "Title", "CrowMara" };

    static readonly HashSet<string> logged = new HashSet<string>();

    // A build switch's value (CROW_*, common.BUILD_SWITCHES in pipeline.py's folder). pipeline.py build checks
    // them and gives the editor each one's value, its default included, so the defaults live there alone.
    // Logged once per build as DOWNGRADE_SWITCH.
    internal static string Switch(string name)
    {
        string value = OptionalSwitch(name);
        if (value == null) throw new InvalidOperationException(name + " is not set: pipeline.py build sets every build switch");
        return value;
    }

    // A switch whose default is the code's own (CROW_MERGE_MAX_VERTICES): null when unset.
    internal static string OptionalSwitch(string name)
    {
        string value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value)) value = null;
        if (logged.Add(name)) Debug.Log("DOWNGRADE_SWITCH " + name + "=" + (value ?? "(unset)"));
        return value;
    }

    // Whether the asset (a mesh or texture) had a readable flag, now off: its data goes to the GPU only.
    internal static bool MarkUnreadable(UnityEngine.Object asset)
    {
        if (asset == null) return false;
        var serialized = new SerializedObject(asset);
        SerializedProperty readable = serialized.FindProperty("m_IsReadable");
        if (readable == null) return false;
        readable.boolValue = false;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        return true;
    }

    // Whether the editor can read a mesh's data (it reads every mesh's, readable or not; one it can't stays
    // out of a merge), once per mesh.
    static readonly Dictionary<Mesh, bool> readable = new Dictionary<Mesh, bool>();

    internal static bool CanRead(Mesh mesh)
    {
        bool can;
        if (readable.TryGetValue(mesh, out can)) return can;
        try
        {
            can = mesh.vertices.Length == mesh.vertexCount;
        }
        catch (Exception)
        {
            can = false;
        }
        readable[mesh] = can;
        return can;
    }

    // Element types of arrays that hold no object reference and no string: Walk skips them.
    static readonly HashSet<string> Plain = new HashSet<string>
    {
        "int", "unsigned int", "SInt8", "UInt8", "SInt16", "UInt16", "SInt64", "UInt64", "float", "double", "bool", "char",
        "Vector2f", "Vector3f", "Vector4f", "Quaternionf", "ColorRGBA", "Rectf",
    };

    // Every object the serialized data of target (a behaviour) references, into objects. With names, every
    // string in it too (a find by name), and with lastPart a path's last part as well; without, strings and
    // arrays of them are skipped. With walked, an FsmTemplate it references is read as part of it (a PlayMaker
    // FSM's actions can live there), each object once.
    internal static void Walk(UnityEngine.Object target, ICollection<UnityEngine.Object> objects, ICollection<string> names,
                              bool lastPart, HashSet<UnityEngine.Object> walked)
    {
        if (walked != null && !walked.Add(target)) return;
        var serialized = new SerializedObject(target);
        SerializedProperty property = serialized.GetIterator();
        bool enter = true;
        while (property.Next(enter))
        {
            enter = true;
            if (property.propertyType == SerializedPropertyType.ObjectReference)
            {
                UnityEngine.Object referenced = property.objectReferenceValue;
                if (referenced != null)
                {
                    objects.Add(referenced);
                    if (walked != null && referenced is ScriptableObject && referenced.GetType().Name == "FsmTemplate")
                        Walk(referenced, objects, names, lastPart, walked);
                }
                enter = false;
            }
            else if (property.propertyType == SerializedPropertyType.String)
            {
                string text = names == null ? null : property.stringValue;
                if (!string.IsNullOrEmpty(text))
                {
                    names.Add(text);
                    int slash = lastPart ? text.LastIndexOf('/') : -1;
                    if (slash >= 0) names.Add(text.Substring(slash + 1));
                }
                enter = false;
            }
            else if (property.isArray && (Plain.Contains(property.arrayElementType) || names == null && property.arrayElementType == "string"))
                enter = false;
        }
    }

    // A field of an object (a plugin's type, reached by name), public or not, its type's or a base type's; null
    // when there is none.
    internal static object Field(object target, string name)
    {
        FieldInfo field = FindField(target, name);
        return field == null ? null : field.GetValue(target);
    }

    internal static void SetField(object target, string name, object value)
    {
        FieldInfo field = FindField(target, name);
        if (field != null) field.SetValue(target, value);
    }

    static FieldInfo FindField(object target, string name)
    {
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        return null;
    }
}
