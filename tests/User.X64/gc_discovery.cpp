#include "gcenv.witos.h"
#include "protocol.h"

using OS = GCToOSInterface;

static WitU64 query(void *buffer, WitU64 size = WIT_MEMORY_INFO_SIZE, WitU64 version = WIT_MEMORY_INFO_VERSION)
{
    WitU64 copied = 999;
    const auto status = wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)buffer, size, version, &copied);
    return copied == (status == WIT_STATUS_OK ? WIT_MEMORY_INFO_SIZE : 0) ? status : 999;
}

static bool same(const void *a, const void *b, size_t size)
{
    const auto x = (const volatile unsigned char *)a;
    const auto y = (const volatile unsigned char *)b;
    for (size_t i = 0; i < size; ++i) {
        if (x[i] != y[i]) {
            return false;
        }
    }
    return true;
}

static void fill(void *p, size_t size)
{
    for (size_t i = 0; i < size; ++i) {
        ((volatile unsigned char *)p)[i] = 0xA5;
    }
}

static bool untouched(const void *p, size_t size)
{
    for (size_t i = 0; i < size; ++i) {
        if (((const volatile unsigned char *)p)[i] != 0xA5) {
            return false;
        }
    }
    return true;
}

static uint64_t smaller(uint64_t a, uint64_t b)
{
    return a < b ? a : b;
}

static bool status_matches(const WitUserMemoryInfo &info)
{
    uint32_t load = 200;
    uint64_t available = UINT64_MAX, paging = UINT64_MAX;
    const auto limit = smaller(info.OwnedLimitBytes, info.PhysicalTotalBytes);
    const auto used = smaller(info.OwnedBytes, limit);
    OS::GetMemoryStatus(UINT64_MAX, &load, &available, &paging);
    if (load != used * 100 / limit || available != smaller(limit - used, info.PhysicalAvailableBytes) || paging != 0) {
        return false;
    }
    OS::GetMemoryStatus(1, &load, &available, nullptr);
    if (load != 100 || available != 0) {
        return false;
    }
    OS::GetMemoryStatus(0, &load, &available, &paging);
    if (load != (info.PhysicalTotalBytes - info.PhysicalAvailableBytes) * 100 / info.PhysicalTotalBytes ||
        available != info.PhysicalAvailableBytes ||
        paging != 0) {
        return false;
    }
    OS::GetMemoryStatus(0, nullptr, nullptr, nullptr);
    return true;
}

