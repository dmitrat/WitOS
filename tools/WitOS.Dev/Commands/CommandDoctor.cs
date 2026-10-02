using WitOS.Dev.Host;
using WitOS.Dev.Interfaces;

namespace WitOS.Dev.Commands;

/// <summary>
/// Reports the repository root, compiler, QEMU version and firmware path.
/// </summary>
internal sealed class CommandDoctor : ICommand
{
    #region ICommand

    public async Task RunAsync(string root, IReadOnlyList<string> arguments)
    {
        Console.WriteLine($"Root: {root}");
        Console.WriteLine($"MSVC: {await Toolchain.FindMsvcAsync(root)}");
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

    public string Name => "doctor";

    public string Arguments => "";

    public string Description => "Check compiler, QEMU and firmware";

    #endregion
}
