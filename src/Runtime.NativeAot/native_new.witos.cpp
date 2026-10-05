#include <new>
#include <stdint.h>
#include "native_heap.witos.h"
#include "../Runtime.Native/native_limits.h"
extern "C" {
#include "bootstrap.h"
}

/* A bounded process-private heap for the runtime's nothrow C++ allocations, the
 * Local family and the C allocations of the UCRT subset; a block is released
 * only through its own family. No CRT, managed GC, compiler TLS, dynamic
 * initialization or kernel events are required.
 *
 * One reservation holds the metadata pages, an uncommitted guard page and the
 * payload pages, so metadata is never inside or after a payload. A request up
 * to the largest size class takes the lowest free block of a page shared by
 * blocks of that class, of any family; a larger one takes the first run of free
 * whole pages. A payload page is committed with its first block and decommitted
 * with its last; a metadata page stays committed until the last block releases
 * the reservation. The quotas follow the component's profile: one loaded with
 * the full runtime profile, which the kernel reports as its reservation
 * capacity, gets the large heap. The owned-memory limit is no such sign, since
 * a supervisor may lower it, as the GC initialization-failure test does.
 *
 * The runtime archive compiles this file without optimization, where every
 * function with a frame adds an unwind record, and the default-profile images
 * that link it must stay within 128 of them. So the work is in allocate and
 * owned, which releases a block or reports its size, and the small repeated
 * expressions are macros. */
static constexpr size_t PAGE_BYTES = 4096;
static constexpr size_t MAX_PAGES = WIT_NATIVE_HEAP_LARGE_ARENA_BYTES / PAGE_BYTES;
static constexpr uint16_t CLASSES[] = {
    16, 32, 48, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 640, 768, 1024, 1360, 2048};
static constexpr unsigned CLASS_COUNT = sizeof(CLASSES) / sizeof(CLASSES[0]);
static constexpr size_t LARGEST_BLOCK = 2048;
static constexpr uint8_t PAGE_FREE = 0, PAGE_RUN = 0xFE, PAGE_TAIL = 0xFF; // otherwise 1 + class index

struct Page {
    uint8_t State;
    uint8_t Kind; // family of a run
    uint16_t Used; // blocks of a class page
    uint16_t Hint; // no free block below it
    uint16_t Unused;
    uint32_t Pages; // pages of a run
    uint32_t Next; // 1 + index of the next class page with a free block, 0 at the end
    uint32_t Previous;
    uint8_t Blocks[PAGE_BYTES / 16 / 4]; // 2 bits per block: 0 free, otherwise 1 + family
};

static constexpr size_t PER_METADATA_PAGE = PAGE_BYTES / sizeof(Page); // a descriptor never spans two pages
static constexpr size_t MAX_METADATA_PAGES = (MAX_PAGES + PER_METADATA_PAGE - 1) / PER_METADATA_PAGE;
static_assert(WIT_NATIVE_HEAP_ARENA_BYTES <= WIT_NATIVE_HEAP_LARGE_ARENA_BYTES &&
        MAX_METADATA_PAGES <= 32 &&
        CLASSES[CLASS_COUNT - 1] == LARGEST_BLOCK,
    "Native heap layout");

static WitU64 arena; // reservation base, 0 without one
static WitU64 payload; // first payload page
static size_t pageCount; // payload pages of the reservation
static size_t capacity; // live blocks
static size_t live;
static uint64_t used[MAX_PAGES / 64]; // payload pages in use
static uint32_t metadata; // committed metadata pages
static uint32_t partial[CLASS_COUNT]; // 1 + index of the first class page with a free block
static volatile WitU32 gate;

// The descriptor of payload page i, whether page i is in use, and the state of a block of a class page.
#define DESCRIPTOR(i) \
    (*(Page *)(uintptr_t)(arena + (i) / PER_METADATA_PAGE * PAGE_BYTES + (i) % PER_METADATA_PAGE * sizeof(Page)))
#define IN_USE(i) ((used[(i) / 64] >> ((i) % 64)) & 1)
#define BLOCK_STATE(p, block) (((p).Blocks[(block) / 4] >> ((block) % 4 * 2)) & 3u)

