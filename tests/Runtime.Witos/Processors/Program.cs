// An ordinary .NET program (plan step P2), built by the stock SDK without a RID and run unchanged on WitOS through
// dotnet: what a program sees of several processors and whether work on all of them at once comes out exact. The
// argument is the number of processors the machine is expected to give the process (every one by default); the
// collector is the one the program's configuration asks for (System.GC.Server), which a second publish with
// ServerGarbageCollection selects. Nothing here knows WitOS.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

var expected = args.Length > 0 ? int.Parse(args[0]) : Environment.ProcessorCount;
// The runtime gives a configuration property as its text.
var server = bool.TryParse(AppContext.GetData("System.GC.Server")?.ToString(), out var configured) && configured;
var mode = server ? "server" : "workstation";

var checks = new (string Name, Func<string?> Run)[]
{
    ("processor count", () => Environment.ProcessorCount == expected ? null : $"Environment.ProcessorCount is {Environment.ProcessorCount}"),
    ("collector", () => GCSettings.IsServerGC == server ? null : $"IsServerGC is {GCSettings.IsServerGC}"),
    ("heaps", () =>
    {
        // The server collector keeps a heap per processor, the workstation collector one.
        var heaps = Convert.ToInt64(GC.GetConfigurationVariables()["HeapCount"]);
        return heaps == (server ? expected : 1) ? null : $"HeapCount is {heaps}";
    }),
    ("processor ids", () =>
    {
        // As many threads as processors, each spinning until all of them together saw every processor's id.
        var seen = new ConcurrentDictionary<int, bool>();
        using var start = new Barrier(expected);
        var deadline = Stopwatch.StartNew();
        var threads = Enumerable.Range(0, expected).Select(_ => new Thread(() =>
        {
            start.SignalAndWait();
            while (seen.Count < expected && deadline.Elapsed < TimeSpan.FromSeconds(20))
                seen.TryAdd(Thread.GetCurrentProcessorId(), true);
        })).ToList();
        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());
        var ids = seen.Keys.Order().ToList();
        return ids.SequenceEqual(Enumerable.Range(0, expected)) ? null : $"the threads saw processors {string.Join(",", ids)}";
    }),
    ("interlocked", () =>
    {
        long counter = 0;
        Together(expected, _ =>
        {
            for (var i = 0; i < 100_000; ++i)
                Interlocked.Increment(ref counter);
        });
        return counter == expected * 100_000L ? null : $"the counter is {counter}";
    }),
    ("monitor", () =>
    {
        var gate = new object();
        long counter = 0;
        Together(expected, _ =>
        {
            for (var i = 0; i < 20_000; ++i)
                lock (gate)
                    ++counter;
        });
        return counter == expected * 20_000L ? null : $"the counter is {counter}";
    }),
    ("parallel", () =>
    {
        const int count = 1_000_000;
        long sum = 0;
        Parallel.For(0, count, () => 0L, (i, _, local) => local + i, local => Interlocked.Add(ref sum, local));
        var plinq = Enumerable.Range(0, count).AsParallel().Select(i => (long)i).Sum();
        const long exact = (long)count * (count - 1) / 2;
        return sum == exact && plinq == exact ? null : $"Parallel.For gave {sum}, PLINQ {plinq}";
    }),
    ("thread pool", () =>
    {
        // As many work items as processors that wait for each other: the pool must run them at once.
        ThreadPool.GetMinThreads(out var workers, out _);
        using var all = new CountdownEvent(expected);
        var met = Enumerable.Range(0, expected).Select(_ => Task.Run(() =>
        {
            all.Signal();
            return all.Wait(TimeSpan.FromSeconds(60));
        })).ToArray();
        Task.WaitAll(met);
        return workers == expected && met.All(task => task.Result) && ThreadPool.ThreadCount >= expected ? null
            : $"minimum workers {workers}, met {met.Count(task => task.Result)}, threads {ThreadPool.ThreadCount}";
    }),
    ("allocation", () =>
    {
        // Every processor allocates and keeps every tenth array while collections of every generation run.
        var before = GC.CollectionCount(2);
        var kept = new List<byte[]>[expected];
        var collecting = Task.Run(() =>
        {
            for (var i = 0; i < 4; ++i)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                Thread.Sleep(10);
            }
        });
        Together(expected, index =>
        {
            kept[index] = [];
            for (var i = 0; i < 20_000; ++i)
            {
                var array = new byte[256 + i % 1024];
                Array.Fill(array, (byte)(index * 31 + i));
                if (i % 10 == 0)
                    kept[index].Add(array);
            }
        });
        collecting.Wait();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        for (var index = 0; index < expected; ++index)
            for (var j = 0; j < kept[index].Count; ++j)
                if (kept[index][j].Length != 256 + (j * 10) % 1024 || kept[index][j].Any(value => value != (byte)(index * 31 + j * 10)))
                    return $"thread {index}'s array {j} changed";
        var collections = GC.CollectionCount(2) - before;
        return collections >= 5 ? null : $"{collections} collections of generation 2";
    }),
    ("producers and consumers", () =>
    {
        using var queue = new BlockingCollection<int>(boundedCapacity: 1024);
        long consumed = 0;
        var consumers = Enumerable.Range(0, expected).Select(_ => Task.Factory.StartNew(() =>
        {
            foreach (var item in queue.GetConsumingEnumerable())
                Interlocked.Add(ref consumed, item);
        }, TaskCreationOptions.LongRunning)).ToArray();
        Together(expected, index =>
        {
            for (var i = 1; i <= 10_000; ++i)
                queue.Add(i);
        });
        queue.CompleteAdding();
        Task.WaitAll(consumers);
        return consumed == expected * (10_000L * 10_001 / 2) ? null : $"the consumers took {consumed}";
    }),
    ("tasks", () =>
    {
        var results = Task.WhenAll(Enumerable.Range(0, 2_000).Select(async i =>
        {
            await Task.Yield();
            await Task.Delay(i % 3);
            return i;
        })).GetAwaiter().GetResult();
        return results.Sum() == 2_000 * 1_999 / 2 ? null : $"the tasks gave {results.Sum()}";
    }),
};

var passed = 0;
foreach (var (name, run) in checks)
{
    string? failure;
    try
    {
        failure = run();
    }
    catch (Exception exception)
    {
        failure = $"threw {exception.GetType().Name}: {exception.Message}";
    }
    if (failure is null)
        ++passed;
    Console.WriteLine($"[P2] {name}: {failure ?? "ok"}");
}
Console.WriteLine($"[P2] {expected} processors, {mode} GC: {passed} of {checks.Length} checks passed " +
    $"({RuntimeInformation.RuntimeIdentifier}, {RuntimeInformation.FrameworkDescription})");
return passed == checks.Length ? 0 : 1;

// Runs the work on as many dedicated threads, released together, and waits for every one.
static void Together(int count, Action<int> work)
{
    using var start = new Barrier(count);
    var threads = Enumerable.Range(0, count).Select(index => new Thread(() =>
    {
        start.SignalAndWait();
        work(index);
    })).ToList();
    threads.ForEach(thread => thread.Start());
    threads.ForEach(thread => thread.Join());
}
