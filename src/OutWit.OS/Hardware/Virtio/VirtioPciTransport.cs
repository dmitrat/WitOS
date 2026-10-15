using System;
using System.IO;
using OutWit.OS.Resources;

namespace OutWit.OS.Hardware.Virtio;

/// <summary>
/// The modern virtio PCI transport of a device (virtio 1.x, §4.1): its configuration space with memory decoding and bus
/// mastering on, and the BAR its vendor capabilities name, holding the common configuration, the notification area and
/// the ISR status and the device-specific configuration. The kernel knows none of this; a driver reads it through the
/// regions DEVICE_MEMORY gave it. The block driver wrote it (plan step D2); the console driver shares it (N4.3a).
/// </summary>
public sealed class VirtioPciTransport : IDisposable
{
    #region Constants

    /// <summary>
    /// VIRTIO_F_VERSION_1, bit 32 of the features: the one feature every driver requires.
    /// </summary>
    public const uint VERSION_1 = 1;

    private const byte CAPABILITY_VENDOR = 9;
    private const int COMMON = 1;
    private const int NOTIFY = 2;
    private const int ISR = 3;
    private const int DEVICE = 4;
    private const int COMMAND = 0x04;
    private const int STATUS = 0x06;
    private const int CAPABILITIES = 0x34;
    private const int BARS = 0x10;

    // The common configuration's registers (virtio 1.x, §4.1.4.3).
    private const int DEVICE_FEATURE_SELECT = 0;
    private const int DEVICE_FEATURE = 4;
    private const int DRIVER_FEATURE_SELECT = 8;
    private const int DRIVER_FEATURE = 12;
    private const int DEVICE_STATUS = 20;
    private const int CONFIG_GENERATION = 21;
    private const int QUEUE_SELECT = 22;
    private const int QUEUE_SIZE = 24;
    private const int QUEUE_ENABLE = 28;
    private const int QUEUE_NOTIFY_OFF = 30;
    private const int QUEUE_DESCRIPTOR = 32;
    private const int QUEUE_DRIVER = 40;
    private const int QUEUE_DEVICE = 48;

    // The device status bits (virtio 1.x, §2.1).
    private const byte ACKNOWLEDGE = 1;
    private const byte DRIVER = 2;
    private const byte DRIVER_OK = 4;
    private const byte FEATURES_OK = 8;

    #endregion

    #region Fields

    private readonly KernelHandle m_configurationObject;
    private readonly KernelHandle m_barObject;

    #endregion

    #region Constructors

    private VirtioPciTransport(KernelHandle configurationObject, MmioRegion configuration, KernelHandle barObject, MmioRegion bar,
        int common, int notify, int isr, int device, uint notifyMultiplier)
    {
        m_configurationObject = configurationObject;
        Configuration = configuration;
        m_barObject = barObject;
        Bar = bar;
        Common = common;
        Notify = notify;
        Isr = isr;
        DeviceConfiguration = device;
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
            if (found != 0x1E || bars[COMMON] != bars[NOTIFY] || bars[COMMON] != bars[ISR] || bars[COMMON] != bars[DEVICE])
                throw new IOException($"The function's virtio capabilities are {found:x} in BARs {bars[COMMON]}, {bars[NOTIFY]}, " +
                    $"{bars[ISR]}, {bars[DEVICE]}.");
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
                    offsets[ISR], offsets[DEVICE], multiplier);
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

    /// <summary>
    /// Resets the device and tells it a driver found it (ACKNOWLEDGE, DRIVER): the start of the initialization (virtio
    /// 1.x, §3.1.1).
    /// </summary>
    public void Reset()
    {
        Bar.Write8(Common + DEVICE_STATUS, 0);
        while (Bar.Read8(Common + DEVICE_STATUS) != 0)
        {
        }
        Bar.Write8(Common + DEVICE_STATUS, ACKNOWLEDGE | DRIVER);
    }

