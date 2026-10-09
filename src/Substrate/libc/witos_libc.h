#ifndef WITOS_LIBC_H
#define WITOS_LIBC_H
/* WitOS's part of the C library (RFC 0011 section 9.1, plan step S1): what musl's Linux system calls become over
 * ABI-1. These files compile inside the libc build with musl's internal headers and the kernel's ABI headers; nothing
 * here is an application API. The process context (witos/libc_context.h) holds the capabilities the process started
 * with: crt1 takes them from the root task's startup descriptor, start.c from the start message of a program another
 * process started (S5.2). */
#include "witos/types.h"
#include "witos/user_abi.h"
#include "witos/syscall.h"
#include "witos/libc_context.h"

/* The start of a program another process started (start.c): the process context from the start message, then the
 * main thread's record and the fault callback, as crt1 sets them up for the root task. A static program's startup
 * (rcrt1.c) calls it once its relocations are applied and before musl's __libc_start_main; the dynamic linker's third
 * stage (patches/musl/dynlink.c.patch, S5.3) calls it before it opens a library. */
__attribute__((__visibility__("hidden"))) void __wit_start_program(const unsigned long *stack);

/* musl's __syscallN (the patched arch/<arch>/syscall_arch.h) call this: a Linux system call number with its
 * arguments, returning the Linux result convention (a value, or a negative errno). */
__attribute__((__visibility__("hidden"))) long __wit_syscall(
    long n, long a1, long a2, long a3, long a4, long a5, long a6);

/* The process's id, its main thread's: getpid (syscall.c) and the calls that name the own process (system.c). */
#define WIT_LIBC_PROCESS_ID 1L

/* What the system layer knows of the machine and of the process's limits (system.c, R2.1). */
struct sysinfo;
struct rlimit;
long __wit_sysinfo(struct sysinfo *out);
long __wit_prlimit(long pid, long resource, const struct rlimit *limit, struct rlimit *old);
long __wit_sched_getaffinity(long tid, long size, unsigned char *mask);

/* Linux memory calls over reserve/commit (memory.c). */
long __wit_mmap(long address, long length, long protection, long flags, long fd, long offset);
long __wit_munmap(long address, long length);
void __wit_mapping_forget(WitU64 base); /* memory.c, under the table lock */
void __wit_mapping_forget_locked(WitU64 base); /* syscall.c: takes the table lock */
long __wit_mprotect(long address, long length, long protection);
long __wit_madvise(long address, long length, long advice);
long __wit_mremap(long address, long old_length, long new_length, long flags);

/* Files over the read-only boot package (files.c, S1.2). */
struct kstat;
struct iovec;
long __wit_openat(long dirfd, const char *path, long flags, long mode);
long __wit_close(long fd);
long __wit_read(long fd, void *buffer, long bytes);
long __wit_pread(long fd, void *buffer, long bytes, long offset);
long __wit_readv(long fd, const struct iovec *vectors, long count);
long __wit_write_file(long fd, long bytes);
long __wit_lseek(long fd, long offset, long whence);
long __wit_fstatat(long dirfd, const char *path, struct kstat *st, long flags);
long __wit_faccessat(long dirfd, const char *path, long mode);
long __wit_readlinkat(long dirfd, const char *path, long size);
long __wit_getdents(long fd, unsigned char *buffer, long bytes);
long __wit_getcwd(char *buffer, long size);
long __wit_fcntl(long fd, long command, long argument);
int __wit_is_file_descriptor(long fd);
/* The current directory (files.c, S6.2): chdir, fchdir; the absolute directory a path names from a base directory
 * (the current one when null) or a directory descriptor names, which must exist, for posix_spawn; the directory the
 * start message names. */
long __wit_chdir(const char *path);
long __wit_fchdir(long fd);
long __wit_descriptor_directory(long fd, char *out, long size);
long __wit_set_directory(const char *path, long bytes);

/* Child processes through the process manager (process.c, S6.1): wait4 over the children posix_spawn started. */
struct rusage;
long __wit_wait4(long pid, int *status, long options, struct rusage *usage);

/* The signal state of one thread (signal.c, S3): the blocked and pending signals as bits sig - 1, and the
 * alternate stack sigaltstack recorded (the kernel holds the same range as the thread's alternate stack). */
typedef struct WitSignalState {
    unsigned long Mask, Pending;
    void *AlternateBase;
    unsigned long AlternateBytes;
} WitSignalState;

/* Threads and the futex equivalent (thread.c, futex.c, S2). */
struct timespec;
void __wit_thread_init(void);
long __wit_gettid(void);
long __wit_prctl(long option, unsigned long argument);
long __wit_set_tid_address(int *address);
__attribute__((__noreturn__)) long __wit_thread_exit(long code, WitU64 reservation);
int __wit_is_exit_word(const volatile void *address);
WitU64 __wit_futex_exit_event(void);
long __wit_futex(
    volatile int *address, int operation, int value, const struct timespec *timeout, volatile int *second, int third);
WitSignalState *__wit_signal_state(void);
WitSignalState *__wit_signal_state_of(WitU64 identity);
long __wit_thread_signal(int tid, int sig);
/* Whether the calling thread is in a cancellation point (musl's __syscall_cp_asm) whose cancel word is set: a path
 * about to park checks it (thread.c). */
int __wit_cancel_requested(void);

/* The library's state of one thread that no thread-local variable holds: libc.so has no TLS of its own, since musl's
 * dynamic linker gives none to itself (S5.3). thread.c keeps it in the thread's record, found by the thread pointer
 * without a system call; zero before musl installed the main thread's pointer (__init_tp) — musl maps the static TLS
 * block through mmap before it — while the library has one thread and holds nothing. */
typedef struct WitThreadLocal {
    int Hold; /* library locks the thread holds that handlers may take (futex.c) */
    int HeldPending; /* a signal arrived while they were held */
    volatile int *CancelPoint; /* the cancel word of a cancellation point in flight */
} WitThreadLocal;

WitThreadLocal *__wit_thread_local(void);

/* Signals (signal.c, S3). A thread holding a library lock that a handler's async-signal-safe calls also take (the
 * futex lock) holds its signals: they wait pending meanwhile and are re-armed when the last such lock goes. */
void __wit_signal_hold_enter(void);
void __wit_signal_hold_leave(void);
/* A library lock (futex.c): a word taken by exchange, yielding while another thread holds it, with the holder's
 * signals held; never held across a wait or an exit. */
void __wit_lock(volatile int *word);
void __wit_unlock(volatile int *word);
struct k_sigaction;
struct sigaltstack;
void __wit_signal_init(void);
void __wit_signal_activation(void);
long __wit_rt_sigaction(int sig, const struct k_sigaction *act, struct k_sigaction *old, long size);
long __wit_rt_sigprocmask(int how, const unsigned long *set, unsigned long *old, long size);
long __wit_rt_sigpending(unsigned long *set, long size);
long __wit_rt_sigsuspend(const unsigned long *set, long size);
long __wit_sigaltstack(const struct sigaltstack *ss, struct sigaltstack *old);
long __wit_tkill(int tid, int sig);
long __wit_tgkill(int tgid, int tid, int sig);
long __wit_kill(int pid, int sig);
long __wit_pause(void);

#endif
