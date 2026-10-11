#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/limits.h"
#include "witos/memory_object.h"
#include "witos/thread_info.h"
#include <errno.h>
#include <string.h>
#include <sys/mman.h>

/* Linux memory calls over ABI-1 (plan steps S1.1 and S5.1). The library keeps a table of its mappings, each one kernel
 * reservation: a plain reservation, whose pages are committed under the requested protection and, for a writable
 * private mapping of a file, filled with the file's bytes; or a mapping of the boot package object, which shows a
 * file's pages without a copy (every private mapping that is not writable, R3.1) and is the only way a file's code
 * executes (plain memory never does; the pages are published for instruction fetch). A file of at least a page
 * starts at a page boundary in the package (S5.1), and a mapping of it without a copy is made of package mappings of
 * at most WIT_MEMORY_OBJECT_PAGES pages at consecutive addresses; mprotect that makes such pages writable gives them
 * a private copy first. MAP_FIXED replaces whatever of the library's mappings lies in its range, as Linux does: a
 * plain part is released from its reservation (the kernel splits it), a package mapping that is only partly covered
 * is mapped again around the range; munmap of any range does the same, and mprotect changes every mapping the range
 * touches. A shared mapping of a shared memory file (shared.c, R3.2b) is made of mappings of its chunk objects, one
 * per chunk it covers, at consecutive addresses; every mapping of the same offsets shows the same pages, the library
 * keeps each page's protection (mprotect changes pages within a mapping, as CoreCLR commits its executable memory page
 * by page, R4.1), and an unmapping that leaves part of one maps that part again with its pages' protections. Shared writable mappings of the package and files other than the
 * package's, /dev/zero and shared memory files are not here. The table lock of __wit_syscall serializes every call. */

#define PAGE 4096UL
#define MAPPINGS 1024U /* the kernel's reservations of a system layer process (R3.2b) */
#define CHUNK ((WitU64)WIT_MEMORY_OBJECT_PAGES * PAGE)

typedef enum MapKind {
    MapPlain = 1, /* a plain reservation */
    MapPackage = 2, /* a mapping of the package object, without a copy */
    MapShared = 3 /* a mapping of one chunk of a shared memory file (shared.c, R3.2b) */
} MapKind;

typedef struct Mapping {
    WitU64 Base, Size;
    WitU64 Source; /* a package mapping: the package offset that Base shows; a shared one: the file's offset */
    WitU64 Protection; /* a package mapping: the kernel protection it was mapped with */
    MapKind Kind;
    WitU32 File; /* a shared mapping: the shared memory file, which the mapping holds a reference of */
    WitU64 Pages[2]; /* a shared mapping: two bits per page of the chunk, its protection (see page_code) */
} Mapping;

static Mapping mappings[MAPPINGS];
static unsigned count;

#define PACKAGE_HANDLE (__wit_process.Package)

static WitU64 round_up(unsigned long length)
{
    return (length + PAGE - 1) & ~(PAGE - 1);
}

static long protection_of(long protection, WitU64 *out)
{
    if (protection & ~(PROT_READ | PROT_WRITE | PROT_EXEC)) {
        return -EINVAL;
    }
    if ((protection & PROT_EXEC) && (protection & PROT_WRITE)) {
        return -EACCES; /* writable and executable at once is never granted */
    }
    if (protection & PROT_EXEC) {
        *out = WIT_MEMORY_READ | WIT_MEMORY_EXECUTE;
    } else if (protection & PROT_WRITE) {
        *out = WIT_MEMORY_READ | WIT_MEMORY_WRITE;
    } else if (protection & PROT_READ) {
        *out = WIT_MEMORY_READ;
    } else {
        *out = WIT_MEMORY_NONE;
    }
    return 0;
}

static long track(WitU64 base, WitU64 size, MapKind kind, WitU64 source, WitU64 protection)
{
    if (count == MAPPINGS) {
        return -ENOMEM;
    }
    mappings[count++] = (Mapping){base, size, source, protection, kind, 0, {0, 0}};
    return 0;
}

/* A shared mapping's pages keep their protections in two bits each: NONE, READ, READ|WRITE, READ|EXECUTE. */
static unsigned page_code(WitU64 protection)
{
    return (protection & WIT_MEMORY_EXECUTE) ? 3U
        : (protection & WIT_MEMORY_WRITE)    ? 2U
        : (protection & WIT_MEMORY_READ)     ? 1U
                                             : 0U;
}

