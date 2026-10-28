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
#include "witos/socket.h"

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
long __wit_sched_setaffinity(long tid, long size, const unsigned char *mask);
long __wit_getcpu(unsigned *cpu, unsigned *node);
struct sched_param;
long __wit_sched_getscheduler(long tid);
long __wit_sched_getparam(long tid, struct sched_param *param);
long __wit_sched_priority(long policy);
long __wit_sched_setscheduler(long tid, long policy, const struct sched_param *param);
long __wit_sched_setparam(long tid, const struct sched_param *param);

/* Linux memory calls over reserve/commit (memory.c). */
long __wit_mmap(long address, long length, long protection, long flags, long fd, long offset);
long __wit_munmap(long address, long length);
void __wit_mapping_forget(WitU64 base); /* memory.c, under the table lock */
void __wit_mapping_forget_locked(WitU64 base); /* syscall.c: takes the table lock */
long __wit_mprotect(long address, long length, long protection);
long __wit_madvise(long address, long length, long advice);
long __wit_msync(long address, long length, long flags);
long __wit_mremap(long address, long old_length, long new_length, long flags);
long __wit_flush_icache(long start, long end, long flags); /* riscv64's riscv_flush_icache as CODE_PUBLISH (V2.4) */

/* Files over the read-only boot package (files.c, S1.2). */
struct kstat;
struct iovec;
long __wit_openat(long dirfd, const char *path, long flags, long mode);
long __wit_close(long fd);
long __wit_read(long fd, void *buffer, long bytes);
long __wit_pread(long fd, void *buffer, long bytes, long offset);
long __wit_readv(long fd, const struct iovec *vectors, long count);
long __wit_write(long fd, const void *buffer, long bytes);
long __wit_writev(long fd, const struct iovec *vectors, long count_vectors);
long __wit_pwrite(long fd, const void *buffer, long bytes, long offset);
long __wit_fsync(long fd);
long __wit_lseek(long fd, long offset, long whence);
long __wit_fstatat(long dirfd, const char *path, struct kstat *st, long flags);
long __wit_faccessat(long dirfd, const char *path, long mode);
long __wit_readlinkat(long dirfd, const char *path, char *buffer, long size);
long __wit_getdents(long fd, unsigned char *buffer, long bytes);
long __wit_getcwd(char *buffer, long size);
long __wit_fcntl(long fd, long command, long argument);
int __wit_is_file_descriptor(long fd);
long __wit_stream_of(long fd); /* the standard stream fd stands for, -1 for none (R2.3a) */
long __wit_dup(long fd, long lowest, int close_on_exec);
long __wit_dup3(long fd, long target, long flags); /* flags -1: dup2 */
long __wit_pipe2(int *fds, long flags);
int __wit_pipe_of(long fd, WitU32 *index, int *write_end, int *nonblocking);
long __wit_pipe_bytes(long fd);

/* Pipes within the process (pipe.c, R2.3a) and between processes (N4.2): a pipe an end of which a posix_spawn file
 * action gives a child is shared — its ring in a memory object, a wakeup endpoint per end — and the child adopts the
 * end over the handles the start gave it. */
long __wit_pipe_create(WitU32 *index);
long __wit_pipe_share(WitU32 index);
long __wit_pipe_handles(WitU32 index, int write_end, WitU64 *object, WitU64 *endpoint);
long __wit_pipe_adopt(WitU64 object, WitU64 endpoint, int write_end, WitU32 *index);
long __wit_descriptor_stream_handles(long fd, WitU64 *object, WitU64 *endpoint, int *write_end, int *nonblocking);
long __wit_stream_handles(long fd, WitU64 *object, WitU64 *endpoint, int *write_end, int *nonblocking); /* syscall.c */
/* What a descriptor is as the source of a child's standard stream (N4.2): the log stream it stands for (0, 1 or 2),
 * WIT_SOURCE_PIPE for a pipe end, WIT_SOURCE_OTHER for a file, a directory or a device, -EBADF for none. */
