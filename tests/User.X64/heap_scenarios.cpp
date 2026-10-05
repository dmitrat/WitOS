#include <stdint.h>
#include <stdlib.h>
#include <new>
extern "C" {
#include "bootstrap.h"
#include "../User/protocol.h"
}
#include "native_heap.witos.h"

/* The native heap of the full runtime profile (P6.4.i3b), which the host's C++ and C allocations share: exactly
 * WIT_NATIVE_HEAP_LARGE_CAPACITY live blocks, a run as large as most of the arena, then a seeded sequence of
 * allocations and releases in all three families over every size class and page runs, each block filled and checked
 * before release, with releases through a wrong family or at an interior address refused. Afterwards the component
 * owns, reserves and commits exactly what it did before. The default profile's heap is in tests/User.X64/native_heap.cpp. */
namespace {

constexpr unsigned SLOTS = 2048, ROUNDS = 60000;

struct Slot {
    unsigned char *Block;
    size_t Size;
    unsigned Family; // 0 C++, 1 Local, 2 C
    unsigned char Pattern;
};

Slot slots[SLOTS];
uint64_t state = 0x9E3779B97F4A7C15ULL;

uint64_t next()
{
    state ^= state << 13;
    state ^= state >> 7;
    state ^= state << 17;
    return state;
}

bool snapshot(WitUserMemoryInfo &info)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)&info, sizeof(info), WIT_MEMORY_INFO_VERSION, nullptr) ==
        WIT_STATUS_OK;
}

bool same(const WitUserMemoryInfo &a, const WitUserMemoryInfo &b)
{
    return a.OwnedBytes == b.OwnedBytes &&
        a.ReservedBytes == b.ReservedBytes &&
        a.ReservationCount == b.ReservationCount &&
        a.DynamicCommittedBytes == b.DynamicCommittedBytes &&
        a.PrivatePageTableBytes == b.PrivatePageTableBytes &&
        a.PhysicalAvailableBytes == b.PhysicalAvailableBytes;
}

void *allocate(unsigned family, size_t size)
{
    switch (family) {
    case 0:
        return ::operator new(size, std::nothrow);
    case 1:
        return wit_native_local_allocate(size);
    default:
        return wit_native_c_allocate(size);
    }
}

bool release(unsigned family, void *block)
{
    switch (family) {
    case 0:
        ::operator delete(block); // ends the process unless the block is a live C++ block
        return true;
    case 1:
        return wit_native_local_release(block);
    default:
        return wit_native_c_release(block);
    }
}

size_t pick_size()
{
    const unsigned kind = (unsigned)(next() % 100);
    if (kind < 70) {
        return 1 + (size_t)(next() % 256);
    }
    if (kind < 95) {
        return 257 + (size_t)(next() % 1792); // up to the largest size class
    }
    return 2049 + (size_t)(next() % 30000); // runs of whole pages
}

bool intact(const Slot &slot)
{
    for (size_t i = 0; i < slot.Size; ++i) {
        if (slot.Block[i] != slot.Pattern) {
            return false;
        }
    }
    return true;
}

WitU64 capacity()
{
    // Chained through the blocks, so no table holds them.
    void *chain = nullptr;
    size_t count = 0;
    while (count <= WIT_NATIVE_HEAP_LARGE_CAPACITY) {
        void *block = ::operator new(16, std::nothrow);
        if (!block) {
            break;
        }
        *(void **)block = chain;
        chain = block;
        ++count;
    }
    WitUserMemoryInfo held, failed;
    if (count != WIT_NATIVE_HEAP_LARGE_CAPACITY ||
        !snapshot(held) ||
        wit_native_c_allocate(4096) ||
        !snapshot(failed) ||
        !same(held, failed)) {
        return 2601;
    }
    while (chain) {
        void *following = *(void **)chain;
        ::operator delete(chain);
        chain = following;
    }
    return 0;
}

WitU64 runs()
{
    if (allocate(0, WIT_NATIVE_HEAP_LARGE_ARENA_BYTES + 1)) {
        return 2611;
    }
    const size_t size = WIT_NATIVE_HEAP_LARGE_ARENA_BYTES - 64 * 1024;
    auto run = (unsigned char *)allocate(2, size);
    if (!run || ((uintptr_t)run & 4095) || wit_native_c_size(run) != size) {
        return 2612;
    }
    run[0] = 0x5A;
    run[size - 1] = 0xA5;
    // The rest of the arena is too small for another run that large, and a C block grows through realloc.
    auto small = (unsigned char *)malloc(20);
    if (allocate(1, size) || !small) {
        return 2613;
    }
    for (unsigned i = 0; i < 20; ++i) {
        small[i] = (unsigned char)i;
    }
    auto grown = (unsigned char *)realloc(small, 5000);
    if (!grown || run[0] != 0x5A || run[size - 1] != 0xA5) {
        return 2614;
    }
    for (unsigned i = 0; i < 20; ++i) {
        if (grown[i] != i) {
            return 2615;
        }
    }
    free(grown);
    return release(2, run) ? 0 : 2616;
}

WitU64 sequence()
{
    for (unsigned round = 0; round < ROUNDS; ++round) {
        Slot &slot = slots[next() % SLOTS];
        if (!slot.Block) {
            slot.Size = pick_size();
            slot.Family = (unsigned)(next() % 3);
            slot.Block = (unsigned char *)allocate(slot.Family, slot.Size);
            if (!slot.Block || ((uintptr_t)slot.Block & 15)) {
                return 2621;
            }
            if (slot.Family == 2 && wit_native_c_size(slot.Block) < slot.Size) {
                return 2622;
            }
            slot.Pattern = (unsigned char)(next() | 1);
            for (size_t i = 0; i < slot.Size; ++i) {
                slot.Block[i] = slot.Pattern;
            }
            continue;
        }
        if (!intact(slot)) {
            return 2623;
        }
        // Another family and an interior address are refused and leave the block live.
        const unsigned foreign = slot.Family == 1 ? 2 : 1;
        if (release(foreign, slot.Block) ||
            (slot.Size > 16 &&
                (release(foreign == 1 ? 2 : 1, slot.Block + 16) || wit_native_c_size(slot.Block + 16))) ||
            !intact(slot)) {
            return 2624;
        }
        if (!release(slot.Family, slot.Block)) {
            return 2625;
        }
        slot.Block = nullptr;
    }
    for (unsigned i = 0; i < SLOTS; ++i) {
        if (slots[i].Block && (!intact(slots[i]) || !release(slots[i].Family, slots[i].Block))) {
            return 2626;
        }
        slots[i].Block = nullptr;
    }
    return 0;
}

} // namespace

extern "C" WitU64 wit_heap_scenarios_probe()
{
    WitUserMemoryInfo before, after;
    if (!snapshot(before)) {
        return 2600;
    }
    WitU64 code = capacity();
    if (!code) {
        code = runs();
    }
    if (!code) {
        code = sequence();
    }
    if (!code && (!snapshot(after) || !same(before, after))) {
        code = 2630;
    }
    return code ? code : WIT_TEST_EXIT_CODE;
}
