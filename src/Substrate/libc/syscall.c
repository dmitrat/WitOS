#define _GNU_SOURCE
#include "witos_libc.h"
#include <errno.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/syscall.h>
#include <sys/uio.h>
#include <sys/utsname.h>
#include <time.h>

/* The dispatch of Linux system calls over ABI-1 (plan step S1.1): what musl asks for, by number, becomes the
 * kernel's calls or an honest -ENOSYS. The standard descriptors are the kernel log: writes to 1 and 2 go through
 * DEBUG_WRITE on the startup descriptor's log handle, reads from 0 end the input, and TIOCGWINSZ succeeds so that
 * musl line-buffers standard output and every line reaches the log whole. Files (S1.2), threads (S2) and signals
 * (S3) are not here yet; their calls return -ENOSYS and nothing pretends to have succeeded. */

#define LOG_HANDLE (__wit_startup->Handles[WIT_ROOT_HANDLE_LOG])
#define DEBUG_WRITE_MAX 65536UL /* WIT_DEBUG_WRITE_MAX of one call */
#define TID 1L

long __wit_errno(WitU64 status)
{
    switch (status) {
    case WIT_STATUS_OK:
        return 0;
    case WIT_STATUS_UNSUPPORTED:
        return -ENOSYS;
    case WIT_STATUS_BAD_HANDLE:
    case WIT_STATUS_WRONG_TYPE:
        return -EBADF;
    case WIT_STATUS_DENIED:
        return -EACCES;
    case WIT_STATUS_BAD_ADDRESS:
        return -EFAULT;
    case WIT_STATUS_TOO_LARGE:
    case WIT_STATUS_NO_MEMORY:
        return -ENOMEM;
    case WIT_STATUS_BUSY:
        return -EBUSY;
    case WIT_STATUS_TIMED_OUT:
        return -ETIMEDOUT;
    case WIT_STATUS_INTERRUPTED:
        return -EINTR;
    case WIT_STATUS_NOT_FOUND:
        return -ENOENT;
    default:
        return -EINVAL;
    }
}

static int standard_descriptor(long fd)
{
    return fd >= 0 && fd <= 2;
}

static long write_log(long fd, const void *buffer, unsigned long length)
{
    unsigned long written = 0;
    if (!standard_descriptor(fd)) {
        return -EBADF;
    }
    if (fd == 0) {
        return -EBADF;
    }
    while (written < length) {
        WitU64 count = length - written;
        WitU64 result = 0;
        if (count > DEBUG_WRITE_MAX) {
            count = DEBUG_WRITE_MAX;
        }
        const WitU64 status = wit_syscall(WIT_CALL_DEBUG_WRITE, LOG_HANDLE, (WitU64)buffer + written, count, &result);
        if (status != WIT_STATUS_OK) {
            return written ? (long)written : __wit_errno(status);
        }
        written += result;
    }
    return (long)written;
}

/* The vectors are joined before they reach the log, so that a line musl writes as its buffer plus the new bytes
 * arrives in one DEBUG_WRITE and carries one prefix; what does not fit the join buffer goes vector by vector. */
static long writev_log(long fd, const struct iovec *vectors, long count)
{
    static unsigned char joined[4096];
    unsigned long total = 0;
    if (count < 0 || count > 1024) {
        return -EINVAL;
    }
    if (!standard_descriptor(fd) || fd == 0) {
        return -EBADF;
    }
    for (long i = 0; i < count; ++i) {
        total += vectors[i].iov_len;
    }
    if (total <= sizeof(joined)) {
        unsigned long filled = 0;
        for (long i = 0; i < count; ++i) {
            for (unsigned long k = 0; k < vectors[i].iov_len; ++k) {
                joined[filled++] = ((const unsigned char *)vectors[i].iov_base)[k];
            }
        }
        return write_log(fd, joined, filled);
    }
    long written_total = 0;
    for (long i = 0; i < count; ++i) {
        if (vectors[i].iov_len == 0) {
            continue;
        }
        const long written = write_log(fd, vectors[i].iov_base, vectors[i].iov_len);
        if (written < 0) {
            return written_total ? written_total : written;
        }
        written_total += written;
        if ((unsigned long)written < vectors[i].iov_len) {
            break;
        }
    }
    return written_total;
}

