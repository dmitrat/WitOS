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

#endif