static WitU64 code_protection(unsigned code)
{
    static const WitU64 protections[4] = {
        WIT_MEMORY_NONE, WIT_MEMORY_READ, WIT_MEMORY_READ | WIT_MEMORY_WRITE, WIT_MEMORY_READ | WIT_MEMORY_EXECUTE};
    return protections[code & 3U];
}

static unsigned page_get(const Mapping *m, WitU64 page)
{
    return (unsigned)(m->Pages[page / 32] >> (2 * (page % 32))) & 3U;
}

static void page_set(Mapping *m, WitU64 page, unsigned code)
{
    m->Pages[page / 32] = (m->Pages[page / 32] & ~(3ULL << (2 * (page % 32)))) | ((WitU64)code << (2 * (page % 32)));
}

static void untrack(unsigned i)
{
    mappings[i] = mappings[--count];
}

/* A mapping the kernel releases itself: the stack an exiting detached thread names to THREAD_EXIT (__unmapself),
 * released once the thread no longer runs. It leaves the table without a release, so that a later mapping in its
 * range meets no stale entry, which munmap would otherwise release in its place (R2.2). Called under the table lock. */
void __wit_mapping_forget(WitU64 base)
{
    for (unsigned i = 0; i < count; ++i) {
        if (mappings[i].Base == base && mappings[i].Kind == MapPlain) {
            untrack(i);
            return;
        }
    }
}

static WitU64 release(WitU64 base, WitU64 size)
{
    WitU64 result = 0;
    return wit_syscall(WIT_CALL_MEMORY_RELEASE, base, size, 0, &result);
}

/* A window of an object at an address (0: the kernel chooses). */
static WitU64 map_object(WitU64 object, WitU64 offset, WitU64 size, WitU64 address, WitU64 protection, WitU64 *base)
{
    WitMemoryMapRequest request;
    request.Version = WIT_MEMORY_MAP_VERSION;
    request.Size = sizeof(request);
    request.Object = object;
    request.Offset = offset;
    request.Bytes = size;
    request.Address = address;
    request.Protection = (WitU32)protection;
    request.Flags = 0;
    request.Target = WIT_PROCESS_SELF;
    return wit_syscall(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&request, sizeof(request), 0, base);
}

/* A mapping of package bytes at an address (0: the kernel chooses), published when executable. */
static WitU64 map_package(WitU64 address, WitU64 source, WitU64 size, WitU64 protection, WitU64 *base)
{
    WitU64 result = 0;
    WitU64 status = map_object(PACKAGE_HANDLE, source, size, address, protection, base);
    if (status == WIT_STATUS_OK && (protection & WIT_MEMORY_EXECUTE)) {
        status = wit_syscall(WIT_CALL_CODE_PUBLISH, *base, size, 0, &result);
        if (status != WIT_STATUS_OK) {
            release(*base, 0);
        }
    }
    return status;
}

/* The part [address, address + size) of a shared memory file's chunk at the file's offset, mapped there under the
 * protection and tracked with a reference of the file. */
static long map_chunk(WitU32 file, WitU64 offset, WitU64 address, WitU64 size, WitU64 protection)
{
    WitU64 object = 0, base = 0;
    const long chunk = __wit_shared_chunk(file, offset, &object);
    if (chunk < 0) {
        return chunk;
    }
    const WitU64 status = map_object(object, offset % CHUNK, size, address, protection, &base);
    if (status != WIT_STATUS_OK) {
        return status == WIT_STATUS_NO_MEMORY ? -ENOMEM : (status == WIT_STATUS_BUSY ? -EEXIST : -EINVAL);
    }
    if (track(base, size, MapShared, offset, protection) < 0) {
        release(base, 0);
        return -ENOMEM;
    }
    mappings[count - 1].File = file;
    for (WitU64 page = 0; page < size / PAGE; ++page) {
        page_set(&mappings[count - 1], page, page_code(protection));
    }
    __wit_shared_reference(file);
    return 0;
}

/* The part of a shared chunk mapping that remains after an unmapping, mapped again with the protections its pages had:
 * mapped without access, then given each run of equal protections. */
