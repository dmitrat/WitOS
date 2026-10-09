#ifndef WITOS_SPAWN_H
#define WITOS_SPAWN_H
#include "witos/types.h"
#include "witos/process.h"

/* libwitos (plan step S5.2): a program of the boot package started in a new process — the static ELF loader of the
 * root task, until the process manager (S6) serves posix_spawn. The program is a static position-independent ELF
 * executable of the ISA (ET_DYN with PT_DYNAMIC and no PT_INTERP, linked with rcrt1.o and libc.a); it starts with
 * its arguments and environment on its stack and the kernel log and the boot package as its capabilities
 * (witos/start.h). */

/* Starts the program at path with argv (argv[0] first, null-terminated) and envp (null-terminated, or null for none)
 * and writes the handle of the new process (WAIT, QUERY, KILL, MANAGE, DUPLICATE and TRANSFER) to *process. Returns
 * 0 or an errno value: an open error of the path (ENOENT), ENOEXEC for a file that is no such program of this ISA or
 * not a package file, E2BIG for arguments and environment beyond the room of the first stack, ENOMEM for a quota
 * (processes, channels, memory objects, pages, handles); nothing of a failed start remains. */
int witos_spawn(WitU64 *process, const char *path, char *const argv[], char *const envp[]);

/* What a start passes beyond the program, its arguments and its environment (S6.1). */
typedef struct witos_spawn_options {
    WitU64 manager; /* the process manager's endpoint, duplicated with SEND into the process; zero for none */
} witos_spawn_options;

/* witos_spawn with options; null options are witos_spawn's. */
int witos_spawn_ex(
    WitU64 *process, const char *path, char *const argv[], char *const envp[], const witos_spawn_options *options);

/* The process manager (S6.1, witos/manager.h): starts the program at path with argv and envp as the first process,
 * serves the spawn requests of every process it starts and of theirs, until the first process ends, and copies that
 * process's record to *info. Returns 0 or an errno value of the first start; the root task runs it. */
int witos_manager_run(const char *path, char *const argv[], char *const envp[], WitProcessInfo *info);

/* Waits until the process ended and copies its record: State EXITED with ExitCode, or FAULTED. Returns 0 or an errno
 * value (EBADF for a handle that names no process); the handle stays open, and its close releases the record. */
int witos_wait(WitU64 process, WitProcessInfo *info);
#endif
