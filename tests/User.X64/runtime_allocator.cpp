#include "common.h"
#include <minipal/mutex.h>
#include "CachedInterfaceDispatchPal.h"
#include "CachedInterfaceDispatch.h"
#include "tls.h"
#include "../User/protocol.h"

static AllocHeap *shared;
static uint8_t *results[24];

static bool snapshot(WitUserMemoryInfo &info)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)&info, sizeof(info), WIT_MEMORY_INFO_VERSION, nullptr) ==
        WIT_STATUS_OK;
}

static bool equal(const WitUserMemoryInfo &a, const WitUserMemoryInfo &b)
{
    return a.OwnedBytes == b.OwnedBytes &&
        a.ReservedBytes == b.ReservedBytes &&
        a.DynamicCommittedBytes == b.DynamicCommittedBytes &&
        a.ReservationCount == b.ReservationCount &&
        a.PrivatePageTableBytes == b.PrivatePageTableBytes;
}

static WitU64 worker(WitU64 index)
{
    for (size_t i = 0; i < 8; ++i) {
        const size_t slot = (size_t)index * 8 + i;
        auto p = shared->AllocAligned(32, 16);
        if (!p || ((uintptr_t)p & 15)) {
            return 1701;
        }
        results[slot] = p;
        for (size_t j = 0; j < 32; ++j) {
            p[j] = (uint8_t)(slot + 1);
        }
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) {
            return 1702;
        }
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" bool wit_test_runtime_allocator()
{
    WitUserMemoryInfo before, after;
    if (!snapshot(before)) {
        return false;
    }
    // More lifetimes than the sixteen-entry mutex registry: destructor cleanup
    // must actually release its Crst rather than merely release heap pages.
    for (size_t iteration = 0; iteration < 40; ++iteration) {
        {
            AllocHeap heap;
            if (!heap.Init()) {
                return false;
            }
            auto first = heap.AllocAligned(31, 16);
            auto second = heap.AllocAligned(8193, 16);
            if (!first ||
                !second ||
                ((uintptr_t)first & 15) ||
                ((uintptr_t)second & 15) ||
                !heap.Contains(first, 31) ||
                !heap.Contains(second, 8193) ||
                heap.Contains(&heap, sizeof(heap))) {
                return false;
            }
            for (size_t i = 0; i < 31; ++i) {
                if (first[i]) {
                    return false;
                }
                first[i] = (uint8_t)(i + 1);
            }
            for (size_t i = 0; i < 8193; ++i) {
                if (second[i]) {
                    return false;
                }
                second[i] = (uint8_t)i;
            }
            for (size_t i = 0; i < 31; ++i) {
                if (first[i] != i + 1) {
                    return false;
                }
            }
        }
        if (!snapshot(after) || !equal(before, after)) {
            return false;
        }
    }
    // Exhaust only native metadata; PAL allocation succeeds, then its real
    // BlockListElem allocation fails and must release the unpublished block.
    {
        AllocHeap heap;
        if (!heap.Init()) {
            return false;
        }
        void *held[128];
        size_t n = 0;
        for (; n < 128; ++n) {
            held[n] = ::operator new(1, std::nothrow);
            if (!held[n]) {
                break;
            }
        }
        if (n != 128 || !snapshot(before)) {
            return false;
        }
        if (heap.AllocAligned(4096, 16) || !snapshot(after) || !equal(before, after)) {
            return false;
        }
        while (n) {
            ::operator delete(held[--n]);
        }
        if (!heap.AllocAligned(4096, 16)) {
            return false; // Failure did not poison the heap.
        }
    }
    if (!snapshot(before)) {
        return false;
    }
    {
        AllocHeap heap;
        if (!heap.Init()) {
            return false;
        }
        shared = &heap;
        WitU64 handles[3], code;
        for (WitU64 i = 0; i < 3; ++i) {
            if (wit_native_thread_create(worker, i, &handles[i]) != WIT_STATUS_OK) {
                return false;
            }
        }
        for (size_t i = 0; i < 3; ++i) {
            if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &code) != WIT_STATUS_OK ||
                code != WIT_TEST_EXIT_CODE) {
                return false;
            }
        }
        for (size_t i = 0; i < 24; ++i) {
            if (!heap.Contains(results[i], 32)) {
                return false;
            }
            for (size_t k = 0; k < i; ++k) {
                if (results[k] == results[i]) {
                    return false;
                }
            }
            for (size_t j = 0; j < 32; ++j) {
                if (results[i][j] != i + 1) {
                    return false;
                }
            }
        }
        shared = nullptr;
    }
    return snapshot(after) && equal(before, after);
}

extern "C" bool wit_test_interface_dispatch()
{
    void *held[128];
    size_t n = 0;
    for (; n < 128; ++n) {
        held[n] = ::operator new(1, std::nothrow);
        if (!held[n]) {
            break;
        }
    }
    WitUserMemoryInfo before, after;
    if (n != 128 || !snapshot(before) || InterfaceDispatch_Initialize() || !snapshot(after) || !equal(before, after)) {
        return false;
    }
    while (n) {
        ::operator delete(held[--n]);
    }
    if (!InterfaceDispatch_Initialize()) {
        return false;
    }
    auto first = (uint8_t *)InterfaceDispatch_AllocDoublePointerAligned(33);
    auto second = (uint8_t *)InterfaceDispatch_AllocPointerAligned(23);
    if (!first ||
        !second ||
        ((uintptr_t)first & 15) ||
        ((uintptr_t)second & 7) ||
        (uintptr_t)second < (uintptr_t)first + 33) {
        return false;
    }
    for (size_t i = 0; i < 33; ++i) {
        if (first[i]) {
            return false;
        }
        first[i] = (uint8_t)(i + 1);
    }
    for (size_t i = 0; i < 23; ++i) {
        if (second[i]) {
            return false;
        }
        second[i] = (uint8_t)(i + 2);
    }
    for (size_t i = 0; i < 33; ++i) {
        if (first[i] != i + 1) {
            return false;
        }
    }
    // The upstream dispatch heap and two locks intentionally live until process
    // teardown. No synthetic shutdown or managed dispatch is claimed here.
    return snapshot(after) && after.ReservationCount == 2 && after.DynamicCommittedBytes == 8192;
}
