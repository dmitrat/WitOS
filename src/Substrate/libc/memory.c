#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/limits.h"
#include "witos/memory_object.h"
#include <errno.h>
#include <sys/mman.h>

/* Linux memory calls over ABI-1 (plan steps S1.1 and S5.1). The library keeps a table of its mappings, each one kernel
 * reservation: a plain reservation, whose pages are committed under the requested protection and, for a file, filled
 * with the file's bytes; or a mapping of the boot package object, which shows a file's pages without a copy and is the
 * only way a file's code executes (plain memory never does; the pages are published for instruction fetch). A file of
 * at least a page starts at a page boundary in the package (S5.1), and an executable mapping is made of package
 * mappings of at most WIT_MEMORY_OBJECT_PAGES pages at consecutive addresses. MAP_FIXED replaces whatever of the
 * library's mappings lies in its range, as Linux does: a plain part is released from its reservation (the kernel splits
 * it), a package mapping that is only partly covered is mapped again around the range; munmap of any range does the
 * same, and mprotect changes every mapping the range touches. Shared writable mappings and files other than the
 * package's and /dev/zero are not here. The table lock of __wit_syscall serializes every call. */

#define PAGE 4096UL
#define MAPPINGS 256U
#define CHUNK ((WitU64)WIT_MEMORY_OBJECT_PAGES * PAGE)

typedef enum MapKind {
    MapPlain = 1, /* a plain reservation */
    MapPackage = 2 /* a mapping of the package object, without a copy */
} MapKind;

typedef struct Mapping {
    WitU64 Base, Size;
    WitU64 Source; /* a package mapping: the package offset that Base shows */
    WitU64 Protection; /* a package mapping: the kernel protection it was mapped with */
    MapKind Kind;
} Mapping;

static Mapping mappings[MAPPINGS];
static unsigned count;

#define PACKAGE_HANDLE (__wit_startup->Handles[WIT_ROOT_HANDLE_PACKAGE])

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
    mappings[count++] = (Mapping){base, size, source, protection, kind};
    return 0;
}

static void untrack(unsigned i)
{
    mappings[i] = mappings[--count];
}

static WitU64 release(WitU64 base, WitU64 size)
{
    WitU64 result = 0;
    return wit_syscall(WIT_CALL_MEMORY_RELEASE, base, size, 0, &result);
}

/* A mapping of package bytes at an address (0: the kernel chooses), published when executable. */
static WitU64 map_package(WitU64 address, WitU64 source, WitU64 size, WitU64 protection, WitU64 *base)
{
    WitMemoryMapRequest request;
    WitU64 result = 0;
    request.Version = WIT_MEMORY_MAP_VERSION;
    request.Size = sizeof(request);
    request.Object = PACKAGE_HANDLE;
    request.Offset = source;
    request.Bytes = size;
    request.Address = address;
    request.Protection = protection;
    request.Flags = 0;
    request.Target = WIT_PROCESS_SELF;
    WitU64 status = wit_syscall(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&request, sizeof(request), 0, base);
    if (status == WIT_STATUS_OK && (protection & WIT_MEMORY_EXECUTE)) {
        status = wit_syscall(WIT_CALL_CODE_PUBLISH, *base, size, 0, &result);
        if (status != WIT_STATUS_OK) {
            release(*base, 0);
        }
    }
    return status;
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
    if ((flags & MAP_SHARED) && (protection & PROT_WRITE)) {
        return -EACCES; /* the package is read-only */
    }
    const int aligned = !((source + (WitU64)offset) & (PAGE - 1));
    if (!(protection & PROT_WRITE) && aligned && (protection & PROT_EXEC)) {
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
    int touched = 0;
    for (unsigned i = 0; i < count; ++i) {
        const Mapping *m = &mappings[i];
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
        touched = 1;
    }
    if (touched) {
        return 0;
    }
    /* Memory the library did not map (a stack, the image): the kernel decides. */
    const WitU64 status = wit_syscall(WIT_CALL_MEMORY_PROTECT, start, end - start, kernel_protection, &result);
    return status == WIT_STATUS_OK ? 0 : (status == WIT_STATUS_NOT_COMMITTED ? -ENOMEM : __wit_errno(status));
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
