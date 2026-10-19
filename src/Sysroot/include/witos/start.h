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
 * message has room; a service that also takes a grant asks its creator for the namespace instead. Version 6 (N4.2)
 * adds the standard streams a creator gives the process as pipes: Streams counts the WitStreamMessages that follow on
 * the granted channel, each moving one end of a pipe (its ring's memory object and its wakeup endpoint,
 * src/Substrate/libc/pipe.c) for one standard descriptor; the grant is then that channel alone, which the libc closes
 * once the streams arrived, so a process with streams has no grant of its own; and, since a message holds four
 * handles, the optional capabilities the first message cannot hold follow in a second one (WIT_START_MORE). N4.3
 * adds the terminal service's clients' endpoint (WIT_START_TERMINAL, witos/terminal.h), after the namespace's, and
 * W2.1a the network service's (WIT_START_NETWORK, witos/socket.h), after the terminal's. The
 * arguments and the environment stay on the stack, where musl reads them and a message of 256 bytes could not hold
 * them. */
#define WIT_AT_START 0x57490001UL

#define WIT_START_VERSION 6U
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
/* The optional capabilities the first message's four slots had no room for follow in a second message on the start
 * endpoint, in the order of their bits, with no bytes (N4.2, N4.3: a process with a manager and a grant takes the
 * namespace and the terminal so). */
#define WIT_START_MORE 8U
#define WIT_START_TERMINAL 16U /* the terminal service's clients' endpoint: SEND (N4.3, witos/terminal.h) */
#define WIT_START_NETWORK 32U /* the network service's clients' endpoint: SEND (W2.1a, witos/socket.h) */
#define WIT_START_CAPABILITIES \
    (WIT_START_MANAGER | \
        WIT_START_GRANT | \
        WIT_START_NAMESPACE | \
        WIT_START_MORE | \
        WIT_START_TERMINAL | \
        WIT_START_NETWORK)

/* The bytes of the start message, followed by DirectoryBytes of the initial directory. */
typedef struct WitStartMessage {
    WitU32 Version, Size;
    WitU64 PackageBytes; /* the size of the boot package object */
    WitU32
        ClosedStreams; /* bit n: standard descriptor n is closed; the others are the log (1, 2) and empty input (0) */
    WitU32
        DirectoryBytes; /* an absolute path without a terminator, at most WIT_START_DIRECTORY_MAXIMUM; zero for "/" */
    WitU32
        Capabilities; /* WIT_START_MANAGER ... WIT_START_NETWORK: the optional capabilities carried, WIT_START_MORE */
    WitU32 Streams; /* the WitStreamMessages that follow on the grant (N4.2), at most WIT_START_STREAMS_MAXIMUM */
} WitStartMessage;

WIT_STATIC_ASSERT(sizeof(WitStartMessage) == WIT_START_SIZE, "Start message");

/* A standard stream given as a pipe (N4.2): one message per stream on the granted channel, after the start message,
 * with the pipe's memory object (MAP, WRITE, DUPLICATE, TRANSFER) in the first handle slot and the end's wakeup
 * endpoint (WAIT, SEND, RECEIVE, DUPLICATE, TRANSFER) in the second. The memory object holds the libc's pipe ring
 * (src/Substrate/libc/pipe.c); every process that holds an end of the pipe maps it and sends a wakeup on its endpoint
 * after each change, so that the other end's waiters look again, and learns the other end's close as PEER_CLOSED. */
#define WIT_START_STREAMS_MAXIMUM 3U
#define WIT_STREAM_SIZE 24U
#define WIT_STREAM_READ_END 0U
#define WIT_STREAM_WRITE_END 1U
#define WIT_STREAM_NONBLOCKING 0x1U /* the end was opened with O_NONBLOCK */

typedef struct WitStreamMessage {
    WitU32 Version, Size; /* WIT_START_VERSION, WIT_STREAM_SIZE */
    WitU32 Descriptor; /* the standard descriptor the end becomes: 0, 1 or 2 */
    WitU32 End; /* WIT_STREAM_READ_END or WIT_STREAM_WRITE_END */
    WitU32 Flags, Reserved; /* WIT_STREAM_NONBLOCKING; zero */
} WitStreamMessage;

WIT_STATIC_ASSERT(sizeof(WitStreamMessage) == WIT_STREAM_SIZE, "Stream message");
#endif