    /// <summary>
    /// Negotiates the features: VIRTIO_F_VERSION_1, which the device must offer, and those of the low 32 bits the
    /// driver accepts that the device offers; FEATURES_OK is set and checked.
    /// </summary>
    /// <param name="accepted">The low feature bits the driver takes when offered.</param>
    /// <returns>The low feature bits negotiated.</returns>
    /// <exception cref="IOException">The device does not offer VIRTIO_F_VERSION_1 or refuses the features.</exception>
    public uint Negotiate(uint accepted)
    {
        Bar.Write32(Common + DEVICE_FEATURE_SELECT, 1);
        if ((Bar.Read32(Common + DEVICE_FEATURE) & VERSION_1) == 0)
            throw new IOException("The device does not offer VIRTIO_F_VERSION_1.");
        Bar.Write32(Common + DEVICE_FEATURE_SELECT, 0);
        var features = Bar.Read32(Common + DEVICE_FEATURE) & accepted;
        Bar.Write32(Common + DRIVER_FEATURE_SELECT, 1);
        Bar.Write32(Common + DRIVER_FEATURE, VERSION_1);
        Bar.Write32(Common + DRIVER_FEATURE_SELECT, 0);
        Bar.Write32(Common + DRIVER_FEATURE, features);
        Bar.Write8(Common + DEVICE_STATUS, ACKNOWLEDGE | DRIVER | FEATURES_OK);
        if ((Bar.Read8(Common + DEVICE_STATUS) & FEATURES_OK) == 0)
            throw new IOException($"The device refused the features {features:x} with VIRTIO_F_VERSION_1.");
        return features;
    }

    /// <summary>
    /// Tells the device the driver is ready (DRIVER_OK): the end of the initialization.
    /// </summary>
    public void DriverReady() => Bar.Write8(Common + DEVICE_STATUS, ACKNOWLEDGE | DRIVER | FEATURES_OK | DRIVER_OK);

    /// <summary>
    /// Quiesces the device: its status reset to zero, so that it stops all queue activity (virtio 1.x, §2.4.2).
    /// </summary>
    public void Quiesce() => Bar.Write8(Common + DEVICE_STATUS, 0);

    /// <summary>
    /// The configuration generation, read before and after the device-specific configuration until it holds still.
    /// </summary>
    public byte ConfigurationGeneration => Bar.Read8(Common + CONFIG_GENERATION);

    /// <summary>
    /// Selects a queue and reads the size the device offers for it.
    /// </summary>
    /// <param name="index">The queue's index.</param>
    /// <returns>The entries the device offers; zero for a queue it has not.</returns>
    public ushort SelectQueue(ushort index)
    {
        Bar.Write16(Common + QUEUE_SELECT, index);
        return Bar.Read16(Common + QUEUE_SIZE);
    }

    /// <summary>
    /// Sets up the selected queue: its size, the physical addresses of its three parts, and enables it; the queue's
    /// notification offset in the BAR.
    /// </summary>
    /// <param name="size">The entries, at most what the device offered.</param>
    /// <param name="descriptors">The descriptor table's physical address.</param>
    /// <param name="driver">The available ring's physical address.</param>
    /// <param name="device">The used ring's physical address.</param>
    /// <returns>The offset in the BAR the driver notifies the queue at.</returns>
    public int EnableQueue(ushort size, ulong descriptors, ulong driver, ulong device)
    {
        Bar.Write16(Common + QUEUE_SIZE, size);
        Bar.Write64(Common + QUEUE_DESCRIPTOR, descriptors);
        Bar.Write64(Common + QUEUE_DRIVER, driver);
        Bar.Write64(Common + QUEUE_DEVICE, device);
        var notify = Notify + (int)(Bar.Read16(Common + QUEUE_NOTIFY_OFF) * NotifyMultiplier);
        Bar.Write16(Common + QUEUE_ENABLE, 1);
        return notify;
    }

    /// <summary>
    /// Reads the ISR status, which ends the interrupt's cause: bit 0 for a queue, bit 1 for a configuration change.
    /// </summary>
    /// <returns>The status.</returns>
    public byte ReadInterruptStatus() => Bar.Read8(Isr);

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
    /// The BAR of the common configuration, the notifications, the ISR status and the device-specific configuration.
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
    /// The device-specific configuration's offset in the BAR.
    /// </summary>
    public int DeviceConfiguration { get; }

    /// <summary>
    /// The bytes between two queues' notification addresses.
    /// </summary>
    public uint NotifyMultiplier { get; }

    #endregion
}
