#include "fixture.h"
#include "witos/cpu_context_info.h"
#include "witos/exception.h"
#include "witos/thread_context.h"
#include "witos/thread_reference.h"

/* Exception fixture over the fault callback and the thread contexts of ABI-1 (RFC 0011 v3 sections 7.3 and 7.5, plan
 * steps K1.4 and K8.3): EXCEPTION_REGISTER, a read fault at address zero delivered to the callback with the interrupted
 * context, EXCEPTION_QUERY and EXCEPTION_CONTINUE with a changed context, THREAD_ACTIVATE of the own thread through the
 * same callback, the alternate stack of the thread (THREAD_STACK_ALTERNATE, S3.1: refusals, a fault with no room below
 * the stack pointer delivered on it, BUSY while on it, cleared), CONTEXT_PROFILE and THREAD_CONTEXT_GET of the own
 * thread; the second mode rejects the fault. The faults and their landings are the assembly of exception_fault and
 * exception_fault_low below: a marked register the record must show and the continuation changes, and a stack pointer
 * with no room below it. The record lives in the data page, the transfer 1024 bytes above it and the state the main
 * thread shares with the callback at 2048. */

#define ALTERNATE_BYTES 16384U
#define MARK 0x1122334455667788ULL
#define CHANGED 0x5A5AULL
#define ACTIVATION_ARGUMENT 0x77U
#define LOW_STACK (WIT_USER_STACK_BOTTOM + 16)

typedef struct State {
    WitU64 Activations, Mode, AlternateBase, SavedSp;
} State;

#define RECORD ((volatile WitUserExceptionInfo *)WIT_USER_DATA)
#define TRANSFER ((volatile WitUserExceptionTransfer *)(WIT_USER_DATA + 1024))
#define STATE ((volatile State *)(WIT_USER_DATA + 2048))

#if defined(__x86_64__)
#define FAULT_VECTOR 14U
#define FAULT_ERROR 4U /* a user read of a missing page */
#define CONTEXT_PROFILE WIT_THREAD_CONTEXT_FXSAVE64
#define ENABLED_STATE WIT_CPU_CONTEXT_LEGACY
#define CONTEXT_PC(c) ((c)->Rip)
#define CONTEXT_SP(c) ((c)->Rsp)
#define CONTEXT_MARKED(c) ((c)->R12)
/* The fault callback is entered with the token, the vector and the address in the SysV argument registers (K8.4d) and
 * no return address. */
#define CALLBACK __attribute__((force_align_arg_pointer, noreturn))
#else
#define FAULT_VECTOR 0x24U /* a data abort from EL0 */
#define FAULT_ERROR 0x92000006U /* its translation-fault syndrome */
#define CONTEXT_PROFILE WIT_THREAD_CONTEXT_FPSIMD
#define ENABLED_STATE WIT_CPU_CONTEXT_FPSIMD
#define CONTEXT_PC(c) ((c)->Pc)
#define CONTEXT_SP(c) ((c)->Sp)
#define CONTEXT_MARKED(c) ((c)->X[22])
#define CALLBACK __attribute__((noreturn))
#endif

/* The assembly below, hidden like everything of one image, so that references need no GOT. */
#define ASSEMBLY __attribute__((visibility("hidden")))
/* Reads address zero with the marked register set to MARK; returns the marked register at the landing. */
ASSEMBLY WitU64 exception_fault(void);
/* Reads address zero with the stack pointer at low_sp, its own saved at *saved; returns at the second landing. */
ASSEMBLY void exception_fault_low(WitU64 low_sp, volatile WitU64 *saved);
/* A system call made with the stack pointer at stack. */
ASSEMBLY WitU64 exception_call_on(WitU64 stack, WitU64 number, WitU64 a0, WitU64 a1);
ASSEMBLY extern const char exception_fault_site[], exception_landing[], exception_fault_site2[], exception_landing2[];

#if defined(__x86_64__)
__asm__(".text\n"
        ".globl exception_fault\n.hidden exception_fault\n"
        "exception_fault:\n"
        "    push %r12\n"
        "    movabs $0x1122334455667788, %r12\n"
        "    xor %eax, %eax\n"
        ".globl exception_fault_site\n.hidden exception_fault_site\n"
        "exception_fault_site:\n"
        "    mov (%rax), %rax\n"
        "    ud2\n"
        ".globl exception_landing\n.hidden exception_landing\n"
        "exception_landing:\n"
        "    mov %r12, %rax\n"
        "    pop %r12\n"
        "    ret\n"
        ".globl exception_fault_low\n.hidden exception_fault_low\n"
        "exception_fault_low:\n"
        "    mov %rsp, (%rsi)\n"
        "    mov %rdi, %rsp\n"
        "    xor %eax, %eax\n"
        ".globl exception_fault_site2\n.hidden exception_fault_site2\n"
        "exception_fault_site2:\n"
        "    mov (%rax), %rax\n"
        "    ud2\n"
        ".globl exception_landing2\n.hidden exception_landing2\n"
        "exception_landing2:\n"
        "    ret\n"
        ".globl exception_call_on\n.hidden exception_call_on\n"
        "exception_call_on:\n"
        "    mov %rsp, %r8\n"
        "    mov %rdi, %rsp\n"
        "    mov %rsi, %rax\n"
        "    mov %rdx, %rdi\n"
        "    mov %rcx, %rsi\n"
        "    xor %edx, %edx\n"
        "    syscall\n"
        "    mov %r8, %rsp\n"
        "    ret\n");
