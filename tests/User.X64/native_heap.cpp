#include "gcenv.witos.h"
#include "../User/protocol.h"
#include <new>

static void *cross_thread[2];

static bool snapshot(WitUserMemoryInfo *info)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)info, sizeof(*info), WIT_MEMORY_INFO_VERSION, nullptr) ==
        WIT_STATUS_OK;
}

static bool same(const WitUserMemoryInfo &a, const WitUserMemoryInfo &b)
{
    return a.OwnedBytes == b.OwnedBytes &&
        a.ReservedBytes == b.ReservedBytes &&
        a.ReservationCount == b.ReservationCount &&
        a.DynamicCommittedBytes == b.DynamicCommittedBytes &&
        a.PrivatePageTableBytes == b.PrivatePageTableBytes &&
        a.PhysicalAvailableBytes == b.PhysicalAvailableBytes;
}

static void *allocate(size_t size)
{
    return ::operator new(size, std::nothrow);
}

static void release(void *p)
{
    ::operator delete(p);
}

static void fill(void *p, size_t size, unsigned char value)
{
    for (size_t i = 0; i < size; ++i) {
        ((volatile unsigned char *)p)[i] = value;
    }
}

static bool equal(void *p, size_t size, unsigned char value)
{
    for (size_t i = 0; i < size; ++i) {
        if (((volatile unsigned char *)p)[i] != value) {
            return false;
        }
    }
    return true;
}

