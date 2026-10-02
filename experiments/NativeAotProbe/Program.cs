using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WitOS.Experiments;

/// <summary>
/// Hosted NativeAOT dependency probe: runs the boundary checks with the pinned packages on Windows.
/// </summary>
internal static class Program
{
    #region Fields

    [ThreadStatic] private static int m_threadValue;

    private static int m_finalized;

    private static readonly TimeSpan DEADLINE = TimeSpan.FromSeconds(15);

    #endregion

    #region Types

    private sealed record Node(int Value, Node? Previous);

    private sealed class Finalizable
    {
        ~Finalizable() => Interlocked.Increment(ref m_finalized);
    }

    #endregion

    #region Tools

    private static int Main()
    {
        Console.WriteLine("WitOS NativeAOT dependency probe (HOSTED WINDOWS; NOT WITOS GUEST)");
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        try
        {
            Check(!RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled,
                "The probe must be published with NativeAOT.");
            Check(Environment.Version == new Version(10, 0, 8), "Unexpected runtime version.");
            Check(!System.Runtime.GCSettings.IsServerGC, "Expected workstation GC.");
            Pass("NativeAotIdentity");
            CollectionsAndGc();
            RuntimeBoundaryChecks.CompositeRoots();
            Pass("GcCompositeRoots");
            RuntimeBoundaryChecks.UnwindRoots();
            Pass("GcRootsAcrossUnwind");
            ManagedExceptions();
            ThreadingAndTls();
            WaitTimeouts();
            TasksAndClock().GetAwaiter().GetResult();
            Console.WriteLine("[PROBE-SUCCESS]");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[PROBE-FAIL] {error}");
            return 1;
        }
    }

    private static void CollectionsAndGc()
    {
        var nodes = new Node[2048];
        long expected = 0;
        for (var i = 0; i < nodes.Length; ++i)
        {
            nodes[i] = new Node(i, i == 0 ? null : nodes[i - 1]);
            expected += i;
        }
        var large = new byte[128 * 1024];
        large[0] = 17;
        large[^1] = 91;
        var collectionBefore = GC.CollectionCount(GC.MaxGeneration);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        long actual = 0;
        for (var node = nodes[^1]; node is not null; node = node.Previous)
            actual += node.Value;
        Check(actual == expected && large[0] == 17 && large[^1] == 91, "GC lost live data.");
        Check(GC.CollectionCount(GC.MaxGeneration) > collectionBefore, "Forced collection did not occur.");
        CreateFinalizable();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        Check(Volatile.Read(ref m_finalized) == 1, "Finalizer did not run.");
        GC.KeepAlive(nodes);
        GC.KeepAlive(large);
        Pass("GcRootsAndFinalizer");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateFinalizable() => _ = new Finalizable();

    private static void ManagedExceptions()
    {
        var ranFinally = false;
        var caught = false;
        try
        {
            try
            { ThrowExpected(); }
            finally { ranFinally = true; }
        }
        catch (InvalidOperationException error) when (error.Message == "expected-probe-exception")
        {
            caught = true;
        }
        Check(caught && ranFinally, "Managed exception handling failed.");
        try
        {
            _ = ReadNode(null);
            throw new InvalidOperationException("Null access unexpectedly succeeded.");
        }
        catch (NullReferenceException) { }
        try
        {
            _ = Divide(123, 0);
            throw new InvalidOperationException("Division by zero unexpectedly succeeded.");
        }
        catch (DivideByZeroException) { }
        Pass("ExceptionsAndFinally");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowExpected() => throw new InvalidOperationException("expected-probe-exception");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadNode(Node? node) => node!.Value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Divide(int numerator, int denominator) => numerator / denominator;

    private static void ThreadingAndTls()
    {
        m_threadValue = 99;
        var failure = new Exception?[2];
        var ids = new int[2];
        var gate = new object();
        var readyCount = 0;
        var release = false;
        var shared = 0;
        using var finished = new CountdownEvent(2);
        var workers = new Thread[2];

        for (var index = 0; index < workers.Length; ++index)
        {
            var captured = index;
            workers[index] = new Thread(() =>
            {
                try
                {
                    Check(m_threadValue == 0, "Thread-static initial value leaked.");
                    m_threadValue = captured + 1;
                    ids[captured] = Environment.CurrentManagedThreadId;
                    lock (gate)
                    {
                        ++readyCount;
                        Monitor.PulseAll(gate);
                        while (!release)
                            Check(Monitor.Wait(gate, DEADLINE), "Worker rendezvous timed out.");
                    }
                    for (var i = 0; i < 4000; ++i)
                    {
                        var live = new Node(m_threadValue, null);
                        Interlocked.Increment(ref shared);
                        if ((i & 255) == 0)
                        {
                            Thread.Yield();
                            GC.Collect(0, GCCollectionMode.Forced, blocking: true);
                        }
                        Check(live.Value == captured + 1 && m_threadValue == captured + 1,
                            "GC root or thread-static state crossed threads.");
                        GC.KeepAlive(live);
                    }
                }
                catch (Exception error) { failure[captured] = error; }
                finally { finished.Signal(); }
            })
            { IsBackground = true };
            workers[index].Start();
        }

        lock (gate)
        {
            while (readyCount != 2)
                Check(Monitor.Wait(gate, DEADLINE), "Parent rendezvous timed out.");
            release = true;
            Monitor.PulseAll(gate);
        }
        Check(finished.Wait(DEADLINE), "Workers did not finish.");
        foreach (var worker in workers)
            Check(worker.Join(DEADLINE), "Thread join timed out.");
        Check(failure[0] is null && failure[1] is null,
            $"Worker failed: {failure[0]?.Message ?? failure[1]?.Message}");
        Check(shared == 8000 && m_threadValue == 99 && ids[0] != ids[1],
            "Thread isolation or atomic increment failed.");
        Pass("ThreadsTlsMonitorAndGc");
    }

    private static void WaitTimeouts()
    {
        using var signal = new AutoResetEvent(false);
        var clock = Stopwatch.StartNew();
        Check(!signal.WaitOne(30), "Unsignalled event completed successfully.");
        Check(clock.ElapsedMilliseconds >= 10, "Wait timeout returned immediately.");
        signal.Set();
        Check(signal.WaitOne(DEADLINE), "Signalled event did not wake.");
        Check(!signal.WaitOne(0), "Auto-reset event retained the signal.");
        using var manual = new ManualResetEvent(false);
        manual.Set();
        Check(manual.WaitOne(0) && manual.WaitOne(0), "Manual-reset event lost its signal.");
        manual.Reset();
        Check(!manual.WaitOne(0), "Manual-reset event ignored reset.");
        Pass("WaitSignalResetAndTimeout");
    }

    private static async Task TasksAndClock()
    {
        var start = Stopwatch.GetTimestamp();
        var work = Enumerable.Range(1, 8).Select(value => Task.Run(() => value * value)).ToArray();
        var result = await Task.WhenAll(work);
        Check(result.Sum() == 204, "ThreadPool task result mismatch.");
        using var cancellation = new CancellationTokenSource();
        var delay = Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
        cancellation.Cancel();
        try
        {
            await delay;
            throw new InvalidOperationException("Cancelled delay completed normally.");
        }
        catch (OperationCanceledException) { }
        await Task.Delay(20);
        Check(Stopwatch.GetTimestamp() > start && Stopwatch.Frequency > 0, "Monotonic clock failed.");
        Pass("TasksCancellationAndClock");
    }

    private static void Pass(string name) => Console.WriteLine($"[PROBE-PASS] {name}");

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    #endregion
}
