#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/manager.h"
#include "witos/exception.h"
#include "witos/thread_context.h"
#include "witos/thread_reference.h"
#include "witos/wait_objects.h"
#include <errno.h>
#include <signal.h>
#include <string.h>
#include "ksigaction.h"

/* Signals (plan step S3, RFC 0011 sections 7.5 and 9.3). The kernel knows faults and activations: a fault or an
 * activation enters the process's one fault callback on the interrupted thread with a token, and the callback reads
 * the record (vector, error, address, the interrupted context) and continues a context of its choice. The libc makes
 * POSIX signals of that. A synchronous signal (SIGSEGV, SIGBUS, SIGFPE, SIGILL, SIGTRAP) is a fault classified by its
 * vector; pthread_kill, raise and tkill are THREAD_ACTIVATE of the target thread with the marker callback below and
 * the signal number as the argument. The disposition table, the per-thread masks, pending sets and alternate stacks
 * are the library's. To run a handler the callback builds a frame (the saved kernel context, a ucontext_t, a
 * siginfo_t) on the thread's stack or on its alternate stack, then continues the interrupted context changed to enter
 * the trampoline with the frame: the delivery is retired before the handler runs, so a handler may fault, longjmp out
 * or be interrupted itself. The trampoline calls the handler and then __wit_sigreturn, which carries the handler's
 * changes to the general registers back into the saved context, restores the mask and resumes the saved context
 * through a delivery of its own (plan step K8.1; RFC 0011 section 8: THREAD_CONTEXT_RESTORE leaves ABI-1): an
 * activation of the own thread with the sigreturn marker, whose delivery continues into the saved context, and
 * pending signals are re-armed as activations that follow it. Signals are per thread here: a process-directed kill
 * reaches the calling thread. */

#define SIGNALS 64
#define SIGNAL_BIT(sig) (1UL << ((sig) - 1))
#define UNBLOCKABLE (SIGNAL_BIT(SIGKILL) | SIGNAL_BIT(SIGSTOP))
#define OWN_PID 1

typedef struct Action {
    void (*Handler)(int);
    unsigned long Flags;
    unsigned long Mask;
} Action;

/* What a handler runs above: the delivery's record (its context is the interrupted one, resumed by sigreturn), the
 * transfer that enters the trampoline, the handler's ucontext_t and siginfo_t. About 2.6 KiB on x64 and 6.4 KiB
 * on ARM64, whose ucontext_t reserves 4 KiB for extension records; WIT_EXCEPTION_STACK_MINIMUM holds it. */
typedef struct SignalFrame {
    WitUserExceptionInfo Record;
    WitUserExceptionTransfer Transfer;
    ucontext_t Context;
    siginfo_t Info;
    WitSignalState *State;
} SignalFrame;

static Action actions[SIGNALS + 1];

void __wit_signal_entry(void); /* the kernel's fault callback, below */
void __wit_signal_trampoline(void); /* enters the handler with the frame, below */
void __wit_signal_activation(void); /* the marker callback THREAD_ACTIVATE carries */
void __wit_sigreturn_activation(void); /* the marker of sigreturn's own activation */
void __wit_signal_deliver(WitU64 token, WitU64 vector, WitU64 address);
void __wit_sigreturn(SignalFrame *frame);
__attribute__((__noreturn__)) void __wit_sigreturn_switch(WitU64 sp); /* raise_marker on the stack sp, below */
__attribute__((__noreturn__)) void __wit_sigreturn_raise(void);

#if defined(__x86_64__)
#define CONTEXT_PROFILE WIT_THREAD_CONTEXT_FXSAVE64
#define CONTEXT_SP(c) ((c)->Rsp)
#define CONTEXT_PC(c) ((c)->Rip)
#define RED_ZONE 128U /* the SysV bytes below the stack pointer a leaf function keeps */
#else
#define RED_ZONE 0U
#define CONTEXT_PROFILE WIT_THREAD_CONTEXT_FPSIMD
#define CONTEXT_SP(c) ((c)->Sp)
#define CONTEXT_PC(c) ((c)->Pc)
#endif

static WitU64 current_sp(void)
{
    return (WitU64)__builtin_frame_address(0);
}

static int on_alternate(const WitSignalState *state, WitU64 sp)
{
    return state->AlternateBytes &&
        sp >= (WitU64)state->AlternateBase &&
        sp < (WitU64)state->AlternateBase + state->AlternateBytes;
}

static __attribute__((__noreturn__)) void fail_fast(const char *text)
{
    WitU64 result = 0;
    wit_syscall(WIT_CALL_DEBUG_WRITE, __wit_process.Log, (WitU64)text, strlen(text), &result);
    for (;;) {
        wit_syscall(WIT_CALL_PROCESS_EXIT, WIT_EXIT_SIGNAL(SIGABRT), 0, 0, &result);
    }
}

