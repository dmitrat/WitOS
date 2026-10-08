#ifndef WITOS_START_H
#define WITOS_START_H
#include "witos/types.h"

/* The start of a program in a new process (plan step S5.2), the beginning of the process manager's protocol (S6).
 * The creator maps the program and its first stack into the process (PROCESS_CREATE, MEMORY_OBJECT_MAP naming the
 * process), builds on the stack what a Linux kernel builds for a static program — argc, argv, envp and the auxiliary
 * vector — and starts the first thread at the program's entry with THREAD_CREATE version 3. The auxiliary vector's
 * WIT_AT_START entry is the child-local handle of the endpoint PROCESS_CREATE installed; before it starts the thread,
 * the creator sends on the other end the start message, which carries the capabilities the process starts with in
 * the message's handle slots, in the order of the WIT_START_HANDLE_* indices. A tag beyond musl's AUX_CNT, so that
 * musl's own startup ignores it; getauxval reads it. */
#define WIT_AT_START 0x57490001UL

#define WIT_START_VERSION 1U
#define WIT_START_SIZE 16U
#define WIT_START_HANDLE_LOG 0U /* the kernel log: DEBUG_WRITE, standard output and error */
#define WIT_START_HANDLE_PACKAGE 1U /* the boot package object: MAP, EXECUTE and QUERY */
#define WIT_START_HANDLES 2U

/* The bytes of the start message. */
typedef struct WitStartMessage {
    WitU32 Version, Size;
    WitU64 PackageBytes; /* the size of the boot package object */
} WitStartMessage;

WIT_STATIC_ASSERT(sizeof(WitStartMessage) == WIT_START_SIZE, "Start message");
#endif
