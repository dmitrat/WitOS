#include "x64.h"
#include "user.h"
#include "witos/platform.h"
unsigned __int64 __readcr0(void);
unsigned __int64 __readcr4(void);
unsigned __int64 __readmsr(unsigned long);
unsigned __int64 __readdr(unsigned int);
void __writedr(unsigned int, unsigned __int64);
void __cpuid(int[4], int);
#pragma intrinsic(__readcr0, __readcr4, __readmsr, __readdr, __writedr, __cpuid)
static WitU32 mxcsr_mask;

WitU32 wit_x64_mxcsr_mask(void)
{
    return mxcsr_mask;
}

static int profile(WitU64 cr0, WitU64 cr4, WitU64 efer)
{
    return !(efer & (1ULL << 14)) &&
        !(cr0 & ((1ULL << 2) | (1ULL << 3))) &&
        (cr4 & (1ULL << 9)) &&
        !(cr4 & ((1ULL << 16) | (1ULL << 18) | (1ULL << 22) | (1ULL << 23) | (1ULL << 25)));
}

static int debug_disabled(void)
{
    return !(__readdr(7) & ~0x400ULL) && !__readdr(0) && !__readdr(1) && !__readdr(2) && !__readdr(3);
}

int wit_arch_context_supported(void)
{
    return profile(__readcr0(), __readcr4(), __readmsr(0xC0000080)) && debug_disabled();
}

void wit_x64_context_profile_initialize(void)
{
    int cpu[4];
    __cpuid(cpu, 1);
    if ((cpu[3] & ((1 << 24) | (1 << 25) | (1 << 26))) != ((1 << 24) | (1 << 25) | (1 << 26)) ||
        !profile(__readcr0(), __readcr4(), __readmsr(0xC0000080))) {
        wit_panic("Unsupported CPU context preservation profile");
    }
    if (__readdr(7) & ~0x400ULL) {
        wit_panic("Inherited hardware debug control is unsupported");
    }
    __writedr(0, 0);
    __writedr(1, 0);
    __writedr(2, 0);
    __writedr(3, 0);
    if (!debug_disabled()) {
        wit_panic("Hardware breakpoints were not disabled");
    }
    __declspec(align(16)) WitU8 legacy[512] = {0};
    wit_x64_fxsave(legacy);
    mxcsr_mask = *(const WitU32 *)&legacy[28];
    if (!mxcsr_mask) {
        mxcsr_mask = 0xFFBF; // Architectural fallback when hardware reports zero.
    }
    if ((mxcsr_mask & 0xFFFF0000U) || (mxcsr_mask & 0x1F80U) != 0x1F80U) {
        wit_panic("Invalid hardware MXCSR mask");
    }
}

#if defined(WITOS_SELFTEST)
void wit_x64_context_profile_self_test(void)
{
    const WitU64 baseline = 1ULL << 9;
    const WitU64 unsupported[] = {0, baseline | (1ULL << 16), baseline | (1ULL << 18), baseline | (1ULL << 22),
        baseline | (1ULL << 23), baseline | (1ULL << 25)};
    if (!profile(0, baseline, 0) ||
        profile(1ULL << 2, baseline, 0) ||
        profile(1ULL << 3, baseline, 0) ||
        profile(0, baseline, 1ULL << 14)) {
        wit_panic("CPU context profile predicate failed");
    }
    for (WitU32 i = 0; i < sizeof(unsupported) / sizeof(unsupported[0]); ++i) {
        if (profile(0, unsupported[i], 0)) {
            wit_panic("Unsupported context mode accepted");
        }
    }
    wit_x64_context_copy_self_test();
    wit_user_suspend_deadline_self_test();
    wit_console_write("[TEST-PASS] Cpu.ContextStateProfile\n");
}
#endif

void wit_arch_cpu_context_describe(WitCpuContextInfo *info, const WitArchFrame *frame)
{
    if (!frame || !wit_arch_frame_returns_to_user(frame)) {
        wit_panic("CPU profile lost current user selectors");
    }
    info->Version = WIT_CPU_CONTEXT_VERSION;
    info->Size = sizeof(*info);
    info->EnabledState = WIT_CPU_CONTEXT_LEGACY;
    info->DebugPolicy = WIT_CPU_DEBUG_DISABLED;
    info->MxcsrMask = mxcsr_mask;
    info->LegacySaveBytes = sizeof(frame->FxState);
    info->CodeSelector = (WitU16)frame->Cs;
    info->StackSelector = (WitU16)frame->Ss;
}