/* The default action of a signal raised by tkill: ignored, or the process ends with WIT_EXIT_SIGNAL(sig), which waitpid
 * reports as a death by that signal (S6.1; a shell shows 128 + the signal); stop and continue signals are refused at
 * the raise. */
static int default_ignores(int sig)
{
    return sig == SIGCHLD || sig == SIGURG || sig == SIGWINCH || sig == SIGCONT;
}

/* Registration at startup (crt1): the fault callback, through which every signal arrives. */
void __wit_signal_init(void)
{
    WitU64 result = 0;
    if (wit_syscall(WIT_CALL_EXCEPTION_REGISTER, (WitU64)__wit_signal_entry, WIT_EXCEPTION_VERSION, 0, &result) !=
        WIT_STATUS_OK) {
        fail_fast("[LIBC] the fault callback could not be registered\n");
    }
}

/* The signal and its code of a fault record; zero for a fault no signal names. The kernel delivers these faults to
 * the callback (wit_arch_exception_deliverable): on x64 #DE, #BP, #UD, #GP and #PF; on ARM64 the unknown and
 * system-register classes, instruction and data aborts, PC and SP alignment and BRK. Any other fault ends the process
 * as the kernel's fault report, never reaching a handler. */
static int classify(
    WitU64 vector, WitU64 error, WitU64 address, const WitThreadContext *context, int *code, void **fault_address)
{
    *code = SI_KERNEL;
    *fault_address = (void *)CONTEXT_PC(context);
#if defined(__x86_64__)
    switch (vector) {
    case 0:
        *code = FPE_INTDIV;
        return SIGFPE;
    case 3:
        *code = TRAP_BRKPT;
        return SIGTRAP;
    case 6:
        *code = ILL_ILLOPN;
        return SIGILL;
    case 13:
        return SIGSEGV; /* a general protection fault: SI_KERNEL, as Linux reports it */
    case 14:
        *code = (error & 1) ? SEGV_ACCERR : SEGV_MAPERR; /* bit 0: the page was present */
        *fault_address = (void *)address;
        return SIGSEGV;
    default:
        return 0;
    }
#else
    const WitU64 status = error & 0x3F; /* the fault status code of an abort's syndrome */
    switch (vector) {
    case 0x00: /* an unknown reason: an undefined instruction */
        *code = ILL_ILLOPC;
        return SIGILL;
    case 0x18: /* a system register access EL0 may not make */
        *code = ILL_PRVOPC;
        return SIGILL;
    case 0x20: /* instruction abort */
    case 0x24: /* data abort */
        *fault_address = (void *)address;
        if (status == 0x21) {
            *code = BUS_ADRALN;
            return SIGBUS;
        }
        *code = (status >= 0x04 && status <= 0x07) ? SEGV_MAPERR /* translation */
            : (status >= 0x08 && status <= 0x0F)   ? SEGV_ACCERR /* access flag or permission */
                                                   : SI_KERNEL;
        return SIGSEGV;
    case 0x22: /* PC alignment */
    case 0x26: /* SP alignment */
        *fault_address = (void *)address;
        *code = BUS_ADRALN;
        return SIGBUS;
    case 0x3C: /* BRK */
        *code = TRAP_BRKPT;
        return SIGTRAP;
    default:
        return 0;
    }
#endif
}

