#ifndef WITOS_FATAL_INFO_H
#define WITOS_FATAL_INFO_H
#include "thread_context.h"
#define WIT_FATAL_INFO_VERSION 1U
#define WIT_FATAL_INFO_SIZE (WIT_THREAD_CONTEXT_SIZE + 152U)
#define WIT_FATAL_PARAMETER_CAPACITY 15U

/* Opaque diagnostic code/parameters. Context is evidence, never resume authority. */
typedef struct WitUserFatalInfo {
    WitU32 Version, Size, Code, ExceptionFlags;
    WitU64 Address;
    WitU32 ParameterCount, NativeContextFlags;
    WitU64 Parameters[WIT_FATAL_PARAMETER_CAPACITY];
    WitThreadContext Context;
} WitUserFatalInfo;

WIT_STATIC_ASSERT(sizeof(WitUserFatalInfo) == WIT_FATAL_INFO_SIZE, "Fatal diagnostic snapshot ABI");
#endif
