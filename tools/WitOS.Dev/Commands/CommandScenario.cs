using WitOS.Dev.Interfaces;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Commands;

/// <summary>
/// Builds one kernel scenario image and boots it in each listed machine profile.
/// </summary>
internal sealed class CommandScenario : ICommand
{
    #region Fields

    private readonly string m_scenario;

    private readonly IReadOnlyList<BootRequest> m_requests;

    private readonly Func<string, Task>? m_prepare;

    #endregion

    #region Constructors

    public CommandScenario(string name, string description, string scenario, IReadOnlyList<BootRequest> requests,
        Func<string, Task>? prepare = null)
    {
        Name = name;
        Description = description;
        m_scenario = scenario;
        m_requests = requests;
        m_prepare = prepare;
    }

    #endregion

    #region ICommand

    public async Task RunAsync(string root, IReadOnlyList<string> arguments)
    {
        if (m_prepare is not null)
        {
            await m_prepare(root);
        }
        var image = await KernelImageBuilder.BuildAsync(root, m_scenario);
        foreach (var request in m_requests)
        {
            await BootScenarioRunner.RunAsync(root, image, request);
        }
    }

    #endregion

    #region Properties

    public string Name { get; }

    public string Arguments => "";

    public string Description { get; }

    #endregion
}
