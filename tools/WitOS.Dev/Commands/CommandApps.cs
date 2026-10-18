using WitOS.Dev.Interfaces;
using WitOS.Dev.Kernel;
using WitOS.Dev.Runtime;

namespace WitOS.Dev.Commands;

/// <summary>
/// The layer of base applications on a data disk (ADR 0027, plan step C4.1): made, brought up to the catalog, or switched
/// to an installed version.
/// </summary>
internal sealed class CommandApps : ICommand
{
    #region Constants

    private const string ARGUMENTS = "[--arch x64|arm64|riscv64] --disk <image> [--use <application>=<version>]";

    #endregion

    #region ICommand

    /// <exception cref="ArgumentException">The options are not those of <see cref="Arguments"/>.</exception>
    public Task RunAsync(string root, IReadOnlyList<string> arguments)
    {
        var (architecture, disk, use) = Parse(arguments);
        return AppsDisk.RunAsync(root, architecture, disk, use);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Reads the command's options.
    /// </summary>
    /// <param name="arguments">The arguments after the command's name.</param>
    /// <returns>The architecture, the disk and the version to use, if any.</returns>
    /// <exception cref="ArgumentException">An option is unknown or lacks its value, the disk is missing, or --use names no
    /// application and version.</exception>
    public static (KernelArchitecture Architecture, string Disk, (string Id, string Version)? Use) Parse(IReadOnlyList<string> arguments)
    {
        var architecture = KernelArchitecture.X64;
        string? disk = null;
        (string, string)? use = null;
        for (var i = 0; i < arguments.Count; i += 2)
        {
            if (i + 1 == arguments.Count)
                throw new ArgumentException($"apps takes only {ARGUMENTS}.");
            var value = arguments[i + 1];
            switch (arguments[i])
            {
                case "--arch":
                    architecture = KernelArchitecture.Find(value);
                    break;
                case "--disk":
                    disk = value;
                    break;
                case "--use":
                    var parts = value.Split('=');
                    if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                        throw new ArgumentException($"--use takes <application>=<version>, not {value}.");
                    use = (parts[0], parts[1]);
                    break;
                default:
                    throw new ArgumentException($"apps takes only {ARGUMENTS}.");
            }
        }
        return (architecture, disk ?? throw new ArgumentException($"apps needs --disk: {ARGUMENTS}."), use);
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public string Name => "apps";

    /// <inheritdoc />
    public string Arguments => ARGUMENTS;

    /// <inheritdoc />
    public string Description =>
        "Make a data disk with the layer of base applications, bring an existing disk's layer up to build/apps (a new version beside the old) or switch an application to an installed version with --use (ADR 0027, C4.1)";

    #endregion
}