#else
__asm__(".text\n"
        ".globl exception_fault\n.hidden exception_fault\n"
        "exception_fault:\n"
        "    stp x22, x30, [sp, #-16]!\n"
        "    movz x22, #0x7788\n"
        "    movk x22, #0x5566, lsl #16\n"
        "    movk x22, #0x3344, lsl #32\n"
        "    movk x22, #0x1122, lsl #48\n"
        "    mov x9, #0\n"
        ".globl exception_fault_site\n.hidden exception_fault_site\n"
        "exception_fault_site:\n"
        "    ldr x9, [x9]\n"
        "    brk #0\n"
        ".globl exception_landing\n.hidden exception_landing\n"
        "exception_landing:\n"
        "    mov x0, x22\n"
        "    ldp x22, x30, [sp], #16\n"
        "    ret\n"
        ".globl exception_fault_low\n.hidden exception_fault_low\n"
        "exception_fault_low:\n"
        "    mov x9, sp\n"
        "    str x9, [x1]\n"
        "    mov sp, x0\n"
        "    mov x9, #0\n"
        ".globl exception_fault_site2\n.hidden exception_fault_site2\n"
        "exception_fault_site2:\n"
        "    ldr x9, [x9]\n"
        "    brk #0\n"
        ".globl exception_landing2\n.hidden exception_landing2\n"
        "exception_landing2:\n"
        "    ret\n"
        ".globl exception_call_on\n.hidden exception_call_on\n"
        "exception_call_on:\n"
        "    mov x9, sp\n"
        "    mov sp, x0\n"
        "    mov x8, x1\n"
        "    mov x0, x2\n"
        "    mov x1, x3\n"
        "    mov x2, #0\n"
        "    svc #0\n"
        "    mov sp, x9\n"
        "    ret\n");
#endif

/* The transfer: version, size, the current token and the record's context, copied a byte at a time through volatile
 * pointers, since a fixture has no memcpy. */
static void build_transfer(WitU64 token)
{
    TRANSFER->Version = WIT_EXCEPTION_TRANSFER_VERSION;
    TRANSFER->Size = sizeof(WitUserExceptionTransfer);
    TRANSFER->RetireThroughToken = token;
    const volatile WitU8 *from = (const volatile WitU8 *)&RECORD->Context;
    volatile WitU8 *to = (volatile WitU8 *)&TRANSFER->Context;
    for (WitU32 i = 0; i < sizeof(WitThreadContext); ++i) {
        to[i] = from[i];
    }
}

static WIT_NORETURN void continue_transfer(WitU64 token)
{
    WitU64 result;
    wit_syscall(WIT_CALL_EXCEPTION_CONTINUE, token, (WitU64)TRANSFER, sizeof(WitUserExceptionTransfer), &result);
    fixture_failed(60);
}

static void activation_target(WitU64 argument)
{
    fixture_check(argument == ACTIVATION_ARGUMENT, 61);
    ++STATE->Activations;
}

/* The fault callback: the token, the vector and the address, on this thread's stack below the interrupted frame or on
 * its alternate stack. */
