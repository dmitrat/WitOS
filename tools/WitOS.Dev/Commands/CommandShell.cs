using WitOS.Dev.Interfaces;
using WitOS.Dev.Kernel;
using WitOS.Dev.Runtime;

namespace WitOS.Dev.Commands;

/// <summary>
/// An interactive WitOS session on this console (plan step C1.4): PowerShell on the terminal beside the services and the
/// .NET install, with a published directory on the data volume when asked.
/// </summary>
internal sealed class CommandShell : ICommand
{
    #region ICommand

    /// <exception cref="ArgumentException">The options are not an optional --arch, --publish and --memory, each with a
    /// value.</exception>
    public Task RunAsync(string root, IReadOnlyList<string> arguments)
    {
        var architecture = KernelArchitecture.X64;
        string? published = null;
        string? disk = null;
        var memory = RuntimePowerShell.MEMORY_MIB;
        for (var i = 0; i < arguments.Count; i += 2)
        {
            if (i + 1 == arguments.Count)
                throw new ArgumentException($"{Name} takes only {Arguments}.");
            switch (arguments[i])
            {
                case "--arch":
                    architecture = KernelArchitecture.Find(arguments[i + 1]);
                    break;
                case "--publish":
                    published = arguments[i + 1];
                    break;
                case "--memory":
                    memory = int.Parse(arguments[i + 1]);
                    break;
                case "--disk":
                    disk = arguments[i + 1];
                    break;
                default:
                    throw new ArgumentException($"{Name} takes only {Arguments}.");
            }
        }
        return RuntimeShell.RunAsync(root, architecture, published, memory, disk);
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public string Name => "shell";

    /// <inheritdoc />
    public string Arguments => "[--arch x64|arm64] [--publish <directory>] [--memory <MiB>] [--disk <image>]";

    /// <inheritdoc />
    public string Description =>
        "Boot WitOS with PowerShell on this console beside the services and the .NET install, with a directory published on the host on the data volume, which --disk keeps between sessions (C1.4)";

    #endregion
}
