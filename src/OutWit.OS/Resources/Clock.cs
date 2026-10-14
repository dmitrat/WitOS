using OutWit.OS.Native;

namespace OutWit.OS.Resources;

/// <summary>
/// The monotonic clock (plan step N4.3a): what a wait's deadline is counted in.
/// </summary>
public static class Clock
{
    #region Functions

    /// <summary>
    /// The clock's reading now, in its ticks.
    /// </summary>
    /// <returns>The ticks.</returns>
    /// <exception cref="CapabilityException">The clock could not be read.</exception>
    public static unsafe ulong Monotonic()
    {
        ulong ticks = 0;
        CapabilityException.ThrowOnError("CLOCK_READ", Libwitos.ClockRead(&ticks));
        return ticks;
    }

    /// <summary>
    /// The clock's ticks per second.
    /// </summary>
    /// <returns>The frequency.</returns>
    /// <exception cref="CapabilityException">The frequency could not be read.</exception>
    public static unsafe ulong Frequency()
    {
        ulong frequency = 0;
        CapabilityException.ThrowOnError("CLOCK_FREQUENCY", Libwitos.ClockFrequency(&frequency));
        return frequency;
    }

    /// <summary>
    /// The absolute deadline a number of seconds from now, for a wait.
    /// </summary>
    /// <param name="seconds">The seconds.</param>
    /// <returns>The deadline in the clock's ticks.</returns>
    public static ulong MonotonicAfter(int seconds) => Monotonic() + (ulong)seconds * Frequency();

    #endregion
}