static CALLBACK void callback(WitU64 token, WitU64 vector, WitU64 address)
{
    /* A foreign token is unknown and the record is read with its exact size only. */
    fixture_expect(
        WIT_CALL_EXCEPTION_QUERY, token + 1, WIT_USER_DATA, sizeof(WitUserExceptionInfo), WIT_STATUS_BAD_HANDLE);
    fixture_expect(
        WIT_CALL_EXCEPTION_QUERY, token, WIT_USER_DATA, sizeof(WitUserExceptionInfo) - 1, WIT_STATUS_INVALID_ARGUMENT);
    fixture_expect(WIT_CALL_EXCEPTION_QUERY, token, WIT_USER_DATA, sizeof(WitUserExceptionInfo), WIT_STATUS_OK);
    fixture_check(RECORD->Version == WIT_EXCEPTION_VERSION && RECORD->Size == sizeof(WitUserExceptionInfo), 40);
    fixture_check(RECORD->Token == token && RECORD->Vector == vector && RECORD->Address == address, 41);
    fixture_check(RECORD->Context.State == WIT_THREAD_CONTEXT_RUNNING &&
            RECORD->Context.Flags == (CONTEXT_PROFILE | WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE),
        42);
    if (vector == WIT_EXCEPTION_ACTIVATION_VECTOR) {
        /* The activation record: Address is the callback and Error its argument; the callback runs here and the
         * context then continues as it was. */
        fixture_check(address == (WitU64)activation_target && RECORD->Error == ACTIVATION_ARGUMENT, 43);
        ((void (*)(WitU64))RECORD->Address)(RECORD->Error);
        build_transfer(token);
        continue_transfer(token);
    }
    /* A read at address zero: the ISA's vector and syndrome, the interrupted PC at the faulting instruction. */
    fixture_check(vector == FAULT_VECTOR && RECORD->Error == FAULT_ERROR && address == 0, 44);
    if (CONTEXT_PC(&RECORD->Context) == (WitU64)exception_fault_site2) {
        /* The second fault had no room below its stack pointer: the callback runs on the alternate stack, the record
         * keeps the interrupted stack pointer, and the context continues at the second landing with the saved one. */
        const WitU64 here = (WitU64)&token;
        fixture_check(here >= STATE->AlternateBase && here < STATE->AlternateBase + ALTERNATE_BYTES, 45);
        fixture_check(CONTEXT_SP(&RECORD->Context) == LOW_STACK, 46);
        build_transfer(token);
        CONTEXT_PC(&TRANSFER->Context) = (WitU64)exception_landing2;
        CONTEXT_SP(&TRANSFER->Context) = STATE->SavedSp;
        continue_transfer(token);
    }
    fixture_check(CONTEXT_PC(&RECORD->Context) == (WitU64)exception_fault_site, 47);
    fixture_check(CONTEXT_MARKED(&RECORD->Context) == MARK, 48);
#if defined(__aarch64__)
    fixture_check(!(RECORD->Context.Pstate & 0x0FFFFFFFU), 49); /* the condition flags alone */
#endif
    if (STATE->Mode == WIT_EXCEPTION_TEST_REJECT) {
        WitU64 result;
        wit_syscall(WIT_CALL_EXCEPTION_REJECT, token, 0, 0, &result);
        fixture_failed(50);
    }
    /* Continue at the landing with the marked register changed; a transfer of the wrong size, a foreign token and a
     * context with a privileged state are refused first. */
    build_transfer(token);
    CONTEXT_PC(&TRANSFER->Context) = (WitU64)exception_landing;
    CONTEXT_MARKED(&TRANSFER->Context) = CHANGED;
    fixture_expect(WIT_CALL_EXCEPTION_CONTINUE, token, (WitU64)TRANSFER, sizeof(WitUserExceptionTransfer) - 1,
        WIT_STATUS_INVALID_ARGUMENT);
    fixture_expect(WIT_CALL_EXCEPTION_CONTINUE, token + 1, (WitU64)TRANSFER, sizeof(WitUserExceptionTransfer),
        WIT_STATUS_BAD_HANDLE);
#if defined(__x86_64__)
    const WitU64 flags = TRANSFER->Context.Rflags;
    TRANSFER->Context.Rflags = flags | 0x3000; /* IOPL 3 */
#else
    const WitU64 flags = TRANSFER->Context.Pstate;
    TRANSFER->Context.Pstate = flags | 0x4; /* M[2]: EL1t */
#endif
    fixture_expect(WIT_CALL_EXCEPTION_CONTINUE, token, (WitU64)TRANSFER, sizeof(WitUserExceptionTransfer),
        WIT_STATUS_INVALID_ARGUMENT);
#if defined(__x86_64__)
    TRANSFER->Context.Rflags = flags;
#else
    TRANSFER->Context.Pstate = flags;
#endif
    continue_transfer(token);
}

static void alternate(WitThreadAlternateStackRequest *request, WitU32 size, WitU64 expected)
{
    fixture_expect(WIT_CALL_THREAD_STACK_ALTERNATE, (WitU64)request, size, 0, expected);
}

