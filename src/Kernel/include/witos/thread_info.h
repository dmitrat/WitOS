#ifndef WITOS_THREAD_INFO_H
#define WITOS_THREAD_INFO_H
#include "types.h"
#define WIT_THREAD_INFO_VERSION 1U
#define WIT_THREAD_INFO_SIZE 56U
/* Kernel-owned current-thread snapshot; stack bounds are low-inclusive,
 * high-exclusive, excluding guard pages. TLS addresses grant no authority. */
typedef struct WitUserThreadInfo {
    WitU32 Version;
    WitU32 Size;
    WitU64 ThreadId;
    WitU64 StackLow;
    WitU64 StackHigh;
    WitU64 RawTls;
    WitU64 CompilerTls;
    WitU32 ProcessId;
    WitU32 ProcessorCount;
} WitUserThreadInfo;
WIT_STATIC_ASSERT(sizeof(WitUserThreadInfo) == WIT_THREAD_INFO_SIZE, "Thread information ABI");
#endif
