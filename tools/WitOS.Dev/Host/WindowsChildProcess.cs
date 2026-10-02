using System.Collections;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WitOS.Dev.Host;

// Host-only Windows 10+ process ownership. JOB_LIST assigns the job atomically
// at creation, before user code can spawn children. No breakaway is permitted.
internal sealed class WindowsChildProcess : IDisposable
{
    private readonly SafeFileHandle job;
    private readonly SafeFileHandle process;
    private WindowsChildProcess(SafeFileHandle job, SafeFileHandle process) { this.job = job; this.process = process; }

    internal sealed class CapturePipe : IDisposable
    {
        public NamedPipeServerStream Reader { get; }
        public NamedPipeClientStream Writer { get; }
        public CapturePipe()
        {
            var name = "WitOS.Dev." + Guid.NewGuid().ToString("N");
            Reader = new(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Writer = new(".", name, PipeDirection.Out, PipeOptions.None, TokenImpersonationLevel.Identification, HandleInheritability.Inheritable);
        }
        public async Task ConnectAsync(CancellationToken token)
        {
            var connected = Reader.WaitForConnectionAsync(token);
            await Writer.ConnectAsync(token);
            await connected;
        }
        public void Dispose() { Writer.Dispose(); Reader.Dispose(); }
    }

    internal sealed class InputPipe : IDisposable
    {
        public NamedPipeServerStream Writer { get; }
        public NamedPipeClientStream Reader { get; }
        public InputPipe()
        {
            var name = "WitOS.Dev.Input." + Guid.NewGuid().ToString("N");
            Writer = new(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Reader = new(".", name, PipeDirection.In, PipeOptions.None, TokenImpersonationLevel.Identification, HandleInheritability.Inheritable);
        }
        public async Task ConnectAsync(CancellationToken token)
        {
            var connected = Writer.WaitForConnectionAsync(token);
            await Reader.ConnectAsync(token);
            await connected;
        }
        public void Dispose() { Reader.Dispose(); Writer.Dispose(); }
    }

