using WitOS.Dev.Interfaces;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Commands;

/// <summary>
/// Builds one kernel scenario image and boots it in each listed machine profile, for x64 or, with
/// <c>--arch arm64</c>, for ARM64 as a foundation-suite boot; the release kernel's suite is the same on both ISAs
/// (plan step T3.2), since a release kernel prints no foundation self-tests.
/// </summary>
internal sealed class CommandScenario : ICommand
{
    #region Fields

    private readonly string m_scenario;

    private readonly Func<KernelArchitecture, IReadOnlyList<BootRequest>> m_requests;

    private readonly Func<string, Task>? m_prepare;

    #endregion

    #region Constructors

    public CommandScenario(string name, string description, string scenario, IReadOnlyList<BootRequest> requests,
        Func<string, Task>? prepare = null)
        : this(name, description, scenario, _ => requests, prepare)
    {
    }

    public CommandScenario(string name, string description, string scenario,
        Func<KernelArchitecture, IReadOnlyList<BootRequest>> requests, Func<string, Task>? prepare = null)
    {
        Name = name;
        Description = description;
        m_scenario = scenario;
        m_requests = requests;
        m_prepare = prepare;
    }

    #endregion

    #region ICommand

    /// <inheritdoc />
    public async Task RunAsync(string root, IReadOnlyList<string> arguments)
    {
        if (m_prepare is not null)
        {
            await m_prepare(root);
        }
        var architecture = arguments.Count switch
        {
            0 => KernelArchitecture.X64,
            2 when arguments[0] == "--arch" => KernelArchitecture.Find(arguments[1]),
            _ => throw new ArgumentException($"Usage: {Name} [--arch x64|arm64]")
        };
        var image = await KernelImageBuilder.BuildAsync(root, m_scenario, architecture: architecture);
        foreach (var request in m_requests(architecture))
        {
            await BootScenarioRunner.RunAsync(root, image, architecture == KernelArchitecture.X64 ? request : request with
            {
                Name = architecture.Name + "-" + request.Name,
                Architecture = architecture,
                Suite = request.Suite == BootSuite.Release ? BootSuite.Release : BootSuite.Foundation
            });
        }
    }

    #endregion

    #region Properties

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Arguments => "[--arch x64|arm64]";

    /// <inheritdoc />
    public string Description { get; }

    #endregion
}