#define WIT_SOURCE_PIPE 3L
#define WIT_SOURCE_OTHER 4L
long __wit_descriptor_source(long fd); /* files.c, under the table lock */
long __wit_source_of(long fd); /* syscall.c: takes the table lock */
long __wit_install_stream(long fd, WitU64 object, WitU64 endpoint, int write_end, int nonblocking);
void __wit_pipe_reference(WitU32 index);
void __wit_pipe_release(WitU32 index);
void __wit_pipe_end(WitU32 index, int write_end, int delta);
long __wit_pipe_read(WitU32 index, unsigned char *buffer, unsigned long bytes, int nonblocking);
long __wit_pipe_write(WitU32 index, const unsigned char *buffer, unsigned long bytes, int nonblocking);
long __wit_pipe_available(WitU32 index);
short __wit_pipe_poll(WitU32 index, int write_end, short events);
short __wit_pipe_edge(WitU32 index, int write_end, short events, WitU64 *token); /* with the edge token (epoll.c) */
struct epoll_event;
long __wit_epoll_allocate(WitU32 *index); /* epoll.c (N4.2): an instance for a new descriptor */
void __wit_epoll_reference(WitU32 index);
void __wit_epoll_release(WitU32 index);
long __wit_epoll_ctl(WitU32 index, int op, long fd, const struct epoll_event *event);
long __wit_epoll_collect(WitU32 index, struct epoll_event *events, int count);

/* The sockets an instance watches (W2.1b): what epoll_wait asks the network service outside the table lock, each with
 * a reference to its record and the edge token read before the question, then reports under the lock again. */
typedef struct WitEpollProbe {
    WitU32 Entry; /* the registration */
    WitU32 Socket; /* its socket's record, referenced */
    WitU32 Id; /* the socket's service identifier at the registration */
    short Asked; /* poll's events the registration asks */
    int Token; /* the session's readiness events before the question */
} WitEpollProbe;

#define WIT_EPOLL_ENTRIES 32
int __wit_epoll_probes(WitU32 index, WitEpollProbe *probes);
long __wit_epoll_report(WitU32 index, const WitEpollProbe *probes, const short *polled, int probed,
    struct epoll_event *events, long first, int count); /* the events from events[first], the number added */
long __wit_epoll_open(int flags); /* files.c: the descriptor of a new instance */
long __wit_epoll_instance(long epfd, WitU32 *index); /* files.c: the instance a descriptor names */
int __wit_pipe_sequence(void);
long __wit_pipe_wait(int seen, WitU64 deadline);
short __wit_descriptor_poll(long fd, short events);
void __wit_poll_bump(void); /* pipe.c: a parked poll looks again (N4.3b: the terminal's input arrived) */

/* The standard streams over the terminal service (terminal.c, N4.3b), when the start message carried its endpoint. */
int __wit_terminal_present(void);
long __wit_terminal_read(void *buffer, long bytes);
long __wit_terminal_write(const void *buffer, long bytes);
long __wit_terminal_ioctl(long stream, long request, void *argument);
short __wit_terminal_poll(long stream, short events);
/* Sockets over the network service (socket.c, W2.1a), when the start message carried its endpoint: the socket calls
 * and a socket's transfers, ioctl and poll outside the table lock; a socket's record referenced by its descriptors
 * (files.c) and the calls in flight, its CLOSE sent after the last reference outside the table lock; /etc/resolv.conf
 * from the lease. */
#define WIT_NOT_A_SOCKET (-0x10002L) /* outside the errno range */
int __wit_is_socket_call(long n);
long __wit_socket_call(long n, long a1, long a2, long a3, long a4, long a5, long a6);
long __wit_socket_transfer(long n, long fd, long a2, long a3);
long __wit_socket_ioctl(long fd, long command, void *argument);
short __wit_socket_poll(WitU32 index, short events);
WitU32 __wit_socket_id(WitU32 index);
int __wit_socket_events(void); /* the session's readiness events, whatever their socket: epoll's edge token (W2.1b) */
void __wit_socket_reference(WitU32 index);
void __wit_socket_release(WitU32 index);
void __wit_socket_flush(void);
long __wit_socket_open(WitU32 index, int flags); /* files.c, under the table lock */
long __wit_socket_of(long fd, WitU32 *index, int *nonblocking); /* files.c, under the table lock */
long __wit_socket_install(WitU32 index, int flags); /* syscall.c: takes the table lock */
long __wit_socket_lookup(long fd, WitU32 *index, int *nonblocking); /* syscall.c: takes the table lock */
long __wit_socket_nonblocking(long fd, int nonblocking); /* syscall.c: FIONBIO as F_SETFL */
long __wit_socket_configuration(
    WitSocketConfiguration *configuration); /* socket.c: CONFIGURATION, -ENODEV without the service */
