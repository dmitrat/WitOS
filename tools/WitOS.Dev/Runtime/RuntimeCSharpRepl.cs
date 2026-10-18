using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The C# REPL scenario (plan step C2.5, ADR 0027): CSharpRepl, a base application of the data volume's layer, started
/// by its launcher /data/apps/bin/csharprepl on the terminal beside the device services and the .NET install; the runner
/// types a session into it — an expression, a class and a call, the system's description, a file of the volume, a
/// script loaded with #load, a compilation error — and exits. The required lines are the REPL's answers in the
/// terminal's transcript. NO_COLOR keeps them plain words, as the other runners' no-colour options do.
/// </summary>
internal static class RuntimeCSharpRepl
{
    #region Constants

    /// <summary>
    /// How /bin/init starts the REPL: the layer's launcher.
    /// </summary>
    public const string CSHARPREPL = "/data/apps/bin/csharprepl";

    /// <summary>
    /// The REPL's prompt, which the runner types after.
    /// </summary>
    public const string PROMPT = "> ";

    /// <summary>
    /// The guest's memory: Roslyn's compiler, its workspaces and MEF through the JIT.
    /// </summary>
    public const int MEMORY_MIB = 1024;

    private const long DISK_BYTES = 1024L << 20;

    private const int TIMEOUT_SECONDS = 3600;

    private const string SCRIPT_TEXT = "var fromScript = \"loaded from a script\";\n";

    private const string FILE_TEXT = "written on the host";

    #endregion

    #region Functions

    /// <summary>
    /// Boots the scenario on the services and the install earlier runs built.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>When the boot passed.</returns>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        var scenario = KernelImageBuilder.RUNTIME_CSHARPREPL_SCENARIO;
        var output = Path.Combine(root, "artifacts", architecture.Name, scenario);
        Directory.CreateDirectory(output);
        var package = (await RuntimeApps.PackageAsync(root, output, architecture, [], ["cd /data", CSHARPREPL])).ToList();
        var environmentIndex = package.FindIndex(file => file.Name == "etc/environment");
        var environment = Path.Combine(output, "environment");
        await File.WriteAllTextAsync(environment, await File.ReadAllTextAsync(package[environmentIndex].Source) + "NO_COLOR=1\n");
        package[environmentIndex] = ("etc/environment", environment);
        var volume = await AppsDisk.VolumeFilesAsync(root, architecture, output);
        var script = Path.Combine(output, "hello.csx");
        await File.WriteAllTextAsync(script, SCRIPT_TEXT);
        var text = Path.Combine(output, "hello.txt");
        await File.WriteAllTextAsync(text, FILE_TEXT);
        volume.Add(("csx/hello.csx", script));
        volume.Add(("csx/hello.txt", text));
        foreach (var log in new[] { "serial", "terminal" })
        {
            var path = Path.Combine(root, "artifacts", "logs", $"{architecture.Name}-{scenario}-{MEMORY_MIB}.{log}.log");
            if (File.Exists(path))
                File.Delete(path);
        }
        await RuntimeApps.BootAsync(root, architecture, scenario, RequiredLines(), TIMEOUT_SECONDS, volume, DISK_BYTES, MEMORY_MIB, package, Script());
        Console.WriteLine($"CSharpRepl on witos-{architecture.Name}: C# typed into the REPL compiled and ran in the guest through Roslyn's " +
            "scripting, from the data volume's layer through its launcher.");
    }

    /// <summary>
    /// What the runner types: the terminal probe's line, then each input at the REPL's prompt. An input ends with a space
    /// before Enter where it ends with a name, so that the completion list closes and Enter submits.
    /// </summary>
    /// <returns>The inputs.</returns>
    public static IReadOnlyList<TerminalInput> Script() =>
    [
        new("[TERMINAL] type a line", RuntimeTerminal.PROBE_LINE + "\n"),
        new(PROMPT, "\"witos-\" + (6 * 7)\r"),
        new(PROMPT, "class Greeter { public string Hi(string name) => \"hi \" + name; }\r"),
        new(PROMPT, "new Greeter().Hi(\"witos\")\r"),
        new(PROMPT, "System.Runtime.InteropServices.RuntimeInformation.OSDescription \r"),
        new(PROMPT, "System.IO.File.ReadAllText(\"/data/csx/hello.txt\")\r"),
        new(PROMPT, "#load \"/data/csx/hello.csx\"\r"),
        new(PROMPT, "fromScript \r"),
        new(PROMPT, "int wrong = \"text\";\r"),
        new(PROMPT, "exit\r")
    ];

    /// <summary>
    /// The lines the scenario requires: each answer of the REPL, the compiler's error and the REPL's exit with 0.
    /// </summary>
    /// <returns>The required lines.</returns>
    public static string[] RequiredLines() =>
    [
        "\"witos-42\"",
        "\"hi witos\"",
        "\"WitOS",
        $"\"{FILE_TEXT}\"",
        "\"loaded from a script\"",
        "CS0029",
        $"[APPS-INIT] {CSHARPREPL}  exited with 0",
        "[APPS-INIT] 1 commands, 0 failed"
    ];

    #endregion
}