static long terminal_ioctl(long fd, long request, void *argument)
{
    if (!standard_descriptor(fd)) {
        return -EBADF;
    }
    if (request == TIOCGWINSZ) {
        struct winsize *size = argument;
        size->ws_row = 25;
        size->ws_col = 80;
        size->ws_xpixel = 0;
        size->ws_ypixel = 0;
        return 0;
    }
    return -ENOTTY;
}

/* Monotonic counts as nanoseconds without overflowing: whole seconds first, then the remainder. */
static void counts_to_timespec(WitU64 counts, WitU64 frequency, struct timespec *ts)
{
    ts->tv_sec = (time_t)(counts / frequency);
    ts->tv_nsec = (long)((counts % frequency) * 1000000000ULL / frequency);
}

static long clock_read(long clock, struct timespec *ts)
{
    WitU64 value = 0, frequency = 0, status;
    switch (clock) {
    case CLOCK_REALTIME:
    case CLOCK_REALTIME_COARSE:
        status = wit_syscall(WIT_CALL_CLOCK_READ, WIT_CLOCK_UTC, 0, 0, &value);
        if (status != WIT_STATUS_OK) {
            return __wit_errno(status);
        }
        ts->tv_sec = (time_t)(value / 1000000000ULL);
        ts->tv_nsec = (long)(value % 1000000000ULL);
        return 0;
    case CLOCK_MONOTONIC:
    case CLOCK_MONOTONIC_RAW:
    case CLOCK_MONOTONIC_COARSE:
    case CLOCK_BOOTTIME:
    case CLOCK_PROCESS_CPUTIME_ID:
    case CLOCK_THREAD_CPUTIME_ID:
        status = wit_syscall(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, &value);
        if (status != WIT_STATUS_OK) {
            return __wit_errno(status);
        }
        status = wit_syscall(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, &frequency);
        if (status != WIT_STATUS_OK || frequency == 0) {
            return -EINVAL;
        }
        counts_to_timespec(value, frequency, ts);
        return 0;
    default:
        return -EINVAL;
    }
}

static long clock_resolution(long clock, struct timespec *ts)
{
    WitU64 frequency = 0;
    switch (clock) {
    case CLOCK_REALTIME:
    case CLOCK_REALTIME_COARSE:
        ts->tv_sec = 0;
        ts->tv_nsec = 1;
        return 0;
    case CLOCK_MONOTONIC:
    case CLOCK_MONOTONIC_RAW:
    case CLOCK_MONOTONIC_COARSE:
    case CLOCK_BOOTTIME:
    case CLOCK_PROCESS_CPUTIME_ID:
    case CLOCK_THREAD_CPUTIME_ID:
        if (wit_syscall(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, &frequency) != WIT_STATUS_OK ||
            frequency == 0) {
            return -EINVAL;
        }
        ts->tv_sec = 0;
        ts->tv_nsec = frequency >= 1000000000ULL ? 1 : (long)(1000000000ULL / frequency);
        return 0;
    default:
        return -EINVAL;
    }
}

/* A relative or absolute sleep on the monotonic clock through SLEEP_UNTIL; the remaining time is zero on return. */
static long sleep_for(long clock, long flags, const struct timespec *request, struct timespec *remaining)
{
    WitU64 now = 0, frequency = 0, result;
    if (request == 0 || request->tv_nsec < 0 || request->tv_nsec >= 1000000000L || request->tv_sec < 0) {
        return -EINVAL;
    }
    if (clock != CLOCK_MONOTONIC && clock != CLOCK_REALTIME) {
        return -ENOTSUP;
    }
    if (wit_syscall(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, &now) != WIT_STATUS_OK ||
        wit_syscall(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, &frequency) != WIT_STATUS_OK ||
        frequency == 0) {
        return -EINVAL;
    }
    WitU64 counts = (WitU64)request->tv_sec * frequency + (WitU64)request->tv_nsec * frequency / 1000000000ULL;
    if (flags & 1) { /* TIMER_ABSTIME: an absolute time of the given clock; only the monotonic one is the kernel's */
        if (clock != CLOCK_MONOTONIC) {
            return -ENOTSUP;
        }
        if (counts <= now) {
            return 0;
        }
    } else {
        counts += now;
    }
    const WitU64 status = wit_syscall(WIT_CALL_SLEEP_UNTIL, counts, 0, 0, &result);
    if (status != WIT_STATUS_OK && status != WIT_STATUS_TIMED_OUT) {
        return __wit_errno(status);
    }
    if (remaining) {
        remaining->tv_sec = 0;
        remaining->tv_nsec = 0;
    }
    return 0;
}

