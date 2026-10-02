using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace WitOS.Dev;

internal sealed record ProcessResult(int ExitCode, string Output, string Error, bool TimedOut);

internal static class Processes
{
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ControlBudget = TimeSpan.FromSeconds(1);

    public static Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, string directory,
        int timeoutSeconds = 60, IReadOnlyDictionary<string, string>? environment = null)
        => RunCoreAsync(executable, arguments, directory, timeoutSeconds, environment, null);

    public static Task<ProcessResult> RunWithTimeoutInputAsync(string executable, IEnumerable<string> arguments,
        string directory, int timeoutSeconds, string timeoutInput)
        => RunCoreAsync(executable, arguments, directory, timeoutSeconds, null, async (input, _, token) =>
        {
            await input.WriteAsync(Encoding.UTF8.GetBytes(timeoutInput), token);
            await input.FlushAsync(token);
        });

    // Callbacks must eventually finish; cancellation cannot terminate arbitrary managed code.
    // The runner bounds its wait even for an uncooperative or synchronously blocking callback.
    public static Task<ProcessResult> RunWithTimeoutControlAsync(string executable, IEnumerable<string> arguments,
        string directory, int timeoutSeconds, Func<Stream, Func<string>, CancellationToken, Task> control)
        => RunCoreAsync(executable, arguments, directory, timeoutSeconds, null, control);

    public static Task<ProcessResult> RunWithTimeoutSignalAsync(string executable, IEnumerable<string> arguments,
        string directory, int timeoutSeconds, Func<CancellationToken, Task> control)
        => RunCoreAsync(executable, arguments, directory, timeoutSeconds, null, (_, _, token) => control(token), false);

    // QEMU uses file-backed standard streams: no inherited named-pipe endpoints
    // can participate in its exit path. Files remain available on cleanup failure.
    public static async Task<ProcessResult> RunWithFilesAsync(string executable, IEnumerable<string> arguments,
        string directory, int timeoutSeconds, string outputPath, string errorPath, Func<CancellationToken, Task> control)
    {
        if (timeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var output = File.OpenHandle(outputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using var error = File.OpenHandle(errorPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using var child = WindowsChildProcess.StartWithFiles(executable, arguments, directory, output, error);
        output.Dispose();
        error.Dispose();
        bool timedOut = false;
        Exception? failure = null;
        try
        {
            while (!child.HasExited)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (new FileInfo(outputPath).Length > BoundedCapture.Limit || new FileInfo(errorPath).Length > BoundedCapture.Limit)
                    throw new InvalidDataException("Process output exceeded file capture limit.");
                await Task.Delay(10, deadline.Token);
            }
            timedOut = deadline.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { timedOut = true; }
        catch (Exception exception) { failure = exception; }
        var cleanup = Stopwatch.StartNew();
        if (timedOut)
            failure = await RequestStopAsync((_, _, token) => control(token), Stream.Null, () => "", cleanup) ?? failure;
        try
        {
            child.Terminate();
            await child.ConfirmTerminationAsync(CleanupBudget - cleanup.Elapsed);
        }
        catch (Exception exception)
        {
            if (failure is null)
                throw;
            failure.Data["CleanupError"] = exception.ToString();
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return new(child.ExitCode, await BoundedCapture.ReadFileAsync(outputPath), await BoundedCapture.ReadFileAsync(errorPath), timedOut);
    }

    private static async Task<ProcessResult> RunCoreAsync(string executable, IEnumerable<string> arguments,
        string directory, int timeoutSeconds, IReadOnlyDictionary<string, string>? environment,
        Func<Stream, Func<string>, CancellationToken, Task>? timeoutControl, bool standardInputControl = true)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Process containment requires Windows 10 or newer.");
        if (timeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var stopIo = new CancellationTokenSource();
        using var outputPipe = new WindowsChildProcess.CapturePipe();
        using var errorPipe = new WindowsChildProcess.CapturePipe();
        using var inputPipe = timeoutControl is null || !standardInputControl ? null : new WindowsChildProcess.InputPipe();
        try
        {
            await Task.WhenAll(outputPipe.ConnectAsync(deadline.Token), errorPipe.ConnectAsync(deadline.Token),
                inputPipe?.ConnectAsync(deadline.Token) ?? Task.CompletedTask);
        }
        catch (OperationCanceledException) { return new(-1, "", "", true); }

        using var outputReader = new StreamReader(outputPipe.Reader, Console.OutputEncoding, true, 4096, true);
        using var errorReader = new StreamReader(errorPipe.Reader, Console.OutputEncoding, true, 4096, true);
        var stdout = new BoundedCapture();
        var stderr = new BoundedCapture();
        using var child = WindowsChildProcess.Start(executable, arguments, directory, environment, outputPipe, errorPipe, inputPipe);
        inputPipe?.Reader.Dispose();
        outputPipe.Writer.Dispose();
        errorPipe.Writer.Dispose();

        async Task Drain(StreamReader reader, BoundedCapture capture)
        {
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), stopIo.Token)) != 0)
                capture.Append(buffer.AsSpan(0, count));
        }
        var all = Task.WhenAll(child.WaitForExitAsync(stopIo.Token), Drain(outputReader, stdout), Drain(errorReader, stderr));
        var timedOut = false;
        Exception? failure = null;
        try
        {
            try
            {
                var completed = await Task.WhenAny(all, stdout.Overflow, stderr.Overflow).WaitAsync(deadline.Token);
                if (completed != all)
                    throw new InvalidDataException($"Process output exceeded {BoundedCapture.Limit} characters per stream: {executable}");
                await all;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { timedOut = true; }
            catch (Exception error) { failure = error; }

            var cleanup = Stopwatch.StartNew();
            if (timedOut && timeoutControl is not null)
                failure = await RequestStopAsync(timeoutControl, inputPipe?.Writer ?? Stream.Null, stdout.Snapshot, cleanup) ?? failure;
            if (timedOut || failure is not null)
            {
                stopIo.Cancel();
                outputPipe.Reader.Dispose();
                errorPipe.Reader.Dispose();
            }
            try
            {
                // Normal root exit also reaps pipe-independent descendants.
                child.Terminate();
                await child.ConfirmTerminationAsync(CleanupBudget - cleanup.Elapsed).ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                if (failure is null)
                    throw;
                failure.Data["CleanupError"] = cleanupError.ToString();
            }
            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
            if (stdout.Truncated || stderr.Truncated)
                throw new InvalidDataException("Process evidence was truncated; success is forbidden.");
            return new(child.ExitCode, stdout.Snapshot(), stderr.Snapshot(), timedOut);
        }
        finally
        {
            stopIo.Cancel();
            outputPipe.Reader.Dispose();
            errorPipe.Reader.Dispose();
            Observe(all);
        }
    }

    private static async Task<Exception?> RequestStopAsync(Func<Stream, Func<string>, CancellationToken, Task> control,
        Stream input, Func<string> output, Stopwatch cleanup)
    {
        using var stop = new CancellationTokenSource(ControlBudget);
        var token = stop.Token;
        var task = Task.Run(() => control(input, output, token));
        try
        {
            await task.WaitAsync(stop.Token);
            var remaining = ControlBudget - cleanup.Elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (IOException) { }
        catch (Exception error) { return error; }
        finally
        {
            stop.Cancel();
            input.Dispose();
            Observe(task);
        }
        return null;
    }

    private static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public static async Task RequireSuccessAsync(string executable, IEnumerable<string> arguments, string directory)
    {
        var result = await RunAsync(executable, arguments, directory);
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed (exit {result.ExitCode}, timeout={result.TimedOut}).\n{result.Output}\n{result.Error}");
    }
}
