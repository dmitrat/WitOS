#ifndef WITOS_MANAGER_H
#define WITOS_MANAGER_H
#include "witos/types.h"

/* The process manager's protocol (plan step S6.1, RFC 0011 v3 section 9.3). The process manager runs in the root task
 * (libwitos's witos_manager_run) and owns one channel: it receives on one end and gives every process it starts a
 * duplicate of the other with SEND alone, the third capability of the start message (witos/start.h). A request
 * carries its data in a memory object, since a message holds 256 bytes, and the end of a channel the requester made
 * for the reply; the reply moves the new process's handle back. The libc's posix_spawn and waitpid speak it
 * (src/Substrate/libc/process.c); nothing of it is an application API. Version 3 (D1.1, ADR 0025) lets a request
 * carry a third capability, the grant the new process starts with (witos/start.h), and lets the process manager start
 * services before its first process, each answering on its granted channel when it is ready. Version 4 (N4.2) lets a
 * spawn request say how many standard streams the requester gives the new process as pipes (WitSpawnRequest's
 * Streams), which the requester sends on the grant after the start (witos/start.h, WitStreamMessage). */
#define WIT_MANAGER_VERSION 4U
#define WIT_MANAGER_SPAWN 1U /* start a program of the boot package */

/* A request: the bytes of the message, with the request's memory object (MAP, TRANSFER) and the reply endpoint
 * (SEND, TRANSFER) in its first two handle slots and, when the third is there, the capability the new process is
 * granted (TRANSFER), which the manager moves into its start message. */
typedef struct WitManagerRequest {
    WitU32 Version, Size;
    WitU32 Operation, Reserved;
    WitU64 Bytes; /* the request's bytes at the start of the memory object */
} WitManagerRequest;

WIT_STATIC_ASSERT(sizeof(WitManagerRequest) == 24, "Manager request");

/* A reply: zero or an errno value; with zero, the new process's handle (WAIT, QUERY, KILL, DUPLICATE, TRANSFER) in its
 * one handle slot. */
typedef struct WitManagerReply {
    WitU32 Version, Size;
    WitU32 Operation;
    WitU32 Error;
} WitManagerReply;

WIT_STATIC_ASSERT(sizeof(WitManagerReply) == 16, "Manager reply");

/* The bytes of a spawn request: the program's path, the arguments and the environment, each a run of zero-terminated
 * strings, and the initial directory, an absolute path (version 2, S6.2), at offsets from the start of the request
 * and inside its Bytes; the standard streams the process starts without (WitStartMessage's ClosedStreams); and the
 * streams the requester gives it as pipes (version 4, N4.2: Streams, at most WIT_START_STREAMS_MAXIMUM, each closed in
 * ClosedStreams too, since the log is not theirs; a request with streams carries the grant, on which the requester
 * sends them). */
typedef struct WitSpawnRequest {
    WitU32 Version, Size;
    WitU32 ArgumentCount, EnvironmentCount;
    WitU64 Bytes;
    WitU64 PathOffset, ArgumentsOffset, EnvironmentOffset, DirectoryOffset;
    WitU32 ClosedStreams, Streams;
} WitSpawnRequest;

WIT_STATIC_ASSERT(sizeof(WitSpawnRequest) == 64, "Spawn request");

/* A service's first message on the channel its creator granted it (D1.1): zero when it is ready, or the errno value
 * of what it could not do. The process manager starts its first process after each service it starts before it has
 * answered or ended. */
typedef struct WitServiceStatus {
    WitU32 Version, Size; /* WIT_MANAGER_VERSION, the size of this structure */
    WitU32 Error;
    WitU32 Reserved;
} WitServiceStatus;

WIT_STATIC_ASSERT(sizeof(WitServiceStatus) == 16, "Service status");

/* A creator's request that its service stop (D6.1), on the channel it granted the service: the service stops the
 * services it started, ends its own work so that nothing of it is lost (a filesystem unmounts its volumes, storage
 * document section 98) and answers a WitServiceStatus, zero or the errno value of what it could not finish, before it
 * ends. Deadline is the absolute monotonic deadline by which the creator waits for the answer (WIT_WAIT_INFINITE for
 * none); a service passes it on to the services it stops. */
#define WIT_SERVICE_STOP 2U

typedef struct WitServiceStop {
    WitU32 Version, Size; /* WIT_MANAGER_VERSION, the size of this structure */
    WitU32 Operation, Reserved; /* WIT_SERVICE_STOP */
    WitU64 Deadline;
} WitServiceStop;

WIT_STATIC_ASSERT(sizeof(WitServiceStop) == 24, "Service stop");

/* The exit code with which the libc ends a process whose signal's default action terminates it (S6.1): waitpid
 * reports it as WIFSIGNALED with the signal. Codes 0 to 255 are exit statuses. */
#define WIT_EXIT_SIGNALED 0x10000ULL
#define WIT_EXIT_SIGNAL(sig) (WIT_EXIT_SIGNALED | (WitU64)(sig))
#endif
