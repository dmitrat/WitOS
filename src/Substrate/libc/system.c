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
 * memory takes: the data arena mmap reserves in, and RLIMIT_NOFILE, the library's descriptor table (R6).
 * sched_getaffinity reports the processors threads run on, the first ones of the kernel's table, and sched_setaffinity
 * accepts a mask that holds every one of them, which changes nothing (R3.2: CoreCLR's PAL gives each new thread the
 * process's mask), refuses one that holds none with EINVAL, as Linux does, and one that holds some alone with ENOSYS
 * until threads run on several processors (phase P). The kernel runs every thread round-robin at one priority, which
 * is what Linux reports of a normal thread (R7.1): SCHED_OTHER at priority 0. Every other limit, setting a limit or a
 * policy and the limits of another process are ENOSYS, never an invented answer. */

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
    if ((pid != 0 && pid != WIT_LIBC_PROCESS_ID) || limit || (resource != RLIMIT_AS && resource != RLIMIT_NOFILE)) {
        return -ENOSYS;
    }
    if (resource == RLIMIT_NOFILE) {
        /* The library's descriptor table (R6), as large as Linux's usual soft limit. */
        if (old) {
            old->rlim_cur = (rlim_t)__wit_descriptor_capacity();
            old->rlim_max = (rlim_t)__wit_descriptor_capacity();
        }
        return 0;
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

/* A thread's policy and priority (R7.1): SCHED_OTHER at 0 for every thread of the process. sched_get_priority_min and
 * max give the normal policies the one priority 0 and refuse the real-time ones with EINVAL, a policy the system
 * layer does not know; setting SCHED_OTHER at 0 changes nothing and is accepted, any other setting is ENOSYS. CoreCLR's
 * PAL reads the policy and its range before SetThreadPriority. */
long __wit_sched_getscheduler(long tid)
{
    const long status = tid != 0 ? __wit_thread_signal((int)tid, 0) : 0;
    return status < 0 ? status : SCHED_OTHER;
}

long __wit_sched_getparam(long tid, struct sched_param *param)
{
    const long status = tid != 0 ? __wit_thread_signal((int)tid, 0) : 0;
    if (status < 0) {
        return status;
    }
    if (!param) {
        return -EINVAL;
    }
    memset(param, 0, sizeof(*param));
    return 0;
}

long __wit_sched_priority(long policy)
{
    policy &= ~(long)SCHED_RESET_ON_FORK;
    return policy == SCHED_OTHER || policy == SCHED_BATCH || policy == SCHED_IDLE ? 0 : -EINVAL;
}

long __wit_sched_setscheduler(long tid, long policy, const struct sched_param *param)
{
    const long status = tid != 0 ? __wit_thread_signal((int)tid, 0) : 0;
    if (status < 0) {
        return status;
    }
    if (!param || policy < 0) {
        return -EINVAL;
    }
    return (policy & ~(long)SCHED_RESET_ON_FORK) == SCHED_OTHER && param->sched_priority == 0 ? 0 : -ENOSYS;
}

long __wit_sched_setparam(long tid, const struct sched_param *param)
{
    return __wit_sched_setscheduler(tid, SCHED_OTHER, param);
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
