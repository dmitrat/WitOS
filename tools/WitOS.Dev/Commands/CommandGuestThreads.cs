using System.Globalization;
using WitOS.Dev.Diagnostics;
using WitOS.Dev.Interfaces;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Commands;

/// <summary>
/// Reads the processes and threads of a guest that ran past its deadline from the memory it left (plan step P3).
/// </summary>
internal sealed class CommandGuestThreads : ICommand
{
    #region ICommand

    /// <exception cref="ArgumentException">An option is missing or unknown.</exception>
    public Task RunAsync(string root, IReadOnlyList<string> arguments)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var symbols = new List<string>();
        var peeks = new List<GuestThreads.Peek>();
        for (var i = 0; i + 1 < arguments.Count; i += 2)
        {
            if (arguments[i] == "--symbols")
                symbols.Add(arguments[i + 1]);
            else if (arguments[i] == "--peek")
                peeks.Add(GuestThreads.Peek.Parse(arguments[i + 1]));
            else
                options[arguments[i]] = arguments[i + 1];
        }
        if (arguments.Count % 2 != 0 || options.Keys.Any(key => key is not ("--core" or "--map" or "--serial" or "--arch" or "--libc" or "--scan")) ||
            !options.ContainsKey("--core") || !options.ContainsKey("--map") || !options.ContainsKey("--serial") ||
            !int.TryParse(options.GetValueOrDefault("--scan", "0"), NumberStyles.None, CultureInfo.InvariantCulture, out var scanWords))
            throw new ArgumentException($"{Name} takes {Arguments}.");
        var architecture = KernelArchitecture.Find(options.GetValueOrDefault("--arch", "arm64"));
        return GuestThreads.RunAsync(root, options["--core"], options["--map"], options["--serial"], architecture,
            options.GetValueOrDefault("--libc"), symbols, peeks, scanWords);
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public string Name => "guest-threads";

    /// <inheritdoc />
    public string Arguments =>
        "--core <file> --map <WitOS.map> --serial <serial.log> [--arch arm64] [--libc <libc.so>] [--symbols <directory>]... " +
        "[--peek <slot>:<address>:<words>]... [--scan <words>]";

    /// <inheritdoc />
    public string Description => "Read the processes and threads of a guest that ran past its deadline from its memory core (WITOS_GUEST_DUMP=1, P3)";

    #endregion
}
