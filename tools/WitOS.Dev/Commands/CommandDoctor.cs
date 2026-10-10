using WitOS.Dev.Kernel;
using WitOS.Dev.Host;
using WitOS.Dev.Interfaces;

namespace WitOS.Dev.Commands;

/// <summary>
/// Reports the repository root, compiler, QEMU version and firmware path.
/// </summary>
internal sealed class CommandDoctor : ICommand
{
    #region ICommand

    /// <inheritdoc />
    public async Task RunAsync(string root, IReadOnlyList<string> arguments)
    {
        Console.WriteLine($"Root: {root}");
        // MSVC builds the native host harnesses on Windows alone; a Linux host builds them with the pinned clang and needs
        // no MSVC anywhere (plan step T2.2).
        Console.WriteLine(OperatingSystem.IsWindows()
            ? $"MSVC: {await Toolchain.FindMsvcAsync(root)} (the native host harnesses)"
            : "MSVC: not needed on this host; the pinned clang builds the native host harnesses");
        Toolchain.RequireClang(root);
        var clang = await Processes.RunAsync(Toolchain.Clang(root), ["--version"], root);
        if (clang.ExitCode != 0 || clang.TimedOut)
        {
            throw new InvalidOperationException($"clang could not start. {clang.Error}");
        }
        Console.WriteLine($"clang: {clang.Output.Split('\n')[0].Trim()} (layer 2: {KernelArchitecture.X64.Triple}, {KernelArchitecture.Arm64.Triple}; " +
            $"kernel: {KernelArchitecture.X64.KernelTriple}, {KernelArchitecture.Arm64.KernelTriple})");
        Toolchain.RequireQemu(root);
        var version = await Processes.RunAsync(Toolchain.Qemu(root), ["--version"], root);
        if (version.ExitCode != 0 || version.TimedOut)
        {
            throw new InvalidOperationException($"QEMU could not start. {version.Error}");
        }
        Console.WriteLine(version.Output.Trim());
        Console.WriteLine($"Firmware: {Toolchain.Firmware(root)}");
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public string Name => "doctor";

    /// <inheritdoc />
    public string Arguments => "";

    /// <inheritdoc />
    public string Description => "Check compiler, QEMU and firmware";

    #endregion
}