WitU64 wit_gc_discovery(const WitUserStartup *startup)
{
    auto baseline = (WitUserMemoryInfo *)WIT_GC_INFO_REPORT;
    auto cross = (WitUserMemoryInfo *)WIT_GC_INFO_CROSS;
    WitUserMemoryInfo current;
    unsigned char invalid[WIT_MEMORY_INFO_SIZE];
    static const unsigned char zero_tail[32] = {};
    if (query(baseline) != WIT_STATUS_OK ||
        query(cross) != WIT_STATUS_OK ||
        !same(baseline, cross, sizeof(*baseline))) {
        return 130;
    }
    if (baseline->Version != WIT_MEMORY_INFO_VERSION ||
        baseline->Size != WIT_MEMORY_INFO_SIZE ||
        baseline->PageSize != 4096 ||
        baseline->ProcessorCount != 1 ||
        baseline->ReservationCount ||
        baseline->ReservedBytes ||
        baseline->DynamicCommittedBytes ||
        !baseline->PrivatePageTableBytes) {
        return 131;
    }
    if (OS::GetTotalProcessorCount() != 0 ||
        !OS::Initialize() ||
        !OS::Initialize() ||
        OS::GetTotalProcessorCount() != baseline->ProcessorCount ||
        OS::GetPageSize() != baseline->PageSize ||
        OS::GetVirtualMemoryLimit() != baseline->VirtualBytes ||
        OS::GetVirtualMemoryMaxAddress() != baseline->VirtualBase + baseline->VirtualBytes ||
        OS::CanEnableGCNumaAware() ||
        OS::CanEnableGCCPUGroups()) {
        return 132;
    }
    WitU64 cache = 0;
    if (wit_native_call(WIT_CALL_CPU_CACHE_SIZE, 0, 0, 0, &cache) != WIT_STATUS_OK ||
        !cache ||
        OS::GetCacheSizePerLogicalCpu(true) != cache ||
        OS::GetCacheSizePerLogicalCpu(false) != cache) {
        return 139;
    }
    *(uint64_t *)(baseline + 1) = cache;
    for (WitU32 i = 0; i < 3; ++i) {
        WitU64 result = 99;
        if (wit_native_call(WIT_CALL_CPU_CACHE_SIZE, i == 0, i == 1, i == 2, &result) != WIT_STATUS_INVALID_ARGUMENT ||
            result) {
            return 140;
        }
    }
    bool restricted = false;
    if (OS::GetPhysicalMemoryLimit(&restricted) != smaller(baseline->OwnedLimitBytes, baseline->PhysicalTotalBytes) ||
        restricted != (baseline->OwnedLimitBytes < baseline->PhysicalTotalBytes) ||
        OS::GetPhysicalMemoryLimit(nullptr) != smaller(baseline->OwnedLimitBytes, baseline->PhysicalTotalBytes) ||
        !status_matches(*baseline)) {
        return 133;
    }

    fill(invalid, sizeof(invalid));
    if (query(invalid, sizeof(invalid), 0) != WIT_STATUS_UNSUPPORTED ||
        query(invalid, sizeof(invalid) - 1) != WIT_STATUS_INVALID_ARGUMENT ||
        query(invalid, sizeof(invalid) + 1) != WIT_STATUS_INVALID_ARGUMENT ||
        query(invalid, UINT64_MAX) != WIT_STATUS_INVALID_ARGUMENT ||
        !untouched(invalid, sizeof(invalid))) {
        return 134;
    }
    const auto config = (const WitUserTestConfig *)startup;
    if (query(nullptr) != WIT_STATUS_BAD_ADDRESS ||
        query((void *)startup) != WIT_STATUS_BAD_ADDRESS ||
        query((void *)(uintptr_t)&wit_gc_discovery) != WIT_STATUS_BAD_ADDRESS ||
        query((void *)(uintptr_t)config->KernelProbe) != WIT_STATUS_BAD_ADDRESS ||
        query((void *)(UINTPTR_MAX - 47)) != WIT_STATUS_BAD_ADDRESS) {
        return 135;
    }
    auto end = (unsigned char *)WIT_GC_INFO_CROSS + 4096;
    fill(end, 64);
    if (query(end) != WIT_STATUS_BAD_ADDRESS || !untouched(end, 64)) {
        return 136;
    }

    auto p = (unsigned char *)OS::VirtualReserve(8192, 0, 0);
    if (!p ||
        query(&current) != WIT_STATUS_OK ||
        current.OwnedBytes != baseline->OwnedBytes ||
        current.PhysicalAvailableBytes != baseline->PhysicalAvailableBytes ||
        current.ReservedBytes != 8192 ||
        current.DynamicCommittedBytes ||
        current.ReservationCount != 1) {
        return 137;
    }
    if (!OS::VirtualCommit(p, 4096) ||
        query(&current) != WIT_STATUS_OK ||
        current.OwnedBytes != baseline->OwnedBytes + 4 * 4096 ||
        current.PrivatePageTableBytes != baseline->PrivatePageTableBytes + 3 * 4096 ||
        current.PhysicalAvailableBytes != baseline->PhysicalAvailableBytes - 4 * 4096 ||
        current.DynamicCommittedBytes != 4096 ||
        !status_matches(current)) {
        return 138;
    }
    auto partial = p + 4096 - 64;
    fill(partial, 64);
    if (query(partial) != WIT_STATUS_BAD_ADDRESS || !untouched(partial, 64)) {
        return 139;
    }
    if (!OS::VirtualCommit(p + 4096, 4096) ||
        wit_native_call(WIT_CALL_MEMORY_PROTECT, (uintptr_t)(p + 4096), 4096, WIT_MEMORY_READ, nullptr) !=
            WIT_STATUS_OK ||
        query(partial) != WIT_STATUS_BAD_ADDRESS ||
        !untouched(partial, 64) ||
        !same(p + 4096, zero_tail, sizeof(zero_tail))) {
        return 140;
    }
    if (wit_native_call(WIT_CALL_MEMORY_PROTECT, (uintptr_t)(p + 4096), 4096, WIT_MEMORY_NONE, nullptr) !=
            WIT_STATUS_OK ||
        query(partial) != WIT_STATUS_BAD_ADDRESS ||
        !untouched(partial, 64) ||
        query(&current) != WIT_STATUS_OK ||
        current.OwnedBytes != baseline->OwnedBytes + 5 * 4096 ||
        current.DynamicCommittedBytes != 8192 ||
        !status_matches(current)) {
        return 141;
    }
    if (wit_native_call(WIT_CALL_MEMORY_PROTECT, (uintptr_t)(p + 4096), 4096, WIT_MEMORY_READ | WIT_MEMORY_WRITE,
            nullptr) != WIT_STATUS_OK ||
        !same(p + 4096, zero_tail, sizeof(zero_tail)) ||
        query(partial) != WIT_STATUS_OK ||
        !same(partial, &current, sizeof(current))) {
        return 142;
    }
    if (!OS::VirtualDecommit(p + 4096, 4096) ||
        query(&current) != WIT_STATUS_OK ||
        current.DynamicCommittedBytes != 4096 ||
        current.OwnedBytes != baseline->OwnedBytes + 4 * 4096 ||
        !status_matches(current)) {
        return 143;
    }
    if (!OS::VirtualRelease(p, 8192) ||
        query(&current) != WIT_STATUS_OK ||
        !same(&current, baseline, sizeof(current))) {
        return 144;
    }
    OS::Shutdown();
    if (OS::GetTotalProcessorCount() != 0 || !OS::Initialize() || OS::GetTotalProcessorCount() != 1) {
        return 145;
    }
    OS::Shutdown();
    return WIT_TEST_EXIT_CODE;
}