/* The handler's view of the interrupted context, in the layout musl's signal.h gives this ISA. */
static void build_ucontext(
    ucontext_t *uc, const WitThreadContext *saved, const WitSignalState *state, unsigned long mask, void *fault_address)
{
    memset(uc, 0, sizeof(*uc));
    uc->uc_stack.ss_sp = state->AlternateBase;
    uc->uc_stack.ss_size = state->AlternateBytes;
    uc->uc_stack.ss_flags =
        on_alternate(state, CONTEXT_SP(saved)) ? SS_ONSTACK : (state->AlternateBytes ? 0 : SS_DISABLE);
    uc->uc_sigmask.__bits[0] = mask;
#if defined(__x86_64__)
    greg_t *g = uc->uc_mcontext.gregs;
    g[REG_R8] = (greg_t)saved->R8;
    g[REG_R9] = (greg_t)saved->R9;
    g[REG_R10] = (greg_t)saved->R10;
    g[REG_R11] = (greg_t)saved->R11;
    g[REG_R12] = (greg_t)saved->R12;
    g[REG_R13] = (greg_t)saved->R13;
    g[REG_R14] = (greg_t)saved->R14;
    g[REG_R15] = (greg_t)saved->R15;
    g[REG_RDI] = (greg_t)saved->Rdi;
    g[REG_RSI] = (greg_t)saved->Rsi;
    g[REG_RBP] = (greg_t)saved->Rbp;
    g[REG_RBX] = (greg_t)saved->Rbx;
    g[REG_RDX] = (greg_t)saved->Rdx;
    g[REG_RAX] = (greg_t)saved->Rax;
    g[REG_RCX] = (greg_t)saved->Rcx;
    g[REG_RSP] = (greg_t)saved->Rsp;
    g[REG_RIP] = (greg_t)saved->Rip;
    g[REG_EFL] = (greg_t)saved->Rflags;
    g[REG_CSGSFS] = (greg_t)(saved->Cs & 0xFFFF);
    g[REG_CR2] = (greg_t)(WitU64)fault_address;
    memcpy(uc->__fpregs_mem, saved->FxState, sizeof(saved->FxState)); /* the FXSAVE image is Linux's _fpstate */
    uc->uc_mcontext.fpregs = (fpregset_t)uc->__fpregs_mem;
#else
    mcontext_t *m = &uc->uc_mcontext;
    m->fault_address = (unsigned long)fault_address;
    for (int i = 0; i < 31; ++i) {
        m->regs[i] = saved->X[i];
    }
    m->sp = saved->Sp;
    m->pc = saved->Pc;
    m->pstate = saved->Pstate;
    struct fpsimd_context *fp = (struct fpsimd_context *)m->__reserved;
    fp->head.magic = FPSIMD_MAGIC;
    fp->head.size = sizeof(*fp);
    fp->fpsr = (unsigned)saved->Fpsr;
    fp->fpcr = (unsigned)saved->Fpcr;
    memcpy(fp->vregs, saved->V, sizeof(fp->vregs));
    /* The terminator record follows: a zero magic and size (the memset above). */
#endif
}

/* The handler's changes go back into the saved context, within what the kernel lets a user context carry. */
static void apply_ucontext(WitThreadContext *saved, const ucontext_t *uc)
{
#if defined(__x86_64__)
    const greg_t *g = uc->uc_mcontext.gregs;
    saved->R8 = (WitU64)g[REG_R8];
    saved->R9 = (WitU64)g[REG_R9];
    saved->R10 = (WitU64)g[REG_R10];
    saved->R11 = (WitU64)g[REG_R11];
    saved->R12 = (WitU64)g[REG_R12];
    saved->R13 = (WitU64)g[REG_R13];
    saved->R14 = (WitU64)g[REG_R14];
    saved->R15 = (WitU64)g[REG_R15];
    saved->Rdi = (WitU64)g[REG_RDI];
    saved->Rsi = (WitU64)g[REG_RSI];
    saved->Rbp = (WitU64)g[REG_RBP];
    saved->Rbx = (WitU64)g[REG_RBX];
    saved->Rdx = (WitU64)g[REG_RDX];
    saved->Rax = (WitU64)g[REG_RAX];
    saved->Rcx = (WitU64)g[REG_RCX];
    saved->Rsp = (WitU64)g[REG_RSP];
    saved->Rip = (WitU64)g[REG_RIP];
    saved->Rflags = ((WitU64)g[REG_EFL] & 0x200CD5ULL) | 0x202ULL;
    if (uc->uc_mcontext.fpregs == (fpregset_t)uc->__fpregs_mem) {
        memcpy(saved->FxState, uc->__fpregs_mem, sizeof(saved->FxState));
    }
#else
    const mcontext_t *m = &uc->uc_mcontext;
    for (int i = 0; i < 31; ++i) {
        saved->X[i] = m->regs[i];
    }
    saved->Sp = m->sp;
    saved->Pc = m->pc;
    saved->Pstate = m->pstate & WIT_CONTEXT_USER_PSTATE;
    const struct fpsimd_context *fp = (const struct fpsimd_context *)m->__reserved;
    if (fp->head.magic == FPSIMD_MAGIC && fp->head.size == sizeof(*fp)) {
        saved->Fpsr = fp->fpsr;
        saved->Fpcr = fp->fpcr;
        memcpy(saved->V, fp->vregs, sizeof(saved->V));
    }
#endif
}

static __attribute__((__noreturn__)) void reject(WitU64 token)
{
    WitU64 result = 0;
    wit_syscall(WIT_CALL_EXCEPTION_REJECT, token, 0, 0, &result);
    fail_fast("[LIBC] a fault could not be rejected\n");
}

/* Continues the interrupted context as it was. */
static __attribute__((__noreturn__)) void continue_unchanged(WitU64 token, const WitUserExceptionInfo *info)
{
    WitUserExceptionTransfer transfer;
    WitU64 result = 0;
    memset(&transfer, 0, sizeof(transfer));
    transfer.Version = WIT_EXCEPTION_TRANSFER_VERSION;
    transfer.Size = sizeof(transfer);
    transfer.RetireThroughToken = token;
    transfer.Context = info->Context;
    wit_syscall(WIT_CALL_EXCEPTION_CONTINUE, token, (WitU64)&transfer, sizeof(transfer), &result);
    reject(token);
}

