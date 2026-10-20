using WitOS.Apps;
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
    /// The SDK's catalog entry, which dotnet must list from the layer before the REPL starts.
    /// </summary>
    public const string SDK_ID = "DotNet.Sdk";

    /// <summary>
    /// The REPL's prompt, which the runner types after.
    /// </summary>
    public const string PROMPT = "> ";

    /// <summary>
    /// The guest's memory: Roslyn's compiler, its workspaces and MEF through the JIT.
    /// </summary>
    public const int MEMORY_MIB = 1024;

    private const long DISK_BYTES = 1536L << 20; // the .NET SDK takes 400 MiB of the layer (C2.5); the disk is written from one array, so below 2 GiB

    private const int TIMEOUT_SECONDS = 3600;

    private const string SCRIPT_TEXT = "var fromScript = \"loaded from a script\";\n";

    private const string FILE_TEXT = "written on the host";

    // dotnet lists the SDKs global.json names (C2.5): the layer's, under its root.
    private const string LIST_SDKS = "/dotnet/dotnet --list-sdks";

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
        var sdk = (await AppCatalog.ReadAsync(root)).Single(entry => entry.Id == SDK_ID);
        if (!AppCatalog.Serves(sdk, architecture))
        {
            // The SDK's publication is per RID (its ReadyToRun code is the RID's, which another ISA's CoreCLR cannot
            // load on WitOS, whose plain memory never executes), and none exists for this architecture.
            Console.WriteLine($"CSharpRepl on witos-{architecture.Name}: not run, no .NET SDK publication for {architecture.Name}.");
            return;
        }
        var package = (await RuntimeApps.PackageAsync(root, output, architecture, [], ["cd /data", LIST_SDKS, CSHARPREPL])).ToList();
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
        await RuntimeApps.BootAsync(root, architecture, scenario, RequiredLines(sdk), TIMEOUT_SECONDS, volume, DISK_BYTES, MEMORY_MIB, package, Script());
        Console.WriteLine($"CSharpRepl on witos-{architecture.Name}: C# typed into the REPL compiled and ran in the guest through Roslyn's " +
            "scripting, from the data volume's layer through its launcher.");
    }

    /// <summary>
    /// What the runner types: the terminal probe's line, then each input at the REPL's prompt. An input ends with a space
    /// before Enter where it ends with a name, so that the completion list closes and Enter submits; Enter goes by
    /// itself once the REPL echoed the input, since PrettyPrompt takes keys that arrive together for a paste, whose Enter
    /// starts a new line instead of submitting (measured on the Linux host: typed at once, the first expression was never
    /// evaluated; with the keys 50 ms apart it answered in 3 s).
    /// </summary>
    /// <returns>The inputs.</returns>
    public static IReadOnlyList<TerminalInput> Script()
    {
        var inputs = new List<TerminalInput> { new("[TERMINAL] type a line", RuntimeTerminal.PROBE_LINE + "\n") };
        foreach (var text in new[]
                 {
                     "\"witos-\" + (6 * 7)",
                     "class Greeter { public string Hi(string name) => \"hi \" + name; }",
                     "new Greeter().Hi(\"witos\")",
                     "System.Runtime.InteropServices.RuntimeInformation.OSDescription ",
                     "System.IO.File.ReadAllText(\"/data/csx/hello.txt\")",
                     "#load \"/data/csx/hello.csx\"",
                     "fromScript ",
                     "int wrong = \"text\";",
                     "exit"
                 })
        {
            inputs.Add(new(PROMPT, text));
            inputs.Add(new(text.TrimEnd(), "\r"));
        }
        return inputs;
    }

    /// <summary>
    /// The lines the scenario requires: dotnet's listing of the layer's SDK, each answer of the REPL, the compiler's
    /// error and the REPL's exit with 0.
    /// </summary>
    /// <param name="sdk">The SDK's catalog entry.</param>
    /// <returns>The required lines.</returns>
    public static string[] RequiredLines(AppEntry sdk) =>
    [
        $"{sdk.Version} [{AppsLayer.MOUNT}/{AppsLayer.ROOT}/{sdk.Id}/{sdk.Version}/{SdkRoots.SDK}]",
        $"[APPS-INIT] {LIST_SDKS} exited with 0",
        "\"witos-42\"",
        "\"hi witos\"",
        "\"WitOS",
        $"\"{FILE_TEXT}\"",
        "\"loaded from a script\"",
        "CS0029",
        $"[APPS-INIT] {CSHARPREPL}  exited with 0",
        "[APPS-INIT] 2 commands, 0 failed"
    ];

    #endregion
}