long __wit_socket_configuration6(WitSocketConfiguration6 *configuration); /* socket.c: CONFIGURATION6 (W4.3) */

/* Netlink route sockets and the interface ioctls (netlink.c, W4.2). */
long __wit_netlink_call(long n, long a1, long a2, long a3, long a4, long a5, long a6); /* WIT_NOT_A_SOCKET otherwise */
long __wit_netlink_transfer(long n, long fd, long a2, long a3); /* WIT_NOT_A_SOCKET otherwise */
long __wit_netlink_ioctl(long fd, long command, void *argument); /* WIT_NOT_A_SOCKET otherwise */
long __wit_interface_ioctl(
    long command, void *argument); /* SIOCGIF* of any socket; WIT_NOT_A_SOCKET for another command */
void __wit_netlink_reference(WitU32 index);
void __wit_netlink_release(WitU32 index);
WitU32 __wit_netlink_id(WitU32 index);
short __wit_netlink_poll(WitU32 index, short events); /* files.c, under the table lock */
short __wit_netlink_edge(WitU32 index, short events, WitU64 *token); /* epoll.c, under the table lock */
long __wit_netlink_open(WitU32 index, int flags); /* files.c, under the table lock */
long __wit_netlink_of(long fd, WitU32 *index, int *nonblocking); /* files.c, under the table lock: 1, 0 or -EBADF */
long __wit_netlink_install(WitU32 index, int flags); /* syscall.c: takes the table lock */
long __wit_netlink_lookup(long fd, WitU32 *index, int *nonblocking); /* syscall.c: takes the table lock */
void __wit_netlink_notify(const WitSocketConfiguration *before, const WitSocketConfiguration *after,
    const WitSocketConfiguration6 *before6, const WitSocketConfiguration6 *after6); /* socket.c's events thread */
void __wit_resolv_refresh(void);
WitU32 __wit_resolv_text(unsigned char *buffer, WitU32 capacity);
int __wit_resolv_present(void);
/* Local sockets (unix.c, W4.1): AF_UNIX's stream and sequenced-packet sockets and socketpair's, without the network
 * service. A connection's rings are the two processes' own; the names sockets bind are the namespace service's
 * (namespace.c). syscall.c sends every socket call, transfer and ioctl here first, outside the table lock; a record lives
 * while a descriptor (files.c's KindLocal) or a call in flight references it, and a bound one's close goes to the
 * namespace service after the last reference, outside the table lock (__wit_local_flush). */
long __wit_local_call(long n, long a1, long a2, long a3, long a4, long a5, long a6); /* WIT_NOT_A_SOCKET otherwise */
long __wit_local_transfer(long n, long fd, long a2, long a3);
long __wit_local_ioctl(long fd, long command, void *argument);
void __wit_local_reference(WitU32 index);
void __wit_local_release(WitU32 index);
void __wit_local_flush(void);
short __wit_local_poll(WitU32 index, short events); /* files.c, under the table lock */
short __wit_local_edge(WitU32 index, short events, WitU64 *token); /* epoll.c, under the table lock */
WitU32 __wit_local_id(WitU32 index);
long __wit_local_open(WitU32 index, int flags); /* files.c, under the table lock */
long __wit_local_open_pair(WitU32 first, WitU32 second, int flags, int *fds); /* files.c: both or neither */
long __wit_local_of(long fd, WitU32 *index, int *nonblocking); /* files.c, under the table lock: 1, 0 or -EBADF */
long __wit_local_name(const char *path, unsigned char *name, WitU32 *length, int connecting); /* files.c */
long __wit_local_install(WitU32 index, int flags); /* syscall.c: takes the table lock */
long __wit_local_install_pair(WitU32 first, WitU32 second, int flags, int *fds);
long __wit_local_lookup(long fd, WitU32 *index, int *nonblocking);
long __wit_local_path(const char *path, unsigned char *name, WitU32 *length, int connecting);
long __wit_remote_socket_bind(const unsigned char *name, WitU32 length, int abstract, int type, WitU64 *id);
long __wit_remote_socket_listen(WitU64 id, WitU64 endpoint, int *moved);
long __wit_remote_socket_connect(const unsigned char *name, WitU32 length, int abstract, int type, WitU64 *endpoint);
long __wit_remote_socket_close(WitU64 id);
void __wit_poll_sources(int delta); /* pipe.c: sources another process changes, which poll looks at every 10 ms */
long __wit_memfd_create(const char *name, long flags);
long __wit_descriptor_capacity(void);
long __wit_ftruncate(long fd, long size);
long __wit_unlinkat(long dirfd, const char *path, long flags);
long __wit_flock(long fd, long operation);
int __wit_lock_sequence(void);
volatile int *__wit_lock_sequence_word(void);
long __wit_mkdirat(long dirfd, const char *path);
long __wit_fchmodat(long dirfd, const char *path, long mode); /* chmod of what a path names (N4.1) */
long __wit_fchmod(long fd, long mode);
struct timespec;
long __wit_utimensat(
    long dirfd, const char *path, const struct timespec *times, long flags); /* a null path: the descriptor's (N4.1) */
