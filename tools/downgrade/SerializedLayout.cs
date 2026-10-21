using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

// What a player build bakes in from the managed code, for pipeline.py quickcode, which replaces only
// the code: a build's scenes and assets are serialized with the fields of the scripts as they were,
// and it lists the [RuntimeInitializeOnLoadMethod] methods. Writes one line per type Unity can
// serialize (UnityEngine.Object subclasses and [Serializable] types) with its base type, one per
// field it serializes (instance, not readonly or [NonSerialized], public or [SerializeField]), and
// one per [RuntimeInitializeOnLoadMethod], sorted. Two builds with the same lines read each other's data.
// A field's line has its place among its type's serialized fields (#0, #1, ...): a player reads the data
// in field order, so the sort mustn't hide a change of order.
// Usage: SerializedLayout <output.txt> <folder to resolve references in;...> <assembly.dll>...
class SerializedLayout
{
    static int Main(string[] args)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (string folder in args[1].Split(';')) if (folder.Length > 0) resolver.AddSearchDirectory(folder);
        foreach (string dll in args.Skip(2)) resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(dll)));
        var lines = new List<string>();
        foreach (string dll in args.Skip(2))
        {
            var assembly = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { AssemblyResolver = resolver });
            string name = assembly.Name.Name;
            foreach (TypeDefinition type in AllTypes(assembly.MainModule.Types))
            {
                foreach (MethodDefinition method in type.Methods)
                    foreach (CustomAttribute attribute in method.CustomAttributes)
                        if (attribute.AttributeType.Name == "RuntimeInitializeOnLoadMethodAttribute")
                            lines.Add(name + " init " + type.FullName + "::" + method.Name + " " +
                                      string.Join(",", attribute.ConstructorArguments.Select(a => Convert.ToString(a.Value)).ToArray()));
                bool unityObject = DerivesFromUnityObject(type);
                if (!unityObject && !type.IsSerializable) continue;
                lines.Add(name + " type " + type.FullName + " : " + (type.BaseType == null ? "" : type.BaseType.FullName));
                int place = 0;
                foreach (FieldDefinition field in type.Fields)
                {
                    if (field.IsStatic || field.IsInitOnly || field.IsLiteral || field.IsNotSerialized) continue;
                    if (!field.IsPublic && !field.CustomAttributes.Any(a => a.AttributeType.Name == "SerializeField")) continue;
                    lines.Add(name + " field " + type.FullName + "." + field.Name + " #" + place++ + " " + field.FieldType.FullName);
                }
            }
        }
        lines.Sort(StringComparer.Ordinal);
        File.WriteAllLines(args[0], lines.ToArray());
        return 0;
    }

    // A base type that doesn't resolve counts as UnityEngine.Object's: the comparison then errs on
    // the side of a full build.
    static bool DerivesFromUnityObject(TypeDefinition type)
    {
        for (TypeReference current = type.BaseType; current != null;)
        {
            if (current.FullName == "UnityEngine.Object") return true;
            TypeDefinition resolved;
            try { resolved = current.Resolve(); }
            catch (AssemblyResolutionException) { return true; }
            if (resolved == null) return true;
            current = resolved.BaseType;
        }
        return false;
    }

    static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }
}
