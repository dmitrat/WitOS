using WitOS.Dev.Interfaces;
using WitOS.Dev.Kernel;
using WitOS.Dev.NativeAot;
using WitOS.Dev.NativeAot.Acceptance;

namespace WitOS.Dev.Commands;

/// <summary>
/// Runs the managed guest acceptance matrix inside one committed runtime-boot attempt.
/// </summary>
internal sealed class CommandRuntimeBoot : ICommand
{
    #region Fields

    private readonly bool m_rebuildRuntime;

    #endregion

    #region Constructors

    /// <param name="rebuildRuntime">Rebuild the runtime from source first instead of booting the
    /// last hash-verified prepared image.</param>
    public CommandRuntimeBoot(bool rebuildRuntime)
    {
        m_rebuildRuntime = rebuildRuntime;
    }

    #endregion

    #region ICommand

    public Task RunAsync(string root, IReadOnlyList<string> arguments)
        => RuntimeBootAttempt.RunAsync(root, Name, async attempt =>
        {
            if (m_rebuildRuntime)
            {
                await RuntimeSourceBuild.RunAsync(root);
            }
            var image = await KernelImageBuilder.BuildAsync(root, "runtime-boot");
            await RuntimeBootMatrix.RunAsync(root, image, attempt);
        });

    #endregion

    #region Properties

    public string Name => m_rebuildRuntime ? "runtime-boot" : "runtime-boot-run";

    public string Arguments => "";

    public string Description => m_rebuildRuntime
        ? "Build and execute the full guest runtime/GC workload"
        : "Rebuild kernel and boot the last hash-verified runtime image";

    #endregion
}