static long remap_chunk(const Mapping *old, WitU64 low, WitU64 high)
{
    long status = map_chunk(old->File, old->Source + (low - old->Base), low, high - low, WIT_MEMORY_NONE);
    if (status < 0) {
        return status;
    }
    Mapping *m = &mappings[count - 1];
    WitU64 result = 0;
    for (WitU64 page = 0; page < (high - low) / PAGE;) {
        const unsigned code = page_get(old, (low - old->Base) / PAGE + page);
        WitU64 run = 1;
        while (page + run < (high - low) / PAGE && page_get(old, (low - old->Base) / PAGE + page + run) == code) {
            ++run;
        }
        if (code &&
            wit_syscall(WIT_CALL_MEMORY_PROTECT, low + page * PAGE, run * PAGE, code_protection(code), &result) !=
                WIT_STATUS_OK) {
            return -ENOMEM;
        }
        for (WitU64 i = 0; i < run; ++i) {
            page_set(m, page + i, code);
        }
        page += run;
    }
    return 0;
}

/* A shared mapping of entry i goes from the table and its file's reference with it, its kernel mapping released. */
static long unmap_shared(unsigned i)
{
    const Mapping m = mappings[i];
    if (release(m.Base, 0) != WIT_STATUS_OK) {
        return -EINVAL;
    }
    untrack(i);
    __wit_shared_release(m.File);
    return 0;
}

/* Linux's unmapping of a range over the library's mappings: what lies outside every mapping is left alone. */
static long remove_range(WitU64 start, WitU64 size)
{
    const WitU64 end = start + size;
    for (unsigned i = 0; i < count;) {
        Mapping m = mappings[i];
        const WitU64 m_end = m.Base + m.Size;
        if (m_end <= start || m.Base >= end) {
            ++i;
            continue;
        }
        const WitU64 low = m.Base > start ? m.Base : start, high = m_end < end ? m_end : end;
        if (m.Kind == MapShared) {
            /* A shared chunk mapping partly covered: it goes whole, and its uncovered sides are mapped again with their
             * pages' protections, holding the file while they are. */
            __wit_shared_reference(m.File);
            long status = unmap_shared(i);
            if (status == 0 && low > m.Base) {
                status = remap_chunk(&m, m.Base, low);
            }
            if (status == 0 && high < m_end) {
                status = remap_chunk(&m, high, m_end);
            }
            __wit_shared_release(m.File);
            if (status < 0) {
                return status;
            }
            i = 0;
            continue;
        }
        if (low == m.Base && high == m_end) {
            if (release(m.Base, 0) != WIT_STATUS_OK) {
                return -EINVAL;
            }
            untrack(i);
            continue; /* the slot now holds another mapping */
        }
        if (m.Kind == MapPlain) {
            const WitU64 status = release(low, high - low);
            if (status != WIT_STATUS_OK) {
                return status == WIT_STATUS_NO_MEMORY ? -ENOMEM : -EINVAL;
            }
            if (low == m.Base) {
                mappings[i].Base = high;
                mappings[i].Size = m_end - high;
            } else if (high == m_end) {
                mappings[i].Size = low - m.Base;
            } else {
                mappings[i].Size = low - m.Base;
                if (track(high, m_end - high, MapPlain, 0, 0) < 0) {
                    return -ENOMEM;
                }
            }
            ++i;
            continue;
        }
        /* A package mapping partly covered: it goes whole, and its uncovered sides are mapped again in place. */
        if (release(m.Base, 0) != WIT_STATUS_OK) {
            return -EINVAL;
        }
        untrack(i);
        WitU64 base = 0;
        if (low > m.Base) {
            if (map_package(m.Base, m.Source, low - m.Base, m.Protection, &base) != WIT_STATUS_OK ||
                track(base, low - m.Base, MapPackage, m.Source, m.Protection) < 0) {
                return -ENOMEM;
            }
        }
        if (high < m_end) {
            const WitU64 source = m.Source + (high - m.Base);
            if (map_package(high, source, m_end - high, m.Protection, &base) != WIT_STATUS_OK ||
                track(base, m_end - high, MapPackage, source, m.Protection) < 0) {
                return -ENOMEM;
            }
        }
        i = 0; /* the table changed order; start over (every pass removes or shrinks a mapping in the range) */
    }
    return 0;
}

