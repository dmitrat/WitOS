#ifndef WITOS_CPU_CONTEXT_INFO_H
#define WITOS_CPU_CONTEXT_INFO_H
#include "types.h"
#define WIT_CPU_CONTEXT_VERSION 2U
#define WIT_CPU_DEBUG_DISABLED 1U
#define WIT_CPU_CONTEXT_X87 1ULL
#define WIT_CPU_CONTEXT_SSE 2ULL
#define WIT_CPU_CONTEXT_CET 4ULL
#define WIT_CPU_CONTEXT_FPSIMD 8ULL
#define WIT_CPU_CONTEXT_LEGACY (WIT_CPU_CONTEXT_X87 | WIT_CPU_CONTEXT_SSE)

/* CONTEXT_PROFILE (RFC 0011 section 7.3): which register block a thread context carries and which floating-point
 * state. x64 reports LEGACY (the FXSAVE64 image of 512 bytes), the user code and stack selectors and the MXCSR mask;
 * ARM64 reports FPSIMD (the 32 vector registers, 512 bytes), zero selectors and the FPCR bits the hardware
 * implements. Hardware debug is disabled for user mode on both. */
typedef struct WitCpuContextInfo {
    WitU32 Version, Size;
    WitU64 EnabledState;
    WitU32 LegacySaveBytes;
    WitU16 CodeSelector, StackSelector;
    WitU32 DebugPolicy, FloatControlMask;
} WitCpuContextInfo;

WIT_STATIC_ASSERT(sizeof(WitCpuContextInfo) == 32, "CPU context profile ABI");
#endif
