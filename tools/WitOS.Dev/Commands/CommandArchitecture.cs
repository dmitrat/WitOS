using WitOS.Dev.Interfaces;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Commands;

/// <summary>
/// Command for one kernel architecture, selected with --arch; x64 when the option is absent.
/// </summary>
internal sealed class CommandArchitecture : ICommand
{
    #region Fields

    private readonly Func<string, KernelArchitecture, Task> m_action;

    #endregion

    #region Constructors

    public CommandArchitecture(string name, string description, Func<string, KernelArchitecture, Task> action)
    {
        Name = name;
        Description = description;
        m_action = action;
    }

    #endregion

    #region ICommand

    /// <exception cref="ArgumentException">The options are not a single --arch with a known architecture.</exception>
    public Task RunAsync(string root, IReadOnlyList<string> arguments)
    {
        var architecture = arguments.Count switch
        {
            0 => KernelArchitecture.X64,
            2 when arguments[0] == "--arch" => KernelArchitecture.Find(arguments[1]),
            _ => throw new ArgumentException($"{Name} takes only {Arguments}.")
        };
        return m_action(root, architecture);
    }

    #endregion

    #region Properties

    public string Name { get; }

    public string Arguments => "[--arch x64|arm64]";

    public string Description { get; }

    #endregion
}
