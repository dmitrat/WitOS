namespace WitOS.Dev.Interfaces;

/// <summary>
/// A development tool command selected by its name on the command line.
/// </summary>
internal interface ICommand
{
    /// <summary>
    /// Runs the command in the repository rooted at <paramref name="root"/>.
    /// </summary>
    /// <param name="root">Repository root that contains WitOS.slnx.</param>
    /// <param name="arguments">Options that follow the command name.</param>
    Task RunAsync(string root, IReadOnlyList<string> arguments);

    /// <summary>
    /// Name typed on the command line.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Option syntax shown in help; empty when the command takes no options.
    /// </summary>
    string Arguments { get; }

    /// <summary>
    /// One-line description shown in help.
    /// </summary>
    string Description { get; }
}
