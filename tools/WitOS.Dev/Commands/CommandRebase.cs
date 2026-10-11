using WitOS.Dev.Interfaces;
using WitOS.Dev.Runtime;

namespace WitOS.Dev.Commands;

/// <summary>
/// The rehearsal of an upgrade (plan step R8): the witos patch set carried to another dotnet/runtime tag, --tag, and
/// with --apply made the pin when it carries without conflicts.
/// </summary>
internal sealed class CommandRebase : ICommand
{
    #region ICommand

    /// <exception cref="ArgumentException">The options are not --tag with a tag and an optional --apply.</exception>
    public Task RunAsync(string root, IReadOnlyList<string> arguments)
    {
        var apply = arguments.Count == 3 && arguments[2] == "--apply";
        if ((arguments.Count != 2 && !apply) || arguments[0] != "--tag" || !arguments[1].StartsWith('v'))
            throw new ArgumentException($"{Name} takes only {Arguments}.");
        return RuntimeRebase.RunAsync(root, arguments[1], apply);
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public string Name => "runtime-rebase";

    /// <inheritdoc />
    public string Arguments => "--tag v<version> [--apply]";

    /// <inheritdoc />
    public string Description =>
        "Carry the witos patch set from the pinned dotnet/runtime to another tag, report unchanged, merged and conflicting files, and with --apply move the pin (R8)";

    #endregion
}
