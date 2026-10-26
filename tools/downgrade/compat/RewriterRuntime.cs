using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.LowLevel;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

// Run-time helpers the rewritten game calls (rewriter/PlayMakerData.cs, rewriter/Patches.cs).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    // For PlayMaker's ActionData, whose serialized lists the rewriter makes arrays (rewriter/
    // PlayMakerData.cs): one shared empty array per element type (the constructor's initial values, and
    // what LoadActions puts in place of each empty array the serializer made), and List<T>.Contains.
    public static class EmptyArray<T>
    {
        public static readonly T[] Value = new T[0];
    }

    public static class ArrayHelper
    {
        public static bool Contains<T>(T[] array, T item)
        {
            if (array == null) throw new NullReferenceException();
            return Array.IndexOf(array, item) >= 0;
        }
    }

    // typeof(T) as a static field: the rewriter sends PlayMaker's typeof()s here (CacheTypeOf).
    public static class TypeOf<T>
    {
        public static readonly Type Type = typeof(T);
    }
}
