using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WitOS.Dev.Host.Native;

namespace WitOS.Dev.Host;

/// <summary>
/// Host-only Windows 10+ process ownership. JOB_LIST assigns the job atomically at creation, before user
/// code can spawn children. No breakaway is permitted.
/// </summary>
internal sealed class WindowsChildProcess : IDisposable
{
    #region Constants

    private const uint WAIT_OBJECT_0 = 0;

    private const uint WAIT_TIMEOUT = 258;

    private const int ERROR_ACCESS_DENIED = 5;

    #endregion

    #region Fields

    private readonly SafeFileHandle m_job;

    private readonly SafeFileHandle m_process;

    #endregion

    #region Constructors

    private WindowsChildProcess(SafeFileHandle job, SafeFileHandle process)
    {
        m_job = job;
        m_process = process;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Starts a child with piped standard streams inside a kill-on-close job.
    /// </summary>
    public static WindowsChildProcess Start(string executable, IEnumerable<string> arguments, string directory,
        IReadOnlyDictionary<string, string>? environment, WindowsChildProcessCapturePipe output,
        WindowsChildProcessCapturePipe error, WindowsChildProcessInputPipe? inputPipe = null)
    {
        using var nullInput = inputPipe is null
            ? File.OpenHandle(@"\\.\NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            : null;
        SafeHandle input = inputPipe is null ? nullInput! : inputPipe.Reader.SafePipeHandle;
        return StartWithHandles(executable, arguments, directory, environment, output.Writer.SafePipeHandle,
            error.Writer.SafePipeHandle, input);
    }

    /// <summary>
    /// Starts a child whose stdout and stderr are files and whose stdin is NUL.
    /// </summary>
    public static WindowsChildProcess StartWithFiles(string executable, IEnumerable<string> arguments, string directory,
        SafeFileHandle output, SafeFileHandle error)
    {
        using var input = File.OpenHandle(@"\\.\NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return StartWithHandles(executable, arguments, directory, null, output, error, input);
    }

    /// <summary>
    /// Terminates the root process and every process in its job.
    /// </summary>
    public void Terminate()
    {
        // Stop the known root directly before terminating all owned descendants.
        // Access denied can race an exiting root; confirmation below still
        // requires its signaled process object and an empty job.
        var state = Kernel32.WaitForSingleObject(m_process, 0);
        if (state != WAIT_OBJECT_0 && state != WAIT_TIMEOUT)
        {
            throw Kernel32.LastError();
        }
        if (state == WAIT_TIMEOUT && !Kernel32.TerminateProcess(m_process, unchecked((uint)-1)))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ERROR_ACCESS_DENIED)
            {
                throw new Win32Exception(error);
            }
        }
        if (!Kernel32.TerminateJobObject(m_job, unchecked((uint)-1)))
        {
            throw Kernel32.LastError();
        }
    }

    /// <summary>
    /// Waits until the root process exits.
    /// </summary>
    public async Task WaitForExitAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var state = Kernel32.WaitForSingleObject(m_process, 0);
            if (state == WAIT_OBJECT_0)
            {
                return;
            }
            if (state != WAIT_TIMEOUT)
            {
                throw Kernel32.LastError();
            }
            await Task.Delay(10, token);
        }
    }

    /// <summary>
    /// Requires the root to be signaled and the job to be empty within <paramref name="grace"/>.
    /// </summary>
    /// <exception cref="TimeoutException">Cleanup did not complete in time.</exception>
    public async Task ConfirmTerminationAsync(TimeSpan grace)
    {
        // Poll authoritative native state without blocking the async I/O
        // continuations involved in process/pipe teardown. EOF is not liveness.
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var state = Kernel32.WaitForSingleObject(m_process, 0);
            if (state != WAIT_OBJECT_0 && state != WAIT_TIMEOUT)
            {
                throw Kernel32.LastError();
            }
            var active = ActiveProcesses;
            if (state == WAIT_OBJECT_0 && active == 0)
            {
                return;
            }
            if (clock.Elapsed >= grace)
            {
                throw new TimeoutException(
                    $"Owned process cleanup exceeded grace (wait={state}, exit={ExitCode}, active={active}).");
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    #endregion

    #region Tools

    private static WindowsChildProcess StartWithHandles(string executable, IEnumerable<string> arguments, string directory,
        IReadOnlyDictionary<string, string>? environment, SafeHandle output, SafeHandle error, SafeHandle input)
    {
        var job = Kernel32.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            throw Kernel32.LastError();
        }
        try
        {
            var limits = new JobExtendedLimits();
            limits.Basic.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
            if (!Kernel32.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimits>()))
            {
                throw Kernel32.LastError();
            }
            foreach (var handle in new[] { input, output, error })
            {
                if (!Kernel32.SetHandleInformation(handle, 1, 1))
                {
                    throw Kernel32.LastError();
                }
            }
            using var attributes = new WindowsChildProcessAttributeList();
            attributes.Add(0x20002, [input.DangerousGetHandle(), output.DangerousGetHandle(), error.DangerousGetHandle()]);
            attributes.Add(0x2000D, [job.DangerousGetHandle()]); // PROC_THREAD_ATTRIBUTE_JOB_LIST
            var startup = new StartupInfoEx();
            startup.Startup.Size = Marshal.SizeOf<StartupInfoEx>();
            startup.Startup.Flags = 0x100;
            startup.Startup.Input = input.DangerousGetHandle();
            startup.Startup.Output = output.DangerousGetHandle();
            startup.Startup.Error = error.DangerousGetHandle();
            startup.Attributes = attributes.Pointer;
            var block = Marshal.StringToHGlobalUni(EnvironmentBlock(environment));
            try
            {
                var command = new StringBuilder(string.Join(' ',
                    new[] { executable }.Concat(arguments).Select(WindowsCommandLine.Quote)));
                if (!Kernel32.CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080400, block,
                        Path.GetFullPath(directory), ref startup, out var info))
                {
                    throw Kernel32.LastError();
                }
                using var thread = new SafeFileHandle(info.Thread, true);
                return new(job, new SafeFileHandle(info.Process, true));
            }
            finally
            {
                Marshal.FreeHGlobal(block);
            }
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    // Sorted, case-insensitive variables; caller entries override inherited ones.
    private static string EnvironmentBlock(IReadOnlyDictionary<string, string>? environment)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            variables[(string)item.Key] = (string)item.Value!;
        }
        if (environment != null)
        {
            foreach (var item in environment)
            {
                if (item.Key.Length == 0 || item.Key.Contains('=') || item.Key.Contains('\0') || item.Value.Contains('\0'))
                {
                    throw new ArgumentException("Invalid environment entry.");
                }
                variables[item.Key] = item.Value;
            }
        }
        return string.Join('\0', variables.Select(v => v.Key + "=" + v.Value)) + "\0\0";
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        m_job.Dispose();
        m_process.Dispose();
    }

    #endregion

    #region Properties

    public bool HasExited
    {
        get
        {
            var state = Kernel32.WaitForSingleObject(m_process, 0);
            if (state != WAIT_OBJECT_0 && state != WAIT_TIMEOUT)
            {
                throw Kernel32.LastError();
            }
            return state == WAIT_OBJECT_0;
        }
    }

    public int ExitCode
    {
        get
        {
            if (!Kernel32.GetExitCodeProcess(m_process, out uint code))
            {
                throw Kernel32.LastError();
            }
            return unchecked((int)code);
        }
    }

    public uint ActiveProcesses
    {
        get
        {
            if (!Kernel32.QueryInformationJobObject(m_job, 1, out var info, (uint)Marshal.SizeOf<JobAccounting>(), IntPtr.Zero))
            {
                throw Kernel32.LastError();
            }
            return info.ActiveProcesses;
        }
    }

    #endregion
}
