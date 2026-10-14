// Loads every type of the given assemblies and compiles every method, on the Mono runtime it runs
// under (pipeline.py loadcheck runs it on the target editor's Mono, in a copy of a Windows player's
// Managed folder with the rewritten assemblies): a type layout the runtime rejects (a vtable it can't
// set up, a method it can't find) shows here instead of when the game first uses the type.
// Usage: LoadCheck <folder> <assembly file>...  Prints "<file>: N types, M methods failed" per file.
using System; using System.Linq; using System.Reflection; using System.Runtime.CompilerServices;
class P { static void Main(string[] a) {
  foreach (var f in a.Skip(1)) {
    Assembly asm; try { asm = Assembly.LoadFrom(System.IO.Path.Combine(a[0], f)); } catch (Exception e) { Console.WriteLine(f + ": load " + e.Message); continue; }
    Type[] types; try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) {
      foreach (var g in e.LoaderExceptions.Where(x => x != null).GroupBy(x => x.Message).Take(10)) Console.WriteLine(f + ": " + g.Count() + "x " + g.Key);
      types = e.Types.Where(t => t != null).ToArray(); }
    int bad = 0;
    foreach (var t in types) {
      if (t.ContainsGenericParameters) continue;
      foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(t.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))) {
        if (m.IsAbstract || m.ContainsGenericParameters) continue;
        try { RuntimeHelpers.PrepareMethod(m.MethodHandle); }
        catch (Exception e) { if (++bad <= 15) Console.WriteLine(f + ": " + t.FullName + "::" + m.Name + " -> " + e.GetType().Name + ": " + e.Message); }
      } }
    Console.WriteLine(f + ": " + types.Length + " types, " + bad + " methods failed"); } } }
