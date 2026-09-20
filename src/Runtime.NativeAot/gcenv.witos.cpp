#include "gcenv.witos.h"

/* Environment initialization is serialized by runtime startup, before worker
 * threads exist. Shutdown likewise requires the runtime's threads to be stopped.
 * No allocator resources or dynamic C++ initialization are needed here. */
static GCSystemInfo system_info;

static bool read_information(WitUserMemoryInfo* info)
{
    WitU64 copied = 0;
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)info, sizeof(*info),
        WIT_MEMORY_INFO_VERSION, &copied) == WIT_STATUS_OK && copied == sizeof(*info) &&
        info->Version == WIT_MEMORY_INFO_VERSION && info->Size == sizeof(*info) &&
        info->PageSize == 4096 && info->ProcessorCount == 1 && info->PhysicalTotalBytes &&
        info->PhysicalAvailableBytes <= info->PhysicalTotalBytes && info->OwnedLimitBytes &&
        info->OwnedBytes <= info->OwnedLimitBytes && info->PrivatePageTableBytes <= info->OwnedBytes &&
        info->DynamicCommittedBytes <= info->OwnedBytes - info->PrivatePageTableBytes &&
        info->VirtualBytes && info->VirtualBase <= UINT64_MAX - info->VirtualBytes &&
        info->ReservedBytes <= info->VirtualBytes && info->ReservationCount <= info->ReservationCapacity;
}
static uint64_t minimum(uint64_t a, uint64_t b) { return a < b ? a : b; }

bool GCToOSInterface::Initialize()
{
    WitU64 version = 0;
    WitUserMemoryInfo info;
    if (wit_native_call(WIT_CALL_QUERY, 0, 0, 0, &version) != WIT_STATUS_OK ||
        version != WIT_ABI_VERSION || !read_information(&info)) return false;
    system_info.dwNumberOfProcessors = info.ProcessorCount;
    system_info.dwPageSize = info.PageSize;
    system_info.dwAllocationGranularity = 65536; // VirtualReserve's minimum alignment.
    return true;
}
void GCToOSInterface::Shutdown()
{
    system_info.dwNumberOfProcessors = 0;
    system_info.dwPageSize = 0;
    system_info.dwAllocationGranularity = 0;
}
uint32_t GCToOSInterface::GetTotalProcessorCount() { return system_info.dwNumberOfProcessors; }
bool GCToOSInterface::CanEnableGCNumaAware() { return false; }
bool GCToOSInterface::CanEnableGCCPUGroups() { return false; }

size_t GCToOSInterface::GetVirtualMemoryLimit()
{
    WitUserMemoryInfo info;
    return read_information(&info) ? (size_t)info.VirtualBytes : 0;
}
size_t GCToOSInterface::GetVirtualMemoryMaxAddress()
{
    WitUserMemoryInfo info;
    return read_information(&info) ? (size_t)(info.VirtualBase + info.VirtualBytes) : 0;
}
uint64_t GCToOSInterface::GetPhysicalMemoryLimit(bool* is_restricted)
{
    WitUserMemoryInfo info;
    if (is_restricted) *is_restricted = false;
    if (!read_information(&info)) return 0;
    if (is_restricted) *is_restricted = info.OwnedLimitBytes < info.PhysicalTotalBytes;
    return minimum(info.OwnedLimitBytes, info.PhysicalTotalBytes);
}
void GCToOSInterface::GetMemoryStatus(uint64_t restricted_limit, uint32_t* memory_load,
    uint64_t* available_physical, uint64_t* available_page_file)
{
    WitUserMemoryInfo info;
    // Fail conservatively. Zero page-file estimate matches the restricted
    // upstream path; WitOS currently has no paging store.
    if (memory_load) *memory_load = 100;
    if (available_physical) *available_physical = 0;
    if (available_page_file) *available_page_file = 0;
    if (!read_information(&info)) return;
    const uint64_t limit = restricted_limit ? minimum(restricted_limit,
        minimum(info.OwnedLimitBytes, info.PhysicalTotalBytes)) : info.PhysicalTotalBytes;
    const uint64_t used = restricted_limit ? minimum(info.OwnedBytes, limit) :
        info.PhysicalTotalBytes - info.PhysicalAvailableBytes;
    // Kernel v1 totals are bounded by the 4 GiB physical allocator, so used*100
    // cannot overflow. Available is an upper bound: new page tables cost frames.
    if (memory_load) *memory_load = (uint32_t)(used * 100 / limit);
    if (available_physical) *available_physical = minimum(limit - used, info.PhysicalAvailableBytes);
}
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

bool GCToOSInterface::VirtualReset(void* address, size_t size, bool unlock)
{
    size_t rounded;
    /* No working-set locking API exists. Reject unlock before mutation. */
    return !unlock && range(address, size, &rounded) &&
        wit_native_call(WIT_CALL_MEMORY_RESET, (uintptr_t)address, rounded, 0, nullptr) == WIT_STATUS_OK;
}

bool GCToOSInterface::SupportsWriteWatch() { return false; }
