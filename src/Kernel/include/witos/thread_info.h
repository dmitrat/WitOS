#ifndef WITOS_THREAD_INFO_H
#define WITOS_THREAD_INFO_H
#include "types.h"
#define WIT_THREAD_INFO_VERSION 6U
#define WIT_THREAD_INFO_SIZE 80U
/* Version 5, still served: the record without Processor (ABI-1 1.4 appended it, P1.4). */
#define WIT_THREAD_INFO_VERSION_5 5U
#define WIT_THREAD_INFO_SIZE_5 72U
/* State of the queried thread. */
#define WIT_THREAD_STATE_RUNNING 1U
#define WIT_THREAD_STATE_READY 2U
#define WIT_THREAD_STATE_WAITING 3U
#define WIT_THREAD_STATE_SUSPENDED 4U
#define WIT_THREAD_STATE_EXITED 5U

/* One atomic kernel snapshot of a thread, by handle or WIT_THREAD_SELF (THREAD_QUERY). Stack bounds are
 * low-inclusive, high-exclusive, excluding guard pages; an exited thread keeps its identity and exit code and reports
 * zero bounds. Version 6 (P1.4, ABI-1 1.4) appended Processor, the processor that runs the thread or ran it last;
 * version 5 (K8.4b) left out version 4's compiler TLS fields and native id; addresses grant no
 * authority. ContextFlags are the WIT_THREAD_CONTEXT_* flags a context of the thread would carry. */
typedef struct WitUserThreadInfo {
    WitU32 Version;
    WitU32 Size;
    WitU64 ThreadId; /* Generation-bearing identity; never a capability. */
    WitU64 StackLow;
    WitU64 StackHigh;
    WitU64 TlsBase; /* FS on x64, TPIDRRO_EL0 on ARM64; zero for none. */
    WitU32 ProcessId;
    WitU32 ProcessorCount;
    WitU32 State;
    WitU32 SuspendCount;
    WitU64 ExitCode;
    WitU32 Rights; /* Rights of the handle used; every thread right for WIT_THREAD_SELF. */
    WitU32 ContextFlags;
    WitU32 Processor; /* the processor that runs the thread, or ran it last (version 6, P1.4) */
    WitU32 Reserved;
} WitUserThreadInfo;

WIT_STATIC_ASSERT(sizeof(WitUserThreadInfo) == WIT_THREAD_INFO_SIZE, "Thread information ABI");
#endif