/* Enters the handler: the frame is completed where the handler will run — in the delivery's own frame on the
 * callback's stack, or at the top of the alternate stack — and the interrupted context is continued into the
 * trampoline with the frame, which retires the delivery. The handler's stack grows below the frame, over the dead
 * frames of the delivery. */
static __attribute__((__noreturn__)) void run_handler(
    WitU64 token, SignalFrame *local, WitSignalState *state, int sig, int code, void *fault_address)
{
    Action *action = &actions[sig];
    const WitU64 sp = CONTEXT_SP(&local->Record.Context);
    const int use_alternate = (action->Flags & SA_ONSTACK) && state->AlternateBytes && !on_alternate(state, sp);
    SignalFrame *frame = local;
    if (use_alternate) {
        frame = (SignalFrame *)(((WitU64)state->AlternateBase + state->AlternateBytes - sizeof(SignalFrame)) & ~15ULL);
        memcpy(&frame->Record, &local->Record, sizeof(frame->Record));
    }
    const WitUserExceptionInfo *info = &frame->Record;
    const unsigned long mask = state->Mask;
    void (*handler)(int) = action->Handler;
    frame->State = state;
    memset(&frame->Info, 0, sizeof(frame->Info));
    frame->Info.si_signo = sig;
    frame->Info.si_code = code;
    if (code == SI_TKILL) {
        frame->Info.si_pid = OWN_PID;
    } else {
        frame->Info.si_addr = fault_address;
    }
    build_ucontext(&frame->Context, &info->Context, state, mask, fault_address);
    /* The handler runs with the signal and the action's mask blocked unless SA_NODEFER; a one-shot action resets. */
    state->Mask = (mask | action->Mask | ((action->Flags & SA_NODEFER) ? 0 : SIGNAL_BIT(sig))) & ~UNBLOCKABLE;
    if (action->Flags & SA_RESETHAND) {
        action->Handler = SIG_DFL;
    }
    WitUserExceptionTransfer *transfer = &frame->Transfer;
    memset(transfer, 0, sizeof(*transfer));
    transfer->Version = WIT_EXCEPTION_TRANSFER_VERSION;
    transfer->Size = sizeof(*transfer);
    transfer->RetireThroughToken = token;
    transfer->Context = info->Context;
    WitThreadContext *c = &transfer->Context;
    const WitU64 frame_sp = (WitU64)frame & ~15ULL;
#if defined(__x86_64__)
    c->Rip = (WitU64)__wit_signal_trampoline;
    c->Rsp = frame_sp;
    c->Rflags &= ~0x400ULL; /* a clear direction flag, as C code expects */
    c->Rdi = (WitU64)sig;
    c->Rsi = (WitU64)&frame->Info;
    c->Rdx = (WitU64)&frame->Context;
    c->Rcx = (WitU64)frame;
    c->R8 = (WitU64)handler;
    /* A default x87 and SSE environment for the handler, as Linux gives one; the saved image stays in the frame. */
    memset(c->FxState, 0, sizeof(c->FxState));
    c->FxState[0] = 0x7F;
    c->FxState[1] = 0x03;
    c->FxState[24] = 0x80;
    c->FxState[25] = 0x1F;
    memcpy(&c->FxState[28], &info->Context.FxState[28], 4); /* MXCSR_MASK as the processor reports it */
#else
    c->Pc = (WitU64)__wit_signal_trampoline;
    c->Sp = frame_sp;
    c->X[0] = (WitU64)sig;
    c->X[1] = (WitU64)&frame->Info;
    c->X[2] = (WitU64)&frame->Context;
    c->X[3] = (WitU64)frame;
    c->X[4] = (WitU64)handler;
#endif
    WitU64 result = 0;
    wit_syscall(WIT_CALL_EXCEPTION_CONTINUE, token, (WitU64)transfer, sizeof(*transfer), &result);
    /* Refused: the alternate stack may not be the kernel's; the thread's own stack is tried once, then nothing is. */
    if (use_alternate) {
        state->Mask = mask;
        if (action->Flags & SA_RESETHAND) {
            action->Handler = handler;
        }
        action->Flags &= ~(unsigned long)SA_ONSTACK;
        run_handler(token, local, state, sig, code, fault_address);
    }
    reject(token);
}

/* sigreturn's own activation (K8.1): its delivery continues into the context the frame holds, which carries the
 * handler's changes, and so ends the signal. It takes no stack beyond the callback's frame, since it may run on an
 * alternate stack the signal frame already fills: the frame comes from the thread's record, where sigreturn left it
 * while holding the thread's signals, and the transfer is the frame's own. The hold ends here; the signals that arrived
 * meanwhile are re-armed and follow this delivery. */
