#ifndef WITOS_START_H
#define WITOS_START_H
#include "witos/types.h"

/* The start of a program in a new process (plan step S5.2), the beginning of the process manager's protocol (S6).
 * The creator maps the program and its first stack into the process (PROCESS_CREATE, MEMORY_OBJECT_MAP naming the
 * process), builds on the stack what a Linux kernel builds for a static program — argc, argv, envp and the auxiliary
 * vector — and starts the first thread at the program's entry with THREAD_CREATE version 3. The auxiliary vector's
 * WIT_AT_START entry is the child-local handle of the endpoint PROCESS_CREATE installed; before it starts the thread,
 * the creator sends on the other end the start message, which carries the capabilities the process starts with in
 * the message's handle slots: the kernel log, the boot package, then the optional ones Capabilities names, in the
 * order of their bits. A tag beyond musl's AUX_CNT, so that musl's own startup ignores it; getauxval reads it.
 * Version 2 (S6.1) added the process manager's endpoint: a process the process manager starts may ask it to start
 * others (witos/manager.h). Version 3 (S6.2) added the standard streams the process starts without and its initial
 * directory, whose bytes follow the structure in the message. Version 4 (D1.1, ADR 0025) adds the capability the
 * creator grants the process, a service's channel to its creator, and names the optional capabilities in
 * Capabilities, since a message's handles leave no gaps. Version 5 (D5.1) adds the namespace service's endpoint, the
 * files of the mounted volumes (witos/namespace.h), which the process manager gives every process it starts while a
 * message has room; a service that also takes a grant asks its creator for the namespace instead. The arguments and the environment stay on the stack, where
 * musl reads them and a message of 256 bytes could not hold them. */
#define WIT_AT_START 0x57490001UL

#define WIT_START_VERSION 5U
#define WIT_START_SIZE 32U
#define WIT_START_DIRECTORY_MAXIMUM 224U /* the rest of a channel message's 256 bytes */
#define WIT_START_HANDLE_LOG 0U /* the kernel log: DEBUG_WRITE, standard output and error */
#define WIT_START_HANDLE_PACKAGE 1U /* the boot package object: MAP, EXECUTE and QUERY */
#define WIT_START_HANDLES 2U /* the capabilities every start message carries */
#define WIT_START_HANDLES_MAXIMUM 4U /* a channel message's handles */

/* The optional capabilities, after the package in this order. */
#define WIT_START_MANAGER 1U /* the process manager's endpoint: SEND (S6.1) */
#define WIT_START_GRANT 2U /* the capability the creator grants (D1.1): a service's channel to its creator */
#define WIT_START_NAMESPACE 4U /* the namespace service's clients' endpoint: SEND (D5.1, witos/namespace.h) */
#define WIT_START_CAPABILITIES (WIT_START_MANAGER | WIT_START_GRANT | WIT_START_NAMESPACE)

/* The bytes of the start message, followed by DirectoryBytes of the initial directory. */
typedef struct WitStartMessage {
    WitU32 Version, Size;
    WitU64 PackageBytes; /* the size of the boot package object */
    WitU32
        ClosedStreams; /* bit n: standard descriptor n is closed; the others are the log (1, 2) and empty input (0) */
    WitU32
        DirectoryBytes; /* an absolute path without a terminator, at most WIT_START_DIRECTORY_MAXIMUM; zero for "/" */
    WitU32
        Capabilities; /* WIT_START_MANAGER, WIT_START_GRANT, WIT_START_NAMESPACE: the optional capabilities carried */
    WitU32 Reserved;
} WitStartMessage;

WIT_STATIC_ASSERT(sizeof(WitStartMessage) == WIT_START_SIZE, "Start message");
#endif
