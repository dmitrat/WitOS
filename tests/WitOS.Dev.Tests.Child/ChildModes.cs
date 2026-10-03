using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace WitOS.Dev.Tests.Child;

/// <summary>
/// Behaviors the host process tests start in a separate process: descendants, pipe ownership, output volume and
/// cooperative stop protocols.
/// </summary>
internal static class ChildModes
{
    #region Constants

    /// <summary>
    /// Exit code for an unknown or missing mode.
    /// </summary>
    public const int UNKNOWN_MODE = 64;

    private const string ENVIRONMENT_VARIABLE = "WITOS_Q0_TEST_ENV";

    #endregion

    #region Functions

    /// <summary>
    /// Runs one mode.
    /// </summary>
    /// <param name="mode">Mode name.</param>
    /// <param name="arguments">Mode arguments.</param>
    /// <returns>Process exit code.</returns>
    public static Task<int> RunAsync(string mode, string[] arguments) => mode switch
    {
        "leaf" => LeafAsync(arguments[0], keepOutput: true),
        "leaf-no-pipes" => LeafAsync(arguments[0], keepOutput: false),
        "launcher" => LauncherAsync(arguments[0], inheritOutput: true),
        "launcher-no-pipes" => LauncherAsync(arguments[0], inheritOutput: false),
        "export-pipes" => Task.FromResult(ExportPipes(int.Parse(arguments[0]), arguments[1])),
        "env" => Task.FromResult(PrintEnvironment()),
        "echo" => Task.FromResult(Echo(arguments)),
        "flood" => Task.FromResult(Flood()),
        "burst-wait" => BurstWaitAsync(arguments[0]),
        "stop-handshake" => StopHandshakeAsync(),
        "stop-input" => StopInputAsync(),
        "wait" => WaitAsync(),
        "capture-limit" => CaptureLimitAsync(arguments[0]),
        _ => Task.FromResult(Unknown(mode))
    };

    #endregion

    #region Tools

    // A launcher waits for the file and the tests read it after a kill: publish it only complete, never as a
    // created but still empty file.
    private static void PublishProcessId(string pidFile)
    {
        var temporary = pidFile + ".tmp";
        File.WriteAllText(temporary, Environment.ProcessId.ToString());
        File.Move(temporary, pidFile, overwrite: true);
    }

    private static async Task<int> LeafAsync(string pidFile, bool keepOutput)
    {
        if (!keepOutput)
        {
            ChildHandles.CloseOutput();
        }
        PublishProcessId(pidFile);
        await Task.Delay(8000);
        if (keepOutput)
        {
            Console.WriteLine("leaf finished");
        }
        return 0;
    }

    private static async Task<int> LauncherAsync(string pidFile, bool inheritOutput)
    {
        if (!inheritOutput)
        {
            ChildHandles.MakeOutputNonInheritable();
        }
        var child = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        child.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        child.ArgumentList.Add(inheritOutput ? "leaf" : "leaf-no-pipes");
        child.ArgumentList.Add(pidFile);
        using var process = Process.Start(child)!;
        while (!File.Exists(pidFile))
        {
            await Task.Delay(10);
        }
        return 0;
    }

    private static int ExportPipes(int receiver, string report)
    {
        Console.WriteLine("before exported pipes");
        ChildHandles.ExportOutput(receiver, report);
        return 0;
    }

    private static int PrintEnvironment()
    {
        Console.Write(Environment.GetEnvironmentVariable(ENVIRONMENT_VARIABLE));
        return 0;
    }

    private static int Echo(string[] arguments)
    {
        Console.WriteLine(JsonSerializer.Serialize(arguments));
        Console.Error.Write("stderr-tail");
        return 7;
    }

    private static int Flood()
    {
        Console.Write(new string('a', 200000));
        Console.Error.Write(new string('b', 200000));
        return 0;
    }

    private static async Task<int> BurstWaitAsync(string pidFile)
    {
        PublishProcessId(pidFile);
        Console.WriteLine("before burst");
        var chunk = new string('x', 65536);
        for (var n = 0; n < 32; ++n)
        {
            Console.Write(chunk);
            Console.Error.Write(chunk);
        }
        await Task.Delay(30000);
        return 0;
    }

    private static async Task<int> StopHandshakeAsync()
    {
        if (await Console.In.ReadLineAsync() != "hello")
        {
            return 26;
        }
        Console.WriteLine("ready");
        return await Console.In.ReadLineAsync() == "quit" ? 25 : 27;
    }

    private static async Task<int> StopInputAsync()
    {
        Console.WriteLine("before cooperative stop");
        var command = await Console.In.ReadLineAsync();
        return command == "quit" ? 23 : 24;
    }

    private static async Task<int> WaitAsync()
    {
        Console.WriteLine("before wait");
        await Task.Delay(30000);
        return 0;
    }

    private static async Task<int> CaptureLimitAsync(string streamName)
    {
        var stream = streamName == "stdout" ? Console.Out : Console.Error;
        var chunk = new string('x', 65536);
        for (var i = 0; i < 150; ++i)
        {
            stream.Write(chunk);
        }
        await Task.Delay(30000);
        return 0;
    }

    private static int Unknown(string mode)
    {
        Console.Error.WriteLine("Unknown child mode: " + mode);
        return UNKNOWN_MODE;
    }

    #endregion
}