void __wit_signal_rearm(WitSignalState *state);

static __attribute__((__noreturn__, __noinline__)) void resume_frame(WitU64 token)
{
    WitU64 result = 0;
    WitThreadLocal *local = __wit_thread_local();
    SignalFrame *frame = local ? (SignalFrame *)local->Resuming : 0;
    if (!frame) {
        reject(token); /* a sigreturn marker no sigreturn raised */
    }
    local->Resuming = 0;
    frame->Transfer.RetireThroughToken = token;
    if (--local->Hold == 0 && local->HeldPending) {
        local->HeldPending = 0;
        __wit_signal_rearm(frame->State);
    }
    wit_syscall(WIT_CALL_EXCEPTION_CONTINUE, token, (WitU64)&frame->Transfer, sizeof(frame->Transfer), &result);
    fail_fast("[LIBC] the context of an interrupted thread could not be resumed\n");
}

static void deliver(WitU64 token, WitU64 vector, WitU64 address);

/* The fault callback in C: sigreturn's own activation resumes its frame; anything else is a fault or a signal. */
void __wit_signal_deliver(WitU64 token, WitU64 vector, WitU64 address)
{
    if (vector == WIT_EXCEPTION_ACTIVATION_VECTOR && address == (WitU64)__wit_sigreturn_activation) {
        resume_frame(token);
    }
    deliver(token, vector, address);
}

/* A fault or a signal, entered on the interrupted thread with the kernel's record to read into the frame a handler may
 * run above. */
static __attribute__((__noinline__)) void deliver(WitU64 token, WitU64 vector, WitU64 address)
{
    SignalFrame frame;
    WitUserExceptionInfo *info = &frame.Record;
    WitU64 result = 0;
    int code = 0;
    void *fault_address = 0;
    memset(info, 0, sizeof(*info));
    info->Version = WIT_EXCEPTION_VERSION;
    info->Size = sizeof(*info);
    if (wit_syscall(WIT_CALL_EXCEPTION_QUERY, token, (WitU64)info, sizeof(*info), &result) != WIT_STATUS_OK) {
        reject(token);
    }
    WitSignalState *state = __wit_signal_state_of(info->Context.ThreadId);
    const int activation = vector == WIT_EXCEPTION_ACTIVATION_VECTOR;
    int sig;
    if (activation) {
        if (info->Address != (WitU64)__wit_signal_activation || info->Error < 1 || info->Error > SIGNALS) {
            continue_unchanged(token, info); /* not a signal of this library */
        }
        sig = (int)info->Error;
        code = SI_TKILL;
    } else {
        sig = classify(vector, info->Error, address, &info->Context, &code, &fault_address);
        if (!sig) {
            reject(token);
        }
    }
    const unsigned long bit = SIGNAL_BIT(sig);
    if (state->Mask & bit) {
        if (!activation) {
            reject(token); /* a blocked synchronous fault ends the process, as on Linux */
        }
        state->Pending |= bit;
        continue_unchanged(token, info);
    }
    WitThreadLocal *local = activation ? __wit_thread_local() : 0;
    if (local && local->Hold) {
        state->Pending |= bit; /* the thread holds a lock a handler may take: the signal waits for its release */
        local->HeldPending = 1;
        continue_unchanged(token, info);
    }
    void (*handler)(int) = actions[sig].Handler;
    if (handler == SIG_IGN) {
        if (!activation) {
            reject(token); /* an ignored fault would re-execute forever */
        }
        continue_unchanged(token, info);
    }
    if (handler == SIG_DFL) {
        if (!activation) {
            reject(token); /* the kernel reports the fault and the process ends */
        }
        if (default_ignores(sig)) {
            continue_unchanged(token, info);
        }
        for (;;) {
            wit_syscall(WIT_CALL_PROCESS_EXIT, WIT_EXIT_SIGNAL(sig), 0, 0, &result); /* waitpid: WIFSIGNALED (S6.1) */
        }
    }
    run_handler(token, &frame, state, sig, code, fault_address);
}

/* Re-arms every pending signal the mask no longer blocks as an activation of the own thread, delivered at the next
 * return to user mode: at the return of the call that re-armed it. */
void __wit_signal_rearm(WitSignalState *state)
{
    WitU64 result = 0;
    unsigned long ready = state->Pending & ~state->Mask;
    while (ready) {
        const int sig = __builtin_ctzl(ready) + 1;
        const unsigned long bit = SIGNAL_BIT(sig);
        ready &= ~bit;
        state->Pending &= ~bit;
        wit_syscall(WIT_CALL_THREAD_ACTIVATE, WIT_THREAD_SELF, (WitU64)__wit_signal_activation, (WitU64)sig, &result);
    }
}

void __wit_signal_hold_enter(void)
{
    WitThreadLocal *local = __wit_thread_local();
    if (local) {
        ++local->Hold;
    }
}

