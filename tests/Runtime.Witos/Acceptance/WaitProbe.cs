namespace WitOS.Acceptance;

/// <summary>
/// The waits of Monitor, the slim events and the semaphore across threads, and a sleep that takes its time.
/// </summary>
internal static class WaitProbe
{
    #region Functions

    /// <summary>
    /// Runs a Monitor pulse, a manual reset event and a semaphore set from other threads, and a sleep.
    /// </summary>
    /// <returns>True when every wait ended as it should.</returns>
    internal static bool Run()
    {
        var gate = new object();
        bool ready = false;
        var pulsed = new Thread(() =>
        {
            lock (gate)
            {
                ready = true;
                Monitor.Pulse(gate);
            }
        });
        lock (gate)
        {
            pulsed.Start();
            while (!ready)
                if (!Monitor.Wait(gate, 10000))
                    return false;
        }
        if (!pulsed.Join(10000))
            return false;

        using var signal = new ManualResetEventSlim(false);
        using var semaphore = new SemaphoreSlim(0);
        var setter = new Thread(() =>
        {
            signal.Set();
            semaphore.Release(2);
        });
        setter.Start();
        bool signaled = signal.Wait(10000);
        bool acquired = semaphore.Wait(10000) && semaphore.Wait(10000) && !semaphore.Wait(0);
        if (!setter.Join(10000))
            return false;

        long start = Environment.TickCount64;
        Thread.Sleep(50);
        long slept = Environment.TickCount64 - start;
        return ready && signaled && acquired && slept >= 40;
    }

    #endregion
}
