using System.Security.Cryptography;
using System.Text.Json;
using WitOS.Dev.Host;

namespace WitOS.Dev.Tests.Support;

/// <summary>
/// Pinned LLVM toolchain for the native coverage, ASan and fuzz lanes, and the coverage report.
/// </summary>
internal static class NativeCoverage
{
    #region Constants

    /// <summary>
    /// Pinned LLVM version.
    /// </summary>
    internal const string VERSION = Toolchain.LLVM_VERSION;

    #endregion

    #region Functions

    /// <summary>
    /// Directory of the extracted pinned LLVM toolchain.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Directory path.</returns>
    internal static string DirectoryPath(string root) => Path.Combine(root, ".tools", "llvm-" + VERSION);

    /// <summary>
    /// Extracts the hash-verified LLVM installer under .tools.
    /// </summary>
    /// <param name="root">Repository root.</param>
    internal static async Task PrepareAsync(string root)
    {
        var installer = await Toolchain.RequireLlvmInstallerAsync(root);
        // Always extract verified bytes: cached installed executables are not a source pin.
        await Processes.RequireSuccessAsync(Toolchain.SevenZip(),
            ["x", installer, "-o" + DirectoryPath(root), "-y", "-bso0", "-bsp0"], root);
    }

    /// <summary>
    /// Merges the corpus profile and writes the branch coverage report.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory with the raw profile.</param>
    /// <param name="executable">Instrumented corpus executable.</param>
    internal static async Task ReportAsync(string root, string output, string executable)
    {
        var bin = Path.Combine(DirectoryPath(root), "bin");
        var profile = Path.Combine(output, "pe-corpus.profdata");
        await Processes.RequireSuccessAsync(Path.Combine(bin, "llvm-profdata.exe"),
            ["merge", "-sparse", Path.Combine(output, "pe-corpus.profraw"), "-o", profile], root);
        var sources = new[] { "src/Kernel/pe.c", "src/Kernel/include/witos/unwind_metadata.h" }.Select(p => Path.Combine(root, p)).ToArray();
        var result = await Processes.RunAsync(Path.Combine(bin, "llvm-cov.exe"),
            ["export", executable, "-instr-profile=" + profile, "-summary-only", .. sources], root);
        if (result.ExitCode != 0 || result.TimedOut)
            throw new InvalidDataException("LLVM coverage export failed: " + result.Error);
        await File.WriteAllTextAsync(Path.Combine(output, "native-coverage.json"), result.Output);
        using var json = JsonDocument.Parse(result.Output);
        foreach (var file in json.RootElement.GetProperty("data")[0].GetProperty("files").EnumerateArray())
        {
            var branch = file.GetProperty("summary").GetProperty("branches");
            Console.WriteLine($"COVERAGE: {Path.GetFileName(file.GetProperty("filename").GetString())}: branches {branch.GetProperty("covered")}/{branch.GetProperty("count")} ({branch.GetProperty("percent")}%)");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "native-coverage-profile.json"), JsonSerializer.Serialize(new
        {
            hostOnly = true,
            llvmVersion = VERSION,
            installerSha256 = Toolchain.LLVM_INSTALLER_SHA256,
            addressSanitizer = true,
            cases = 555,
            structuralVerdictCases = 26,
            profile = "clang-cl /O0, LLVM branch instrumentation + ASan; actual common kernel parser and metadata header",
            executableSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable))).ToLowerInvariant(),
            sources = sources.Select(file => new { file, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant() })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    #endregion
}