    public static WindowsChildProcess Start(string executable, IEnumerable<string> arguments, string directory,
        IReadOnlyDictionary<string, string>? environment, CapturePipe output, CapturePipe error, InputPipe? inputPipe = null)
    {
        using var nullInput = inputPipe is null ? File.OpenHandle(@"\\.\NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null;
        SafeHandle input = inputPipe is null ? nullInput! : inputPipe.Reader.SafePipeHandle;
        return StartWithHandles(executable, arguments, directory, environment, output.Writer.SafePipeHandle, error.Writer.SafePipeHandle, input);
    }

    internal static WindowsChildProcess StartWithFiles(string executable, IEnumerable<string> arguments, string directory,
        SafeFileHandle output, SafeFileHandle error)
    {
        using var input = File.OpenHandle(@"\\.\NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return StartWithHandles(executable, arguments, directory, null, output, error, input);
    }

    private static WindowsChildProcess StartWithHandles(string executable, IEnumerable<string> arguments, string directory,
        IReadOnlyDictionary<string, string>? environment, SafeHandle output, SafeHandle error, SafeHandle input)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        { job.Dispose(); throw Error(); }
        try
        {
            var limits = new ExtendedLimits();
            limits.Basic.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw Error();
            foreach (var handle in new[] { input, output, error })
                if (!SetHandleInformation(handle, 1, 1))
                    throw Error();
            using var attributes = new Attributes();
            attributes.Add(0x20002, [input.DangerousGetHandle(), output.DangerousGetHandle(), error.DangerousGetHandle()]);
            attributes.Add(0x2000D, [job.DangerousGetHandle()]); // PROC_THREAD_ATTRIBUTE_JOB_LIST
            var startup = new StartupInfoEx();
            startup.Startup.Size = Marshal.SizeOf<StartupInfoEx>();
            startup.Startup.Flags = 0x100;
            startup.Startup.Input = input.DangerousGetHandle();
            startup.Startup.Output = output.DangerousGetHandle();
            startup.Startup.Error = error.DangerousGetHandle();
            startup.Attributes = attributes.Pointer;
            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
                variables[(string)item.Key] = (string)item.Value!;
            if (environment != null)
                foreach (var item in environment)
                {
                    if (item.Key.Length == 0 || item.Key.Contains('=') || item.Key.Contains('\0') || item.Value.Contains('\0'))
                        throw new ArgumentException("Invalid environment entry.");
                    variables[item.Key] = item.Value;
                }
            var block = Marshal.StringToHGlobalUni(string.Join('\0', variables.Select(v => v.Key + "=" + v.Value)) + "\0\0");
            try
            {
                var command = new StringBuilder(string.Join(' ', new[] { executable }.Concat(arguments).Select(WindowsCommandLine.Quote)));
                if (!CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080400, block, Path.GetFullPath(directory), ref startup, out var info))
                    throw Error();
                using var thread = new SafeFileHandle(info.Thread, true);
                return new(job, new SafeFileHandle(info.Process, true));
            }
            finally { Marshal.FreeHGlobal(block); }
        }
        catch { job.Dispose(); throw; }
    }
    public bool HasExited
    {
        get
        {
            var state = WaitForSingleObject(process, 0);
            if (state != 0 && state != 258)
                throw Error();
            return state == 0;
        }
    }
    public int ExitCode { get { if (!GetExitCodeProcess(process, out uint code)) throw Error(); return unchecked((int)code); } }
    public void Terminate()
    {
        // Stop the known root directly before terminating all owned descendants.
        // Access denied can race an exiting root; confirmation below still
        // requires its signaled process object and an empty job.
        var state = WaitForSingleObject(process, 0);
        if (state != 0 && state != 258)
            throw Error();
        if (state == 258 && !TerminateProcess(process, unchecked((uint)-1)))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 5)
                throw new Win32Exception(error);
        }
        if (!TerminateJobObject(job, unchecked((uint)-1)))
            throw Error();
    }
    public async Task WaitForExitAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var state = WaitForSingleObject(process, 0);
            if (state == 0)
                return;
            if (state != 258)
                throw Error();
            await Task.Delay(10, token);
        }
    }
    public uint ActiveProcesses
    {
        get
        {
            if (!QueryInformationJobObject(job, 1, out var info, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero))
                throw Error();
            return info.ActiveProcesses;
        }
    }
    public async Task ConfirmTerminationAsync(TimeSpan grace)
    {
        // Poll authoritative native state without blocking the async I/O
        // continuations involved in process/pipe teardown. EOF is not liveness.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var state = WaitForSingleObject(process, 0);
            if (state != 0 && state != 258)
                throw Error();
            var active = ActiveProcesses;
            if (state == 0 && active == 0)
                return;
            if (clock.Elapsed >= grace)
                throw new TimeoutException($"Owned process cleanup exceeded grace (wait={state}, exit={ExitCode}, active={active}).");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
    public void Dispose() { job.Dispose(); process.Dispose(); }
    private static Win32Exception Error() => new(Marshal.GetLastWin32Error());
    private sealed class Attributes : IDisposable
    {
        public IntPtr Pointer { get; }
        private readonly List<IntPtr> values = [];
        public Attributes()
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            Pointer = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(Pointer, 2, 0, ref size))
            { Marshal.FreeHGlobal(Pointer); throw Error(); }
        }
        public void Add(nuint key, IntPtr[] handles)
        {
            var data = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
            values.Add(data);
            Marshal.Copy(handles, 0, data, handles.Length);
            if (!UpdateProcThreadAttribute(Pointer, 0, key, data, (nuint)(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw Error();
        }
        public void Dispose() { DeleteProcThreadAttributeList(Pointer); foreach (var value in values) Marshal.FreeHGlobal(value); Marshal.FreeHGlobal(Pointer); }
    }
#pragma warning disable CS0649 // Native structures populated by Win32.
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveLimit; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting { public long User, Kernel, PeriodUser, PeriodKernel; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, Width, Height, CharsX, CharsY, Fill, Flags; public ushort Show, ReservedBytes; public IntPtr ReservedData, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
#pragma warning restore CS0649
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out Accounting info, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeFileHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint key, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string? application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
}
