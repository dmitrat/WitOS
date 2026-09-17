#include "gcenv.witos.h"
#include "protocol.h"

using OS = GCToOSInterface;
WitU64 wit_gc_discovery(const WitUserStartup* startup);
WitU64 wit_gc_events(WitU64 mode);
static bool zero(volatile unsigned char* p, size_t size)
{
    for (size_t i = 0; i < size; ++i) if (p[i]) return false;
    return true;
}
static WitU64 normal()
{
    constexpr size_t span = 16 * 1024 * 1024;
    if (OS::SupportsWriteWatch() || OS::VirtualReserve(0, 0, 0) ||
        OS::VirtualReserve(SIZE_MAX, 0, 0) || OS::VirtualReserve(4096, 3, 0) ||
        OS::VirtualReserve(4096, 0, VirtualReserveFlags::WriteWatch) ||
        OS::VirtualReserve(4096, 0, 2) || OS::VirtualReserve(4096, 0, 0, 1) ||
        OS::VirtualReserve(4096, (size_t)1 << 63, 0)) return 101;
    auto p = (unsigned char*)OS::VirtualReserve(span + 1, 2 * 1024 * 1024, 0, 0);
    if (!p || ((uintptr_t)p & ((2 * 1024 * 1024) - 1))) return 102;
    if (!OS::VirtualCommit(p, 8192) || !zero(p, 8192)) return 103;
    *(volatile unsigned char*)p = 0xAC;
    *(volatile unsigned char*)(p + 8191) = 0xBD;
    if (!OS::VirtualCommit(p, 4096) || p[0] != 0xAC || p[8191] != 0xBD) return 104;
    if (OS::VirtualCommit(p + 1, 4096) || OS::VirtualCommit(p, 0) ||
        OS::VirtualCommit(p, SIZE_MAX) || OS::VirtualCommit(p, 4096, 1) ||
        OS::VirtualCommit(p + span + 4096, 4096) || OS::VirtualDecommit(p + 1, 4096) ||
        OS::VirtualDecommit(p + span + 4096, 4096) || OS::VirtualRelease(p + 4096, span)) return 105;
    if (p[0] != 0xAC || p[8191] != 0xBD || !OS::VirtualDecommit(p, 1) ||
        !OS::VirtualDecommit(p, 4096) || !OS::VirtualCommit(p, 1) || !zero(p, 4096) || p[8191] != 0xBD)
        return 106;
    if (!OS::VirtualCommit(p + span, 1, 0) || !zero(p + span, 4096) ||
        !OS::VirtualRelease(p, span + 1) || OS::VirtualCommit(p, 4096) ||
        OS::VirtualDecommit(p, 4096) || OS::VirtualRelease(p, 0)) return 107;

    /* This request fits the per-call bound, but not the remaining owned-frame
     * quota. Existing data must survive; partial additions must be rolled back. */
    p = (unsigned char*)OS::VirtualReserve(128 * 4096, 0, 0);
    if (!p || ((uintptr_t)p & 65535) || !OS::VirtualCommit(p, 4096)) return 108;
    *(volatile unsigned char*)p = 0xCE;
    if (OS::VirtualCommit(p, 128 * 4096) || p[0] != 0xCE ||
        !OS::VirtualCommit(p + 4096, 4096) || !zero(p + 4096, 4096) ||
        !OS::VirtualRelease(p, 0)) return 109;
    void* reservations[8];
    for (size_t i = 0; i < 8; ++i) {
        reservations[i] = OS::VirtualReserve(1, 0, 0);
        if (!reservations[i]) return 110;
    }
    if (OS::VirtualReserve(1, 0, 0)) return 111;
    for (size_t i = 0; i < 8; ++i) if (!OS::VirtualRelease(reservations[i], 1)) return 112;
    p = (unsigned char*)OS::VirtualReserve(1, 4096, 0);
    if (p != reservations[0] || !OS::VirtualCommit(p, 1) || !zero(p, 4096) || !OS::VirtualRelease(p, 1))
        return 113;
    return WIT_TEST_EXIT_CODE;
}

/* A real image relocation, used when testing the same binary at two bases. */
static WitU64 (* const volatile selected_test)() = normal;

extern "C" WitU64 wit_native_main(const WitUserStartup* startup)
{
    if (!startup || startup->Version != WIT_ABI_VERSION || startup->Size != WIT_ABI_STARTUP_SIZE ||
        !startup->ImageInfo) return 100;
    const auto config = (const WitUserTestConfig*)startup;
    if (config->Mode >= WIT_GC_TEST_EVENT_STATE) return wit_gc_events(config->Mode);
    if (config->Mode == WIT_GC_TEST_DISCOVERY) return wit_gc_discovery(startup);
    if (config->Mode == WIT_GC_TEST_NORMAL) return selected_test();
    if (config->Mode == WIT_GC_TEST_ROLLBACK) {
        auto p = (unsigned char*)OS::VirtualReserve(128 * 4096, 0, 0);
        if (!p || !OS::VirtualCommit(p, 4096)) return 118;
        *(volatile unsigned char*)p = 0xCE;
        if (OS::VirtualCommit(p, 128 * 4096) || p[0] != 0xCE) return 119;
        return *(volatile unsigned char*)(p + 4096);
    }
    auto p = (unsigned char*)OS::VirtualReserve(4096, 0, 0);
    if (!p) return 114;
    if (config->Mode == WIT_GC_TEST_RESERVED) *(volatile unsigned char*)p = 1;
    if (!OS::VirtualCommit(p, 4096)) return 115;
    if (config->Mode == WIT_GC_TEST_DECOMMITTED) {
        if (!OS::VirtualDecommit(p, 4096)) return 116;
        return *(volatile unsigned char*)p;
    }
    if (config->Mode == WIT_GC_TEST_NX) {
        *(volatile unsigned char*)p = 0xC3;
        ((void(*)())p)();
    }
    return 117;
}
