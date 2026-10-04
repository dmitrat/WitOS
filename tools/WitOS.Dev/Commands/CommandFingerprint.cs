using WitOS.Dev.Interfaces;
using WitOS.Dev.Quality;

namespace WitOS.Dev.Commands;

/// <summary>
/// Builds scenarios with a fixed build id and hashes every section of the produced native images.
/// </summary>
internal sealed class CommandFingerprint : ICommand
{
    #region ICommand

    /// <inheritdoc />
    public Task RunAsync(string root, IReadOnlyList<string> arguments) => ImageFingerprint.RunAsync(root, arguments);

    #endregion

    #region Properties

    /// <inheritdoc />
    public string Name => "fingerprint";

    /// <inheritdoc />
    public string Arguments => "[--output <file>] [--compare <file>] [scenario...]";

    /// <inheritdoc />
    public string Description => "Hash code/data sections of built images, ignoring debug records";

    #endregion
}