long __wit_shared_open(const unsigned char *name, WitU32 length, long flags, WitU32 *index);
long __wit_shared_anonymous(WitU32 *index);
long __wit_shared_anonymous_fixed(WitU64 size, WitU32 *index); /* a file of a size set once, its chunks as large (R9) */
long __wit_shared_remote(WitU32 remote, WitU64 size, int writable, WitU32 *index);
WitU32 __wit_shared_remote_of(WitU32 index);
long __wit_shared_unlink(const unsigned char *name, WitU32 length);
void __wit_shared_reference(WitU32 index);
void __wit_shared_release(WitU32 index);
WitU64 __wit_shared_size(WitU32 index);
WitU32 __wit_shared_mode(WitU32 index); /* the permission bits a stat reports (N4.1) */
long __wit_shared_chmod(WitU32 index, long mode);
long __wit_shared_truncate(WitU32 index, WitU64 size);
long __wit_shared_chunk(WitU32 index, WitU64 offset, WitU64 *handle);
long __wit_shared_find(const unsigned char *name, WitU32 length, WitU32 *index);
long __wit_shared_read(WitU32 index, WitU64 offset, unsigned char *buffer, WitU64 bytes);
long __wit_shared_write(WitU32 index, WitU64 offset, const unsigned char *buffer, WitU64 bytes);
/* Files of the volumes the namespace service mounts (namespace.c, D5.1): whether a name of files.c lies below a
 * mount point, and the operations on such names and on the remote records their descriptors hold. */
struct kstat;
int __wit_remote_name(const unsigned char *name, WitU32 length);
/* The mount table the libc serves as /etc/mtab (C1.2): the package at / and each mount point, one mntent line each. */
#define WIT_MOUNTS_TEXT_BYTES 2048U
WitU32 __wit_mounts_text(unsigned char *buffer, WitU32 capacity);
long __wit_remote_open(const unsigned char *name, WitU32 length, long flags, long mode, WitU32 *index, int *directory);
void __wit_remote_reference(WitU32 index);
void __wit_remote_release(WitU32 index);
WitU32 __wit_remote_directory(WitU32 index, unsigned char *name);
long __wit_remote_read(WitU32 index, WitU64 offset, void *buffer, WitU64 bytes);
long __wit_remote_write(WitU32 index, WitU64 offset, const void *buffer, WitU64 bytes);
long __wit_remote_fstat(WitU32 index, struct kstat *st);
/* The session's turn (S8.1): one request at a time on the session, the window its meanwhile; a thread that finds the
 * session busy parks on its word. Every __wit_remote_* call takes it by itself; a transfer of files.c outside the
 * table lock takes it once around the calls in turn below. */
void __wit_remote_turn_take(void);
void __wit_remote_turn_give(void);
long __wit_remote_read_in_turn(WitU32 index, WitU64 offset, void *buffer, WitU64 bytes);
long __wit_remote_write_in_turn(WitU32 index, WitU64 offset, const void *buffer, WitU64 bytes);
long __wit_remote_fstat_in_turn(WitU32 index, struct kstat *st);
/* A read or a write of a remote file outside the table lock (S8.1, files.c): begin and end under the lock, the
 * transfer between them holding the session's turn alone. Begin answers WIT_NOT_A_REMOTE for any other descriptor. */
