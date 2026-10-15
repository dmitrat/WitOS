using WitOS.Dev.Interfaces;
using WitOS.Dev.Runtime;

namespace WitOS.Dev.Commands;

/// <summary>
/// Publishes the compatibility suite and the portable application on this host with the stock SDK and no RID (plan
/// step N4.4), with a manifest of every file's bytes, for runtime-published on any host.
/// </summary>
internal sealed class CommandPublishApps : ICommand
{
    #region ICommand

    /// <exception cref="ArgumentException">The options are not an optional --output with a value.</exception>
    public Task RunAsync(string root, IReadOnlyList<string> arguments)
    {
        var output = arguments.Count switch
        {
            0 => RuntimePublished.DEFAULT_DIRECTORY,
            2 when arguments[0] == "--output" => arguments[1],
            _ => throw new ArgumentException($"{Name} takes only {Arguments}.")
        };
        return RuntimePublished.PublishAsync(root, output);
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public string Name => "publish-apps";

    /// <inheritdoc />
    public string Arguments => "[--output <directory>]";

    /// <inheritdoc />
    public string Description =>
        "Publish the compatibility suite and the portable application with the stock SDK and no RID, as Visual Studio would, with a manifest of their bytes (N4.4)";

    #endregion
}
