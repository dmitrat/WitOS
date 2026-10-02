#include "user.h"
#include "witos/platform.h"

/* x64 conversion between saved frames and the WitThreadContext ABI layout (FXSAVE64 profile). */

static void copy_fx(WitU8 *output, const WitU8 *input)
{
    for (WitU32 i = 0; i < 512; ++i) {
        output[i] = 0;
    }
    // REX.W FXSAVE: FCW/FSW/FTW, FOP, 64-bit FIP/FDP, MXCSR/mask.
    for (WitU32 i = 0; i < 5; ++i) {
        output[i] = input[i];
    }
    for (WitU32 i = 6; i < 32; ++i) {
        output[i] = input[i];
    }
    *(WitU32 *)&output[28] = wit_x64_mxcsr_mask();
    output[7] &= 7; // FOP bits 15:11 are reserved.
    for (WitU32 reg = 0; reg < 8; ++reg) {
        for (WitU32 i = 0; i < 10; ++i) {
            output[32 + reg * 16 + i] = input[32 + reg * 16 + i];
        }
    }
    for (WitU32 i = 160; i < 416; ++i) {
        output[i] = input[i]; // XMM0-15.
    }
}

void wit_x64_context_copy_self_test(void)
{
    WitU8 source[512], destination[512];
    for (WitU32 i = 0; i < 512; ++i) {
        source[i] = 0xA5;
        destination[i] = 0xCC;
    }
    copy_fx(destination, source);
    for (WitU32 i = 0; i < 512; ++i) {
        const int defined =
            i < 5 || (i >= 6 && i < 32) || (i >= 32 && i < 160 && (i - 32) % 16 < 10) || (i >= 160 && i < 416);
        const WitU8 expected = i >= 28 && i < 32 ? (WitU8)(wit_x64_mxcsr_mask() >> (8 * (i - 28)))
            : i == 7                             ? 5
            : defined                            ? 0xA5
                                                 : 0;
        if (destination[i] != expected || source[i] != 0xA5) {
            wit_panic("FXSAVE context sanitization failed");
        }
    }
    wit_console_write("[TEST-PASS] Cpu.ContextSanitization\n");
}

WitU32 wit_arch_context_profile(void)
{
    return WIT_THREAD_CONTEXT_FXSAVE64;
}

void wit_arch_context_describe(WitThreadContext *context)
{
    *(WitU32 *)&context->FxState[28] = wit_x64_mxcsr_mask();
}

void wit_arch_context_capture(WitThreadContext *context, const WitArchFrame *frame)
{
    context->Rip = frame->Rip;
    context->Rsp = frame->Rsp;
    context->Rflags = frame->Rflags;
    context->Cs = frame->Cs;
    context->Ss = frame->Ss;
    context->Rax = frame->Rax;
    context->Rbx = frame->Rbx;
    context->Rcx = frame->Rcx;
    context->Rdx = frame->Rdx;
    context->Rbp = frame->Rbp;
    context->Rsi = frame->Rsi;
    context->Rdi = frame->Rdi;
    context->R8 = frame->R8;
    context->R9 = frame->R9;
    context->R10 = frame->R10;
    context->R11 = frame->R11;
    context->R12 = frame->R12;
    context->R13 = frame->R13;
    context->R14 = frame->R14;
    context->R15 = frame->R15;
    copy_fx(context->FxState, frame->FxState);
}

int wit_arch_context_registers_valid(const WitThreadContext *context)
{
    return context->Cs == WIT_USER_CS &&
        context->Ss == WIT_USER_SS &&
        !(context->Rflags & ~WIT_CONTEXT_USER_FLAGS) &&
        (context->Rflags & 0x202) == 0x202;
}

int wit_arch_context_state_valid(const WitThreadContext *context)
{
    if (!wit_x64_mxcsr_mask() || (*(const WitU32 *)&context->FxState[24] & ~wit_x64_mxcsr_mask())) {
        return 0;
    }
    WitU8 canonical[512];
    copy_fx(canonical, context->FxState);
    for (WitU32 i = 0; i < 512; ++i) {
        if (canonical[i] != context->FxState[i]) {
            return 0;
        }
    }
    return 1;
}

WitU64 wit_arch_context_pc(const WitThreadContext *context)
{
    return context->Rip;
}

WitU64 wit_arch_context_sp(const WitThreadContext *context)
{
    return context->Rsp;
}

void wit_arch_context_apply(WitArchFrame *frame, const WitThreadContext *context)
{
    frame->Rip = context->Rip;
    frame->Rsp = context->Rsp;
    frame->Rflags = context->Rflags;
    frame->Cs = context->Cs;
    frame->Ss = context->Ss;
    frame->Rax = context->Rax;
    frame->Rbx = context->Rbx;
    frame->Rcx = context->Rcx;
    frame->Rdx = context->Rdx;
    frame->Rbp = context->Rbp;
    frame->Rsi = context->Rsi;
    frame->Rdi = context->Rdi;
    frame->R8 = context->R8;
    frame->R9 = context->R9;
    frame->R10 = context->R10;
    frame->R11 = context->R11;
    frame->R12 = context->R12;
    frame->R13 = context->R13;
    frame->R14 = context->R14;
    frame->R15 = context->R15;
    copy_fx(frame->FxState, context->FxState);
}
