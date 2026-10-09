using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WitOS.Dev.Host.Native;

namespace WitOS.Dev.Host;

/// <summary>
/// A child process tree on a Unix host (plan step T2.1a). The program runs through setsid as the leader of a new
/// session and process group, so one signal to the group stops the whole tree, as a job object does on Windows. A
/// descendant that starts a session of its own would leave the group; the tools WitOS runs never do. A container
/// that runs the tool needs an init process that reaps orphans (docker run --init), or a stopped group never empties.
/// </summary>
internal sealed class UnixChildProcess : IDisposable
{
    #region Constants

    private const int ENOENT = 2;

    #endregion

    #region Fields

    private readonly Process m_process;

    #endregion

    #region Constructors

    private UnixChildProcess(Process process) => m_process = process;

    #endregion

    #region Functions

    /// <summary>
    /// Starts a child with piped standard streams.
    /// </summary>
    /// <param name="executable">Program to run.</param>
    /// <param name="arguments">Program arguments.</param>
    /// <param name="directory">Working directory.</param>
    /// <param name="environment">Extra environment variables, or null.</param>
    /// <returns>The running child.</returns>
    /// <exception cref="Win32Exception">The program does not exist, as Process.Start reports it.</exception>
    public static UnixChildProcess Start(string executable, IEnumerable<string> arguments, string directory,
        IReadOnlyDictionary<string, string>? environment)
    {
        // setsid starts whatever it is given, so a missing program is found before it rather than as its exit code.
        if (Resolve(executable, directory) is null)
            throw new Win32Exception(ENOENT, $"Cannot start {executable}: no such program.");
        var start = new ProcessStartInfo("setsid")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(executable);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            start.Environment[name] = value;
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        return new UnixChildProcess(process);
    }

    /// <summary>
    /// Stops every process of the child's group.
    /// </summary>
    public void Terminate()
    {
        if (LibC.kill(-m_process.Id, LibC.SIGKILL) != 0 && Marshal.GetLastPInvokeError() != LibC.ESRCH)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    /// <summary>
    /// Waits until the root process exits.
    /// </summary>
    /// <param name="token">Cancellation.</param>
    public Task WaitForExitAsync(CancellationToken token) => m_process.WaitForExitAsync(token);

    /// <summary>
    /// Requires the root to have exited and the group to be empty within <paramref name="grace"/>.
    /// </summary>
    /// <param name="grace">Time allowed.</param>
    /// <exception cref="TimeoutException">Cleanup did not complete in time.</exception>
    public async Task ConfirmTerminationAsync(TimeSpan grace)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var empty = LibC.kill(-m_process.Id, 0) != 0 && Marshal.GetLastPInvokeError() == LibC.ESRCH;
            if (m_process.HasExited && empty)
                return;
            if (clock.Elapsed >= grace)
                throw new TimeoutException($"Process group {m_process.Id} did not end within {grace.TotalSeconds:0.#} s.");
            await Task.Delay(10);
        }
    }

    /// <inheritdoc />
    public void Dispose() => m_process.Dispose();

    // The file execvp would run: a name with a separator relative to the working directory, any other on the path.
    private static string? Resolve(string executable, string directory)
    {
        if (executable.Contains('/'))
        {
            var path = Path.Combine(directory, executable);
            return File.Exists(path) ? path : null;
        }
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => Path.Combine(entry, executable)).FirstOrDefault(File.Exists);
    }

    #endregion

    #region Properties

    /// <summary>
    /// Standard input of the child.
    /// </summary>
    public Stream Input => m_process.StandardInput.BaseStream;

    /// <summary>
    /// Standard output of the child.
    /// </summary>
    public Stream Output => m_process.StandardOutput.BaseStream;

    /// <summary>
    /// Standard error of the child.
    /// </summary>
    public Stream Error => m_process.StandardError.BaseStream;

    /// <summary>
    /// Whether the root process has exited.
    /// </summary>
    public bool HasExited => m_process.HasExited;

    /// <summary>
    /// Exit code of the root process.
    /// </summary>
    public int ExitCode => m_process.ExitCode;

    #endregion
}
