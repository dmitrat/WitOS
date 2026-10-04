#include "library.h"
#include "../User/protocol.h"
#include "../../src/Kernel/include/witos/user_layout.h"
#pragma optimize("", off)
#define CHECK(v, c) \
    do { \
        if (!(v)) return c; \
    } while (0)

typedef struct TlsModule {
    WitU64 Handle, Value, Set, Address, Zero, Index;
} TlsModule;

static TlsModule modules[2];
static WitU64 mainAddresses[2];

static int snapshot(WitUserMemoryInfo *v)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (WitU64)v, sizeof(*v), WIT_MEMORY_INFO_VERSION, 0) == WIT_STATUS_OK;
}

static int same(const WitUserMemoryInfo *a, const WitUserMemoryInfo *b)
{
    return a->OwnedBytes == b->OwnedBytes &&
        a->PhysicalAvailableBytes == b->PhysicalAvailableBytes &&
        a->ReservedBytes == b->ReservedBytes &&
        a->DynamicCommittedBytes == b->DynamicCommittedBytes &&
        a->ReservationCount == b->ReservationCount &&
        a->PrivatePageTableBytes == b->PrivatePageTableBytes;
}

static WitU64 symbols(TlsModule *m)
{
    const char *names[] = {"TlsValue", "TlsSet", "TlsAddress", "TlsZero", "TlsIndex"};
    WitU64 *outputs[] = {&m->Value, &m->Set, &m->Address, &m->Zero, &m->Index};
    for (WitU32 i = 0; i < 5; ++i) {
        WitU32 n = 0;
        while (names[i][n]) {
            ++n;
        }
        if (wit_native_library_symbol(m->Handle, names[i], n, 0, outputs[i]) != WIT_STATUS_OK) {
            return 0;
        }
    }
    return 1;
}

static WitU64 child_body(void)
{
    CHECK(wit_native_library_thread_enter() == WIT_STATUS_OK, 3410);
    for (WitU32 i = 0; i < 2; ++i) {
        TlsModule *m = &modules[i];
        CHECK(((int (*)(void))m->Value)() == 736 && !((int (*)(void))m->Zero)(), 3411);
        CHECK((WitU64)((void *(*)(void))m->Address)() != mainAddresses[i], 3412);
        ((void (*)(int))m->Set)(7 + (int)i);
        CHECK(((int (*)(void))m->Value)() == 12 + (int)i, 3413);
    }
    CHECK(wit_native_library_thread_leave() == WIT_STATUS_OK, 3414);
    return 42;
}

static WIT_NORETURN void child(WitU64 unused)
{
    (void)unused;
    const WitU64 code = child_body();
    (void)wit_native_call(WIT_CALL_THREAD_COMPLETE, code, 0, 0, 0);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

WitU64 wit_native_library_tls_test(WitU64 mode)
{
    WitUserMemoryInfo before, after;
    WitUserThreadInfo threadInfo;
    CHECK(snapshot(&before), 3401);
    CHECK(wit_native_call(WIT_CALL_THREAD_QUERY, (WitU64)&threadInfo, sizeof(threadInfo), WIT_THREAD_INFO_VERSION, 0) ==
                WIT_STATUS_OK &&
            !threadInfo.CompilerTls,
        3402);
    const char first[] = "/native/statictls.dll", second[] = "/native/tlssecond.dll";
    modules[0].Handle = 99;
    const WitU64 loaded = wit_native_library_load(first, sizeof(first) - 1, &modules[0].Handle);
    if (mode) {
        if (loaded == WIT_STATUS_OK) {
            CHECK(wit_native_library_unload(modules[0].Handle) == WIT_STATUS_OK, 3403);
        } else {
            CHECK(loaded == WIT_STATUS_NO_MEMORY && modules[0].Handle == 99, 3404);
        }
        CHECK(snapshot(&after) && same(&before, &after), 3405);
        return loaded == WIT_STATUS_OK ? 42 : 43;
    }
    if (loaded != WIT_STATUS_OK) {
        return 3450 + loaded;
    }
    CHECK(wit_native_library_load(second, sizeof(second) - 1, &modules[1].Handle) == WIT_STATUS_OK, 3406);
    CHECK(symbols(&modules[0]) && symbols(&modules[1]), 3407);
    unsigned indexes[2];
    for (WitU32 i = 0; i < 2; ++i) {
        TlsModule *m = &modules[i];
        CHECK(((int (*)(void))m->Value)() == 736 && !((int (*)(void))m->Zero)(), 3408);
        indexes[i] = ((unsigned (*)(void))m->Index)();
        CHECK(indexes[i] && indexes[i] <= WIT_LIBRARY_CAPACITY, 3409);
        mainAddresses[i] = (WitU64)((void *(*)(void))m->Address)();
        ((void (*)(int))m->Set)(20 + (int)i);
    }
    CHECK(indexes[0] != indexes[1] && mainAddresses[0] != mainAddresses[1], 3415);
    for (WitU32 run = 0; run < 2; ++run) {
        WitU64 handle = 0, result = 0;
        CHECK(wit_native_call(WIT_CALL_THREAD_CREATE, (WitU64)child, 0, WIT_THREAD_LIBRARY_NOTIFICATIONS, &handle) ==
                WIT_STATUS_OK,
            3416);
        CHECK(wit_native_call(WIT_CALL_THREAD_JOIN, handle, 0, 0, &result) == WIT_STATUS_OK && result == 42, 3417);
    }
    CHECK(((int (*)(void))modules[0].Value)() == 25 && ((int (*)(void))modules[1].Value)() == 26, 3418);
    CHECK(wit_native_call(WIT_CALL_THREAD_QUERY, (WitU64)&threadInfo, sizeof(threadInfo), WIT_THREAD_INFO_VERSION, 0) ==
                WIT_STATUS_OK &&
            !threadInfo.CompilerTls &&
            threadInfo.CompilerTlsHeader,
        3419);
    // Writable compiler vectors are not backing ownership authority.
    ((volatile WitU64 *)threadInfo.CompilerTlsHeader)[0x58 / 8] = 0;
    ((volatile WitU64 *)threadInfo.CompilerTlsHeader)[0x80 / 8 + indexes[0]] = ~0ULL;
    CHECK(wit_native_library_unload(modules[0].Handle) == WIT_STATUS_OK &&
            wit_native_library_unload(modules[1].Handle) == WIT_STATUS_OK,
        3420);
    CHECK(wit_native_call(WIT_CALL_THREAD_QUERY, (WitU64)&threadInfo, sizeof(threadInfo), WIT_THREAD_INFO_VERSION, 0) ==
                WIT_STATUS_OK &&
            !threadInfo.CompilerTls &&
            !threadInfo.CompilerTlsHeader,
        3421);
    CHECK(snapshot(&after) && same(&before, &after), 3422);
    CHECK(wit_native_library_load(first, sizeof(first) - 1, &modules[0].Handle) == WIT_STATUS_OK &&
            symbols(&modules[0]) &&
            ((int (*)(void))modules[0].Value)() == 736,
        3423);
    CHECK(wit_native_library_unload(modules[0].Handle) == WIT_STATUS_OK && snapshot(&after) && same(&before, &after),
        3424);
    return 42;
}
