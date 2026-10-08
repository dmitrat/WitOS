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
} WitProcessContext;

extern WitProcessContext __wit_process;

/* The package bytes a descriptor reads: 0 with the offset in the package and the length for a package file, 1 for
 * /dev/zero, -ENODEV for another device, -EBADF for no descriptor. */
long __wit_file_map_source(long fd, WitU64 *source, WitU64 *length);

/* An ABI-1 status as a negative errno. */
long __wit_errno(WitU64 status);
#endif
