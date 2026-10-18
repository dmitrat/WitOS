using System;
using System.Collections.Generic;
using System.IO;
using WitOS.Apps;

namespace WitOS.Installer;

/// <summary>
/// What the installer works on: the layer, the feed, if any, and the apphost the launchers are written from.
/// </summary>
/// <param name="Layer">The layer's root, /data/apps in the guest.</param>
/// <param name="Feed">The folder feed, or null.</param>
/// <param name="Apphost">The witos apphost, beside the installer in the boot package.</param>
internal sealed record InstallerOptions(string Layer, string? Feed, string Apphost)
{
    #region Constants

    /// <summary>
    /// The apphost's file beside the installer.
    /// </summary>
    public const string APPHOST = "apphost";

    private const string USAGE =
        "witos-apps [list | install <application> [<version>] | use <application> <version>] [--feed <directory>] [--layer <directory>] [--apphost <file>]";

    #endregion

    #region Functions

    /// <summary>
    /// Reads the command line: a verb and its operands, then the options; no verb opens the screen.
    /// </summary>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The verb (empty for the screen), its operands and the options.</returns>
    /// <exception cref="ArgumentException">An option is unknown or lacks its value, or a verb lacks its operands.</exception>
    public static (string Verb, IReadOnlyList<string> Operands, InstallerOptions Options) Parse(IReadOnlyList<string> arguments)
    {
        var layer = AppsDirectory.GUEST_ROOT;
        string? feed = null;
        var apphost = Path.Combine(AppContext.BaseDirectory, APPHOST);
        var words = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                words.Add(argument);
                continue;
            }
            if (i + 1 == arguments.Count)
                throw new ArgumentException($"{argument} needs a value: {USAGE}");
            var value = arguments[++i];
            switch (argument)
            {
                case "--feed":
                    feed = value;
                    break;
                case "--layer":
                    layer = value;
                    break;
                case "--apphost":
                    apphost = value;
                    break;
                default:
                    throw new ArgumentException($"Unknown option {argument}: {USAGE}");
            }
        }
        var verb = words.Count > 0 ? words[0] : "";
        var operands = words.Count > 1 ? words.GetRange(1, words.Count - 1) : [];
        var valid = verb switch
        {
            "" or "list" => operands.Count == 0,
            "install" => operands.Count is 1 or 2 && feed is not null,
            "use" => operands.Count == 2,
            _ => false
        };
        if (!valid)
            throw new ArgumentException($"Usage: {USAGE}");
        return (verb, operands, new InstallerOptions(layer, feed, apphost));
    }

    #endregion
}
