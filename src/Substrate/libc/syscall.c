#define _GNU_SOURCE
#include "witos_libc.h"
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <string.h>
#include <sys/file.h>
#include <sys/ioctl.h>
#include <sys/membarrier.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/uio.h>
#include <sys/utsname.h>
#include <time.h>

/* The dispatch of Linux system calls over ABI-1 (plan step S1.1): what musl asks for, by number, becomes the
 * kernel's calls or an honest -ENOSYS. The standard descriptors are the kernel log: writes to 1 and 2 go through
 * DEBUG_WRITE on the process's log handle, reads from 0 end the input, and TIOCGWINSZ succeeds on an output stream so
 * that musl line-buffers standard output and every line reaches the log whole (standard input is no terminal). Files (S1.2, files.c), threads (S2,
 * thread.c, futex.c) and signals (S3, signal.c) have their own files; what none of them serves returns -ENOSYS and
 * nothing pretends to have succeeded. */

#define LOG_HANDLE (__wit_process.Log)
#define DEBUG_WRITE_MAX 65536UL /* WIT_DEBUG_WRITE_MAX of one call */
#define TID WIT_LIBC_PROCESS_ID

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

/* The output stream fd stands for: a standard descriptor that is open, which the start message or a close may have
 * taken away (S6.2), or a duplicate of one (R2.3a); standard input is no output. */
static int output_stream(long fd)
{
    return __wit_stream_of(fd) > 0;
}