FIXTURE_ENTRY void wit_user_start(const WitRootStartup *startup)
{
    const WitUserTestConfig *config = fixture_config(startup);
    WitThreadAlternateStackRequest request;
    WitCpuContextInfo profile;
    fixture_check(startup->AbiVersion == WIT_ABI_VERSION, 1);
    STATE->Activations = 0;
    STATE->Mode = config->Mode;

    /* Register the callback: a data address is rejected, then the callback is installed. */
    fixture_expect(WIT_CALL_EXCEPTION_REGISTER, WIT_USER_DATA, WIT_EXCEPTION_VERSION, 0, WIT_STATUS_BAD_ADDRESS);
    fixture_expect(WIT_CALL_EXCEPTION_REGISTER, (WitU64)callback, WIT_EXCEPTION_VERSION, 0, WIT_STATUS_OK);
    /* The fault, continued at the landing with the marked register changed. */
    fixture_check(exception_fault() == CHANGED, 2);

    /* The activation of the own thread: its callback runs through the fault callback before this call returns. */
    fixture_expect(
        WIT_CALL_THREAD_ACTIVATE, WIT_THREAD_SELF, (WitU64)activation_target, ACTIVATION_ARGUMENT, WIT_STATUS_OK);
    fixture_check(STATE->Activations == 1, 3);

    /* The alternate stack (S3.1): 16 KiB committed at the start of a 64 KiB reservation, refused whole before anything
     * changes, then installed. */
    const WitU64 base = fixture_expect(WIT_CALL_MEMORY_RESERVE, 65536, 4096, 0, WIT_STATUS_OK);
    STATE->AlternateBase = base;
    fixture_expect(WIT_CALL_MEMORY_COMMIT, base, ALTERNATE_BYTES, WIT_MEMORY_READ | WIT_MEMORY_WRITE, WIT_STATUS_OK);
    request.Version = 2;
    request.Size = sizeof(request);
    request.Base = base;
    request.Bytes = ALTERNATE_BYTES;
    alternate(&request, sizeof(request), WIT_STATUS_UNSUPPORTED);
    request.Version = WIT_THREAD_ALTERNATE_STACK_VERSION;
    alternate(&request, sizeof(request) - 1, WIT_STATUS_INVALID_ARGUMENT);
    request.Base = base + 8; /* unaligned */
    alternate(&request, sizeof(request), WIT_STATUS_INVALID_ARGUMENT);
    request.Base = base;
    request.Bytes = 4080; /* too small */
    alternate(&request, sizeof(request), WIT_STATUS_INVALID_ARGUMENT);
    request.Bytes = ALTERNATE_BYTES;
    request.Base = WIT_USER_STACK_BOTTOM; /* the thread's own stack */
    alternate(&request, sizeof(request), WIT_STATUS_INVALID_ARGUMENT);
    request.Base = 0xFFFF800000000000ULL; /* not a user address */
    alternate(&request, sizeof(request), WIT_STATUS_BAD_ADDRESS);
    request.Base = base + ALTERNATE_BYTES; /* reserved, not committed */
    alternate(&request, sizeof(request), WIT_STATUS_BAD_ADDRESS);
    request.Base = base;
    alternate(&request, sizeof(request), WIT_STATUS_OK);
    /* A fault with no room below the stack pointer: delivered on the alternate stack, continued at the second landing
     * with the saved stack pointer. */
    exception_fault_low(LOW_STACK, &STATE->SavedSp);
    /* On the alternate stack the thread cannot change it; off it, the stack is cleared. */
    fixture_check(exception_call_on(base + ALTERNATE_BYTES - 64, WIT_CALL_THREAD_STACK_ALTERNATE, (WitU64)&request,
                      sizeof(request)) == WIT_STATUS_BUSY,
        4);
    request.Base = 0;
    request.Bytes = 0;
    alternate(&request, sizeof(request), WIT_STATUS_OK);

    /* CONTEXT_PROFILE: the ISA's block and floating-point state. */
    fixture_expect(WIT_CALL_CONTEXT_PROFILE, (WitU64)&profile, sizeof(profile), WIT_CPU_CONTEXT_VERSION, WIT_STATUS_OK);
    fixture_check(profile.Version == WIT_CPU_CONTEXT_VERSION &&
            profile.Size == sizeof(profile) &&
            profile.EnabledState == ENABLED_STATE,
        5);
    /* The own context: running, the ISA's block, the fixed stack bounds; the running thread's context cannot be set. */
    volatile WitThreadContext *own = (volatile WitThreadContext *)WIT_USER_DATA;
    fixture_expect(WIT_CALL_THREAD_CONTEXT_GET, WIT_THREAD_SELF, (WitU64)own, sizeof(WitThreadContext), WIT_STATUS_OK);
    fixture_check(own->State == WIT_THREAD_CONTEXT_RUNNING && own->Flags == CONTEXT_PROFILE, 6);
    fixture_check(own->StackLow == WIT_USER_STACK_BOTTOM && own->StackHigh == WIT_USER_STACK_TOP, 7);
    fixture_expect(
        WIT_CALL_THREAD_CONTEXT_SET, WIT_THREAD_SELF, (WitU64)own, sizeof(WitThreadContext), WIT_STATUS_BUSY);
    fixture_exit(WIT_TEST_EXIT_CODE);
}
