#ifndef WITOS_THREAD_INFO_H
#define WITOS_THREAD_INFO_H
#include "types.h"
#define WIT_THREAD_INFO_VERSION 3U
#define WIT_THREAD_INFO_SIZE 72U
/* Kernel-owned current-thread snapshot; stack bounds are low-inclusive,
 * high-exclusive, excluding guard pages. TLS addresses grant no authority. */
typedef struct WitUserThreadInfo {
    WitU32 Version;
    WitU32 Size;
    WitU64 ThreadId; /* Generation-bearing ownership identity, not native DWORD ID. */
    WitU64 StackLow;
    WitU64 StackHigh;
    WitU64 RawTls;
    WitU64 CompilerTls; /* Main-module compiler TLS readiness/base; zero if absent. */
    WitU32 ProcessId;
    WitU32 ProcessorCount;
    WitU32 NativeId; /* Kernel-wide monotonically allocated DWORD; never a capability. */
    WitU32 Reserved;
    WitU64 CompilerTlsHeader; /* Actual compiler vector/header, possibly DLL-only. */
} WitUserThreadInfo;
WIT_STATIC_ASSERT(sizeof(WitUserThreadInfo) == WIT_THREAD_INFO_SIZE, "Thread information ABI");
#endif