#define WIT_NOT_A_REMOTE (-0x10003L) /* outside the errno range */
long __wit_remote_transfer_begin(long fd, int writing, WitU32 *index, WitU32 *description);
long __wit_remote_transfer(WitU32 index, WitU32 description, int writing, void *buffer, long bytes, long offset);
void __wit_remote_transfer_end(WitU32 index, WitU32 description);
long __wit_remote_stat(const unsigned char *name, WitU32 length, struct kstat *st);
struct WitNamespaceVolume;
/* The volume a path or an open file of a volume lies on, with its space (E1: statfs and fstatfs). */
long __wit_remote_volume(const unsigned char *name, WitU32 length, struct WitNamespaceVolume *volume);
long __wit_remote_fvolume(WitU32 index, struct WitNamespaceVolume *volume);
struct statfs;
/* statfs (fd -1) and fstatfs (path null) (E1): a volume's from the namespace service, the package's and /dev/shm's here. */
long __wit_statfs(long fd, const char *path, struct statfs *buf);
/* mount(2) and umount2(2) of a share (E2.2), outside the table lock, since the namespace service logs on to a server
 * over the network: the target's name normalized under the lock (__wit_resolve_name, the name without its leading
 * slash, length 0 for an empty target), then the service's MOUNT or UNMOUNT in the session's turn. */
long __wit_resolve_name(const char *path, unsigned char *name, WitU32 capacity, WitU32 *length);
long __wit_remote_mount(const char *source, const unsigned char *target, WitU32 target_length, const char *type,
    unsigned long flags, const char *data);
long __wit_remote_unmount(const unsigned char *name, WitU32 length, long flags);
long __wit_remote_list(WitU32 index, WitU64 *cursor, unsigned char *buffer, long bytes);
long __wit_remote_mkdir(const unsigned char *name, WitU32 length);
long __wit_remote_remove(const unsigned char *name, WitU32 length, int directory);
long __wit_remote_rename(const unsigned char *from, WitU32 from_length, const unsigned char *to, WitU32 to_length);
long __wit_remote_truncate(WitU32 index, WitU64 size);
long __wit_remote_map(WitU32 index, WitU64 offset, int writable, WitU64 *object);
long __wit_remote_sync(WitU32 index);
long __wit_remote_readlink(const unsigned char *name, WitU32 length, char *buffer, long size);
long __wit_remote_chmod(const unsigned char *name, WitU32 length, long mode); /* N4.1 */
long __wit_remote_fchmod(WitU32 index, long mode);
struct WitNamespaceTimes;
long __wit_remote_utimes(const unsigned char *name, WitU32 length, const struct WitNamespaceTimes *times, WitU32 flags);
long __wit_remote_futimes(WitU32 index, const struct WitNamespaceTimes *times, WitU32 flags);
long __wit_renameat2(long olddirfd, const char *oldpath, long newdirfd, const char *newpath, long flags);
/* The current directory (files.c, S6.2): chdir, fchdir; the absolute directory a path names from a base directory
 * (the current one when null) or a directory descriptor names, which must exist, for posix_spawn; the directory the
 * start message names. */
long __wit_chdir(const char *path);
long __wit_fchdir(long fd);
long __wit_descriptor_directory(long fd, char *out, long size);
long __wit_set_directory(const char *path, long bytes);

/* Child processes through the process manager (process.c, S6.1): wait4 and waitid (N4.2; info is a siginfo_t) over
 * the children posix_spawn started, and kill of one of them (SIGKILL ends it, 0 finds it, anything else is EPERM). */
struct rusage;
long __wit_wait4(long pid, int *status, long options, struct rusage *usage);
long __wit_waitid(long idtype, long id, void *info, long options);
long __wit_kill_child(int pid, int sig);

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
int __wit_thread_start_detached(void *(*entry)(void *), void *argument); /* a thread of the library's own: 0 or errno */
long __wit_prctl(long option, unsigned long argument);
long __wit_set_tid_address(int *address);
__attribute__((__noreturn__)) long __wit_thread_exit(long code, WitU64 reservation);
int __wit_is_exit_word(const volatile void *address);
WitU64 __wit_futex_exit_event(void);
long __wit_futex_deadline(const struct timespec *relative, WitU64 *deadline);
long __wit_futex_wait_until(volatile int *address, int value, WitU64 deadline);
long __wit_futex(
    volatile int *address, int operation, int value, const struct timespec *timeout, volatile int *second, int third);
WitSignalState *__wit_signal_state(void);
WitSignalState *__wit_signal_state_of(WitU64 identity);
long __wit_thread_signal(int tid, int sig);
WitU64 __wit_thread_handle(int tid);
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
    void *Resuming; /* the signal frame sigreturn resumes through its own activation (signal.c, K8.1) */
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
