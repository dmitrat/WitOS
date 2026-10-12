using System.Diagnostics;

namespace DotNetCompatibility;

/// <summary>
/// Timers and clocks: the threading timer, the periodic timer, System.Timers, delays and the stopwatch.
/// </summary>
internal static class TimerChecks
{
    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check("Timers", "ThreadingTimer", async () =>
        {
            var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var timer = new Timer(_ => fired.TrySetResult(), null, 20, Timeout.Infinite);
            await fired.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return CheckResult.That(true, "fired");
        });
        yield return new Check("Timers", "PeriodicTimer", async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            var ticks = 0;
            while (ticks < 3 && await timer.WaitForNextTickAsync())
                ticks++;
            return CheckResult.That(ticks == 3, $"{ticks} ticks");
        });
        yield return new Check("Timers", "SystemTimers", async () =>
        {
            var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timer = new System.Timers.Timer(20) { AutoReset = false };
            timer.Elapsed += (_, _) => fired.TrySetResult();
            timer.Start();
            await fired.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return CheckResult.That(true, "elapsed");
        });
        yield return new Check("Timers", "Delay", async () =>
        {
            var clock = Stopwatch.StartNew();
            await Task.Delay(100);
            var elapsed = clock.Elapsed.TotalMilliseconds;
            return CheckResult.That(elapsed >= 95, $"{elapsed:F0} ms for a delay of 100");
        });
        yield return new Check("Timers", "Stopwatch", () =>
        {
            var first = Stopwatch.GetTimestamp();
            Thread.SpinWait(10_000);
            var second = Stopwatch.GetTimestamp();
            return CheckResult.Now(second > first && Stopwatch.Frequency > 0 && Stopwatch.IsHighResolution,
                $"frequency {Stopwatch.Frequency}, high resolution {Stopwatch.IsHighResolution}");
        });
        yield return new Check("Timers", "UtcNow", () =>
        {
            var now = DateTime.UtcNow;
            return CheckResult.Now(now.Year >= 2025 && now.Kind == DateTimeKind.Utc, $"{now:O}");
        });
    }
}
