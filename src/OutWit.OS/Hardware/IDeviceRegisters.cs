namespace OutWit.OS.Hardware;

/// <summary>
/// A device's registers as 32-bit words (plan step H8.2): what a driver core reads and writes, so that the guest gives it
/// a mapped region (<see cref="MmioRegion"/>) and a host test a model of the device. A controller that serves only
/// accesses of 32 bits, as the Raspberry Pi 4's SD controller does, is driven through this alone.
/// </summary>
public interface IDeviceRegisters
{
    /// <summary>
    /// Reads the 32-bit register at an offset.
    /// </summary>
    /// <param name="offset">The register's offset, a multiple of 4.</param>
    /// <returns>Its value.</returns>
    uint Read32(int offset);

    /// <summary>
    /// Writes the 32-bit register at an offset.
    /// </summary>
    /// <param name="offset">The register's offset, a multiple of 4.</param>
    /// <param name="value">The value.</param>
    void Write32(int offset, uint value);

    /// <summary>
    /// A full barrier for the device's view of memory: what the driver wrote to memory before, such as a DMA descriptor,
    /// reaches the device before a register written after.
    /// </summary>
    void Fence();
}
