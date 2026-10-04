#ifndef WITOS_CPU_CONTEXT_INFO_H
#define WITOS_CPU_CONTEXT_INFO_H
#include "types.h"
#define WIT_CPU_CONTEXT_VERSION 2U
#define WIT_CPU_DEBUG_DISABLED 1U
#define WIT_CPU_CONTEXT_X87 1ULL
#define WIT_CPU_CONTEXT_SSE 2ULL
#define WIT_CPU_CONTEXT_CET 4ULL
#define WIT_CPU_CONTEXT_LEGACY (WIT_CPU_CONTEXT_X87 | WIT_CPU_CONTEXT_SSE)

typedef struct WitCpuContextInfo {
    WitU32 Version, Size;
    WitU64 EnabledState;
    WitU32 LegacySaveBytes;
    WitU16 CodeSelector, StackSelector;
    WitU32 DebugPolicy, MxcsrMask;
} WitCpuContextInfo;

WIT_STATIC_ASSERT(sizeof(WitCpuContextInfo) == 32, "CPU context profile ABI");
#endif
