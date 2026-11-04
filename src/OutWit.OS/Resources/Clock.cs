using OutWit.OS.Native;

namespace OutWit.OS.Resources;

/// <summary>
/// The monotonic clock (plan step N4.3a): what a wait's deadline is counted in; and UTC, which the holder of the clock
/// capability sets on a board without a real-time clock (H7.4).
/// </summary>
public static class Clock
{
    #region Constants

    /// <summary>
    /// The errno value of a UTC read on a board without a real-time clock that nothing has set yet.
    /// </summary>
    public const int NO_UTC = 38; // ENOSYS

    #endregion

    #region Functions

    /// <summary>
    /// UTC now, in nanoseconds since 1970, or false on a board without a real-time clock that nothing has set yet.
    /// </summary>
    /// <param name="nanoseconds">The time.</param>
    /// <returns>Whether the board has UTC.</returns>
    /// <exception cref="CapabilityException">The clock could not be read for another reason.</exception>
    public static unsafe bool TryUtc(out ulong nanoseconds)
    {
        ulong value = 0;
        var error = Libwitos.ClockUtc(&value);
        nanoseconds = value;
        if (error == NO_UTC)
            return false;
        CapabilityException.ThrowOnError("CLOCK_READ", error);
        return true;
    }

    /// <summary>
    /// Sets UTC through the clock capability; later reads go on from the time given.
    /// </summary>
    /// <param name="clock">The clock capability.</param>
    /// <param name="nanoseconds">The time, in nanoseconds since 1970.</param>
    /// <exception cref="CapabilityException">The handle is not the capability, or the time lies before the boot or beyond the
    /// clock's range.</exception>
    public static void SetUtc(KernelHandle clock, ulong nanoseconds) =>
        CapabilityException.ThrowOnError("CLOCK_SET", Libwitos.ClockSet(clock.Value, nanoseconds));

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
