using WitOS.Dev.Interfaces;

namespace WitOS.Dev.Commands;

/// <summary>
/// A command without options that runs one action in the repository.
/// </summary>
internal sealed class CommandAction : ICommand
{
    #region Fields

    private readonly Func<string, Task> m_action;

    #endregion

    #region Constructors

    public CommandAction(string name, string description, Func<string, Task> action)
    {
        Name = name;
        Description = description;
        m_action = action;
    }

    #endregion

    #region ICommand

    /// <inheritdoc />
    public Task RunAsync(string root, IReadOnlyList<string> arguments) => m_action(root);

    #endregion

    #region Properties

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Arguments => "";

    /// <inheritdoc />
    public string Description { get; }

    #endregion
}