/* The thread released a library lock its handlers may take; at the last one, what arrived meanwhile is re-armed. */
void __wit_signal_hold_leave(void)
{
    WitThreadLocal *local = __wit_thread_local();
    if (local && --local->Hold == 0 && local->HeldPending) {
        local->HeldPending = 0;
        __wit_signal_rearm(__wit_signal_state());
    }
}

/* After the handler: the saved context, with the handler's changes and the mask it left, resumes through an activation
 * of the own thread (K8.1), whose delivery resume_frame continues into it. The thread's signals are held from here to
 * that delivery, so that no handler run meanwhile takes the frame's place in the thread's record; a full activation
 * queue drains one activation per return to user mode, so the marker waits for room. */
void __wit_sigreturn(SignalFrame *frame)
{
    WitU64 result = 0;
    WitSignalState *state = frame->State;
    WitThreadContext *saved = &frame->Record.Context;
    apply_ucontext(saved, &frame->Context);
    state->Mask = frame->Context.uc_sigmask.__bits[0] & ~UNBLOCKABLE;
    saved->Version = WIT_THREAD_CONTEXT_VERSION;
    saved->Size = sizeof(*saved);
    saved->State = WIT_THREAD_CONTEXT_RUNNING;
    saved->Flags = CONTEXT_PROFILE | WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE; /* as the marker's delivery continues it */
    saved->SuspendCount = 0;
    saved->Reserved = 0;
    WitUserExceptionTransfer *transfer = &frame->Transfer;
    memset(transfer, 0, sizeof(*transfer));
    transfer->Version = WIT_EXCEPTION_TRANSFER_VERSION;
    transfer->Size = sizeof(*transfer);
    transfer->Context = *saved;
    WitThreadLocal *local = __wit_thread_local();
    if (!local) {
        fail_fast("[LIBC] a signal returned on a thread without its record\n");
    }
    __wit_signal_hold_enter();
    local->Resuming = frame;
    if (state->Pending & ~state->Mask) {
        local->HeldPending = 1; /* the mask the handler left lets them through: re-armed behind the marker */
    }
    /* The marker's delivery needs WIT_EXCEPTION_STACK_MINIMUM below the stack it is raised on, which an alternate stack
     * holding the frame may not have: a handler that ran there returns from the interrupted stack, below its red zone. */
    const WitU64 interrupted = CONTEXT_SP(saved);
    if (on_alternate(state, current_sp()) && !on_alternate(state, interrupted)) {
        __wit_sigreturn_switch((interrupted - RED_ZONE) & ~15ULL);
    }
    (void)result;
    __wit_sigreturn_raise();
}

/* sigreturn's own activation, raised until the queue has room: a full queue drains one activation per return to user
 * mode. Its delivery never comes back here. */
void __wit_sigreturn_raise(void)
{
    WitU64 result = 0;
    while (wit_syscall(WIT_CALL_THREAD_ACTIVATE, WIT_THREAD_SELF, (WitU64)__wit_sigreturn_activation, 0, &result) ==
        WIT_STATUS_NO_MEMORY) {
        wit_syscall(WIT_CALL_THREAD_YIELD, 0, 0, 0, &result);
    }
    fail_fast("[LIBC] the context of an interrupted thread could not be resumed\n");
}

/* rt_sigaction(sig, act, old, 8): musl's k_sigaction; the kernel restorer is never used here. */
long __wit_rt_sigaction(int sig, const struct k_sigaction *act, struct k_sigaction *old, long size)
{
    if (size != 8 || sig < 1 || sig > SIGNALS) {
        return -EINVAL;
    }
    if (act && (sig == SIGKILL || sig == SIGSTOP)) {
        return -EINVAL;
    }
    Action *action = &actions[sig];
    if (old) {
        memset(old, 0, sizeof(*old));
        old->handler = action->Handler;
        old->flags = action->Flags;
        memcpy(old->mask, &action->Mask, sizeof(old->mask));
    }
    if (act) {
        unsigned long mask = 0;
        memcpy(&mask, act->mask, sizeof(mask));
        action->Handler = act->handler;
        action->Flags = act->flags & ~(unsigned long)SA_RESTORER;
        action->Mask = mask & ~UNBLOCKABLE;
    }
    return 0;
}

/* rt_sigprocmask(how, set, old, 8) of the calling thread; unblocking delivers what became pending meanwhile. */
long __wit_rt_sigprocmask(int how, const unsigned long *set, unsigned long *old, long size)
{
    if (size != 8) {
        return -EINVAL;
    }
    WitSignalState *state = __wit_signal_state();
    if (old) {
        *old = state->Mask;
    }
    if (set) {
        switch (how) {
        case SIG_BLOCK:
            state->Mask |= *set;
            break;
        case SIG_UNBLOCK:
            state->Mask &= ~*set;
            break;
        case SIG_SETMASK:
            state->Mask = *set;
            break;
        default:
            return -EINVAL;
        }
        state->Mask &= ~UNBLOCKABLE;
        __wit_signal_rearm(state);
    }
    return 0;
}

