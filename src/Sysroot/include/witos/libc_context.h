#ifndef WITOS_LIBC_CONTEXT_H
#define WITOS_LIBC_CONTEXT_H
#include "witos/types.h"

/* The process context the C library keeps (plan step S5.2) and what libwitos reaches through it: the capabilities the
 * process started with and the package bytes behind a file descriptor. An interface between WitOS's two libraries of
 * layer 2 (src/Substrate/libc and src/Substrate/libwitos), never an application API. The root task's crt1 fills the
 * context from the kernel's startup descriptor; a program another process started fills it from its creator's start
 * message (witos/start.h) before musl's startup runs. */
typedef struct WitProcessContext {
    WitU64 Log; /* the kernel log: standard output and error (DEBUG_WRITE) */
    WitU64 Package; /* the boot package object: the files and their executable mappings */
    WitU64 PackageBytes; /* the size of the package */
    WitU64 Manager; /* the process manager's endpoint (S6.1); zero in the root task and without a manager */
    WitU32 ClosedStreams; /* bit n: standard descriptor n is closed (S6.2) */
    WitU32 Reserved;
} WitProcessContext;

extern WitProcessContext __wit_process;

/* The package bytes a descriptor reads: 0 with the offset in the package and the length for a package file, 1 for
 * /dev/zero, -ENODEV for another device, -EBADF for no descriptor. */
long __wit_file_map_source(long fd, WitU64 *source, WitU64 *length);

/* An ABI-1 status as a negative errno. */
long __wit_errno(WitU64 status);

/* The absolute directory a path names from a base directory, an absolute path or the current directory when null,
 * written to out (S6.2): the directory must exist; the length without the terminator, or a negative errno. */
long __wit_directory_resolve(const char *base, const char *path, char *out, long size);
#endif
