using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace DotNetCompatibility;

/// <summary>
/// Assembly loading: by name from the framework, into a collectible context and out again, the resolving event and
/// locations.
/// </summary>
internal static class AssemblyLoadingChecks
{
    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check("AssemblyLoading", "ByName", () =>
        {
            var assembly = Assembly.Load("System.Xml.ReaderWriter");
            var reader = assembly.GetType("System.Xml.XmlReader") ?? Type.GetType("System.Xml.XmlReader, System.Private.Xml");
            return CheckResult.Now(reader is not null, assembly.GetName().Name ?? "");
        });
        yield return new Check("AssemblyLoading", "CollectibleContext", () =>
        {
            var unloaded = LoadAndUnload(out var distinct);
            for (var i = 0; i < 20 && unloaded.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return CheckResult.Now(distinct && !unloaded.IsAlive, $"a distinct assembly {distinct}, unloaded {!unloaded.IsAlive}");
        });
        yield return new Check("AssemblyLoading", "Resolving", () =>
        {
            var asked = "";
            Func<AssemblyLoadContext, AssemblyName, Assembly?> resolving = (_, name) =>
            {
                asked = name.Name ?? "";
                return null;
            };
            AssemblyLoadContext.Default.Resolving += resolving;
            try
            {
                Assembly.Load(new AssemblyName("WitOS.Compatibility.Missing"));
                return CheckResult.Now(false, "a missing assembly loaded");
            }
            catch (FileNotFoundException)
            {
                return CheckResult.Now(asked == "WitOS.Compatibility.Missing", $"the event asked for '{asked}'");
            }
            finally
            {
                AssemblyLoadContext.Default.Resolving -= resolving;
            }
        });
        yield return new Check("AssemblyLoading", "Location", () =>
        {
            var location = typeof(object).Assembly.Location;
            return CheckResult.Now(Path.GetFileName(location) == "System.Private.CoreLib.dll" && File.Exists(location), location);
        });
    }

    // A framework assembly loaded into a collectible context from its path, apart from the default context's copy, and the
    // context unloaded; the context's weak reference.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndUnload(out bool distinct)
    {
        var context = new AssemblyLoadContext("compatibility", isCollectible: true);
        var path = typeof(System.Text.Json.JsonSerializer).Assembly.Location;
        var assembly = context.LoadFromAssemblyPath(path);
        distinct = assembly != typeof(System.Text.Json.JsonSerializer).Assembly && assembly.GetType("System.Text.Json.JsonSerializer") is not null;
        context.Unload();
        return new WeakReference(context);
    }
}