long __wit_rt_sigpending(unsigned long *set, long size)
{
    if (size != 8) {
        return -EINVAL;
    }
    const WitSignalState *state = __wit_signal_state();
    *set = state->Pending & state->Mask;
    return 0;
}

/* sigaltstack: the library's record and the kernel's alternate range of the calling thread (THREAD_STACK_ALTERNATE),
 * which the kernel enters on its own only when the interrupted stack is exhausted. */
long __wit_sigaltstack(const stack_t *ss, stack_t *old)
{
    WitSignalState *state = __wit_signal_state();
    const int on_it = on_alternate(state, current_sp());
    if (old) {
        old->ss_sp = state->AlternateBase;
        old->ss_size = state->AlternateBytes;
        old->ss_flags = on_it ? SS_ONSTACK : (state->AlternateBytes ? 0 : SS_DISABLE);
    }
    if (!ss) {
        return 0;
    }
    if (on_it) {
        return -EPERM;
    }
    if (ss->ss_flags & ~(SS_DISABLE | SS_ONSTACK)) {
        return -EINVAL;
    }
    WitThreadAlternateStackRequest request;
    WitU64 result = 0, base = 0, end = 0;
    memset(&request, 0, sizeof(request));
    request.Version = WIT_THREAD_ALTERNATE_STACK_VERSION;
    request.Size = sizeof(request);
    if (!(ss->ss_flags & SS_DISABLE)) {
        base = ((WitU64)ss->ss_sp + 15) & ~15ULL;
        end = ((WitU64)ss->ss_sp + ss->ss_size) & ~15ULL;
        if (end <= base || end - base < WIT_EXCEPTION_STACK_MINIMUM) {
            return -ENOMEM; /* the kernel's callback frame needs this much; MINSIGSTKSZ is smaller */
        }
        request.Base = base;
        request.Bytes = end - base;
    }
    WitU64 status = wit_syscall(WIT_CALL_THREAD_STACK_ALTERNATE, (WitU64)&request, sizeof(request), 0, &result);
    /* A guard page at the bottom of the range, which CoreCLR's PAL makes PROT_NONE (R3.2), is no room for the kernel's
     * callback frame: the kernel takes the writable part above it, page by page while enough is left, and the library
     * keeps the whole range, whose top its handlers use. */
    while (status == WIT_STATUS_BAD_ADDRESS && request.Bytes) {
        const WitU64 next = (request.Base & ~4095ULL) + 4096;
        if (next >= end || end - next < WIT_EXCEPTION_STACK_MINIMUM) {
            break;
        }
        request.Base = next;
        request.Bytes = end - next;
        status = wit_syscall(WIT_CALL_THREAD_STACK_ALTERNATE, (WitU64)&request, sizeof(request), 0, &result);
    }
    if (status == WIT_STATUS_INVALID_ARGUMENT) {
        return -EINVAL;
    }
    if (status == WIT_STATUS_BAD_ADDRESS) {
        return -EFAULT;
    }
    if (status == WIT_STATUS_BUSY) {
        return -EPERM;
    }
    if (status != WIT_STATUS_OK) {
        return __wit_errno(status);
    }
    state->AlternateBase = request.Bytes ? (void *)base : 0;
    state->AlternateBytes = request.Bytes ? end - base : 0;
    return 0;
}

/* tkill and tgkill: the signal reaches the named thread as an activation; kill reaches the calling thread of the own
 * process, and no other process exists to this library yet. */
long __wit_tkill(int tid, int sig)
{
    if (sig < 0 || sig > SIGNALS) {
        return -EINVAL;
    }
    if (sig == SIGSTOP || sig == SIGTSTP || sig == SIGTTIN || sig == SIGTTOU || sig == SIGKILL) {
        return -EINVAL; /* no job control, and no thread ends another by force */
    }
    return __wit_thread_signal(tid, sig);
}

long __wit_tgkill(int tgid, int tid, int sig)
{
    if (tgid != OWN_PID) {
        return -ESRCH;
    }
    return __wit_tkill(tid, sig);
}

long __wit_kill(int pid, int sig)
{
    if (pid != OWN_PID && pid != 0 && pid != -1) {
        return -ESRCH;
    }
    return __wit_tkill((int)__wit_gettid(), sig);
}

