using WitOS.Dev.Interfaces;
using WitOS.Dev.Quality;

namespace WitOS.Dev.Commands;

/// <summary>
/// Builds scenarios with a fixed build id and hashes every section of the produced native images.
/// </summary>
internal sealed class CommandFingerprint : ICommand
{
    #region ICommand

    public Task RunAsync(string root, IReadOnlyList<string> arguments) => ImageFingerprint.RunAsync(root, arguments);

    #endregion

    #region Properties

    public string Name => "fingerprint";

    public string Arguments => "[--output <file>] [--compare <file>] [scenario...]";

    public string Description => "Hash code/data sections of built images, ignoring debug records";

    #endregion
}