/* Anonymous memory: a reservation committed under the protection, or reserved alone for PROT_NONE. */
static long map_anonymous(WitU64 address, WitU64 size, WitU64 protection)
{
    WitU64 base = 0, result = 0;
    if (protection & WIT_MEMORY_EXECUTE) {
        return -EACCES; /* code arrives through memory objects, never as plain memory */
    }
    WitU64 status = wit_syscall(WIT_CALL_MEMORY_RESERVE, size, PAGE, address, &base);
    if (status != WIT_STATUS_OK) {
        return status == WIT_STATUS_BUSY ? -EEXIST : -ENOMEM;
    }
    if (protection != WIT_MEMORY_NONE) {
        status = wit_syscall(WIT_CALL_MEMORY_COMMIT, base, size, protection, &result);
        if (status != WIT_STATUS_OK) {
            release(base, 0);
            return -ENOMEM;
        }
    }
    if (track(base, size, MapPlain, 0, 0) < 0) {
        release(base, 0);
        return -ENOMEM;
    }
    return (long)base;
}

/* A file's bytes copied into plain memory, the rest of the pages zero: what a writable private mapping needs, and
 * a file that does not start at a page boundary in the package. */
static long map_copy(long fd, WitU64 address, WitU64 size, WitU64 protection, WitU64 offset, WitU64 length)
{
    WitU64 result = 0;
    if (protection & WIT_MEMORY_EXECUTE) {
        return -EACCES;
    }
    const long base = map_anonymous(address, size, WIT_MEMORY_READ | WIT_MEMORY_WRITE);
    if (base < 0) {
        return base;
    }
    if (offset < length) {
        const WitU64 bytes = length - offset < size ? length - offset : size;
        if (__wit_pread(fd, (void *)base, (long)bytes, (long)offset) != (long)bytes) {
            remove_range((WitU64)base, size);
            return -EIO;
        }
    }
    if (protection != (WIT_MEMORY_READ | WIT_MEMORY_WRITE) &&
        wit_syscall(WIT_CALL_MEMORY_PROTECT, (WitU64)base, size, protection, &result) != WIT_STATUS_OK) {
        remove_range((WitU64)base, size);
        return -EACCES;
    }
    return base;
}

/* A file's pages shown from the package without a copy, at consecutive addresses in mappings of at most CHUNK; the
 * pages past the file's last are plain reserved memory, as Linux's would fault. */
static long map_shared_pages(WitU64 address, WitU64 size, WitU64 protection, WitU64 source, WitU64 file_pages)
{
    WitU64 base = address;
    if (!base) {
        /* An address for the whole range: reserved, then given back for the mappings to take. */
        if (wit_syscall(WIT_CALL_MEMORY_RESERVE, size, PAGE, 0, &base) != WIT_STATUS_OK) {
            return -ENOMEM;
        }
        release(base, 0);
    }
    const WitU64 shown = file_pages < size ? file_pages : size;
    for (WitU64 done = 0; done < shown;) {
        const WitU64 bytes = shown - done < CHUNK ? shown - done : CHUNK;
        WitU64 mapped = 0;
        if (map_package(base + done, source + done, bytes, protection, &mapped) != WIT_STATUS_OK ||
            track(mapped, bytes, MapPackage, source + done, protection) < 0) {
            remove_range(base, done);
            return -ENOMEM;
        }
        done += bytes;
    }
    if (shown < size) {
        const long tail = map_anonymous(base + shown, size - shown, WIT_MEMORY_NONE);
        if (tail < 0) {
            remove_range(base, shown);
            return tail;
        }
    }
    return (long)base;
}

/* A shared memory file's pages from the offset at consecutive addresses, one mapping per chunk covered (R3.2b). */
static long map_shared_file(WitU64 address, WitU64 size, WitU64 protection, WitU32 file, WitU64 offset, WitU64 length)
{
    if (offset > length || size > length - offset) {
        return -ENXIO; /* past the file's end, which ftruncate sets */
    }
    WitU64 base = address;
    if (!base) {
        if (wit_syscall(WIT_CALL_MEMORY_RESERVE, size, PAGE, 0, &base) != WIT_STATUS_OK) {
            return -ENOMEM;
        }
        release(base, 0);
    }
    for (WitU64 done = 0; done < size;) {
        const WitU64 at = offset + done;
        const WitU64 bytes = CHUNK - at % CHUNK < size - done ? CHUNK - at % CHUNK : size - done;
        const long status = map_chunk(file, at, base + done, bytes, protection);
        if (status < 0) {
            remove_range(base, done);
            return status;
        }
        done += bytes;
    }
    return (long)base;
}