/* rt_sigsuspend and pause: the thread sleeps until an activation ran a handler, which ends the sleep INTERRUPTED. */
long __wit_rt_sigsuspend(const unsigned long *set, long size)
{
    if (size != 8) {
        return -EINVAL;
    }
    WitSignalState *state = __wit_signal_state();
    const unsigned long saved = state->Mask;
    WitU64 result = 0;
    state->Mask = *set & ~UNBLOCKABLE;
    __wit_signal_rearm(state); /* a pending signal the new mask admits runs its handler before the sleep */
    for (;;) {
        const WitU64 status = wit_syscall(WIT_CALL_SLEEP_UNTIL, WIT_WAIT_INFINITE, 0, 0, &result);
        if (status == WIT_STATUS_INTERRUPTED) {
            break;
        }
        if (status != WIT_STATUS_OK) {
            state->Mask = saved;
            return __wit_errno(status);
        }
    }
    state->Mask = saved;
    __wit_signal_rearm(state);
    return -EINTR;
}

long __wit_pause(void)
{
    WitU64 result = 0;
    for (;;) {
        const WitU64 status = wit_syscall(WIT_CALL_SLEEP_UNTIL, WIT_WAIT_INFINITE, 0, 0, &result);
        if (status == WIT_STATUS_INTERRUPTED) {
            return -EINTR;
        }
        if (status != WIT_STATUS_OK) {
            return __wit_errno(status);
        }
    }
}

#if defined(__x86_64__)
/* The fault callback: the kernel enters with the token, the vector and the address in RDI, RSI and RDX (the SysV
 * argument registers, K8.4d) on a 16-byte aligned stack, as the C delivery takes them. The trampoline is entered
 * by EXCEPTION_CONTINUE with RDI the signal, RSI the siginfo, RDX the ucontext, RCX the frame and R8 the handler on
 * the 16-byte aligned frame; the frame survives in RBX across the handler. musl's kernel restorers are never
 * installed (musl's generic restore.c stays, unused): a handler returns to the trampoline. */
__asm__(".text\n"
        ".global __wit_signal_entry\n"
        ".type __wit_signal_entry,@function\n"
        "__wit_signal_entry:\n"
        "    xor %ebp, %ebp\n"
        "    and $-16, %rsp\n"
        "    call __wit_signal_deliver\n"
        "    ud2\n"
        ".global __wit_signal_trampoline\n"
        ".type __wit_signal_trampoline,@function\n"
        "__wit_signal_trampoline:\n"
        "    mov %rcx, %rbx\n"
        "    xor %ebp, %ebp\n"
        "    call *%r8\n"
        "    mov %rbx, %rdi\n"
        "    call __wit_sigreturn\n"
        "    ud2\n"
        ".global __wit_signal_activation\n"
        ".type __wit_signal_activation,@function\n"
        "__wit_signal_activation:\n"
        "    ret\n"
        ".global __wit_sigreturn_activation\n"
        ".type __wit_sigreturn_activation,@function\n"
        "__wit_sigreturn_activation:\n"
        "    ret\n"
        ".global __wit_sigreturn_switch\n"
        ".type __wit_sigreturn_switch,@function\n"
        "__wit_sigreturn_switch:\n"
        "    mov %rdi, %rsp\n"
        "    xor %ebp, %ebp\n"
        "    call __wit_sigreturn_raise\n"
        "    ud2\n");
#else
/* The fault callback: x0 the token, x1 the vector, x2 the address, on a 16-byte aligned stack with x30 zero. The
 * trampoline is entered with x0 the signal, x1 the siginfo, x2 the ucontext, x3 the frame and x4 the handler; the
 * frame survives in x19 across the handler. musl's kernel restorers (its generic restore.c) are never entered. */
__asm__(".text\n"
        ".global __wit_signal_entry\n"
        ".type __wit_signal_entry,%function\n"
        "__wit_signal_entry:\n"
        "    mov x29, #0\n"
        "    bl __wit_signal_deliver\n"
        "    brk #0\n"
        ".global __wit_signal_trampoline\n"
        ".type __wit_signal_trampoline,%function\n"
        "__wit_signal_trampoline:\n"
        "    mov x19, x3\n"
        "    mov x29, #0\n"
        "    mov x30, #0\n"
        "    blr x4\n"
        "    mov x0, x19\n"
        "    bl __wit_sigreturn\n"
        "    brk #0\n"
        ".global __wit_signal_activation\n"
        ".type __wit_signal_activation,%function\n"
        "__wit_signal_activation:\n"
        "    ret\n"
        ".global __wit_sigreturn_activation\n"
        ".type __wit_sigreturn_activation,%function\n"
        "__wit_sigreturn_activation:\n"
        "    ret\n"
        ".global __wit_sigreturn_switch\n"
        ".type __wit_sigreturn_switch,%function\n"
        "__wit_sigreturn_switch:\n"
        "    mov sp, x0\n"
        "    mov x29, #0\n"
        "    mov x30, #0\n"
        "    bl __wit_sigreturn_raise\n"
        "    brk #0\n");
#endif
