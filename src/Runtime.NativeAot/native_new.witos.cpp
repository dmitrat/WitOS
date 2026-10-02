#include <new>
#include <stdint.h>
#include "native_heap.witos.h"
extern "C" {
#include "bootstrap.h"
}

/* A bounded process-private heap for the runtime's nothrow C++ allocations.
 * Metadata is outside allocation payloads. No CRT, managed GC, compiler TLS,
 * dynamic initialization or kernel events are required. */
static constexpr size_t CAPACITY = 128;
static constexpr size_t ARENA_BYTES = 256 * 1024;
static constexpr size_t PAGE_BYTES = 4096;

struct Allocation {
    size_t Offset;
    size_t Size;
    unsigned Kind;
};

static Allocation allocations[CAPACITY];
static unsigned short references[ARENA_BYTES / PAGE_BYTES];
static WitU64 arena;
static size_t live;
static volatile WitU32 gate;

static WIT_NORETURN void fatal()
{
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

static void lock()
{
    while (!wit_native_try_lock(&gate)) {
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) {
            fatal();
        }
    }
}

static void unlock()
{
    wit_native_unlock(&gate);
}

static void release_arena()
{
    if (wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) {
        fatal();
    }
    arena = 0;
}

static void *allocate(size_t size, unsigned kind = 0)
{
    if (!size) {
        size = 1;
    }
    if (size > ARENA_BYTES) {
        return nullptr;
    }
    size = (size + 15) & ~(size_t)15;
    lock();
    size_t slot = CAPACITY;
    for (size_t i = 0; i < CAPACITY; ++i) {
        if (!allocations[i].Size) {
            slot = i;
            break;
        }
    }
    if (slot == CAPACITY) {
        unlock();
        return nullptr;
    }
    // First fit over a bounded registry. Every collision advances past a live
    // allocation; a free does not require allocating a new metadata node.
    size_t offset = 0;
    for (;;) {
        bool overlap = false;
        if (size > ARENA_BYTES - offset) {
            unlock();
            return nullptr;
        }
        for (size_t i = 0; i < CAPACITY; ++i) {
            const Allocation &a = allocations[i];
            if (!a.Size || offset >= a.Offset + a.Size || offset + size <= a.Offset) {
                continue;
            }
            offset = a.Offset + a.Size;
            overlap = true;
            break;
        }
        if (!overlap) {
            break;
        }
    }
    if (!arena && wit_native_call(WIT_CALL_MEMORY_RESERVE, ARENA_BYTES, 65536, 0, &arena) != WIT_STATUS_OK) {
        unlock();
        return nullptr;
    }
    const size_t first = offset / PAGE_BYTES;
    const size_t end = (offset + size + PAGE_BYTES - 1) / PAGE_BYTES;
    // Kernel commitment rolls back new pages on failure while preserving pages
    // shared with existing allocations. Publish metadata only after success.
    if (wit_native_call(WIT_CALL_MEMORY_COMMIT, arena + first * PAGE_BYTES, (end - first) * PAGE_BYTES,
            WIT_MEMORY_READ | WIT_MEMORY_WRITE, nullptr) != WIT_STATUS_OK) {
        if (!live) {
            release_arena();
        }
        unlock();
        return nullptr;
    }
    for (size_t page = first; page < end; ++page) {
        ++references[page];
    }
    allocations[slot].Offset = offset;
    allocations[slot].Size = size;
    allocations[slot].Kind = kind;
    ++live;
    void *result = (void *)(uintptr_t)(arena + offset);
    unlock();
    return result;
}

static bool release(void *address, unsigned kind)
{
    if (!address) {
        return true;
    }
    lock();
    size_t slot = CAPACITY;
    for (size_t i = 0; i < CAPACITY; ++i) {
        if (allocations[i].Size && (uintptr_t)address == arena + allocations[i].Offset) {
            slot = i;
            break;
        }
    }
    // Never dereference caller-supplied metadata or free an interior pointer.
    if (slot == CAPACITY || allocations[slot].Kind != kind) {
        unlock();
        return false;
    }
    const size_t first = allocations[slot].Offset / PAGE_BYTES;
    const size_t end = (allocations[slot].Offset + allocations[slot].Size + PAGE_BYTES - 1) / PAGE_BYTES;
    allocations[slot].Size = 0;
    allocations[slot].Kind = 0;
    --live;
    for (size_t page = first; page < end; ++page) {
        if (!references[page]) {
            fatal();
        }
        --references[page];
        if (live &&
            !references[page] &&
            wit_native_call(WIT_CALL_MEMORY_DECOMMIT, arena + page * PAGE_BYTES, PAGE_BYTES, 0, nullptr) !=
                WIT_STATUS_OK) {
            fatal();
        }
    }
    if (!live) {
        release_arena();
    }
    unlock();
    return true;
}

static void deallocate(void *address)
{
    if (!release(address, 0)) {
        fatal();
    }
}

extern "C" void *wit_native_local_allocate(size_t bytes)
{
    return allocate(bytes, 1);
}

extern "C" bool wit_native_local_release(void *address)
{
    return release(address, 1);
}

// Match the real MSVC C++ declarations used by the upstream runtime. Throwing
// and over-aligned allocation remain unresolved until their contracts exist.
namespace std {
const nothrow_t nothrow{};
}

void *__cdecl operator new(size_t size, const std::nothrow_t &) noexcept
{
    return allocate(size);
}

void *__cdecl operator new[](size_t size, const std::nothrow_t &) noexcept
{
    return allocate(size);
}

void __cdecl operator delete(void *address) noexcept
{
    deallocate(address);
}

void __cdecl operator delete[](void *address) noexcept
{
    deallocate(address);
}

void __cdecl operator delete(void *address, size_t) noexcept
{
    deallocate(address);
}

void __cdecl operator delete[](void *address, size_t) noexcept
{
    deallocate(address);
}

void __cdecl operator delete(void *address, const std::nothrow_t &) noexcept
{
    deallocate(address);
}

void __cdecl operator delete[](void *address, const std::nothrow_t &) noexcept
{
    deallocate(address);
}
