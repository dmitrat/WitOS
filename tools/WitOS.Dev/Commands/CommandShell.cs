using WitOS.Dev.Interfaces;
using WitOS.Dev.Kernel;
using WitOS.Dev.Runtime;

namespace WitOS.Dev.Commands;

/// <summary>
/// An interactive WitOS session on this console (plan step C1.4): PowerShell on the terminal beside the services and the
/// .NET install, with a published directory on the data volume when asked; in QEMU's window with the display and the
/// keyboard (C2.4), prepared on one host and booted on another when asked.
/// </summary>
internal sealed class CommandShell : ICommand
{
    #region Constants

    private const string ARGUMENTS =
        "[--arch x64|arm64|riscv64] [--publish <directory>] [--memory <MiB>] [--disk <image>] [--window] [--accel] [--prepare <directory>] [--image <image>]";

    #endregion

    #region ICommand

    /// <exception cref="ArgumentException">The options are not those of <see cref="Arguments"/>, or a prepared image is
    /// asked for with options that build one.</exception>
    public Task RunAsync(string root, IReadOnlyList<string> arguments)
    {
        var (architecture, options) = Parse(arguments);
        return RuntimeShell.RunAsync(root, architecture, options);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads the command's options.
    /// </summary>
    /// <param name="arguments">The arguments after the command's name.</param>
    /// <returns>The architecture and what the person asked for.</returns>
    /// <exception cref="ArgumentException">An option is unknown or lacks its value, or --image comes with --publish or
    /// --prepare, or without --disk.</exception>
    public static (KernelArchitecture Architecture, ShellOptions Options) Parse(IReadOnlyList<string> arguments)
    {
        var architecture = KernelArchitecture.X64;
        string? published = null;
        string? disk = null;
        string? prepare = null;
        string? image = null;
        var window = false;
        var accelerated = false;
        var memory = RuntimePowerShell.MEMORY_MIB;
        for (var i = 0; i < arguments.Count; ++i)
        {
            if (arguments[i] == "--window")
            {
                window = true;
                continue;
            }
            if (arguments[i] == "--accel")
            {
                accelerated = true;
                continue;
            }
            if (i + 1 == arguments.Count)
                throw new ArgumentException($"shell takes only {ARGUMENTS}.");
            var value = arguments[++i];
            switch (arguments[i - 1])
            {
                case "--arch":
                    architecture = KernelArchitecture.Find(value);
                    break;
                case "--publish":
                    published = value;
                    break;
                case "--memory":
                    memory = int.Parse(value);
                    break;
                case "--disk":
                    disk = value;
                    break;
                case "--prepare":
                    prepare = value;
                    break;
                case "--image":
                    image = value;
                    break;
                default:
                    throw new ArgumentException($"shell takes only {ARGUMENTS}.");
            }
        }
        if (image is not null && (published is not null || prepare is not null || disk is null))
            throw new ArgumentException("A prepared --image boots with its --disk, without --publish or --prepare.");
        return (architecture, new ShellOptions(published, memory, disk, window, prepare, image, accelerated));
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public string Name => "shell";

    /// <inheritdoc />
    public string Arguments => ARGUMENTS;

    /// <inheritdoc />
    public string Description =>
        "Boot WitOS with PowerShell on this console beside the services and the .NET install, with a directory published on the host on the data volume, which --disk keeps between sessions (C1.4); " +
        "--window shows the display and takes the keyboard in QEMU's window, --prepare writes the image and the disk for a host with a screen, which boots them with --image and --disk (C2.4); " +
        "--accel runs the guest on the host's hardware virtualization, WHPX or KVM, for a guest of the host's ISA (C2.7)";

    #endregion
}