static void done(WitU64 code)
{
    (void)wit_native_call(WIT_CALL_THREAD_EXIT, code, 0, 0, nullptr);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

static void worker(WitU64 index)
{
    const unsigned char pattern = (unsigned char)(0xA0 + index);
    for (size_t round = 0; round < 16; ++round) {
        void *p = allocate(4097);
        if (!p || ((uintptr_t)p & 15)) {
            done(850);
        }
        fill(p, 4097, pattern);
        GCToOSInterface::YieldThread(0);
        if (!equal(p, 4097, pattern)) {
            done(851);
        }
        release(p);
    }
    cross_thread[index] = allocate(33);
    if (!cross_thread[index]) {
        done(852);
    }
    fill(cross_thread[index], 33, pattern);
    done(WIT_TEST_EXIT_CODE);
}

WitU64 wit_native_heap(WitU64 mode)
{
    WitUserMemoryInfo before, after;
    if (!snapshot(&before)) {
        return 800;
    }
    if (mode == WIT_NATIVE_TEST_HEAP) {
        const size_t sizes[] = {0, 1, 15, 16, 17, 4095, 4096, 4097, 32768};
        void *values[9];
        for (size_t i = 0; i < 9; ++i) {
            values[i] = allocate(sizes[i]);
            if (!values[i] || ((uintptr_t)values[i] & 15)) {
                return 801;
            }
            fill(values[i], sizes[i], (unsigned char)(i + 1));
            for (size_t j = 0; j < i; ++j) {
                if (values[i] == values[j]) {
                    return 802;
                }
            }
        }
        if (allocate(SIZE_MAX) || allocate(256 * 1024 + 1)) {
            return 803;
        }
        for (size_t i = 0; i < 9; ++i) {
            if (!equal(values[i], sizes[i], (unsigned char)(i + 1))) {
                return 804;
            }
            ::operator delete(values[i], sizes[i]); // Exercise the upstream sized form.
        }
        auto object = new (std::nothrow) WitU64(0x12345678);
        auto array = new (std::nothrow) WitU64[8];
        if (!object || *object != 0x12345678 || !array) {
            return 805;
        }
        array[7] = *object;
        if (array[7] != 0x12345678) {
            return 806;
        }
        delete object;
        delete[] array;
        release(nullptr);
        ::operator delete[](nullptr);
    } else if (mode == WIT_NATIVE_TEST_HEAP_REUSE) {
        void *values[128];
        for (size_t round = 0; round < 3; ++round) {
            for (size_t i = 0; i < 128; ++i) {
                values[i] = allocate(16);
                if (!values[i]) {
                    return 810;
                }
                fill(values[i], 16, (unsigned char)i);
            }
            if (allocate(1)) {
                return 811;
            }
            for (size_t i = 0; i < 128; i += 2) {
                release(values[i]);
            }
            for (size_t i = 0; i < 128; i += 2) {
                void *replacement = allocate(16);
                if (replacement != values[i]) {
                    return 812;
                }
                fill(replacement, 16, (unsigned char)i);
            }
            for (size_t i = 0; i < 128; ++i) {
                if (!equal(values[i], 16, (unsigned char)i)) {
                    return 813;
                }
                release(values[i]);
            }
        }
        auto a = allocate(4096);
        auto b = allocate(8192);
        auto c = allocate(4096);
        if (!a || !b || !c) {
            return 814;
        }
        fill(a, 4096, 0xA1);
        fill(c, 4096, 0xC1);
        WitUserMemoryInfo held, freed;
        if (!snapshot(&held)) {
            return 815;
        }
        release(b);
        if (!snapshot(&freed) ||
            held.DynamicCommittedBytes - freed.DynamicCommittedBytes != 8192 ||
            !equal(a, 4096, 0xA1) ||
            !equal(c, 4096, 0xC1)) {
            return 816;
        }
        auto reused = allocate(8192);
        if (reused != b || !equal(reused, 8192, 0)) {
            return 817;
        }
        release(a);
        release(c);
        release(reused);
        auto entire = allocate(256 * 1024);
        if (!entire || allocate(1)) {
            return 818;
        }
        release(entire);
    } else if (mode == WIT_NATIVE_TEST_HEAP_FAILURE) {
        void *reservations[8];
        for (size_t i = 0; i < 8; ++i) {
            reservations[i] = GCToOSInterface::VirtualReserve(4096, 0, 0);
            if (!reservations[i]) {
                return 820;
            }
        }
        WitUserMemoryInfo held, failed;
        if (!snapshot(&held) || allocate(1) || !snapshot(&failed) || !same(held, failed)) {
            return 821;
        }
        for (size_t i = 0; i < 8; ++i) {
            if (!GCToOSInterface::VirtualRelease(reservations[i], 0)) {
                return 822;
            }
        }
        // First allocation fails with no frames: its fresh reservation must roll back.
        auto pressure = (unsigned char *)GCToOSInterface::VirtualReserve(128 * 4096, 0, 0);
        if (!pressure) {
            return 823;
        }
        size_t pages = 0;
        while (pages < 128 && GCToOSInterface::VirtualCommit(pressure + pages * 4096, 4096)) {
            ++pages;
        }
        if (pages < 4 ||
            pages == 128 ||
            !snapshot(&held) ||
            allocate(16) ||
            !snapshot(&failed) ||
            !same(held, failed) ||
            !GCToOSInterface::VirtualRelease(pressure, 0)) {
            return 824;
        }
        // A later failed allocation shares a page with live data; additions must roll back.
        auto live = allocate(16);
        if (!live) {
            return 825;
        }
        fill(live, 16, 0xBD);
        pressure = (unsigned char *)GCToOSInterface::VirtualReserve(128 * 4096, 0, 0);
        if (!pressure) {
            return 826;
        }
        pages = 0;
        while (pages < 128 && GCToOSInterface::VirtualCommit(pressure + pages * 4096, 4096)) {
            ++pages;
        }
        if (!pages ||
            !GCToOSInterface::VirtualDecommit(pressure + (pages - 1) * 4096, 4096) ||
            !snapshot(&held) ||
            allocate(3 * 4096) ||
            !equal(live, 16, 0xBD) ||
            !snapshot(&failed) ||
            !same(held, failed)) {
            return 827;
        }
        if (!GCToOSInterface::VirtualRelease(pressure, 0)) {
            return 828;
        }
        auto recovered = allocate(3 * 4096);
        if (!recovered || !equal(live, 16, 0xBD)) {
            return 829;
        }
        release(recovered);
        release(live);
    } else if (mode == WIT_NATIVE_TEST_HEAP_THREADS) {
        WitU64 handles[2];
        for (size_t i = 0; i < 2; ++i) {
            if (wit_native_call(WIT_CALL_THREAD_CREATE, (uintptr_t)worker, i, 0, &handles[i]) != WIT_STATUS_OK) {
                return 830;
            }
        }
        for (size_t i = 0; i < 2; ++i) {
            WitU64 code = 0;
            if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &code) != WIT_STATUS_OK ||
                code != WIT_TEST_EXIT_CODE ||
                !equal(cross_thread[i], 33, (unsigned char)(0xA0 + i))) {
                return 831;
            }
            release(cross_thread[i]);
        }
    } else {
        auto p = (unsigned char *)allocate(4096);
        if (!p) {
            return 840;
        }
        if (mode == WIT_NATIVE_TEST_HEAP_NX) {
            p[0] = 0xC3;
            ((void (*)())p)();
            return 841;
        }
        if (mode == WIT_NATIVE_TEST_HEAP_FREED) {
            release(p);
            return *(volatile unsigned char *)p;
        }
        auto live = allocate(16);
        if (!live) {
            return 842;
        }
        *(WitU64 *)WIT_GC_INFO_REPORT = mode;
        if (mode == WIT_NATIVE_TEST_HEAP_DOUBLE_FREE) {
            release(p);
        }
        release(mode == WIT_NATIVE_TEST_HEAP_BAD_FREE ? p + 1 : p);
        return 843;
    }
    return snapshot(&after) && same(before, after) ? WIT_TEST_EXIT_CODE : 899;
}
