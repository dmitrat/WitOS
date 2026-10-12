using WitOS.Dev.Interfaces;
using WitOS.Dev.Kernel;
using WitOS.Dev.Runtime;

namespace WitOS.Dev.Commands;

/// <summary>
/// The NuGet libraries' own test suites (plan step N2.2) for one architecture, every suite or the group --group names,
/// as one CI job runs a group.
/// </summary>
internal sealed class CommandNuGetSuites : ICommand
{
    #region ICommand

    /// <exception cref="ArgumentException">The options are not an optional --arch and an optional --group, each with a
    /// value.</exception>
    public Task RunAsync(string root, IReadOnlyList<string> arguments)
    {
        var architecture = KernelArchitecture.X64;
        string? group = null;
        for (var i = 0; i < arguments.Count; i += 2)
        {
            if (i + 1 == arguments.Count)
                throw new ArgumentException($"{Name} takes only {Arguments}.");
            switch (arguments[i])
            {
                case "--arch":
                    architecture = KernelArchitecture.Find(arguments[i + 1]);
                    break;
                case "--group":
                    group = arguments[i + 1];
                    break;
                default:
                    throw new ArgumentException($"{Name} takes only {Arguments}.");
            }
        }
        return RuntimeNuGetSuites.RunAsync(root, architecture, group);
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public string Name => "runtime-nuget-suites";

    /// <inheritdoc />
    public string Arguments => "[--arch x64|arm64] [--group <group>]";

    /// <inheritdoc />
    public string Description =>
        "Build the representative NuGet libraries' own test suites, every one or a group's, run them on this Linux host, then in the guest through dotnet on the runtime runtime-witos built (N2.2)";

    #endregion
}