static long write_log(long fd, const void *buffer, unsigned long length)
{
    unsigned long written = 0;
    if (!output_stream(fd)) {
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
    if (!output_stream(fd)) {
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

static long writev_total(const struct iovec *vectors, long count)
{
    long total = 0;
    if (count < 0 || count > 1024) {
        return -EINVAL;
    }
    for (long i = 0; i < count; ++i) {
        total += (long)vectors[i].iov_len;
    }
    return total;
}

static long terminal_ioctl(long fd, long request, void *argument)
{
    if (__wit_stream_of(fd) < 0) {
        if (request == FIONREAD) {
            const long bytes = __wit_pipe_bytes(fd); /* the bytes a pipe's read end holds */
            if (bytes >= 0) {
                *(int *)argument = (int)bytes;
                return 0;
            }
        }
        return __wit_fcntl(fd, F_GETFD, 0) < 0 ? -EBADF : -ENOTTY;
    }
    /* An output stream is the log's terminal, so that musl line-buffers it; standard input is none, since its reads end
     * at once (isatty is TIOCGWINSZ in musl, and .NET reads a terminal until a key comes, R4.1). */
    if (request == TIOCGWINSZ && __wit_stream_of(fd) != 0) {
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

/* The clocks ABI-1 has: UTC and the monotonic domain. The processor-time clocks are not among them, since the kernel
 * reports no thread's or process's processor time; they are refused with EINVAL, as Linux refuses a clock it lacks,
 * never answered with elapsed time (R1.2b: the runtime's configure measures CLOCK_THREAD_CPUTIME_ID). */
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
    if (__wit_cancel_requested()) {
        return -EINTR; /* a cancellation point about to park with its cancel word set (thread.c) */
    }
    const WitU64 status = wit_syscall(WIT_CALL_SLEEP_UNTIL, counts, 0, 0, &result);
    if (status == WIT_STATUS_INTERRUPTED) {
        /* A signal handler ran (S3): the sleep ends early with the time that was left, as nanosleep reports it. */
        if (remaining &&
            !(flags & 1) &&
            wit_syscall(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, &now) == WIT_STATUS_OK &&
            counts > now) {
            const WitU64 left = counts - now;
            remaining->tv_sec = (time_t)(left / frequency);
            remaining->tv_nsec = (long)((left % frequency) * 1000000000ULL / frequency);
        } else if (remaining) {
            remaining->tv_sec = 0;
            remaining->tv_nsec = 0;
        }
        return -EINTR;
    }
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

/* membarrier (S5.3): the private expedited barrier is the kernel's process write barrier, which fences every
 * online processor (PROCESS_WRITE_BARRIER); musl's dynamic linker issues it when dlopen installs a module's TLS for
 * the threads that exist. Registration needs nothing, and the barriers that reach other processes are not there. */
static long process_barrier(long command, long flags)
{
    WitU64 result = 0;
    if (flags) {
        return -EINVAL;
    }
    switch (command) {
    case MEMBARRIER_CMD_QUERY:
        return MEMBARRIER_CMD_PRIVATE_EXPEDITED | MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED;
    case MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED:
        return 0;
    case MEMBARRIER_CMD_PRIVATE_EXPEDITED:
        return __wit_errno(wit_syscall(WIT_CALL_PROCESS_WRITE_BARRIER, 0, 0, 0, &result));
    default:
        return -EINVAL;
    }
}

/* The calls that change or read the library's shared tables — the mappings (memory.c) and the descriptors and the
 * package (files.c), the log descriptors included — run under one lock, since threads (S2) share them. The calls
 * that wait, sleep, exit or signal never take it. */
static volatile int tables;

static int uses_tables(long n)
{
    switch (n) {
    case SYS_exit_group:
    case SYS_exit:
    case SYS_futex:
    case SYS_clock_gettime:
    case SYS_clock_getres:
    case SYS_clock_nanosleep:
    case SYS_nanosleep:
    case SYS_getrandom:
    case SYS_sched_yield:
    case SYS_set_tid_address:
    case SYS_gettid:
    case SYS_rt_sigaction:
    case SYS_rt_sigprocmask:
    case SYS_rt_sigpending:
    case SYS_rt_sigsuspend:
    case SYS_sigaltstack:
    case SYS_tkill:
    case SYS_tgkill:
    case SYS_kill:
    case SYS_ppoll:
    case SYS_flock:
#if defined(SYS_poll)
    case SYS_poll:
#endif
#if defined(SYS_pause)
    case SYS_pause:
#endif
    case SYS_getpid:
    case SYS_uname:
    case SYS_membarrier:
    case SYS_wait4:
        return 0;
    default:
        return 1;
    }
}

static long dispatch(long n, long a1, long a2, long a3, long a4, long a5, long a6);

#define NOT_A_PIPE (-0x10000L) /* outside the errno range */

/* readv and writev on a pipe: vector by vector, except that a write of at most PIPE_BUF bytes in all is joined first,
 * so that it stays one atomic write (musl's stdio flushes its buffer and the new bytes as two vectors). */
static long pipe_vectors(WitU32 index, int write_end, int nonblocking, const struct iovec *vectors, long count)
{
    long total = 0;
    if (count < 0 || count > 1024) {
        return -EINVAL;
    }
    if (write_end) {
        unsigned long all = 0;
        for (long i = 0; i < count; ++i) {
            all += vectors[i].iov_len;
        }
        if (all <= PIPE_BUF) {
            unsigned char joined[PIPE_BUF];
            unsigned long filled = 0;
            for (long i = 0; i < count; ++i) {
                memcpy(joined + filled, vectors[i].iov_base, vectors[i].iov_len);
                filled += vectors[i].iov_len;
            }
            return filled ? __wit_pipe_write(index, joined, filled, nonblocking) : 0;
        }
    }
    for (long i = 0; i < count; ++i) {
        if (vectors[i].iov_len == 0) {
            continue;
        }
        const long moved = write_end ? __wit_pipe_write(index, vectors[i].iov_base, vectors[i].iov_len, nonblocking)
                                     : __wit_pipe_read(index, vectors[i].iov_base, vectors[i].iov_len, nonblocking);
        if (moved < 0) {
            return total ? total : moved;
        }
        total += moved;
        if ((unsigned long)moved < vectors[i].iov_len) {
            break;
        }
    }
    return total;
}

/* A transfer on a pipe's end (pipe.c) runs outside the table lock, since it may park until another thread moves
 * bytes: the lock is held only to find the pipe and take a reference, and again to drop it. NOT_A_PIPE otherwise. */
static long pipe_transfer(long n, long fd, long a2, long a3)
{
    WitU32 index;
    int write_end, nonblocking;
    __wit_lock(&tables);
    const int found = __wit_pipe_of(fd, &index, &write_end, &nonblocking);
    __wit_unlock(&tables);
    if (!found) {
        return NOT_A_PIPE;
    }
    const int writing = n == SYS_write || n == SYS_writev;
    long r;
    if (writing != write_end) {
        r = -EBADF;
    } else if (n == SYS_read) {
        r = a3 < 0 ? -EINVAL : __wit_pipe_read(index, (unsigned char *)a2, (unsigned long)a3, nonblocking);
    } else if (n == SYS_write) {
        r = a3 < 0 ? -EINVAL : __wit_pipe_write(index, (const unsigned char *)a2, (unsigned long)a3, nonblocking);
    } else {
        r = pipe_vectors(index, write_end, nonblocking, (const struct iovec *)a2, a3);
    }
    __wit_lock(&tables);
    __wit_pipe_release(index);
    __wit_unlock(&tables);
    return r;
}

/* poll and ppoll (R3.2): the readiness of every descriptor under the table lock, then, when none is ready and the
 * timeout allows, a park until a pipe changes, the deadline or a signal handler; the sequence is read before the
 * readiness, so that a change between the two wakes the park at once. A signal mask for the wait is not there. */
static long poll_descriptors(struct pollfd *fds, unsigned long count, const struct timespec *timeout)
{
    WitU64 deadline = WIT_WAIT_INFINITE;
    if (timeout) {
        const long converted = __wit_futex_deadline(timeout, &deadline);
        if (converted < 0) {
            return converted;
        }
    }
    if (count > (unsigned long)INT_MAX) {
        return -EINVAL;
    }
    for (;;) {
        const int seen = __wit_pipe_sequence();
        long ready = 0;
        __wit_lock(&tables);
        for (unsigned long i = 0; i < count; ++i) {
            fds[i].revents = fds[i].fd < 0 ? 0 : __wit_descriptor_poll(fds[i].fd, fds[i].events);
            ready += fds[i].revents != 0;
        }
        __wit_unlock(&tables);
        if (ready || (timeout && !timeout->tv_sec && !timeout->tv_nsec)) {
            return ready;
        }
        const long waited = __wit_pipe_wait(seen, deadline);
        if (waited == -ETIMEDOUT) {
            return 0;
        }
        if (waited < 0) {
            return waited;
        }
    }
}

/* flock (R4.1): the lock under the table lock; when another description holds a conflicting one and LOCK_NB was not
 * asked, a park until a lock goes, the sequence read before the attempt, then the attempt again. */
static long flock_descriptor(long fd, long operation)
{
    for (;;) {
        const int seen = __wit_lock_sequence();
        __wit_lock(&tables);
        const long r = __wit_flock(fd, operation);
        __wit_unlock(&tables);
        if (r != -EWOULDBLOCK || (operation & LOCK_NB)) {
            return r;
        }
        const long waited = __wit_futex_wait_until(__wit_lock_sequence_word(), seen, WIT_WAIT_INFINITE);
        if (waited == -EINTR) {
            return -EINTR;
        }
    }
}

/* The stack of an exiting detached thread leaves the mapping table under the table lock (__unmapself). */
void __wit_mapping_forget_locked(WitU64 base)
{
    __wit_lock(&tables);
    __wit_mapping_forget(base);
    __wit_unlock(&tables);
}

long __wit_syscall(long n, long a1, long a2, long a3, long a4, long a5, long a6)
{
    if (!uses_tables(n)) {
        return dispatch(n, a1, a2, a3, a4, a5, a6);
    }
    if (n == SYS_read || n == SYS_readv || n == SYS_write || n == SYS_writev) {
        const long transferred = pipe_transfer(n, a1, a2, a3);
        if (transferred != NOT_A_PIPE) {
            return transferred;
        }
    }
    __wit_lock(&tables);
    const long r = dispatch(n, a1, a2, a3, a4, a5, a6);
    __wit_unlock(&tables);
    return r;
}

static long dispatch(long n, long a1, long a2, long a3, long a4, long a5, long a6)
{
    WitU64 result = 0;
    (void)a5;
    (void)a6;
    switch (n) {
    case SYS_exit_group:
        for (;;) {
            wit_syscall(WIT_CALL_PROCESS_EXIT, (WitU64)(a1 & 0xFF), 0, 0, &result);
        }
    case SYS_exit: /* one thread (S2): the last thread's exit ends the process with its code */
        return __wit_thread_exit(a1, 0);
    case SYS_futex:
        return __wit_futex(
            (volatile int *)a1, (int)a2, (int)a3, (const struct timespec *)a4, (volatile int *)a5, (int)a6);
    case SYS_write:
        return __wit_is_file_descriptor(a1) ? __wit_write_file(a1, a3)
                                            : write_log(a1, (const void *)a2, (unsigned long)a3);
    case SYS_writev:
        return __wit_is_file_descriptor(a1) ? __wit_write_file(a1, writev_total((const struct iovec *)a2, a3))
                                            : writev_log(a1, (const struct iovec *)a2, a3);
    case SYS_read: /* standard input ends at once; an output stream is not for reading */
        return __wit_stream_of(a1) >= 0 ? (__wit_stream_of(a1) == 0 ? 0 : -EBADF) : __wit_read(a1, (void *)a2, a3);
    case SYS_readv:
        return __wit_stream_of(a1) >= 0 ? (__wit_stream_of(a1) == 0 ? 0 : -EBADF)
                                        : __wit_readv(a1, (const struct iovec *)a2, a3);
    case SYS_pread64:
        return __wit_pread(a1, (void *)a2, a3, a4);
    case SYS_ioctl:
        return terminal_ioctl(a1, a2, (void *)a3);
    case SYS_openat:
        return __wit_openat(a1, (const char *)a2, a3, a4);
    case SYS_close:
        return __wit_close(a1);
    case SYS_lseek:
        return __wit_lseek(a1, a2, a3);
    case SYS_fstat:
        return __wit_fstatat(a1, "", (struct kstat *)a2, AT_EMPTY_PATH);
#if defined(SYS_fstatat)
    case SYS_fstatat:
        return __wit_fstatat(a1, (const char *)a2, (struct kstat *)a3, a4);
#endif
#if defined(SYS_newfstatat)
    case SYS_newfstatat:
        return __wit_fstatat(a1, (const char *)a2, (struct kstat *)a3, a4);
#endif
    case SYS_faccessat:
        return __wit_faccessat(a1, (const char *)a2, a3);
    case SYS_getdents64:
        return __wit_getdents(a1, (unsigned char *)a2, a3);
    case SYS_getcwd:
        return __wit_getcwd((char *)a1, a2);
    case SYS_chdir:
        return __wit_chdir((const char *)a1);
    case SYS_fchdir:
        return __wit_fchdir(a1);
    case SYS_fcntl:
        return __wit_fcntl(a1, a2, a3);
    case SYS_dup:
        return __wit_dup(a1, 0, 0);
    case SYS_dup3:
        return __wit_dup3(a1, a2, a3);
#if defined(SYS_dup2)
    case SYS_dup2:
        return __wit_dup3(a1, a2, -1);
#endif
    case SYS_pipe2:
        return __wit_pipe2((int *)a1, a2);
    case SYS_memfd_create:
        return __wit_memfd_create((const char *)a1, a2);
    case SYS_flock:
        return flock_descriptor(a1, a2);
    case SYS_mkdirat:
        return __wit_mkdirat(a1, (const char *)a2);
#if defined(SYS_mkdir)
    case SYS_mkdir:
        return __wit_mkdirat(AT_FDCWD, (const char *)a1);
#endif
#if defined(SYS_lstat)
    case SYS_lstat: /* the package has no symbolic links */
        return __wit_fstatat(AT_FDCWD, (const char *)a1, (struct kstat *)a2, AT_SYMLINK_NOFOLLOW);
#endif
    case SYS_ftruncate:
        return __wit_ftruncate(a1, a2);
    case SYS_unlinkat:
        return __wit_unlinkat(a1, (const char *)a2, a3);
#if defined(SYS_unlink)
    case SYS_unlink:
        return __wit_unlinkat(AT_FDCWD, (const char *)a1, 0);
#endif
#if defined(SYS_pipe)
    case SYS_pipe:
        return __wit_pipe2((int *)a1, 0);
#endif
#if defined(SYS_open)
    case SYS_open:
        return __wit_openat(AT_FDCWD, (const char *)a1, a2, a3);
#endif
#if defined(SYS_access)
    case SYS_access:
        return __wit_faccessat(AT_FDCWD, (const char *)a1, a2);
#endif
#if defined(SYS_stat)
    case SYS_stat:
        return __wit_fstatat(AT_FDCWD, (const char *)a1, (struct kstat *)a2, 0);
#endif
    case SYS_readlinkat:
        return __wit_readlinkat(a1, (const char *)a2, a4);
#if defined(SYS_readlink)
    case SYS_readlink:
        return __wit_readlinkat(AT_FDCWD, (const char *)a1, a3);
#endif
    case SYS_statx:
        return -ENOSYS; /* musl falls back to fstatat */
    case SYS_sysinfo:
        return __wit_sysinfo((struct sysinfo *)a1);
    case SYS_prlimit64:
        return __wit_prlimit(a1, a2, (const struct rlimit *)a3, (struct rlimit *)a4);
    case SYS_sched_getaffinity:
        return __wit_sched_getaffinity(a1, a2, (unsigned char *)a3);
    case SYS_sched_setaffinity:
        return __wit_sched_setaffinity(a1, a2, (const unsigned char *)a3);
    case SYS_mmap:
        return __wit_mmap(a1, a2, a3, a4, a5, a6);
    case SYS_munmap:
        return __wit_munmap(a1, a2);
    case SYS_mprotect:
        return __wit_mprotect(a1, a2, a3);
    case SYS_madvise:
        return __wit_madvise(a1, a2, a3);
    case SYS_mremap:
        return __wit_mremap(a1, a2, a3, a4);
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
        return __wit_set_tid_address((int *)a1);
    case SYS_gettid:
        return __wit_gettid();
    case SYS_prctl:
        return __wit_prctl(a1, (unsigned long)a2);
    /* Signals (S3, signal.c). */
    case SYS_rt_sigaction:
        return __wit_rt_sigaction((int)a1, (const struct k_sigaction *)a2, (struct k_sigaction *)a3, a4);
    case SYS_rt_sigprocmask:
        return __wit_rt_sigprocmask((int)a1, (const unsigned long *)a2, (unsigned long *)a3, a4);
    case SYS_rt_sigpending:
        return __wit_rt_sigpending((unsigned long *)a1, a2);
    case SYS_rt_sigsuspend:
        return __wit_rt_sigsuspend((const unsigned long *)a1, a2);
    case SYS_sigaltstack:
        return __wit_sigaltstack((const struct sigaltstack *)a1, (struct sigaltstack *)a2);
    case SYS_tkill:
        return __wit_tkill((int)a1, (int)a2);
    case SYS_tgkill:
        return __wit_tgkill((int)a1, (int)a2, (int)a3);
    case SYS_kill:
        return __wit_kill((int)a1, (int)a2);
    case SYS_rt_sigreturn:
        return -EINVAL; /* a handler returns through the trampoline, never through a kernel restorer */
#if defined(SYS_pause)
    case SYS_pause:
        return __wit_pause();
#endif
    case SYS_ppoll: /* musl's pause() where the kernel has no pause: no descriptors, no timeout */
        if (a1 == 0 && a2 == 0 && a3 == 0) {
            return __wit_pause();
        }
        return a4 ? -ENOSYS : poll_descriptors((struct pollfd *)a1, (unsigned long)a2, (const struct timespec *)a3);
#if defined(SYS_poll)
    case SYS_poll: {
        const struct timespec timeout = {(int)a3 / 1000, (long)((int)a3 % 1000) * 1000000L};
        return poll_descriptors((struct pollfd *)a1, (unsigned long)a2, (int)a3 < 0 ? 0 : &timeout);
    }
#endif
    case SYS_rt_sigqueueinfo:
    case SYS_rt_sigtimedwait:
        return -ENOSYS; /* queued values and synchronous waits for signals are not there yet */
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
    case SYS_membarrier:
        return process_barrier(a1, a2);
    case SYS_wait4:
        return __wit_wait4(a1, (int *)a2, a3, (struct rusage *)a4);
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