long __wit_mmap(long address, long length, long protection, long flags, long fd, long offset)
{
    WitU64 kernel_protection;
    long converted;
    if (length <= 0 || offset < 0 || ((unsigned long)offset & (PAGE - 1))) {
        return -EINVAL;
    }
    if ((converted = protection_of(protection, &kernel_protection)) != 0) {
        return converted;
    }
    const WitU64 size = round_up((unsigned long)length);
    WitU64 place = 0;
    if (flags & (MAP_FIXED | MAP_FIXED_NOREPLACE)) {
        if (((unsigned long)address & (PAGE - 1)) || !address) {
            return -EINVAL;
        }
        place = (WitU64)address;
        if ((flags & MAP_FIXED) && !(flags & MAP_FIXED_NOREPLACE) && (converted = remove_range(place, size)) < 0) {
            return converted;
        }
    }
    if (flags & MAP_ANONYMOUS) {
        return map_anonymous(place, size, kernel_protection);
    }
    WitU64 source = 0, file_length = 0;
    const long kind = __wit_file_map_source(fd, &source, &file_length);
    if (kind < 0) {
        return kind;
    }
    if (kind == 1) {
        return map_anonymous(place, size, kernel_protection); /* /dev/zero */
    }
    if (kind == 2 || kind == 3) {
        /* A shared memory file: shared mappings alone, writable only through a descriptor opened for writing. */
        if (!(flags & MAP_SHARED)) {
            return -ENOSYS;
        }
        if (kind == 3 && (protection & PROT_WRITE)) {
            return -EACCES;
        }
        return map_shared_file(place, size, kernel_protection, (WitU32)source, (WitU64)offset, file_length);
    }
    if ((flags & MAP_SHARED) && (protection & PROT_WRITE)) {
        return -EACCES; /* the package is read-only */
    }
    const int aligned = !((source + (WitU64)offset) & (PAGE - 1));
    /* A private mapping that is not writable shows the package's pages without a copy (R3.1): code and read-only data
     * cost no pages of the process, as musl's dynamic linker maps a whole library read-only first. */
    if (!(protection & PROT_WRITE) && aligned) {
        const WitU64 file_pages = (WitU64)offset < file_length ? round_up(file_length - (WitU64)offset) : 0;
        return map_shared_pages(place, size, kernel_protection, source + (WitU64)offset, file_pages);
    }
    return map_copy(fd, place, size, kernel_protection, (WitU64)offset, file_length);
}

long __wit_munmap(long address, long length)
{
    if (length <= 0 || ((unsigned long)address & (PAGE - 1))) {
        return -EINVAL;
    }
    return remove_range((WitU64)address, round_up((unsigned long)length));
}

/* Copy-on-write, made eager (R3.1): the part of a package mapping in [start, end) that mprotect makes writable becomes
 * plain memory holding the same bytes, as a private mapping of a file does on Linux; the package stays read-only. The
 * bytes are read through a window of the package, since the mapping itself may be PROT_NONE. A mapping made with
 * PROT_EXEC stays the package's: plain memory never executes, so its code would not run again, and the kernel refuses
 * the write instead (EACCES). */
static long privatize(WitU64 start, WitU64 end)
{
    for (;;) {
        unsigned i = 0;
        while (i < count &&
            (mappings[i].Kind != MapPackage ||
                (mappings[i].Protection & WIT_MEMORY_EXECUTE) ||
                mappings[i].Base + mappings[i].Size <= start ||
                mappings[i].Base >= end)) {
            ++i;
        }
        if (i == count) {
            return 0;
        }
        const Mapping m = mappings[i];
        const WitU64 low = m.Base > start ? m.Base : start;
        const WitU64 high = m.Base + m.Size < end ? m.Base + m.Size : end;
        WitU64 window = 0;
        if (map_package(0, m.Source + (low - m.Base), high - low, WIT_MEMORY_READ, &window) != WIT_STATUS_OK) {
            return -ENOMEM;
        }
        long status = remove_range(low, high - low);
        if (status >= 0) {
            status = map_anonymous(low, high - low, WIT_MEMORY_READ | WIT_MEMORY_WRITE);
        }
        if (status >= 0) {
            memcpy((void *)low, (const void *)window, high - low);
        }
        release(window, 0);
        if (status < 0) {
            return status;
        }
    }
}

