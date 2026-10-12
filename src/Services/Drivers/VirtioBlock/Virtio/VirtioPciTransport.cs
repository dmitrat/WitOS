using System;
using System.IO;
using OutWit.OS.Hardware;
using OutWit.OS.Resources;

namespace OutWit.OS.Drivers.VirtioBlock.Virtio;

/// <summary>
/// The modern virtio PCI transport of a device (virtio 1.x, §4.1): its configuration space with memory decoding and bus
/// mastering on, and the BAR its vendor capabilities name, holding the common configuration, the notification area and
/// the ISR status. The kernel knows none of this; the driver reads it through the regions DEVICE_MEMORY gave it.
/// </summary>
internal sealed class VirtioPciTransport : IDisposable
{
    #region Constants

    private const byte CAPABILITY_VENDOR = 9;
    private const int COMMON = 1;
    private const int NOTIFY = 2;
    private const int ISR = 3;
    private const int DEVICE = 4;
    private const int COMMAND = 0x04;
    private const int STATUS = 0x06;
    private const int CAPABILITIES = 0x34;
    private const int BARS = 0x10;

    #endregion

    #region Fields

    private readonly KernelHandle m_configurationObject;
    private readonly KernelHandle m_barObject;

    #endregion

    #region Constructors

    private VirtioPciTransport(KernelHandle configurationObject, MmioRegion configuration, KernelHandle barObject, MmioRegion bar,
        int common, int notify, int isr, uint notifyMultiplier)
    {
        m_configurationObject = configurationObject;
        Configuration = configuration;
        m_barObject = barObject;
        Bar = bar;
        Common = common;
        Notify = notify;
        Isr = isr;
        NotifyMultiplier = notifyMultiplier;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Opens the transport of the device the driver holds.
    /// </summary>
    /// <param name="device">The device, with BIND.</param>
    /// <param name="descriptor">Its descriptor: region 0 its configuration space, the others its BARs.</param>
    /// <returns>The transport.</returns>
    /// <exception cref="IOException">The device has no modern virtio capabilities, or its BAR is not among its regions.</exception>
    /// <exception cref="CapabilityException">A region could not be had.</exception>
    public static VirtioPciTransport Open(KernelHandle device, DeviceDescriptor descriptor)
    {
        var configurationObject = Device.Memory(device, 0);
        var configuration = MmioRegion.Map(configurationObject, MemoryObject.PAGE);
        try
        {
            configuration.Write16(COMMAND, (ushort)(configuration.Read16(COMMAND) | 6)); // memory decoding, bus mastering
            if ((configuration.Read8(STATUS) & 0x10) == 0)
                throw new IOException("The function has no capability list.");
            Span<int> bars = stackalloc int[5];
            Span<int> offsets = stackalloc int[5];
            uint multiplier = 0, found = 0;
            for (int at = configuration.Read8(CAPABILITIES); at != 0; at = configuration.Read8(at + 1))
            {
                int type = configuration.Read8(at + 3);
                if (configuration.Read8(at) != CAPABILITY_VENDOR || type is < COMMON or > DEVICE)
                    continue;
                found |= 1U << type;
                bars[type] = configuration.Read8(at + 4);
                offsets[type] = (int)configuration.Read32(at + 8);
                if (type == NOTIFY)
                    multiplier = configuration.Read32(at + 16);
            }
            if (found != 0x1E || bars[COMMON] != bars[NOTIFY] || bars[COMMON] != bars[ISR])
                throw new IOException($"The function's virtio capabilities are {found:x} in BARs {bars[COMMON]}, {bars[NOTIFY]}, {bars[ISR]}.");
            // The BAR the capabilities name, among the descriptor's regions by its firmware-assigned base.
            var raw = configuration.Read32(BARS + bars[COMMON] * 4);
            ulong address = raw & 0xFFFFFFF0U;
            if ((raw & 6) == 4)
                address |= (ulong)configuration.Read32(BARS + 4 + bars[COMMON] * 4) << 32;
            var region = 1;
            while (region < descriptor.Regions.Count && descriptor.Regions[region].Base != address)
                region++;
            if (region == descriptor.Regions.Count)
                throw new IOException($"The virtio BAR at {address:x} is not among the descriptor's regions.");
            var barObject = Device.Memory(device, region);
            try
            {
                var bar = MmioRegion.Map(barObject, (int)descriptor.Regions[region].Size);
                return new VirtioPciTransport(configurationObject, configuration, barObject, bar, offsets[COMMON], offsets[NOTIFY],
                    offsets[ISR], multiplier);
            }
            catch
            {
                barObject.Dispose();
                throw;
            }
        }
        catch
        {
            configuration.Dispose();
            configurationObject.Dispose();
            throw;
        }
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Bar.Dispose();
        m_barObject.Dispose();
        Configuration.Dispose();
        m_configurationObject.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>
    /// The function's configuration space.
    /// </summary>
    public MmioRegion Configuration { get; }

    /// <summary>
    /// The BAR of the common configuration, the notifications and the ISR status.
    /// </summary>
    public MmioRegion Bar { get; }

    /// <summary>
    /// The common configuration's offset in the BAR.
    /// </summary>
    public int Common { get; }

    /// <summary>
    /// The notification area's offset in the BAR.
    /// </summary>
    public int Notify { get; }

    /// <summary>
    /// The ISR status register's offset in the BAR.
    /// </summary>
    public int Isr { get; }

    /// <summary>
    /// The bytes between two queues' notification addresses.
    /// </summary>
    public uint NotifyMultiplier { get; }

    #endregion
}
