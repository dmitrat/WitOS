#include "gcenv.witos.h"
#include "../User/protocol.h"

using OS = GCToOSInterface;

static bool snapshot(WitUserMemoryInfo *info)
{
    WitU64 size = 0;
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)info, sizeof(*info), WIT_MEMORY_INFO_VERSION, &size) ==
        WIT_STATUS_OK &&
        size == sizeof(*info);
}

static bool same(const WitUserMemoryInfo &a, const WitUserMemoryInfo &b)
{
    return a.OwnedBytes == b.OwnedBytes &&
        a.PrivatePageTableBytes == b.PrivatePageTableBytes &&
        a.DynamicCommittedBytes == b.DynamicCommittedBytes &&
        a.ReservedBytes == b.ReservedBytes &&
        a.ReservationCount == b.ReservationCount &&
        a.PhysicalAvailableBytes == b.PhysicalAvailableBytes;
}

static bool zero(volatile unsigned char *p, size_t size)
{
    for (size_t i = 0; i < size; ++i) {
        if (p[i]) {
            return false;
        }
    }
    return true;
}

static bool rejected(void *address, size_t size, WitU64 flags, WitU64 expected)
{
    WitU64 result = ~0ULL;
    return wit_native_call(WIT_CALL_MEMORY_RESET, (uintptr_t)address, size, flags, &result) == expected && !result;
}

WitU64 wit_gc_reset(WitU64 mode)
{
    constexpr size_t span = 16 * 1024 * 1024;
    auto p = (unsigned char *)OS::VirtualReserve(span, 0, 0);
    if (!p || !OS::VirtualCommit(p, 3 * 4096)) {
        return 701;
    }
    WitUserMemoryInfo before, after;
    if (!snapshot(&before)) {
        return 702;
    }
    for (size_t i = 0; i < 3 * 4096; ++i) {
        ((volatile unsigned char *)p)[i] = 0xAC;
    }
    // Reset accepts committed protected pages, but must not grant write access.
    if (mode != WIT_GC_TEST_RESET) {
        const WitU64 protection = mode == WIT_GC_TEST_RESET_RO ? WIT_MEMORY_READ : WIT_MEMORY_NONE;
        if (wit_native_call(WIT_CALL_MEMORY_PROTECT, (uintptr_t)p, 4096, protection, nullptr) != WIT_STATUS_OK ||
            !OS::VirtualReset(p, 4096, false) ||
            !snapshot(&after) ||
            !same(before, after)) {
            return 703;
        }
        if (protection == WIT_MEMORY_READ && !zero(p, 4096)) {
            return 704;
        }
        *(volatile unsigned char *)p = 1; // Must fault with the original protection.
        return 705;
    }
    // A partial-size GC request rounds to pages; adjacent data is not discarded.
    if (!OS::VirtualReset(p + 4096, 1, false) ||
        !zero(p + 4096, 4096) ||
        p[0] != 0xAC ||
        p[4095] != 0xAC ||
        p[8192] != 0xAC ||
        p[12287] != 0xAC ||
        !snapshot(&after) ||
        !same(before, after)) {
        return 706;
    }
    if (OS::VirtualReset(nullptr, 4096, false) ||
        OS::VirtualReset(p + 1, 4096, false) ||
        OS::VirtualReset(p, 0, false) ||
        OS::VirtualReset(p, SIZE_MAX, false) ||
        OS::VirtualReset((void *)(UINTPTR_MAX - 4095), 4096, false) ||
        OS::VirtualReset(p, 4096, true) ||
        p[0] != 0xAC) {
        return 707;
    }
    if (!rejected(p, 4096, 1, WIT_STATUS_INVALID_ARGUMENT) ||
        !rejected(p + 1, 4096, 0, WIT_STATUS_INVALID_ARGUMENT) ||
        !rejected(p, 4095, 0, WIT_STATUS_INVALID_ARGUMENT) ||
        !rejected(p, 0, 0, WIT_STATUS_INVALID_ARGUMENT) ||
        !rejected((void *)(UINTPTR_MAX - 4095), 8192, 0, WIT_STATUS_BAD_ADDRESS)) {
        return 708;
    }
    if (!rejected((void *)((uintptr_t)&wit_gc_reset & ~(uintptr_t)4095), 4096, 0, WIT_STATUS_BAD_ADDRESS) ||
        !rejected(p + span, 4096, 0, WIT_STATUS_NOT_RESERVED) ||
        !rejected(p, span + 4096, 0, WIT_STATUS_NOT_RESERVED) ||
        !rejected(p, span, 0, WIT_STATUS_NOT_COMMITTED) ||
        !rejected(p, 4 * 4096, 0, WIT_STATUS_NOT_COMMITTED) ||
        p[0] != 0xAC ||
        p[12287] != 0xAC ||
        !snapshot(&after) ||
        !same(before, after)) {
        return 709;
    }
    // A hole in the middle must reject the entire reset, leaving both sides intact.
    if (!OS::VirtualDecommit(p + 4096, 4096) ||
        !snapshot(&before) ||
        !rejected(p, 3 * 4096, 0, WIT_STATUS_NOT_COMMITTED) ||
        p[0] != 0xAC ||
        p[12287] != 0xAC ||
        !snapshot(&after) ||
        !same(before, after) ||
        !OS::VirtualCommit(p + 4096, 4096)) {
        return 710;
    }
    // No-access backing is discarded without changing commitment or permissions.
    if (wit_native_call(WIT_CALL_MEMORY_PROTECT, (uintptr_t)(p + 4096), 4096, WIT_MEMORY_NONE, nullptr) !=
            WIT_STATUS_OK ||
        !snapshot(&before) ||
        !OS::VirtualReset(p, 3 * 4096, false) ||
        !snapshot(&after) ||
        !same(before, after) ||
        wit_native_call(WIT_CALL_MEMORY_PROTECT, (uintptr_t)(p + 4096), 4096, WIT_MEMORY_READ | WIT_MEMORY_WRITE,
            nullptr) != WIT_STATUS_OK ||
        !zero(p, 3 * 4096)) {
        return 711;
    }
    // Exhaust actual commitment. Reset still succeeds without another backing page.
    size_t committed = 3;
    while (committed < 128 && OS::VirtualCommit(p + committed * 4096, 4096)) {
        ++committed;
    }
    if (committed == 128 || !snapshot(&before)) {
        return 712;
    }
    *(volatile unsigned char *)p = 0xBD;
    if (!OS::VirtualReset(p, committed * 4096, false) ||
        !zero(p, committed * 4096) ||
        !snapshot(&after) ||
        !same(before, after)) {
        return 713;
    }
    *(volatile unsigned char *)p = 0xCE;
    if (!OS::VirtualCommit(p, 4096) ||
        p[0] != 0xCE ||
        !OS::VirtualReset(p, 4096, false) ||
        !OS::VirtualReset(p, 4096, false) ||
        !zero(p, 4096) ||
        !OS::VirtualRelease(p, 0) ||
        !rejected(p, 4096, 0, WIT_STATUS_NOT_RESERVED)) {
        return 714;
    }
    return WIT_TEST_EXIT_CODE;
}
