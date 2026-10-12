namespace DotNetCompatibility;

/// <summary>
/// Tasks and async code: Strategy §55's application, continuations, cancellation, completion sources, parallel loops and
/// the flow of async-local state.
/// </summary>
internal static class TaskChecks
{
    private static readonly AsyncLocal<int> m_local = new();

    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check("Tasks", "WhenAllOfDelays", async () =>
        {
            var finished = 0;
            await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
            {
                await Task.Delay(10);
                Interlocked.Increment(ref finished);
            }));
            return CheckResult.That(finished == 100, $"{finished} of 100 tasks finished");
        });
        yield return new Check("Tasks", "Run", async () =>
        {
            var value = await Task.Run(() => Enumerable.Range(1, 1000).Sum());
            return CheckResult.That(value == 500500, $"{value}");
        });
        yield return new Check("Tasks", "ContinueWith", async () =>
        {
            var value = await Task.Run(() => 20).ContinueWith(previous => previous.Result + 22, TaskScheduler.Default);
            return CheckResult.That(value == 42, $"{value}");
        });
        yield return new Check("Tasks", "Cancellation", async () =>
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellation.Token);
                return CheckResult.That(false, "the delay ran to its end");
            }
            catch (TaskCanceledException)
            {
                return CheckResult.That(true, "TaskCanceledException");
            }
        });
        yield return new Check("Tasks", "CompletionSource", async () =>
        {
            var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            new Thread(() => source.SetResult("from another thread")).Start();
            var value = await source.Task;
            return CheckResult.That(value == "from another thread", value);
        });
        yield return new Check("Tasks", "ParallelFor", () =>
        {
            long total = 0;
            Parallel.For(0, 10_000, i => Interlocked.Add(ref total, i));
            return CheckResult.Now(total == 49_995_000, $"{total}");
        });
        yield return new Check("Tasks", "AsyncLocal", async () =>
        {
            m_local.Value = 7;
            await Task.Yield();
            var inside = await Task.Run(() => m_local.Value);
            return CheckResult.That(m_local.Value == 7 && inside == 7, $"after await {m_local.Value}, in Task.Run {inside}");
        });
    }
}
