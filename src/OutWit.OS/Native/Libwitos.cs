using System.Runtime.InteropServices;

namespace OutWit.OS.Native;

/// <summary>
/// libwitos's capability API (witos/capabilities.h, plan step D1.1). Every function returns zero or an errno value. A
/// NativeAOT service links libwitos statically and binds these functions directly (DirectPInvoke).
/// </summary>
internal static unsafe partial class Libwitos
{
    #region Constants

    private const string LIBRARY = "libwitos";

    #endregion

    #region Functions

    [LibraryImport(LIBRARY, EntryPoint = "witos_take_grant")]
    internal static partial int TakeGrant(ulong* handle);

    [LibraryImport(LIBRARY, EntryPoint = "witos_handle_close")]
    internal static partial int HandleClose(ulong handle);

    [LibraryImport(LIBRARY, EntryPoint = "witos_handle_duplicate")]
    internal static partial int HandleDuplicate(ulong handle, ulong rights, ulong* duplicate);

    [LibraryImport(LIBRARY, EntryPoint = "witos_channel_create")]
    internal static partial int ChannelCreate(ulong* first, ulong* second);

    [LibraryImport(LIBRARY, EntryPoint = "witos_channel_send")]
    internal static partial int ChannelSend(ulong endpoint, byte* bytes, uint count, ulong* handles, uint handleCount);

    [LibraryImport(LIBRARY, EntryPoint = "witos_channel_receive")]
    internal static partial int ChannelReceive(ulong endpoint, byte* bytes, uint capacity, uint* count, ulong* handles,
        uint handleCapacity, uint* handleCount);

    [LibraryImport(LIBRARY, EntryPoint = "witos_memory_map")]
    internal static partial int MemoryMap(ulong memoryObject, ulong offset, ulong bytes, uint protection, void** address);

    [LibraryImport(LIBRARY, EntryPoint = "witos_memory_unmap")]
    internal static partial int MemoryUnmap(void* address);

    [LibraryImport(LIBRARY, EntryPoint = "witos_device_acquire")]
    internal static partial int DeviceAcquire(ulong table, uint index, ulong* device);

    [LibraryImport(LIBRARY, EntryPoint = "witos_device_memory")]
    internal static partial int DeviceMemory(ulong device, uint region, ulong* memoryObject);

    [LibraryImport(LIBRARY, EntryPoint = "witos_memory_object_create")]
    internal static partial int MemoryObjectCreate(ulong bytes, ulong* memoryObject);

    [LibraryImport(LIBRARY, EntryPoint = "witos_event_create")]
    internal static partial int EventCreate(ulong* handle);

    [LibraryImport(LIBRARY, EntryPoint = "witos_object_wait")]
    internal static partial int ObjectWait(ulong* handles, uint count, ulong deadline, uint* index);

    [LibraryImport(LIBRARY, EntryPoint = "witos_interrupt_bind")]
    internal static partial int InterruptBind(ulong device, uint line, ulong handle, ulong* binding);

    [LibraryImport(LIBRARY, EntryPoint = "witos_interrupt_ack")]
    internal static partial int InterruptAck(ulong binding);

    [LibraryImport(LIBRARY, EntryPoint = "witos_dma_pin")]
    internal static partial int DmaPin(ulong device, ulong memoryObject, ulong offset, ulong bytes, ulong* ranges, uint capacity,
        ulong* pin);

    [LibraryImport(LIBRARY, EntryPoint = "witos_dma_unpin")]
    internal static partial int DmaUnpin(ulong pin);

    [LibraryImport(LIBRARY, EntryPoint = "witos_device_barrier")]
    internal static partial void DeviceBarrier();

    [LibraryImport(LIBRARY, EntryPoint = "witos_manager_spawn")]
    internal static partial int ManagerSpawn(byte* path, byte** argv, byte** envp, ulong grant, ulong* process);

    #endregion
}
