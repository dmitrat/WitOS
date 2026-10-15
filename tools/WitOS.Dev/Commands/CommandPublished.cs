using WitOS.Dev.Interfaces;
using WitOS.Dev.Kernel;
using WitOS.Dev.Runtime;

namespace WitOS.Dev.Commands;

/// <summary>
/// The published applications on witos (plan step N4.4): the directory publish-apps wrote, on this host or another,
/// runs as exactly those bytes on the runtime an earlier run built.
/// </summary>
internal sealed class CommandPublished : ICommand
{
    #region ICommand

    /// <exception cref="ArgumentException">The options are not an optional --arch and an optional --published, each
    /// with a value.</exception>
    public Task RunAsync(string root, IReadOnlyList<string> arguments)
    {
        var architecture = KernelArchitecture.X64;
        string? published = null;
        for (var i = 0; i < arguments.Count; i += 2)
        {
            if (i + 1 == arguments.Count)
                throw new ArgumentException($"{Name} takes only {Arguments}.");
            switch (arguments[i])
            {
                case "--arch":
                    architecture = KernelArchitecture.Find(arguments[i + 1]);
                    break;
                case "--published":
                    published = arguments[i + 1];
                    break;
                default:
                    throw new ArgumentException($"{Name} takes only {Arguments}.");
            }
        }
        return RuntimePublished.RunAsync(root, architecture, published);
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public string Name => "runtime-published";

    /// <inheritdoc />
    public string Arguments => "[--arch x64|arm64] [--published <directory>]";

    /// <inheritdoc />
    public string Description =>
        "Boot the compatibility suite and the portable application as the bytes publish-apps wrote, on this host or on Windows, and require the suite's checks to end as runtime-compatibility recorded (N4.4)";

    #endregion
}
