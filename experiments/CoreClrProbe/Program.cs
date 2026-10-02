using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace WitOS.CoreClrProbe;

internal static class Program
{
    private static int finalizers;
    private sealed class Finalizable { ~Finalizable() => Interlocked.Increment(ref finalizers); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference MakeFinalizable() => new(new Finalizable());
    private static T Identity<T>(T value) => value;
    private static void Require(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("CoreCLR probe failed: " + name);
        Console.WriteLine("[CORECLR-PASS] " + name);
    }

    private static async Task<int> Main()
    {
        Require(RuntimeFeature.IsDynamicCodeSupported && RuntimeFeature.IsDynamicCodeCompiled, "JitRuntimeIdentity");
        var method = new DynamicMethod("Add", typeof(int), [typeof(int), typeof(int)]);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ret);
        var add = method.CreateDelegate<Func<int, int, int>>();
        Require(add(731, 11) == 742, "DynamicMethodExecution");
        var generic = typeof(Program).GetMethod(nameof(Identity), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(Guid));
        var value = Guid.NewGuid();
        Require((Guid)generic.Invoke(null, [value])! == value, "RuntimeGenericReflection");
        var weak = MakeFinalizable();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); GC.Collect();
        Require(finalizers == 1 && !weak.IsAlive, "GcAndFinalization");
        var roots = new object[] { new byte[8192], new List<int> { 17, 29 } };
        var finished = false;
        try { throw new ApplicationException("payload"); }
        catch (ApplicationException e) when (e.Message == "payload")
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        }
        finally { finished = true; }
        Require(finished && ((List<int>)roots[1])[1] == 29, "ExceptionsAndRoots");
        var values = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => { GC.Collect(); return i * i; })));
        Require(values.Sum() == 140, "ThreadPoolTaskAndGc");
        var context = new AssemblyLoadContext("portable-probe", isCollectible: true);
        using (var bytes = File.OpenRead(Assembly.GetExecutingAssembly().Location))
        {
            var loaded = context.LoadFromStream(bytes);
            Require(loaded != Assembly.GetExecutingAssembly() && AssemblyLoadContext.GetLoadContext(loaded) == context, "AssemblyLoadContextFromStream");
        }
        context.Unload();
        // Hosted evidence only: verify the actual source-built native modules.
        // The IL workload itself remains an ordinary SDK/TFM assembly.
        var expected = Environment.GetEnvironmentVariable("WITOS_CORECLR_REFERENCE");
        if (expected is not null)
        {
            using var process = Process.GetCurrentProcess();
            var modules = process.Modules.Cast<ProcessModule>().ToArray();
            foreach (var name in new[] { "coreclr.dll", "clrjit.dll" })
            {
                var module = modules.Single(m => string.Equals(m.ModuleName, name, StringComparison.OrdinalIgnoreCase));
                Require(string.Equals(Path.GetFullPath(module.FileName), Path.GetFullPath(Path.Combine(expected, name)), StringComparison.OrdinalIgnoreCase), "SourceModule:" + name);
            }
        }
        GC.KeepAlive(roots);
        return 42;
    }
}
