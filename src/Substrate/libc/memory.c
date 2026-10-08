#define _GNU_SOURCE
#include "witos_libc.h"
#include <errno.h>
#include <sys/mman.h>

/* Linux memory calls over ABI-1 reservations (plan step S1.1). An anonymous private mmap is a reservation of the
 * rounded length committed at once under the requested protection, remembered here so that munmap of the whole
 * mapping releases the reservation; munmap of a part decommits it and the reservation stays, which is what
 * mallocng's partial unmaps tolerate. Executable anonymous memory is refused: code arrives through memory objects.
 * File mappings, fixed addresses and shared mappings are not here (-ENODEV, -ENOTSUP). */

#define PAGE 4096UL
#define MAPPINGS 256U

typedef struct Mapping {
    WitU64 Base, Size;
} Mapping;

static Mapping mappings[MAPPINGS];
static unsigned count;

static WitU64 round_up(unsigned long length)
{
    return (length + PAGE - 1) & ~(PAGE - 1);
}

static long protection_of(long protection, WitU64 *out)
{
    if (protection & ~(PROT_READ | PROT_WRITE | PROT_EXEC)) {
        return -EINVAL;
    }
    if (protection & PROT_EXEC) {
        return -EACCES;
    }
    if (protection & PROT_WRITE) {
        *out = WIT_MEMORY_READ | WIT_MEMORY_WRITE;
    } else if (protection & PROT_READ) {
        *out = WIT_MEMORY_READ;
    } else {
        *out = WIT_MEMORY_NONE;
    }
    return 0;
}

long __wit_mmap(long address, long length, long protection, long flags, long fd, long offset)
{
    WitU64 base = 0, result = 0, kernel_protection;
    long converted;
    if (length <= 0 || offset != 0) {
        return -EINVAL;
    }
    if (!(flags & MAP_ANONYMOUS) || fd != -1) {
        return -ENODEV; /* files come with the package (S1.2) and the namespace service (D5) */
    }
    if ((flags & MAP_FIXED) || (flags & MAP_SHARED) || address != 0) {
        return -ENOTSUP;
    }
    if ((converted = protection_of(protection, &kernel_protection)) != 0) {
        return converted;
    }
    const WitU64 size = round_up((unsigned long)length);
    WitU64 status = wit_syscall(WIT_CALL_MEMORY_RESERVE, size, PAGE, 0, &base);
    if (status != WIT_STATUS_OK) {
        return -ENOMEM;
    }
    status = wit_syscall(WIT_CALL_MEMORY_COMMIT, base, size, kernel_protection, &result);
    if (status != WIT_STATUS_OK) {
        wit_syscall(WIT_CALL_MEMORY_RELEASE, base, 0, 0, &result);
        return -ENOMEM;
    }
    if (count < MAPPINGS) {
        mappings[count].Base = base;
        mappings[count].Size = size;
        ++count;
    }
    return (long)base;
}

long __wit_munmap(long address, long length)
{
    WitU64 result = 0;
    if (length <= 0 || ((unsigned long)address & (PAGE - 1))) {
        return -EINVAL;
    }
    const WitU64 size = round_up((unsigned long)length);
    for (unsigned i = 0; i < count; ++i) {
        if (mappings[i].Base == (WitU64)address && mappings[i].Size == size) {
            const WitU64 status = wit_syscall(WIT_CALL_MEMORY_RELEASE, (WitU64)address, 0, 0, &result);
            if (status != WIT_STATUS_OK) {
                return __wit_errno(status);
            }
            mappings[i] = mappings[--count];
            return 0;
        }
    }
    const WitU64 status = wit_syscall(WIT_CALL_MEMORY_DECOMMIT, (WitU64)address, size, 0, &result);
    return status == WIT_STATUS_OK || status == WIT_STATUS_NOT_COMMITTED ? 0 : __wit_errno(status);
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
    const WitU64 status = wit_syscall(
        WIT_CALL_MEMORY_PROTECT, (WitU64)address, round_up((unsigned long)length), kernel_protection, &result);
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
        return status == WIT_STATUS_OK ? 0 : __wit_errno(status);
    }
    return 0; /* advice the kernel has no mechanism for yet changes nothing, which is what advice allows */
}
