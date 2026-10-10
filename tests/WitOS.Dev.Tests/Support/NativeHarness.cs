using WitOS.Dev.Host;

namespace WitOS.Dev.Tests.Support;

/// <summary>
/// Builds a native C harness for the host the tests run on (plan step T2.2): MSVC with the Windows SDK on Windows, the
/// pinned clang against the system C library on Linux. A harness is plain C17 with its host's memory calls behind
/// <c>#if defined(_WIN32)</c>.
/// </summary>
internal static class NativeHarness
{
    #region Functions

    /// <summary>
    /// Compiles and links the sources into one executable.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Directory for the objects and the executable.</param>
    /// <param name="name">Executable name without an extension.</param>
    /// <param name="sources">C sources, relative to the repository root.</param>
    /// <param name="includes">Include directories, relative to the repository root.</param>
    /// <returns>Path of the executable.</returns>
    public static async Task<string> BuildAsync(string root, string output, string name, string[] sources, string[] includes)
    {
        var paths = sources.Select(source => Path.Combine(root, source)).ToArray();
        if (!OperatingSystem.IsWindows())
        {
            Toolchain.RequireClang(root);
            var program = Path.Combine(output, name);
            await Processes.RequireSuccessAsync(Toolchain.Clang(root),
            [
                "-std=c17", "-O2", "-Wall", "-Wextra", "-Werror", "-D_GNU_SOURCE",
                .. includes.Select(include => "-I" + Path.Combine(root, include)), .. paths, "-o", program
            ], root);
            return program;
        }
        var msvc = await Toolchain.FindMsvcAsync(root);
        var vc = Path.GetFullPath(Path.Combine(msvc, "../../.."));
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits/10");
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var exe = Path.Combine(output, name + ".exe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"),
        [
            "/nologo", "/MD", "/TC", "/std:c17", "/W4", "/WX", "/O2",
            "/I" + Path.Combine(vc, "include"), "/I" + Path.Combine(sdk, "Include", version, "ucrt"),
            "/I" + Path.Combine(sdk, "Include", version, "um"), "/I" + Path.Combine(sdk, "Include", version, "shared"),
            .. includes.Select(include => "/I" + Path.Combine(root, include)),
            "/Fo" + output + "/", "/Fe" + exe, .. paths,
            "/link", "/LIBPATH:" + Path.Combine(vc, "lib/x64"), "/LIBPATH:" + Path.Combine(sdk, "Lib", version, "ucrt/x64"),
            "/LIBPATH:" + Path.Combine(sdk, "Lib", version, "um/x64"), "kernel32.lib"
        ], root);
        return exe;
    }

    #endregion
}
