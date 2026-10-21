using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Generic script costs the PS Vita's main thread pays in every area, cut without changing what runs.
//   - FastProperty: PlayMaker's Set Property and Get Property actions (FsmProperty.SetValue and
//     GetValue, 1,138 of them in the game's data, on 26 properties: TextMeshPro's colour and text,
//     Light.intensity, range and colour, AudioSource.volume and pitch, ParticleSystem.emissionRate,
//     Image.color.a, ...) set and read the property by reflection on every call: PropertyInfo.SetValue
//     checks the value with the default binder, packs it in an object[] and goes through IL2CPP's
//     Runtime::Invoke, about 0.1 ms a call on the console (the fight's action profile: SetProperty
//     0.63 ms a frame for 3 calls, with the profiler's doubling). The rewriter (rewriter/FastPaths.cs)
//     sends FsmProperty's ReflectionUtils.SetMemberValue/GetMemberValue calls through Set and Get here,
//     with a cache field of its own per FsmProperty: a property of the target (or a float field of a
//     property's Color/Vector/Quaternion value, PlayMaker's "color.a") is set and read through
//     delegates bound to the target and to the property's accessors, the overrides the target's own
//     class has (reflection's Invoke calls those: IL2CPP's CreateDelegate binds the method it is
//     given, without virtual dispatch). A value of another type than the property's (reflection
//     converts or refuses it), a field, a static or indexed property, a longer path or anything else
//     goes to PlayMaker's reflection as before. An exception the accessor throws is no longer wrapped
//     in a TargetInvocationException; the action stops at it either way.
//   - Names: UnityEngine.Object.name makes a new string from the native name at every read. The
//     enemies' FSMs pass flags through their children's names ("0"/"1", set with SetName) and read
//     them every frame with GetName and StringCompare (132 everyFrame actions in the enemy FSMs):
//     an engine call and a string of garbage each. The rewriter sends every name read and write in
//     the game's assemblies through Names.Get and Names.Set: a read of a live object returns the
//     name read before, kept by instance ID, until the game's code sets that object's name again
//     (Set forgets it); a destroyed object's read goes to Unity, which throws as before. The cache is
//     emptied whenever a scene unloads. Unity itself never renames an object it has made; CrowCompat
//     renames only objects it has just made (the fight's stress copies, in the frame they are made).
//   - VitaLit's main camera (compat/VitaLit.cs, CacheCamera): Camera.main walked the scene's objects
//     on every key light selection, which runs in every frame a watched spot light comes on.
// Flags (defaults on every player):
//   -crow-fast-property game   every Set and Get Property by reflection, as PlayMaker has it
//   -crow-fast-property check  both ways, compared (CROW_FAST_CHECK lines: a Get's two values, a Set's
//                              value read back by reflection after each way; a Set runs twice)
//   -crow-lit-camera game      Camera.main on every key light selection
//   -crow-fast-names game      every name read from Unity
// -crow-cycle, -crow-fight-cycle and -crow-ab take fastprop=on|game, litcam=on|game and names=on|game.
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    public static class FastPaths
    {
        public static void Install(string[] args)
        {
            string property = CrowFlags.Value(args, "-crow-fast-property");
            if (property == "game") FastProperty.On = false;
            FastProperty.Check = property == "check";
            if (CrowFlags.Value(args, "-crow-lit-camera") == "game") VitaLit.CacheCamera = false;
            if (CrowFlags.Value(args, "-crow-fast-names") == "game") Names.On = false;
            CrowEvents.Unloaded("FastPaths.sceneUnloaded", scene => Names.Clear());
        }

        // A -crow-cycle / -crow-ab change; false when it isn't one of these.
        internal static bool Change(string key, string value)
        {
            switch (key)
            {
                case "fastprop":
                    FastProperty.On = value != "game";
                    break;
                case "litcam":
                    VitaLit.CacheCamera = value != "game";
                    break;
                case "names":
                    Names.On = value != "game";
                    Names.Clear();
                    break;
                default:
                    return false;
            }
            Debug.Log("CROW_FAST " + key + "=" + value);
            return true;
        }
    }

    public static class Names
    {
        public static bool On = true;
        static readonly Dictionary<int, string> names = new Dictionary<int, string>();

        // UnityEngine.Object.name's getter.
        public static string Get(UnityEngine.Object target)
        {
            if (!On || target == null) return target.name;  // null or destroyed: Unity's exception
            int id = target.GetInstanceID();
            string name;
            if (names.TryGetValue(id, out name)) return name;
            name = target.name;
            names[id] = name;
            return name;
        }

        // UnityEngine.Object.name's setter.
        public static void Set(UnityEngine.Object target, string value)
        {
            target.name = value;
            if (names.Count > 0) names.Remove(target.GetInstanceID());
        }

        internal static void Clear()
        {
            names.Clear();
        }
    }

    public static class FastProperty
    {
        public static bool On = true;
        public static bool Check;
        static int checks, mismatches;

        enum Kind { None, Float, Int, Bool, String, Color, Vector2, Vector3, Vector4, Quaternion, Rect }

        // One FsmProperty's accessors, for its member path and target (FsmProperty.Init makes a new path
        // whenever the target changes).
        sealed class Accessor
        {
            public MemberInfo[] Members;
            public object Target;
            public Kind Kind;
            public Delegate Get, Set;
            public int Component = -1;  // with a two-member path: the float field of the property's value
            public Type ValueType;  // what a set value must be exactly: the property's type, or float
        }

        // FsmProperty.SetValue's ReflectionUtils.SetMemberValue(members, target, value); false: not done,
        // reflection's turn.
        public static bool Set(MemberInfo[] members, object target, object value, ref object cache)
        {
            if (!On) return false;
            if (Check) return CheckSet(members, target, value, ref cache);
            return FastSet(members, target, value, ref cache);
        }

        static bool FastSet(MemberInfo[] members, object target, object value, ref object cache)
        {
            Accessor accessor = For(members, target, ref cache);
            if (accessor.Set == null) return false;
            if (value == null ? accessor.Kind != Kind.String || accessor.Component >= 0 : value.GetType() != accessor.ValueType) return false;
            if (accessor.Component >= 0) return accessor.Get != null && SetComponent(accessor, (float)value);
            switch (accessor.Kind)
            {
                case Kind.Float: ((Action<float>)accessor.Set)((float)value); return true;
                case Kind.Int: ((Action<int>)accessor.Set)((int)value); return true;
                case Kind.Bool: ((Action<bool>)accessor.Set)((bool)value); return true;
                case Kind.String: ((Action<string>)accessor.Set)((string)value); return true;
                case Kind.Color: ((Action<Color>)accessor.Set)((Color)value); return true;
                case Kind.Vector2: ((Action<Vector2>)accessor.Set)((Vector2)value); return true;
                case Kind.Vector3: ((Action<Vector3>)accessor.Set)((Vector3)value); return true;
                case Kind.Vector4: ((Action<Vector4>)accessor.Set)((Vector4)value); return true;
                case Kind.Quaternion: ((Action<Quaternion>)accessor.Set)((Quaternion)value); return true;
                case Kind.Rect: ((Action<Rect>)accessor.Set)((Rect)value); return true;
            }
            return false;
        }

        // FsmProperty.SetValue's sets of a value type, unboxed (rewriter TypedPropertySets): the same as
        // Set with the value boxed, without the box (FsmProperty boxed the parameter's value on every
        // call: Pool standing, 185 floats a second). False: the caller boxes the value and calls Set
        // (check mode, a value of another type than the property's, reflection's turn).
        public static bool SetFloat(MemberInfo[] members, object target, float value, ref object cache)
        {
            if (!On || Check) return false;
            Accessor accessor = For(members, target, ref cache);
            if (accessor.Set == null || accessor.ValueType != typeof(float)) return false;
            if (accessor.Component >= 0) return accessor.Get != null && SetComponent(accessor, value);
            if (accessor.Kind != Kind.Float) return false;
            ((Action<float>)accessor.Set)(value);
            return true;
        }

        public static bool SetInt(MemberInfo[] members, object target, int value, ref object cache)
        {
            Action<int> set = Typed<int>(members, target, ref cache, Kind.Int);
            if (set == null) return false;
            set(value);
            return true;
        }

        public static bool SetBool(MemberInfo[] members, object target, bool value, ref object cache)
        {
            Action<bool> set = Typed<bool>(members, target, ref cache, Kind.Bool);
            if (set == null) return false;
            set(value);
            return true;
        }

        public static bool SetVector2(MemberInfo[] members, object target, Vector2 value, ref object cache)
        {
            Action<Vector2> set = Typed<Vector2>(members, target, ref cache, Kind.Vector2);
            if (set == null) return false;
            set(value);
            return true;
        }

        public static bool SetVector3(MemberInfo[] members, object target, Vector3 value, ref object cache)
        {
            Action<Vector3> set = Typed<Vector3>(members, target, ref cache, Kind.Vector3);
            if (set == null) return false;
            set(value);
            return true;
        }

        public static bool SetQuaternion(MemberInfo[] members, object target, Quaternion value, ref object cache)
        {
            Action<Quaternion> set = Typed<Quaternion>(members, target, ref cache, Kind.Quaternion);
            if (set == null) return false;
            set(value);
            return true;
        }

        public static bool SetColor(MemberInfo[] members, object target, Color value, ref object cache)
        {
            Action<Color> set = Typed<Color>(members, target, ref cache, Kind.Color);
            if (set == null) return false;
            set(value);
            return true;
        }

        public static bool SetRect(MemberInfo[] members, object target, Rect value, ref object cache)
        {
            Action<Rect> set = Typed<Rect>(members, target, ref cache, Kind.Rect);
            if (set == null) return false;
            set(value);
            return true;
        }

        // The property's setter when it takes exactly a T (Set's test: the value's type is the property's).
        static Action<T> Typed<T>(MemberInfo[] members, object target, ref object cache, Kind kind)
        {
            if (!On || Check) return null;
            Accessor accessor = For(members, target, ref cache);
            if (accessor.Set == null || accessor.Component >= 0 || accessor.Kind != kind || accessor.ValueType != typeof(T)) return null;
            return (Action<T>)accessor.Set;
        }

        // FsmProperty.GetValue's ReflectionUtils.GetMemberValue(members, target): the value, boxed as
        // reflection boxes it.
        public static bool Get(MemberInfo[] members, object target, ref object cache, out object value)
        {
            value = null;
            if (!On) return false;
            if (!Check) return FastGet(members, target, ref cache, out value);
            bool done = FastGet(members, target, ref cache, out value);
            if (!done) Declined("get", members, null);
            if (done) Compare("get", members, value, Reflected(members, target));
            return done;
        }

        static bool FastGet(MemberInfo[] members, object target, ref object cache, out object value)
        {
            value = null;
            Accessor accessor = For(members, target, ref cache);
            if (accessor.Get == null) return false;
            if (accessor.Component >= 0)
            {
                value = GetComponent(accessor);
                return true;
            }
            switch (accessor.Kind)
            {
                case Kind.Float: value = ((Func<float>)accessor.Get)(); return true;
                case Kind.Int: value = ((Func<int>)accessor.Get)(); return true;
                case Kind.Bool: value = ((Func<bool>)accessor.Get)(); return true;
                case Kind.String: value = ((Func<string>)accessor.Get)(); return true;
                case Kind.Color: value = ((Func<Color>)accessor.Get)(); return true;
                case Kind.Vector2: value = ((Func<Vector2>)accessor.Get)(); return true;
                case Kind.Vector3: value = ((Func<Vector3>)accessor.Get)(); return true;
                case Kind.Vector4: value = ((Func<Vector4>)accessor.Get)(); return true;
                case Kind.Quaternion: value = ((Func<Quaternion>)accessor.Get)(); return true;
                case Kind.Rect: value = ((Func<Rect>)accessor.Get)(); return true;
            }
            return false;
        }

        // -crow-fast-property check: the set done the fast way and read back by reflection, then done by
        // reflection as PlayMaker does it and read back again; the two must agree.
        static bool CheckSet(MemberInfo[] members, object target, object value, ref object cache)
        {
            if (!FastSet(members, target, value, ref cache))
            {
                Declined("set", members, value);
                return false;
            }
            object fast = Reflected(members, target);
            ReflectedSet(members, target, value);
            Compare("set", members, fast, Reflected(members, target));
            return true;
        }

        // ReflectionUtils.GetMemberValue and SetMemberValue's way.
        static object Reflected(MemberInfo[] members, object target)
        {
            object value = target;
            foreach (MemberInfo member in members)
                value = member is PropertyInfo ? ((PropertyInfo)member).GetValue(value, null) : ((FieldInfo)member).GetValue(value);
            return value;
        }

        static void ReflectedSet(MemberInfo[] members, object target, object value)
        {
            if (members.Length == 1)
            {
                ((PropertyInfo)members[0]).SetValue(target, value, null);
                return;
            }
            object boxed = ((PropertyInfo)members[0]).GetValue(target, null);
            ((FieldInfo)members[1]).SetValue(boxed, value);
            ((PropertyInfo)members[0]).SetValue(target, boxed, null);
        }

        static readonly HashSet<string> declined = new HashSet<string>();

        static void Declined(string what, MemberInfo[] members, object value)
        {
            string name = what + " " + (members == null || members.Length == 0 ? "?" : members[0].DeclaringType.Name + "." +
                string.Join(".", Array.ConvertAll(members, m => m.Name))) + (value == null ? "" : " (" + value.GetType().Name + ")");
            if (declined.Add(name)) Debug.Log("CROW_FAST_CHECK declined " + name);
        }

        static void Compare(string what, MemberInfo[] members, object fast, object reflected)
        {
            checks++;
            bool same = fast == null ? reflected == null : fast.Equals(reflected);
            if (!same) mismatches++;
            if (!same || (checks & (checks - 1)) == 0)
                Debug.Log("CROW_FAST_CHECK " + what + " " + members[0].DeclaringType.Name + "." + string.Join(".", Array.ConvertAll(members, m => m.Name)) +
                          (same ? " same" : " DIFFERENT fast=" + fast + " reflection=" + reflected) + " checks=" + checks + " mismatches=" + mismatches);
        }

        // PlayMaker's path for "color.a": the property's value read, its field set, the value set back.
        static bool SetComponent(Accessor accessor, float value)
        {
            int index = accessor.Component;
            switch (accessor.Kind)
            {
                case Kind.Color:
                {
                    Color color = ((Func<Color>)accessor.Get)();
                    color[index] = value;
                    ((Action<Color>)accessor.Set)(color);
                    return true;
                }
                case Kind.Vector2:
                {
                    Vector2 vector = ((Func<Vector2>)accessor.Get)();
                    vector[index] = value;
                    ((Action<Vector2>)accessor.Set)(vector);
                    return true;
                }
                case Kind.Vector3:
                {
                    Vector3 vector = ((Func<Vector3>)accessor.Get)();
                    vector[index] = value;
                    ((Action<Vector3>)accessor.Set)(vector);
                    return true;
                }
                case Kind.Vector4:
                {
                    Vector4 vector = ((Func<Vector4>)accessor.Get)();
                    vector[index] = value;
                    ((Action<Vector4>)accessor.Set)(vector);
                    return true;
                }
                case Kind.Quaternion:
                {
                    Quaternion rotation = ((Func<Quaternion>)accessor.Get)();
                    rotation[index] = value;
                    ((Action<Quaternion>)accessor.Set)(rotation);
                    return true;
                }
            }
            return false;
        }

        static object GetComponent(Accessor accessor)
        {
            int index = accessor.Component;
            switch (accessor.Kind)
            {
                case Kind.Color: return ((Func<Color>)accessor.Get)()[index];
                case Kind.Vector2: return ((Func<Vector2>)accessor.Get)()[index];
                case Kind.Vector3: return ((Func<Vector3>)accessor.Get)()[index];
                case Kind.Vector4: return ((Func<Vector4>)accessor.Get)()[index];
                case Kind.Quaternion: return ((Func<Quaternion>)accessor.Get)()[index];
            }
            return null;
        }

        static Accessor For(MemberInfo[] members, object target, ref object cache)
        {
            var accessor = cache as Accessor;
            if (accessor != null && ReferenceEquals(accessor.Members, members) && ReferenceEquals(accessor.Target, target)) return accessor;
            accessor = Build(members, target);
            cache = accessor;
            return accessor;
        }

        static Accessor Build(MemberInfo[] members, object target)
        {
            var accessor = new Accessor { Members = members, Target = target };
            if (members == null || target == null || target.GetType().IsValueType) return accessor;
            try
            {
                if (members.Length == 1)
                {
                    var property = members[0] as PropertyInfo;
                    if (property != null && Bind(accessor, property, target)) accessor.ValueType = property.PropertyType;
                }
                else if (members.Length == 2)
                {
                    var property = members[0] as PropertyInfo;
                    var field = members[1] as FieldInfo;
                    if (property != null && field != null && !field.IsStatic && field.FieldType == typeof(float) && field.DeclaringType == property.PropertyType)
                    {
                        int component = Component(property.PropertyType, field.Name);
                        if (component >= 0 && Bind(accessor, property, target))
                        {
                            accessor.Component = component;
                            accessor.ValueType = typeof(float);
                        }
                    }
                }
            }
            catch (Exception)
            {
                accessor = new Accessor { Members = members, Target = target };
            }
            if (accessor.ValueType == null) accessor.Get = accessor.Set = null;
            return accessor;
        }

        static bool Bind(Accessor accessor, PropertyInfo property, object target)
        {
            Kind kind = KindOf(property.PropertyType);
            if (kind == Kind.None || property.GetIndexParameters().Length != 0) return false;
            MethodInfo get = property.GetGetMethod(true), set = property.GetSetMethod(true);
            if ((get != null && get.IsStatic) || (set != null && set.IsStatic)) return false;
            Type runtime = target.GetType();
            if (!property.DeclaringType.IsAssignableFrom(runtime)) return false;
            accessor.Kind = kind;
            if (get != null) accessor.Get = Delegate.CreateDelegate(FuncOf(kind), target, Override(get, runtime), false);
            if (set != null) accessor.Set = Delegate.CreateDelegate(ActionOf(kind), target, Override(set, runtime), false);
            return true;
        }

        // The method a virtual call on an object of the runtime type reaches: the override nearest to it.
        static readonly Dictionary<Type, Dictionary<MethodInfo, MethodInfo>> overrides = new Dictionary<Type, Dictionary<MethodInfo, MethodInfo>>();

        static MethodInfo Override(MethodInfo method, Type runtime)
        {
            if (!method.IsVirtual || method.DeclaringType == runtime) return method;
            Dictionary<MethodInfo, MethodInfo> known;
            if (!overrides.TryGetValue(runtime, out known)) overrides[runtime] = known = new Dictionary<MethodInfo, MethodInfo>();
            MethodInfo found;
            if (known.TryGetValue(method, out found)) return found;
            found = method;
            MethodInfo baseDefinition = method.GetBaseDefinition();
            const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type type = runtime; type != null && type != method.DeclaringType && found == method; type = type.BaseType)
                foreach (MethodInfo candidate in type.GetMethods(declared))
                    if (candidate.Name == method.Name && candidate.IsVirtual && candidate.GetBaseDefinition().Equals(baseDefinition))
                    {
                        found = candidate;
                        break;
                    }
            known[method] = found;
            return found;
        }

        static Kind KindOf(Type type)
        {
            if (type == typeof(float)) return Kind.Float;
            if (type == typeof(int)) return Kind.Int;
            if (type == typeof(bool)) return Kind.Bool;
            if (type == typeof(string)) return Kind.String;
            if (type == typeof(Color)) return Kind.Color;
            if (type == typeof(Vector2)) return Kind.Vector2;
            if (type == typeof(Vector3)) return Kind.Vector3;
            if (type == typeof(Vector4)) return Kind.Vector4;
            if (type == typeof(Quaternion)) return Kind.Quaternion;
            if (type == typeof(Rect)) return Kind.Rect;
            return Kind.None;
        }

        static Type FuncOf(Kind kind)
        {
            switch (kind)
            {
                case Kind.Float: return typeof(Func<float>);
                case Kind.Int: return typeof(Func<int>);
                case Kind.Bool: return typeof(Func<bool>);
                case Kind.String: return typeof(Func<string>);
                case Kind.Color: return typeof(Func<Color>);
                case Kind.Vector2: return typeof(Func<Vector2>);
                case Kind.Vector3: return typeof(Func<Vector3>);
                case Kind.Vector4: return typeof(Func<Vector4>);
                case Kind.Quaternion: return typeof(Func<Quaternion>);
                default: return typeof(Func<Rect>);
            }
        }

        static Type ActionOf(Kind kind)
        {
            switch (kind)
            {
                case Kind.Float: return typeof(Action<float>);
                case Kind.Int: return typeof(Action<int>);
                case Kind.Bool: return typeof(Action<bool>);
                case Kind.String: return typeof(Action<string>);
                case Kind.Color: return typeof(Action<Color>);
                case Kind.Vector2: return typeof(Action<Vector2>);
                case Kind.Vector3: return typeof(Action<Vector3>);
                case Kind.Vector4: return typeof(Action<Vector4>);
                case Kind.Quaternion: return typeof(Action<Quaternion>);
                default: return typeof(Action<Rect>);
            }
        }

        // The float field's index in the value's indexer (Color: r g b a; vectors and Quaternion: x y z w).
        static int Component(Type type, string name)
        {
            string order = type == typeof(Color) ? "rgba" : type == typeof(Vector2) ? "xy" : type == typeof(Vector3) ? "xyz"
                : type == typeof(Vector4) || type == typeof(Quaternion) ? "xyzw" : "";
            return name.Length == 1 ? order.IndexOf(name[0]) : -1;
        }
    }
}