long __wit_mprotect(long address, long length, long protection)
{
    WitU64 result = 0, kernel_protection;
    long converted;
    if (length < 0 || ((unsigned long)address & (PAGE - 1))) {
        return -EINVAL;
    }
    if (length == 0) {
        return 0;
    }
    if ((converted = protection_of(protection, &kernel_protection)) != 0) {
        return converted;
    }
    const WitU64 start = (WitU64)address, end = start + round_up((unsigned long)length);
    if ((kernel_protection & WIT_MEMORY_WRITE) && (converted = privatize(start, end)) < 0) {
        return converted;
    }
    int touched = 0;
    for (unsigned i = 0; i < count; ++i) {
        Mapping *m = &mappings[i];
        if (m->Base + m->Size <= start || m->Base >= end) {
            continue;
        }
        if (m->Kind == MapPlain && (kernel_protection & WIT_MEMORY_EXECUTE)) {
            return -EACCES;
        }
        const WitU64 low = m->Base > start ? m->Base : start, high = m->Base + m->Size < end ? m->Base + m->Size : end;
        /* A PROT_NONE mapping is reserved without pages: a plain range gets its missing pages (zero) first, which
         * commit adds without touching the pages it has, and then the protection. */
        if (m->Kind == MapPlain &&
            wit_syscall(WIT_CALL_MEMORY_COMMIT, low, high - low, kernel_protection, &result) != WIT_STATUS_OK) {
            return -ENOMEM;
        }
        const WitU64 status = wit_syscall(WIT_CALL_MEMORY_PROTECT, low, high - low, kernel_protection, &result);
        if (status != WIT_STATUS_OK) {
            return status == WIT_STATUS_NOT_COMMITTED ? -ENOMEM
                                                      : (status == WIT_STATUS_DENIED ? -EACCES : __wit_errno(status));
        }
        if (m->Kind == MapShared) {
            for (WitU64 page = (low - m->Base) / PAGE; page < (high - m->Base) / PAGE; ++page) {
                page_set(m, page, page_code(kernel_protection));
            }
        }
        touched = 1;
    }
    if (touched) {
        return 0;
    }
    /* Memory the library did not map (a stack, the image): the kernel decides. */
    const WitU64 status = wit_syscall(WIT_CALL_MEMORY_PROTECT, start, end - start, kernel_protection, &result);
    return status == WIT_STATUS_OK ? 0 : (status == WIT_STATUS_NOT_COMMITTED ? -ENOMEM : __wit_errno(status));
}

/* mremap (R2.1): no mapping grows where it lies, since each is one kernel reservation with nothing reserved after it
 * for it, and moving one is not implemented. Growing in place a range inside one of the library's mappings or inside
 * the calling thread's stack is ENOMEM, as Linux answers a mapping that cannot grow there; musl measures the main
 * thread's stack that way (pthread_getattr_np), down to the first range that is neither. Everything else is ENOSYS:
 * musl's realloc copies instead. */
long __wit_mremap(long address, long old_length, long new_length, long flags)
{
    if (flags != 0 || old_length <= 0 || new_length <= old_length || ((unsigned long)address & (PAGE - 1))) {
        return -ENOSYS;
    }
    const WitU64 start = (WitU64)address, end = start + round_up((unsigned long)old_length);
    for (unsigned i = 0; i < count; ++i) {
        if (mappings[i].Base <= start && end <= mappings[i].Base + mappings[i].Size) {
            return -ENOMEM;
        }
    }
    WitUserThreadInfo info;
    WitU64 result = 0;
    info.Version = WIT_THREAD_INFO_VERSION;
    info.Size = sizeof(info);
    if (wit_syscall(WIT_CALL_THREAD_QUERY, WIT_THREAD_SELF, (WitU64)&info, sizeof(info), &result) == WIT_STATUS_OK &&
        info.StackLow <= start &&
        end <= info.StackHigh) {
        return -ENOMEM;
    }
    return -ENOSYS;
}

long __wit_madvise(long address, long length, long advice)
{
    WitU64 result = 0;
    if (length < 0 || ((unsigned long)address & (PAGE - 1))) {
        return -EINVAL;
    }
    if (advice == MADV_DONTNEED && length > 0) {
        /* The pages read as zero afterwards: the kernel's reset zeroes them and keeps them committed. */
        const WitU64 status =
            wit_syscall(WIT_CALL_MEMORY_RESET, (WitU64)address, round_up((unsigned long)length), 0, &result);
        /* Pages never committed read as zero when they are: nothing to reset. */
        return status == WIT_STATUS_OK || status == WIT_STATUS_NOT_COMMITTED ? 0 : __wit_errno(status);
    }
    return 0; /* advice the kernel has no mechanism for yet changes nothing, which is what advice allows */
}