static WIT_NORETURN void fatal()
{
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

static void lock()
{
    wit_native_lock(&gate);
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
    payload = 0;
    pageCount = 0;
    metadata = 0;
    // The last release emptied every page and list; checking rather than clearing also keeps memset out.
    for (size_t i = 0; i < MAX_PAGES / 64; ++i) {
        if (used[i]) {
            fatal();
        }
    }
    for (unsigned i = 0; i < CLASS_COUNT; ++i) {
        if (partial[i]) {
            fatal();
        }
    }
}

static void *allocate(size_t size, unsigned kind = 0)
{
    if (!size) {
        size = 1;
    }
    if (size > WIT_NATIVE_HEAP_LARGE_ARENA_BYTES) {
        return nullptr;
    }
    lock();
    if (!arena) {
        // Static under the gate: on the stack the record would be a GS buffer,
        // and the archived object's GS handler would add its unwind records.
        static WitUserMemoryInfo info;
        WitU64 copied = 0, base = 0;
        if (wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)&info, sizeof(info), WIT_MEMORY_INFO_VERSION, &copied) !=
                WIT_STATUS_OK ||
            copied != sizeof(info)) {
            unlock();
            return nullptr;
        }
        const bool large = info.ReservationCapacity >= WIT_RUNTIME_RESERVATION_CAPACITY;
        const size_t pages = (large ? WIT_NATIVE_HEAP_LARGE_ARENA_BYTES : WIT_NATIVE_HEAP_ARENA_BYTES) / PAGE_BYTES;
        const size_t metadataPages = (pages + PER_METADATA_PAGE - 1) / PER_METADATA_PAGE;
        if (wit_native_call(WIT_CALL_MEMORY_RESERVE, (metadataPages + 1 + pages) * PAGE_BYTES, 65536, 0, &base) !=
            WIT_STATUS_OK) {
            unlock();
            return nullptr;
        }
        arena = base;
        payload = base + (metadataPages + 1) * PAGE_BYTES;
        pageCount = pages;
        capacity = large ? WIT_NATIVE_HEAP_LARGE_CAPACITY : WIT_NATIVE_HEAP_CAPACITY;
    }
    if (live == capacity) {
        unlock();
        return nullptr;
    }
    // The smallest class that fits, or CLASS_COUNT for a run of whole pages.
    unsigned c = 0;
    size_t count = 1;
    while (c < CLASS_COUNT && CLASSES[c] < size) {
        ++c;
    }
    if (c == CLASS_COUNT) {
        count = (size + PAGE_BYTES - 1) / PAGE_BYTES;
    }
    size_t index = c < CLASS_COUNT && partial[c] ? partial[c] - 1 : pageCount;
    if (index == pageCount) {
        // New pages: the first run of free ones, committed with any metadata
        // page they still lack. Kernel commitment rolls back its own pages on
        // failure; this rolls back the metadata it added, so a failure leaves
        // no trace. Descriptors are published only after commitment.
        size_t length = 0;
        for (size_t i = 0; i < pageCount; ++i) {
            length = IN_USE(i) ? 0 : length + 1;
            if (length == count) {
                index = i + 1 - count;
                break;
            }
        }
        uint32_t added = 0;
        bool failed = index == pageCount;
        for (size_t m = index / PER_METADATA_PAGE; !failed && m <= (index + count - 1) / PER_METADATA_PAGE; ++m) {
            if (!(metadata & (1u << m))) {
                failed = wit_native_call(WIT_CALL_MEMORY_COMMIT, arena + m * PAGE_BYTES, PAGE_BYTES,
                             WIT_MEMORY_READ | WIT_MEMORY_WRITE, nullptr) != WIT_STATUS_OK;
                added |= failed ? 0 : 1u << m;
            }
        }
        failed = failed ||
            wit_native_call(WIT_CALL_MEMORY_COMMIT, payload + index * PAGE_BYTES, count * PAGE_BYTES,
                WIT_MEMORY_READ | WIT_MEMORY_WRITE, nullptr) != WIT_STATUS_OK;
        if (failed) {
            for (size_t m = 0; m < MAX_METADATA_PAGES; ++m) {
                if ((added & (1u << m)) &&
                    wit_native_call(WIT_CALL_MEMORY_DECOMMIT, arena + m * PAGE_BYTES, PAGE_BYTES, 0, nullptr) !=
                        WIT_STATUS_OK) {
                    fatal();
                }
            }
            if (!live) {
                release_arena();
            }
            unlock();
            return nullptr;
        }
        metadata |= added;
        for (size_t i = index; i < index + count; ++i) {
            used[i / 64] |= 1ULL << (i % 64);
        }
        Page &head = DESCRIPTOR(index); // a free descriptor is zero
        if (c == CLASS_COUNT) {
            head.State = PAGE_RUN;
            head.Kind = (uint8_t)kind;
            head.Pages = (uint32_t)count;
            for (size_t i = index + 1; i < index + count; ++i) {
                DESCRIPTOR(i).State = PAGE_TAIL;
            }
        } else {
            head.State = (uint8_t)(1 + c);
            head.Next = partial[c];
            if (partial[c]) {
                DESCRIPTOR(partial[c] - 1).Previous = (uint32_t)(index + 1);
            }
            partial[c] = (uint32_t)(index + 1);
        }
    }
    void *result = (void *)(uintptr_t)(payload + index * PAGE_BYTES);
    if (c < CLASS_COUNT) {
        // The lowest free block of the first listed page, which leaves the list when full.
        Page &p = DESCRIPTOR(index);
        const unsigned blocks = PAGE_BYTES / CLASSES[c];
        unsigned block = p.Hint;
        while (block < blocks && BLOCK_STATE(p, block)) {
            ++block;
        }
        if (block == blocks) {
            fatal(); // a listed page has a free block
        }
        p.Blocks[block / 4] = (uint8_t)(p.Blocks[block / 4] | ((kind + 1) << (block % 4 * 2)));
        p.Hint = (uint16_t)(block + 1);
        if (++p.Used == blocks) {
            partial[c] = p.Next;
            if (p.Next) {
                DESCRIPTOR(p.Next - 1).Previous = 0;
            }
            p.Next = 0;
        }
        result = (void *)(uintptr_t)(payload + index * PAGE_BYTES + block * CLASSES[c]);
    }
    ++live;
    unlock();
    return result;
}

