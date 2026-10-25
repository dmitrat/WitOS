namespace OutWit.OS.Hardware;

/// <summary>
/// A PCI function's MSI capability (PCI 3.0 §6.8.1, plan step H3.1): one message the function writes to the address and
/// with the data of a message target the kernel bound to an event (<see cref="InterruptBinding.BindMessage"/>, ADR 0029),
/// in the function's configuration space itself, where MSI-X keeps a table in a BAR. A driver of a function with MSI
/// alone — an AHCI controller, VMware's and QEMU's ICH9 — enables one vector. The kernel programs no device: everything
/// here is the driver's writes through the configuration region it holds.
/// </summary>
public sealed class Msi
{
    #region Constants

    /// <summary>
    /// The capability's identifier.
    /// </summary>
    public const byte CAPABILITY = 0x05;

    // Message Control.
    private const ushort ENABLE = 0x0001;
    private const ushort MULTIPLE_MESSAGE_ENABLE = 0x0070;
    private const ushort ADDRESS_64 = 0x0080;
    private const ushort VECTOR_MASKING = 0x0100;

    #endregion

    #region Fields

    private readonly MmioRegion m_configuration;
    private readonly int m_capability;

    #endregion

    #region Constructors

    private Msi(MmioRegion configuration, int capability)
    {
        m_configuration = configuration;
        m_capability = capability;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Finds the function's MSI capability.
    /// </summary>
    /// <param name="configuration">The function's configuration space, writable.</param>
    /// <returns>The capability; null when the function has none.</returns>
    public static Msi? Find(MmioRegion configuration)
    {
        var capability = PciConfiguration.FindCapability(configuration, CAPABILITY);
        return capability == 0 ? null : new Msi(configuration, capability);
    }

    /// <summary>
    /// Writes the message of the function's one vector, its mask bit cleared where the function masks vectors, and turns
    /// MSI on with a single message: the function sends it and no INTx from here.
    /// </summary>
    /// <param name="address">The message's address.</param>
    /// <param name="data">The message's data, of which the function writes the low 16 bits.</param>
    public void Enable(ulong address, uint data)
    {
        var control = m_configuration.Read16(m_capability + 2);
        m_configuration.Write32(m_capability + 4, (uint)address);
        var dataAt = m_capability + 8;
        if ((control & ADDRESS_64) != 0)
        {
            m_configuration.Write32(m_capability + 8, (uint)(address >> 32));
            dataAt = m_capability + 12;
        }
        m_configuration.Write16(dataAt, (ushort)data);
        if ((control & VECTOR_MASKING) != 0)
            m_configuration.Write32(dataAt + 4, 0); // the mask bits follow the data at a dword
        m_configuration.Write16(m_capability + 2, (ushort)(control & ~MULTIPLE_MESSAGE_ENABLE | ENABLE));
    }

    /// <summary>
    /// Turns MSI off for the function.
    /// </summary>
    public void Disable()
    {
        var control = m_configuration.Read16(m_capability + 2);
        m_configuration.Write16(m_capability + 2, (ushort)(control & ~ENABLE));
    }

    #endregion
}
