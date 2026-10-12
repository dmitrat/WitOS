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

/* What a start passes beyond the program, its arguments and its environment (S6.1, S6.2, D1.1). */
typedef struct witos_spawn_options {
    WitU64 manager; /* the process manager's endpoint, duplicated with SEND into the process; zero for none */
    const char *directory; /* the initial directory, an absolute path of the package; null for "/" */
    WitU32 closed_streams; /* bit n: the process starts without standard descriptor n */
    WitU32 reserved; /* zero */
    WitU64 grant; /* a capability the process starts with (D1.1): moved into its start message, closed when the start
                     fails; zero for none */
    WitU64 namespace_endpoint; /* the namespace's clients' endpoint (D5.1), duplicated with SEND into the process when
                                  its start message has room; zero for none */
} witos_spawn_options;

/* witos_spawn with options; null options are witos_spawn's. */
int witos_spawn_ex(
    WitU64 *process, const char *path, char *const argv[], char *const envp[], const witos_spawn_options *options);

/* The process manager (S6.1, witos/manager.h): starts the program at path with argv and envp as the first process,
 * serves the spawn requests of every process it starts and of theirs, until the first process ends, and copies that
 * process's record to *info. Returns 0 or an errno value of the first start; the root task runs it. */
int witos_manager_run(const char *path, char *const argv[], char *const envp[], WitProcessInfo *info);

/* A service the process manager starts before its first process (D1.1, ADR 0025). */
typedef struct witos_service {
    const char *path; /* a program of the boot package, started with its path as its one argument */
    WitU64 grant; /* the end of a channel to the creator, moved into the service's start message */
    WitU64 ready; /* the creator's end, on which the service answers with a WitServiceStatus (witos/manager.h) */
    WitU64 process; /* out: the service's process (WAIT, QUERY, KILL, MANAGE), zero when it did not start */
    int status; /* out: 0 when it answered that it is ready, its errno value, or ECHILD when it ended first */
} witos_service;

/* witos_manager_run that first starts each service in turn, serving requests until the service answered or ended, then
 * the first process; the services run on until the first process ends and the root task with it. Every grant is moved
 * or closed, and the ready ends stay the caller's. */
int witos_manager_run_services(witos_service *services, WitU32 count, const char *path, char *const argv[],
    char *const envp[], WitProcessInfo *info);

/* The process manager's client (D1.2): asks the manager this process was started under to start the program at path
 * with argv and envp in "/", granting it the capability given (zero for none), which moves into its start message;
 * writes the new process's handle (WAIT, QUERY, KILL, DUPLICATE, TRANSFER) to *process. The grant is moved or closed.
 * Returns 0 or an errno value: ENOSYS without a process manager, the manager's answer otherwise. */
int witos_manager_spawn(const char *path, char *const argv[], char *const envp[], WitU64 grant, WitU64 *process);

/* Waits until the process ended and copies its record: State EXITED with ExitCode, or FAULTED. Returns 0 or an errno
 * value (EBADF for a handle that names no process); the handle stays open, and its close releases the record. */
int witos_wait(WitU64 process, WitProcessInfo *info);
#endif
