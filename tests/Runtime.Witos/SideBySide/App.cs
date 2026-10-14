// .NET 8 beside .NET 10 on WitOS (plan steps N3.2, N3.3): the application, built once for .NET 8 and once for .NET 10,
// starts through 10.0's dotnet and hostfxr, which resolve its runtimeconfig and rollForward to one framework of the
// install, and runs on that framework's CoreCLR and libraries. Its argument names the major version the host must pick;
// each check exercises a part of the runtime on WitOS, and the line names what it was built for, what it runs on and
// how many checks passed.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal static class App
{
    private sealed record Sample(string Name, int[] Values);

    private static int Main(string[] arguments)
    {
        (string Name, Func<bool> Check)[] checks =
        [
            ("framework", () => arguments is [var major] && Environment.Version.Major == int.Parse(major, CultureInfo.InvariantCulture) &&
                (AppContext.GetData("FX_DEPS_FILE") as string ?? "").StartsWith($"/dotnet/shared/Microsoft.NETCore.App/{major}.0.",
                    StringComparison.Ordinal)),
            ("platform", () => OperatingSystem.IsOSPlatform("WITOS") && !OperatingSystem.IsLinux() &&
                RuntimeInformation.RuntimeIdentifier.StartsWith("witos-", StringComparison.Ordinal)),
            ("tasks", () => Enumerable.Range(1, 8).Select(index => Task.Run(() => index * index)).Sum(task => task.Result) == 204),
            ("threads", Threads),
            ("exceptions", Exceptions),
            ("collection", Collection),
            ("files", Files),
            ("culture", () => CultureInfo.GetCultureInfo("fr-FR").NumberFormat.NumberDecimalSeparator == ","),
            ("json", () => JsonSerializer.Deserialize<Sample>(JsonSerializer.Serialize(new Sample("witos", [1, 2, 3])))!.Values.Sum() == 6),
            ("regex", () => new Regex(@"^(\w+)-(\d+)$", RegexOptions.Compiled).Match("witos-8").Groups[2].Value == "8")
        ];
        var passed = 0;
        foreach (var (name, check) in checks)
        {
            try
            {
                if (check())
                    ++passed;
                else
                    Console.WriteLine($"[SIDE-BY-SIDE] FAILED: {name}");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[SIDE-BY-SIDE] FAILED: {name}: {exception}");
            }
        }
        var built = typeof(App).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkDisplayName ?? "?";
        Console.WriteLine($"[SIDE-BY-SIDE] App.dll for {built} on {RuntimeInformation.FrameworkDescription}, " +
            $"{RuntimeInformation.ProcessArchitecture}: {passed} of {checks.Length} checks passed");
        return passed == checks.Length ? 0 : 1;
    }

    // Threads that wait for each other through a monitor.
    private static bool Threads()
    {
        var gate = new object();
        var count = 0;
        var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            lock (gate)
            {
                ++count;
                Monitor.PulseAll(gate);
            }
        })).ToList();
        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());
        return count == 4;
    }

    // A filter, a finally and a rethrow across frames.
    private static bool Exceptions()
    {
        var order = new List<string>();
        try
        {
            try
            {
                throw new InvalidOperationException("witos");
            }
            finally
            {
                order.Add("finally");
            }
        }
        catch (InvalidOperationException exception) when (exception.Message == "witos")
        {
            order.Add("caught");
        }
        return order is ["finally", "caught"];
    }

    // A finalizer runs after its object is collected.
    private static bool Collection()
    {
        static WeakReference Allocate() => new(new object());
        var reference = Allocate();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return !reference.IsAlive && GC.CollectionCount(0) > 0;
    }

    // A file of the process's shared memory directory, written and read back.
    private static bool Files()
    {
        var path = Path.Combine("/dev/shm", "side-by-side.txt");
        File.WriteAllText(path, "WitOS runs .NET 8");
        var text = File.ReadAllText(path);
        File.Delete(path);
        return text == "WitOS runs .NET 8" && !File.Exists(path);
    }
}
