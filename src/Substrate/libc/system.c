#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/memory_info.h"
#include <errno.h>
#include <sched.h>
#include <string.h>
#include <sys/resource.h>
#include <sys/sysinfo.h>

/* What the system layer knows of the machine and of the process's limits (plan step R2.1), from the kernel's memory
 * query: the runtime's GC and NativeAOT's PAL read it through sysinfo, sysconf, getrlimit and sched_getaffinity.
 * sysinfo reports the physical memory and the time since boot as CLOCK_BOOTTIME has it; there is no swap, no load
 * average and no count of processes, which stay zero. getrlimit reports RLIMIT_AS, the address space that anonymous
 * memory takes: the data arena mmap reserves in. sched_getaffinity reports the processors threads run on, the first
 * ones of the kernel's table, and sched_setaffinity accepts a mask that holds every one of them, which changes nothing
 * (R3.2: CoreCLR's PAL gives each new thread the process's mask), refuses one that holds none with EINVAL, as Linux
 * does, and one that holds some alone with ENOSYS until threads run on several processors (phase P). Every other
 * limit, setting a limit and the limits of another process are ENOSYS, never an invented answer. */

static long memory_info(WitUserMemoryInfo *info)
{
    WitU64 copied = 0;
    return __wit_errno(
        wit_syscall(WIT_CALL_MEMORY_QUERY, (WitU64)info, sizeof(*info), WIT_MEMORY_INFO_VERSION, &copied));
}

long __wit_sysinfo(struct sysinfo *out)
{
    WitUserMemoryInfo info;
    WitU64 now = 0, frequency = 0;
    const long status = memory_info(&info);
    if (status < 0) {
        return status;
    }
    if (wit_syscall(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, &now) != WIT_STATUS_OK ||
        wit_syscall(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, &frequency) != WIT_STATUS_OK ||
        frequency == 0) {
        return -EINVAL;
    }
    memset(out, 0, sizeof(*out));
    out->uptime = (unsigned long)(now / frequency);
    out->totalram = (unsigned long)info.PhysicalTotalBytes;
    out->freeram = (unsigned long)info.PhysicalAvailableBytes;
    out->mem_unit = 1;
    return 0;
}

long __wit_prlimit(long pid, long resource, const struct rlimit *limit, struct rlimit *old)
{
    WitUserMemoryInfo info;
    long status;
    if ((pid != 0 && pid != WIT_LIBC_PROCESS_ID) || limit || resource != RLIMIT_AS) {
        return -ENOSYS;
    }
    if ((status = memory_info(&info)) < 0) {
        return status;
    }
    if (old) {
        old->rlim_cur = (rlim_t)info.VirtualBytes;
        old->rlim_max = (rlim_t)info.VirtualBytes;
    }
    return 0;
}

long __wit_sched_getaffinity(long tid, long size, unsigned char *mask)
{
    WitUserMemoryInfo info;
    long status;
    if (tid != 0 && (status = __wit_thread_signal((int)tid, 0)) < 0) {
        return status;
    }
    if ((status = memory_info(&info)) < 0) {
        return status;
    }
    /* As Linux answers: whole words of the mask, and EINVAL for a buffer that is shorter or not made of words. */
    const unsigned long bits = 8 * sizeof(unsigned long);
    const unsigned long bytes = (info.ProcessorCount + bits - 1) / bits * sizeof(unsigned long);
    if (size <= 0 || (unsigned long)size < bytes || (size & (sizeof(unsigned long) - 1))) {
        return -EINVAL;
    }
    memset(mask, 0, bytes);
    for (WitU32 processor = 0; processor < info.ProcessorCount; ++processor) {
        mask[processor / 8] |= (unsigned char)(1U << (processor % 8));
    }
    return (long)bytes;
}

long __wit_sched_setaffinity(long tid, long size, const unsigned char *mask)
{
    WitUserMemoryInfo info;
    long status;
    if (size < 0) {
        return -EINVAL;
    }
    if (tid != 0 && (status = __wit_thread_signal((int)tid, 0)) < 0) {
        return status;
    }
    if ((status = memory_info(&info)) < 0) {
        return status;
    }
    /* The processors threads run on that the mask holds; a byte past the mask's size holds none, as on Linux. */
    WitU32 held = 0;
    for (WitU32 processor = 0; processor < info.ProcessorCount; ++processor) {
        if (processor / 8 < (unsigned long)size && (mask[processor / 8] & (1U << (processor % 8)))) {
            ++held;
        }
    }
    return held == info.ProcessorCount ? 0 : (held == 0 ? -EINVAL : -ENOSYS);
}