// The usable bytes of a live block of the family at the address, which it
// releases when asked, or 0 for anything else. One function checks the address
// for both uses: it never dereferences caller-supplied metadata, trusts the
// address only within the payload range and the committed descriptors, and
// refuses interior addresses and other families.
static size_t owned(const void *address, unsigned kind, bool release)
{
    const WitU64 value = (uintptr_t)address;
    lock();
    if (!arena || value < payload || value - payload >= pageCount * PAGE_BYTES) {
        unlock();
        return 0;
    }
    const size_t index = (size_t)((value - payload) / PAGE_BYTES);
    const size_t offset = (size_t)((value - payload) % PAGE_BYTES);
    Page &p = DESCRIPTOR(index); // read only once the page is known to be in use
    const unsigned size = IN_USE(index) && p.State != PAGE_FREE && p.State < PAGE_RUN ? CLASSES[p.State - 1] : 0;
    const unsigned block = size ? (unsigned)(offset / size) : 0;
    size_t bytes = 0;
    if (IN_USE(index) && p.State == PAGE_RUN && !offset && p.Kind == kind) {
        bytes = p.Pages * PAGE_BYTES;
    } else if (size && !(offset % size) && block < PAGE_BYTES / size && BLOCK_STATE(p, block) == kind + 1) {
        bytes = size;
    }
    if (!bytes || !release) {
        unlock();
        return bytes;
    }
    --live;
    size_t count = 0; // pages the release empties
    if (p.State == PAGE_RUN) {
        count = p.Pages;
    } else {
        const unsigned c = p.State - 1u;
        const unsigned blocks = PAGE_BYTES / size;
        const bool full = p.Used == blocks;
        p.Blocks[block / 4] = (uint8_t)(p.Blocks[block / 4] & ~(3u << (block % 4 * 2)));
        if (block < p.Hint) {
            p.Hint = (uint16_t)block;
        }
        if (!--p.Used) {
            if (!full) {
                // Leave the list of pages with a free block.
                if (p.Previous) {
                    DESCRIPTOR(p.Previous - 1).Next = p.Next;
                } else {
                    partial[c] = p.Next;
                }
                if (p.Next) {
                    DESCRIPTOR(p.Next - 1).Previous = p.Previous;
                }
                p.Next = 0;
                p.Previous = 0;
            }
            count = 1;
        } else if (full) {
            // A full page has a free block again: first in its list.
            p.Next = partial[c];
            if (partial[c]) {
                DESCRIPTOR(partial[c] - 1).Previous = (uint32_t)(index + 1);
            }
            partial[c] = (uint32_t)(index + 1);
        }
    }
    for (size_t i = index; i < index + count; ++i) {
        Page &q = DESCRIPTOR(i);
        q.State = PAGE_FREE;
        q.Kind = 0;
        q.Hint = 0;
        q.Pages = 0;
        used[i / 64] &= ~(1ULL << (i % 64));
    }
    if (count &&
        live &&
        wit_native_call(WIT_CALL_MEMORY_DECOMMIT, payload + index * PAGE_BYTES, count * PAGE_BYTES, 0, nullptr) !=
            WIT_STATUS_OK) {
        fatal();
    }
    if (!live) {
        release_arena();
    }
    unlock();
    return bytes;
}

static void deallocate(void *address)
{
    if (address && !owned(address, 0, true)) {
        fatal();
    }
}

extern "C" void *wit_native_local_allocate(size_t bytes)
{
    return allocate(bytes, 1);
}

extern "C" bool wit_native_local_release(void *address)
{
    return !address || owned(address, 1, true);
}

extern "C" void *wit_native_c_allocate(size_t bytes)
{
    return allocate(bytes, 2);
}

extern "C" bool wit_native_c_release(void *address)
{
    return !address || owned(address, 2, true);
}

extern "C" size_t wit_native_c_size(const void *address)
{
    return owned(address, 2, false);
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
