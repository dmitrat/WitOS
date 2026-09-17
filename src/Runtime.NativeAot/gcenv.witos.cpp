#include "gcenv.witos.h"

/* First source-port slice, not a complete GC environment. All other methods
 * deliberately remain undefined. No state or allocation lives in this shim. */
static bool page_size(size_t size, size_t* rounded)
{
    if (!size || size > SIZE_MAX - 4095) return false;
    *rounded = (size + 4095) & ~(size_t)4095;
    return true;
}
static bool range(void* address, size_t size, size_t* rounded)
{
    const uintptr_t base = (uintptr_t)address;
    return base && !(base & 4095) && page_size(size, rounded) && base <= UINTPTR_MAX - *rounded;
}
static bool node_supported(uint16_t node)
{
    return node == NUMA_NODE_UNDEFINED || node == 0;
}

void* GCToOSInterface::VirtualReserve(size_t size, size_t alignment, uint32_t flags, uint16_t node)
{
    size_t rounded;
    WitU64 address = 0;
    if (!page_size(size, &rounded) || flags != VirtualReserveFlags::None || !node_supported(node) ||
        (alignment && (alignment & (alignment - 1)))) return nullptr;
    /* Preserve the Windows GC path's minimum allocation alignment, with the
     * stronger requested alignment when supported by the kernel arena. */
    if (alignment < 65536) alignment = 65536;
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, rounded, alignment, 0, &address) != WIT_STATUS_OK)
        return nullptr;
    return (void*)(uintptr_t)address;
}

bool GCToOSInterface::VirtualCommit(void* address, size_t size, uint16_t node)
{
    size_t rounded;
    return node_supported(node) && range(address, size, &rounded) &&
        wit_native_call(WIT_CALL_MEMORY_COMMIT, (uintptr_t)address, rounded,
            WIT_MEMORY_READ | WIT_MEMORY_WRITE, nullptr) == WIT_STATUS_OK;
}

bool GCToOSInterface::VirtualDecommit(void* address, size_t size)
{
    size_t rounded;
    return range(address, size, &rounded) &&
        wit_native_call(WIT_CALL_MEMORY_DECOMMIT, (uintptr_t)address, rounded, 0, nullptr) == WIT_STATUS_OK;
}

bool GCToOSInterface::VirtualRelease(void* address, size_t size)
{
    /* Like upstream's Windows implementation, size is unused: the kernel owns
     * the reservation extent and accepts only its exact starting address. */
    (void)size;
    return wit_native_call(WIT_CALL_MEMORY_RELEASE, (uintptr_t)address, 0, 0, nullptr) == WIT_STATUS_OK;
}

bool GCToOSInterface::SupportsWriteWatch() { return false; }
