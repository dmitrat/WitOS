using OutWit.OS.Hardware;

namespace OutWit.OS.DeviceManager.Uhi;

/// <summary>
/// A device as the device manager knows it (RFC 0007 §47–55): a stable identity apart from the descriptor's index, the
/// hardware class or the semantic contract it serves, the provider of its interface, and the descriptor it came from.
/// </summary>
/// <param name="Id">The stable identity: the bus address with the vendor and device words.</param>
/// <param name="Class">The semantic contract where the provider is known (BlockStorageController), otherwise the
/// hardware class (HostBridge).</param>
/// <param name="Provider">The interface's provider: Virtio, Intel, QEMU.</param>
/// <param name="Descriptor">The platform's descriptor.</param>
internal sealed record DeviceRecord(string Id, string Class, string Provider, DeviceDescriptor Descriptor)
{
    #region Properties

    /// <summary>
    /// The discovery line Implementation Strategy §44 asks for.
    /// </summary>
    public string DiscoveryLine =>
        $"Device discovered: Class: {Class}, Provider: {Provider}, DeviceId: {Id}, " +
        $"Regions: {Descriptor.Regions.Count}, Lines: {Descriptor.Lines.Count}";

    #endregion
}
