using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WitOS.Apps;

namespace WitOS.Installer;

/// <summary>
/// The installer's commands for scripts, without the screen: list the layer and the feed, install a feed's version, use
/// an installed version.
/// </summary>
internal static class InstallerCommands
{
    #region Functions

    /// <summary>
    /// Runs a verb.
    /// </summary>
    /// <param name="verb">list, install or use.</param>
    /// <param name="operands">The verb's operands.</param>
    /// <param name="options">The layer, the feed and the apphost.</param>
    /// <param name="output">Where the lines go.</param>
    /// <param name="error">Where a failure goes.</param>
    /// <returns>The exit code: 0, or 1 when the verb failed.</returns>
    public static async Task<int> RunAsync(string verb, IReadOnlyList<string> operands, InstallerOptions options, TextWriter output, TextWriter error)
    {
        try
        {
            var model = new InstallerModel(options);
            switch (verb)
            {
                case "list":
                    foreach (var item in model.Rows)
                        await output.WriteLineAsync(item.Line);
                    foreach (var problem in model.Problems)
                        await output.WriteLineAsync($"feed problem: {problem}");
                    break;
                case "install":
                    var id = operands[0];
                    var entry = AppFeed.Read(options.Feed!).Entries.Where(item => item.Manifest.Id == id &&
                        (operands.Count == 1 || item.Manifest.Version == operands[1])).LastOrDefault()
                        ?? throw new InvalidOperationException($"The feed {options.Feed} offers no {id}{(operands.Count == 2 ? " " + operands[1] : "")}.");
                    var row = model.Rows.Single(item => item.Id == id) with { Offered = entry };
                    // Each step a line, the last saying what happened.
                    await model.InstallAsync(row, line => output.WriteLine(line));
                    break;
                case "use":
                    await output.WriteLineAsync(model.Use(operands[0], operands[1]));
                    break;
            }
            return 0;
        }
        catch (Exception failure) when (failure is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"witos-apps {verb}: {failure.Message}");
            return 1;
        }
    }

    #endregion
}
