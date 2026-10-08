#ifndef WITOS_PROCESS_H
#define WITOS_PROCESS_H
#include "user_abi.h"

/* Processes (RFC 0011 section 7.8, plan step K5.2c). PROCESS_CREATE makes an empty address space with one capability,
 * the channel endpoint the creator names; the creator places the image and the first stack with MEMORY_OBJECT_MAP
 * naming the process handle, starts the first thread with THREAD_CREATE version 3, and sends every other capability
 * over the channel. The kernel reads no image, no name and no path. */
#define WIT_PROCESS_CREATE_VERSION 1U
#define WIT_PROCESS_CREATE_SIZE 32U
#define WIT_PROCESS_INFO_VERSION 1U
#define WIT_PROCESS_INFO_SIZE 40U

/* The request of PROCESS_CREATE: Endpoint is a live channel endpoint handle of the caller with TRANSFER, moved into
 * the child with its rights (the child-local handle value is written to the call's output); Pages bounds the child's
 * page quota, zero for the kernel's default and at most that default; Flags is zero. */
typedef struct WitProcessCreateRequest {
    WitU32 Version, Size;
    WitU64 Endpoint;
    WitU64 Pages;
    WitU32 Flags, Reserved;
} WitProcessCreateRequest;

WIT_STATIC_ASSERT(sizeof(WitProcessCreateRequest) == WIT_PROCESS_CREATE_SIZE, "Process create request ABI");

/* The snapshot of PROCESS_QUERY: State is LIVE while the process runs (with or without threads), EXITED once it
 * exited or was killed with ExitCode, FAULTED once a fault of a thread ended it; ChargedPages are the pages its
 * address space owns and the pages of the live memory objects it created; Threads are its live threads. */
#define WIT_PROCESS_STATE_LIVE 1U
#define WIT_PROCESS_STATE_EXITED 2U
#define WIT_PROCESS_STATE_FAULTED 3U

typedef struct WitProcessInfo {
    WitU32 Version, Size;
    WitU32 State, Threads;
    WitU64 ExitCode;
    WitU64 ChargedPages;
    WitU64 Reserved;
} WitProcessInfo;

WIT_STATIC_ASSERT(sizeof(WitProcessInfo) == WIT_PROCESS_INFO_SIZE, "Process info ABI");
#endif
