#ifndef WITOS_LIBC_H
#define WITOS_LIBC_H
/* WitOS's part of the C library (RFC 0011 section 9.1, plan step S1): what musl's Linux system calls become over
 * ABI-1. These files compile inside the libc build with musl's internal headers and the kernel's ABI headers; nothing
 * here is an application API. The process context is the root startup descriptor until the process manager (S6)
 * hands a started process its first message. */
#include "witos/types.h"
#include "witos/user_abi.h"
#include "witos/root.h"
#include "witos/syscall.h"

/* The startup descriptor the kernel handed the first thread; set by crt1 before the library starts. */
extern const WitRootStartup *__wit_startup;

/* musl's __syscallN (the patched arch/<arch>/syscall_arch.h) call this: a Linux system call number with its
 * arguments, returning the Linux result convention (a value, or a negative errno). */
__attribute__((__visibility__("hidden"))) long __wit_syscall(
    long n, long a1, long a2, long a3, long a4, long a5, long a6);

/* An ABI-1 status as a negative errno. */
long __wit_errno(WitU64 status);

/* Linux memory calls over reserve/commit (memory.c). */
long __wit_mmap(long address, long length, long protection, long flags, long fd, long offset);
long __wit_munmap(long address, long length);
long __wit_mprotect(long address, long length, long protection);
long __wit_madvise(long address, long length, long advice);

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
long __wit_getdents(long fd, unsigned char *buffer, long bytes);
long __wit_getcwd(char *buffer, long size);
long __wit_fcntl(long fd, long command, long argument);
int __wit_is_file_descriptor(long fd);
long __wit_file_map_source(long fd, WitU64 *source, WitU64 *length);

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
 * about to park checks it (thread.c). Called only once __wit_tls_ready is set. */
int __wit_cancel_requested(void);

/* Signals (signal.c, S3). A thread holding a library lock that a handler's async-signal-safe calls also take (the
 * futex lock) holds its signals: they wait pending meanwhile and are re-armed when the last such lock goes. */
void __wit_signal_hold_enter(void);
void __wit_signal_hold_leave(void);
/* Set once musl installed the main thread's pointer (a constructor of signal.c): thread-local state may be used.
 * Before it — musl maps the static TLS block through mmap — the library has one thread and holds nothing. The
 * thread-local state lives behind calls into other files, so that no compiler hoists a thread-local load above
 * this check (it treats thread-local variables as always dereferenceable). */
extern int __wit_tls_ready;
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