static long system_name(struct utsname *name)
{
    memset(name, 0, sizeof(*name));
    strcpy(name->sysname, "WitOS");
    strcpy(name->nodename, "witos");
    strcpy(name->release, "0.1");
    strcpy(name->version, "ABI-1");
#if defined(__x86_64__)
    strcpy(name->machine, "x86_64");
#else
    strcpy(name->machine, "aarch64");
#endif
    return 0;
}

long __wit_syscall(long n, long a1, long a2, long a3, long a4, long a5, long a6)
{
    WitU64 result = 0;
    (void)a4;
    (void)a5;
    (void)a6;
    switch (n) {
    case SYS_exit:
    case SYS_exit_group:
        for (;;) {
            wit_syscall(WIT_CALL_PROCESS_EXIT, (WitU64)(a1 & 0xFF), 0, 0, &result);
        }
    case SYS_write:
        return write_log(a1, (const void *)a2, (unsigned long)a3);
    case SYS_writev:
        return writev_log(a1, (const struct iovec *)a2, a3);
    case SYS_read:
    case SYS_readv:
        return a1 == 0 ? 0 : -EBADF; /* nothing to read from the terminal yet; no other descriptors */
    case SYS_ioctl:
        return terminal_ioctl(a1, a2, (void *)a3);
    case SYS_mmap:
        return __wit_mmap(a1, a2, a3, a4, a5, a6);
    case SYS_munmap:
        return __wit_munmap(a1, a2);
    case SYS_mprotect:
        return __wit_mprotect(a1, a2, a3);
    case SYS_madvise:
        return __wit_madvise(a1, a2, a3);
    case SYS_mremap:
        return -ENOSYS; /* musl's realloc copies instead */
    case SYS_brk:
        return -ENOMEM; /* musl's allocators fall back to mmap */
    case SYS_clock_gettime:
        return clock_read(a1, (struct timespec *)a2);
    case SYS_clock_getres:
        return clock_resolution(a1, (struct timespec *)a2);
    case SYS_clock_nanosleep:
        return sleep_for(a1, a2, (const struct timespec *)a3, (struct timespec *)a4);
    case SYS_nanosleep:
        return sleep_for(CLOCK_MONOTONIC, 0, (const struct timespec *)a1, (struct timespec *)a2);
    case SYS_getrandom: {
        WitU64 count = (unsigned long)a2 > WIT_ABI_MAX_RANDOM ? WIT_ABI_MAX_RANDOM : (WitU64)a2;
        const WitU64 status = wit_syscall(WIT_CALL_RANDOM, (WitU64)a1, count, 0, &result);
        return status == WIT_STATUS_OK ? (long)result : __wit_errno(status);
    }
    case SYS_sched_yield:
        wit_syscall(WIT_CALL_THREAD_YIELD, 0, 0, 0, &result);
        return 0;
    case SYS_set_tid_address:
    case SYS_gettid:
    case SYS_getpid:
        return TID;
    case SYS_getppid:
    case SYS_getuid:
    case SYS_geteuid:
    case SYS_getgid:
    case SYS_getegid:
        return 0;
    case SYS_uname:
        return system_name((struct utsname *)a1);
#if defined(SYS_arch_prctl)
    case SYS_arch_prctl:
        if (a1 == 0x1002) { /* ARCH_SET_FS: the thread pointer of the calling thread */
            return __wit_errno(wit_syscall(WIT_CALL_THREAD_SET_TLS, (WitU64)a2, 0, 0, &result));
        }
        return -EINVAL;
#endif
    default:
        return -ENOSYS;
    }
}
