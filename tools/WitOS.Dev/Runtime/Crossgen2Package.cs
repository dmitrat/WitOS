using WitOS.Apps;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The guest's crossgen2 in the boot package (plan step R9.2): the tree's crossgen2, whose assemblies are IL and serve
/// any host, compiled to ReadyToRun for the architecture as the layer's versions are, beside the JIT interface and the
/// JIT the witos build of the runtime makes for the architecture, under /system/Crossgen2 with its launcher, the witos
/// apphost. The installer compiles a version's native code with it on the device (WitOS.Apps.NativeCode). Part of the
/// system, updated with the image; none where the layer stays IL (riscv64, until R9.3).
/// </summary>
internal static class Crossgen2Package
{
    #region Constants

    /// <summary>
    /// crossgen2's directory in the boot package.
    /// </summary>
    public const string DIRECTORY = "system/Crossgen2";

    /// <summary>
    /// crossgen2's launcher in the guest, which the installer runs.
    /// </summary>
    public const string COMMAND = "/" + DIRECTORY + "/crossgen2";

    private const string ASSEMBLY = "crossgen2.dll";

    #endregion

    #region Functions

    /// <summary>
    /// crossgen2's files in the boot package, none where the architecture's layer stays IL.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="output">A directory of the host for the launcher.</param>
    /// <returns>Package paths and the files to place there.</returns>
    /// <exception cref="InvalidOperationException">The tree holds no crossgen2, JIT interface or JIT.</exception>
    public static async Task<IReadOnlyList<(string Name, string Source)>> FilesAsync(string root, KernelArchitecture architecture, string output)
    {
        var native = NativeCodeBuild.For(root, architecture);
        if (native is null)
            return [];
        var coreclr = Path.Combine(root, "artifacts", "runtime-witos", "src", "artifacts", "bin", "coreclr");
        var managed = Path.Combine(coreclr, "linux.x64.Release", "crossgen2");
        var target = Path.Combine(coreclr, $"witos.{architecture.Name}.Release");
        string[] libraries = [$"libjitinterface_{architecture.Name}.so", TargetJit(architecture)];
        if (!File.Exists(Path.Combine(managed, ASSEMBLY)) || libraries.Any(library => !File.Exists(Path.Combine(target, library))))
            throw new InvalidOperationException($"The tree holds no crossgen2 in {managed} or no {string.Join(" or ", libraries)} in {target}. " +
                $"Run: dotnet run --project tools/WitOS.Dev -- runtime-witos --arch {architecture.Name}");
        // Its assemblies and their dependency and runtime files: the host's apphost, symbols and the host's JITs stay out.
        var files = Directory.EnumerateFiles(managed).Where(file => file.EndsWith(".dll", StringComparison.Ordinal) || file.EndsWith(".json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(file => (Path.GetFileName(file), file)).ToList();
        var compiled = await native.CompileAsync("crossgen2", "crossgen2", files);
        Directory.CreateDirectory(output);
        var launcher = Path.Combine(output, "crossgen2");
        await File.WriteAllBytesAsync(launcher, AppLauncher.Write(await File.ReadAllBytesAsync(AppsLayer.ApphostPath(root, architecture)), ASSEMBLY));
        return
        [
            .. compiled.Select(file => ($"{DIRECTORY}/{file.Path}", file.Source)),
            .. libraries.Select(library => ($"{DIRECTORY}/{library}", Path.Combine(target, library))),
            (COMMAND.TrimStart('/'), launcher)
        ];
    }

    // The JIT crossgen2 loads to compile for the architecture on a host of the same one: clrjit_<os>_<target>_<host>,
    // where ARM64's JIT serves every operating system.
    private static string TargetJit(KernelArchitecture architecture) => architecture.Name switch
    {
        "arm64" => "libclrjit_universal_arm64_arm64.so",
        var name => $"libclrjit_unix_{name}_{name}.so"
    };

    #endregion
}
