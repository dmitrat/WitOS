namespace WitOS.Acceptance;

/// <summary>
/// Work items on the thread pool's threads: tasks that return values and compose, and a queued work item.
/// </summary>
internal static class ThreadPoolProbe
{
    #region Functions

    /// <summary>
    /// Runs eight tasks, a continuation and a work item.
    /// </summary>
    /// <returns>True when every result arrived from a thread pool thread.</returns>
    internal static bool Run()
    {
        var tasks = new Task<int>[8];
        for (int i = 0; i < tasks.Length; ++i)
        {
            int value = i;
            tasks[i] = Task.Run(() => value * value);
        }
        if (!Task.WaitAll(tasks, 10000))
            return false;
        int sum = 0;
        foreach (var task in tasks)
            sum += task.Result;
        int continued = Task.Run(() => 20).ContinueWith(task => task.Result + 1).Result;
        using var done = new ManualResetEventSlim(false);
        int queued = 0;
        bool posted = ThreadPool.QueueUserWorkItem(_ =>
        {
            queued = Thread.CurrentThread.IsThreadPoolThread ? 1 : 2;
            done.Set();
        });
        return sum == 140 && continued == 21 && posted && done.Wait(10000) && queued == 1;
    }

    #endregion
}
