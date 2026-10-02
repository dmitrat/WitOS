using WitOS.Dev.Interfaces;
using WitOS.Dev.Quality;

namespace WitOS.Dev.Commands;

/// <summary>
/// Applies or verifies the repository style for the files listed in build/format.json.
/// </summary>
internal sealed class CommandFormat : ICommand
{
    #region Fields

    private readonly bool m_check;

    #endregion

    #region Constructors

    public CommandFormat(bool check)
    {
        m_check = check;
    }

    #endregion

    #region ICommand

    public Task RunAsync(string root, IReadOnlyList<string> arguments) => SourceFormat.RunAsync(root, m_check);

    #endregion

    #region Properties

    public string Name => m_check ? "format-check" : "format";

    public string Arguments => "";

    public string Description => m_check
        ? "Verify the repository style without changing files"
        : "Apply the repository style to files listed in build/format.json";

    #endregion
}
