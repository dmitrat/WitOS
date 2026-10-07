#ifndef WITOS_THREAD_INFO_H
#define WITOS_THREAD_INFO_H
#include "types.h"
#define WIT_THREAD_INFO_VERSION 4U
#define WIT_THREAD_INFO_SIZE 96U
/* State of the queried thread. */
#define WIT_THREAD_STATE_RUNNING 1U
#define WIT_THREAD_STATE_READY 2U
#define WIT_THREAD_STATE_WAITING 3U
#define WIT_THREAD_STATE_SUSPENDED 4U
#define WIT_THREAD_STATE_EXITED 5U

/* One atomic kernel snapshot of a thread, by handle or WIT_THREAD_SELF (THREAD_QUERY). Stack bounds are
 * low-inclusive, high-exclusive, excluding guard pages; an exited thread keeps its identity and exit code and reports
 * zero bounds. The TLS fields serve the frozen Windows-form line and leave with it (K8); addresses grant no authority.
 * ContextFlags are the WIT_THREAD_CONTEXT_* flags a context of the thread would carry. */
typedef struct WitUserThreadInfo {
    WitU32 Version;
    WitU32 Size;
    WitU64 ThreadId; /* Generation-bearing identity, also the native thread id; never a capability. */
    WitU64 StackLow;
    WitU64 StackHigh;
    WitU64 RawTls;
    WitU64 CompilerTls; /* Main-module compiler TLS readiness/base; zero if absent. */
    WitU32 ProcessId;
    WitU32 ProcessorCount;
    WitU32 NativeId; /* Kernel-wide monotonically allocated 32-bit id of the frozen line. */
    WitU32 State;
    WitU64 CompilerTlsHeader; /* Actual compiler vector/header, possibly DLL-only. */
    WitU64 ExitCode;
    WitU32 SuspendCount;
    WitU32 Rights; /* Rights of the handle used; every thread right for WIT_THREAD_SELF. */
    WitU32 ContextFlags;
    WitU32 Reserved;
} WitUserThreadInfo;

WIT_STATIC_ASSERT(sizeof(WitUserThreadInfo) == WIT_THREAD_INFO_SIZE, "Thread information ABI");
#endif
