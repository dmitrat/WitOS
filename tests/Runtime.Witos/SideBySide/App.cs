// .NET 8 beside .NET 10 on WitOS (plan step N3.2): an application built for .NET 8 starts through 10.0's dotnet and
// hostfxr, which resolve its runtimeconfig to the 8.0 framework of the install, and runs on that framework's CoreCLR and
// libraries. Each check exercises a part of the 8.0 runtime on WitOS; the line names the framework and how many passed.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
            ("framework", () => (AppContext.GetData("FX_DEPS_FILE") as string ?? "").StartsWith("/dotnet/shared/Microsoft.NETCore.App/8.0.",
                StringComparison.Ordinal) && Environment.Version.Major == 8 && arguments is ["witos"]),
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
        Console.WriteLine($"[SIDE-BY-SIDE] App.dll on {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}: " +
            $"{passed} of {checks.Length} checks passed");
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
