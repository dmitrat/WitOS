using System;

namespace OutWit.OS.Hardware;

/// <summary>
/// How a driver core waits for its controller between looks at its status (plan steps H8.2 and H8.3): the driver's
/// interrupt binding in the guest; in a host test, nothing, or the advance of a clock the test keeps, for a model that
/// answers at once.
/// </summary>
public interface IInterruptWait
{
    /// <summary>
    /// Waits until the device may have changed its status, or the time passed.
    /// </summary>
    /// <param name="timeout">The longest wait.</param>
    void Wait(TimeSpan timeout);
}
