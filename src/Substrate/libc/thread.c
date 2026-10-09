#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/manager.h"
#include "witos/thread_info.h"
#include "witos/thread_reference.h"
#include <errno.h>
#include <sched.h>
#include <string.h>
#include <sys/prctl.h>

/* Threads (plan step S2): musl's pthreads over the kernel's threads. musl prepares a thread's stack and TLS itself
 * and calls __clone with the start function, the stack pointer, the thread pointer and two words: the parent's copy
 * of the thread id (CLONE_PARENT_SETTID) and the word the kernel zeroes when the thread is gone
 * (CLONE_CHILD_CLEARTID: the thread list lock, which the exiting thread holds until then). Here a thread is a
 * THREAD_CREATE of the one form, started suspended so that its id is in place before it runs, entered through a
 * trampoline that installs the thread pointer where the kernel cannot (TPIDR_EL0 on ARM64) and calls the start
 * function; a thread's exit is THREAD_EXIT with the exit request naming that word and the futex event behind it, so
 * that the kernel serves the clear and the wake after the thread stopped running — never from the thread itself,
 * whose stack a joiner may then free. Thread ids are the library's own small integers; the main thread is 1 and the
 * first record. The library keeps each thread's kernel handle while the thread lives (S3: pthread_kill is
 * THREAD_ACTIVATE of that handle) and the thread's signal state (signal.c). */

#define THREADS 64
#define MAIN_TID 1

typedef struct Thread {
    WitU64 Identity; /* the kernel's thread identity, from THREAD_QUERY */
    WitU64 Handle; /* the kernel's reference handle, closed by the thread at its exit */
    WitU64 Pointer; /* the thread pointer it was created with; zero for the main thread, whose pointer musl moves */
    int Tid;
    int *ClearWord; /* CLONE_CHILD_CLEARTID: zeroed by the kernel at the exit */
    WitSignalState Signals;
    WitThreadLocal Local;
    char Name[16]; /* the thread's name (prctl PR_SET_NAME), the library's as its id is; a new thread inherits it */
} Thread;

typedef struct Launch {
    int (*Function)(void *);
    void *Argument;
    void *ThreadPointer;
} Launch;

/* threads[0] is the main thread, whose id holds from the start: musl's dynamic linker installs the main thread's
 * pointer and reads its id (set_tid_address) before the start message arrives and its record is complete (S5.3). */
static Thread threads[THREADS] = {[0] = {.Tid = MAIN_TID}};
static int next_tid = MAIN_TID + 1;

#if defined(__x86_64__)
static volatile int pointer_set; /* musl installed the main thread's pointer (__set_thread_area, below) */
#endif

void __wit_thread_entry(void); /* the trampoline below */
long __cancel(void); /* musl's cancellation (pthread_cancel.c) */

static WitU64 current_identity(void)
{
    WitUserThreadInfo info;
    WitU64 result = 0;
    memset(&info, 0, sizeof(info));
    info.Version = WIT_THREAD_INFO_VERSION;
    info.Size = sizeof(info);
    if (wit_syscall(WIT_CALL_THREAD_QUERY, WIT_THREAD_SELF, (WitU64)&info, sizeof(info), &result) != WIT_STATUS_OK) {
        return 0;
    }
    return info.ThreadId;
}

static Thread *find_exact(WitU64 identity)
{
    for (unsigned i = 0; i < THREADS; ++i) {
        if (threads[i].Tid && threads[i].Identity == identity) {
            return &threads[i];
        }
    }
    return 0;
}

static Thread *find_identity(WitU64 identity)
{
    Thread *thread = find_exact(identity);
    return thread ? thread : &threads[0]; /* a thread the library did not create runs as the main thread */
}

static Thread *find_current(void)
{
    return find_identity(current_identity());
}

static Thread *find_tid(int tid)
{
    for (unsigned i = 0; i < THREADS; ++i) {
        if (threads[i].Tid == tid) {
            return &threads[i];
        }
    }
    return 0;
}

/* The main thread's record, before the library starts (crt1): its identity and a handle of its own. */
void __wit_thread_init(void)
{
    Thread *main = &threads[0];
    WitU64 handle = 0, result = 0;
    main->Tid = MAIN_TID;
    main->Identity = current_identity();
    if (wit_syscall(WIT_CALL_HANDLE_DUPLICATE, WIT_THREAD_SELF, (WitU64)&handle, 0, &result) == WIT_STATUS_OK) {
        main->Handle = handle;
    }
}

/* The calling thread's pointer, zero before musl installed the main thread's: the FS base's first word, which musl's
 * thread structure points at itself, on x64; TPIDR_EL0, which the thread writes itself, on ARM64. */
static WitU64 current_pointer(void)
{
    WitU64 pointer = 0;
#if defined(__x86_64__)
    if (pointer_set) {
        __asm__ volatile("mov %%fs:0, %0" : "=r"(pointer));
    }
#else
    __asm__ volatile("mrs %0, tpidr_el0" : "=r"(pointer));
#endif
    return pointer;
}

/* The record of a thread the library created is found by the pointer it was created with; every other pointer is
 * the main thread's. */
WitThreadLocal *__wit_thread_local(void)
{
    const WitU64 pointer = current_pointer();
    if (!pointer) {
        return 0;
    }
    for (unsigned i = 1; i < THREADS; ++i) {
        if (threads[i].Tid && threads[i].Pointer == pointer) {
            return &threads[i].Local;
        }
    }
    return &threads[0].Local;
}

long __wit_gettid(void)
{
    return find_current()->Tid;
}

WitSignalState *__wit_signal_state(void)
{
    return &find_current()->Signals;
}

WitSignalState *__wit_signal_state_of(WitU64 identity)
{
    return &find_identity(identity)->Signals;
}

/* A signal for the thread with the id: THREAD_ACTIVATE of its handle (the own thread by WIT_THREAD_SELF) with the
 * signal library's marker callback and the signal as the argument (signal.c); zero checks the thread exists. */
long __wit_thread_signal(int tid, int sig)
{
    WitU64 result = 0;
    const Thread *target = find_tid(tid);
    if (!target) {
        return -ESRCH;
    }
    if (!sig) {
        return 0;
    }
    const WitU64 handle = target == find_current() ? WIT_THREAD_SELF : target->Handle;
    if (!handle) {
        return -ESRCH;
    }
    const WitU64 status =
        wit_syscall(WIT_CALL_THREAD_ACTIVATE, handle, (WitU64)__wit_signal_activation, (WitU64)sig, &result);
    switch (status) {
    case WIT_STATUS_OK:
        return 0;
    case WIT_STATUS_BAD_HANDLE:
        return -ESRCH;
    case WIT_STATUS_NO_MEMORY:
        return -EAGAIN; /* the kernel's pending activations of the thread are all taken */
    default:
        return __wit_errno(status);
    }
}

/* Whether a word is one the exit request clears (musl: the thread list lock, named by every thread and by
 * set_tid_address): its waiters share the event the kernel sets (futex.c). */
int __wit_is_exit_word(const volatile void *address)
{
    if (!address) {
        return 0;
    }
    for (unsigned i = 0; i < THREADS; ++i) {
        if (threads[i].Tid && (const volatile void *)threads[i].ClearWord == address) {
            return 1;
        }
    }
    return 0;
}

long __wit_set_tid_address(int *address)
{
    Thread *thread = find_current();
    thread->ClearWord = address;
    return thread->Tid;
}

/* The exit of the calling thread (SYS_exit, or __unmapself with the stack's reservation): the kernel clears the
 * thread's word and sets the exit word's event once the thread no longer runs. The record and the handle go first;
 * closing a reference never ends the thread. The request and the reservation are the library's own, so a refused
 * exit is a defect: the process ends with a report rather than spin in musl's exit loop without its record. */
long __wit_thread_exit(long code, WitU64 reservation)
{
    WitThreadExitRequest request;
    WitU64 result = 0;
    Thread *thread = find_exact(current_identity());
    if (!thread) {
        thread = &threads[0];
    }
    int *word = thread->ClearWord;
    request.Version = WIT_THREAD_EXIT_VERSION;
    request.Size = sizeof(request);
    request.ClearAddress = (WitU64)word;
    request.Event = word ? __wit_futex_exit_event() : 0;
    if (thread->Handle) {
        wit_syscall(WIT_CALL_HANDLE_CLOSE, thread->Handle, 0, 0, &result);
        thread->Handle = 0;
    }
    if (thread != &threads[0]) {
        thread->Tid = 0; /* the record is free once the kernel has the exit; the identity stays in the kernel */
        thread->Identity = 0;
        thread->Pointer = 0;
        thread->ClearWord = 0;
        memset(&thread->Signals, 0, sizeof(thread->Signals));
        memset(&thread->Local, 0, sizeof(thread->Local));
    }
    const WitU64 status = wit_syscall(WIT_CALL_THREAD_EXIT, (WitU64)code, reservation, (WitU64)&request, &result);
    static const char hex[] = "0123456789abcdef";
    char text[] = "[LIBC] the kernel refused a thread's exit: status 0x00\n";
    text[sizeof(text) - 4] = hex[(status >> 4) & 15];
    text[sizeof(text) - 3] = hex[status & 15];
    wit_syscall(WIT_CALL_DEBUG_WRITE, __wit_process.Log, (WitU64)text, sizeof(text) - 1, &result);
    for (;;) {
        wit_syscall(WIT_CALL_PROCESS_EXIT, WIT_EXIT_SIGNAL(6), 0, 0, &result); /* as an abort would report it */
    }
}

/* musl's __clone(start, stack, flags, argument, parent tid, thread pointer, child tid word). */
int __clone(int (*function)(void *), void *stack, int flags, void *argument, ...)
{
    __builtin_va_list ap;
    int *parent_tid = 0;
    void *thread_pointer = 0;
    int *child_tid = 0;
    __builtin_va_start(ap, argument);
    if (flags & CLONE_PARENT_SETTID) {
        parent_tid = __builtin_va_arg(ap, int *);
    } else if (flags & (CLONE_SETTLS | CLONE_CHILD_CLEARTID)) {
        (void)__builtin_va_arg(ap, int *);
    }
    if (flags & CLONE_SETTLS) {
        thread_pointer = __builtin_va_arg(ap, void *);
    } else if (flags & CLONE_CHILD_CLEARTID) {
        (void)__builtin_va_arg(ap, void *);
    }
    if (flags & CLONE_CHILD_CLEARTID) {
        child_tid = __builtin_va_arg(ap, int *);
    }
    __builtin_va_end(ap);
    if (!(flags & CLONE_VM) || !(flags & CLONE_THREAD) || !function || !stack) {
        return -EINVAL; /* only a thread of this process: no fork, no new address space */
    }
    Thread *record = 0;
    for (unsigned i = 1; i < THREADS; ++i) {
        if (!threads[i].Tid) {
            record = &threads[i];
            break;
        }
    }
    if (!record) {
        return -EAGAIN;
    }
    /* The launch block lies below musl's start arguments, on the new thread's own stack. */
    Launch *launch = (Launch *)(((WitU64)stack - sizeof(Launch)) & ~15ULL);
    launch->Function = function;
    launch->Argument = argument;
    launch->ThreadPointer = thread_pointer;
    WitThreadCreateRequest2 request;
    WitU64 handle = 0, result = 0;
    memset(&request, 0, sizeof(request));
    request.Version = WIT_THREAD_CREATE_VERSION_2;
    request.Size = sizeof(request);
    request.Entry = (WitU64)__wit_thread_entry;
    request.Argument = (WitU64)launch;
    request.StackPointer = (WitU64)launch;
    request.TlsBase = (WitU64)thread_pointer;
    request.Flags = WIT_THREAD_START_SUSPENDED;
    WitU64 status = wit_syscall(WIT_CALL_THREAD_CREATE, (WitU64)&request, sizeof(request), 0, &handle);
    if (status != WIT_STATUS_OK) {
        return status == WIT_STATUS_NO_MEMORY ? -EAGAIN : (int)__wit_errno(status);
    }
    WitUserThreadInfo info;
    memset(&info, 0, sizeof(info));
    info.Version = WIT_THREAD_INFO_VERSION;
    info.Size = sizeof(info);
    status = wit_syscall(WIT_CALL_THREAD_QUERY, handle, (WitU64)&info, sizeof(info), &result);
    if (status != WIT_STATUS_OK) {
        wit_syscall(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, &result); /* a never-resumed thread ends with its handle */
        return (int)__wit_errno(status);
    }
    const int tid = next_tid++;
    memset(&record->Signals, 0, sizeof(record->Signals));
    record->Signals.Mask = find_current()->Signals.Mask; /* inherited, as musl's start() then sets it */
    memset(&record->Local, 0, sizeof(record->Local));
    record->Identity = info.ThreadId;
    record->Handle = handle;
    record->Pointer = (WitU64)thread_pointer;
    record->Tid = tid;
    record->ClearWord = child_tid;
    memcpy(record->Name, find_current()->Name, sizeof(record->Name));
    if (parent_tid) {
        *parent_tid = tid;
    }
    status = wit_syscall(WIT_CALL_THREAD_RESUME, handle, 0, 0, &result);
    if (status != WIT_STATUS_OK) {
        wit_syscall(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, &result);
        record->Handle = 0;
        record->Tid = 0;
        return (int)__wit_errno(status);
    }
    return tid;
}

/* prctl (R2.1): a thread's name, PR_SET_NAME and PR_GET_NAME of the calling thread, which musl's pthread_setname_np
 * and pthread_getname_np use for the own thread; the kernel knows no thread's name. Every other option is ENOSYS. */
long __wit_prctl(long option, unsigned long argument)
{
    Thread *thread = find_current();
    if (option == PR_SET_NAME) {
        const char *name = (const char *)argument;
        size_t length = 0;
        while (length < sizeof(thread->Name) - 1 && name[length]) {
            ++length;
        }
        memcpy(thread->Name, name, length);
        memset(thread->Name + length, 0, sizeof(thread->Name) - length);
        return 0;
    }
    if (option == PR_GET_NAME) {
        memcpy((char *)argument, thread->Name, sizeof(thread->Name));
        return 0;
    }
    return -ENOSYS;
}

/* musl's __unmapself(base, size): the exiting detached thread's stack goes with the thread. The kernel releases the
 * reservation once the thread no longer runs, so the library's table forgets the mapping first. */
void __unmapself(void *base, size_t size)
{
    (void)size;
    __wit_mapping_forget_locked((WitU64)base);
    __wit_thread_exit(0, (WitU64)base);
    __builtin_unreachable();
}

/* musl's cancellation point (pthread_cancel.c calls __syscall_cp_asm with the thread's cancel word): the flag is
 * checked first, as musl's assembly does, and the call runs with the word published so that a blocking path that is
 * about to park checks it again (futex.c, syscall.c) — Linux closes that window with the atomic system call
 * instruction, WitOS with the check before the kernel wait. The window symbols musl's cancel handler compares the
 * interrupted PC against are empty here: a cancellation arriving in a blocking call ends the wait INTERRUPTED and
 * __syscall_cp_c cancels on the EINTR it sees. */
long __syscall_cp_asm(volatile void *cancel, long nr, long a, long b, long c, long d, long e, long f)
{
    if (*(volatile int *)cancel) {
        return __cancel();
    }
    WitThreadLocal *local = __wit_thread_local();
    if (local) {
        local->CancelPoint = (volatile int *)cancel;
    }
    const long r = __wit_syscall(nr, a, b, c, d, e, f);
    if (local) {
        local->CancelPoint = 0;
    }
    return r;
}

int __wit_cancel_requested(void)
{
    const WitThreadLocal *local = __wit_thread_local();
    return local && local->CancelPoint && *local->CancelPoint;
}

#if defined(__x86_64__)
/* The thread pointer (FS base) came from the request; RDI is the launch block, RSP its address. */
__asm__(".text\n"
        ".global __wit_thread_entry\n"
        ".type __wit_thread_entry,@function\n"
        "__wit_thread_entry:\n"
        "    mov (%rdi), %rax\n"
        "    mov 8(%rdi), %rdi\n"
        "    and $-16, %rsp\n"
        "    call *%rax\n"
        "    ud2\n"
        ".global __cp_begin\n"
        ".hidden __cp_begin\n"
        ".global __cp_end\n"
        ".hidden __cp_end\n"
        ".global __cp_cancel\n"
        ".hidden __cp_cancel\n"
        "__cp_begin:\n"
        "__cp_end:\n"
        "    ret\n"
        "__cp_cancel:\n"
        "    jmp __cancel\n");

/* musl sets the main thread's pointer once (__init_tp); on x64 that is the FS base, which the kernel sets. */
__attribute__((__visibility__("hidden"))) int __set_thread_area(void *p)
{
    WitU64 result = 0;
    const WitU64 status = wit_syscall(WIT_CALL_THREAD_SET_TLS, (WitU64)p, 0, 0, &result);
    if (status != WIT_STATUS_OK) {
        return (int)__wit_errno(status);
    }
    pointer_set = 1;
    return 0;
}
#elif defined(__aarch64__)
/* TPIDR_EL0 is the thread's own register at EL0: the trampoline installs the thread pointer, then calls. */
__asm__(".text\n"
        ".global __wit_thread_entry\n"
        ".type __wit_thread_entry,%function\n"
        "__wit_thread_entry:\n"
        "    ldr x9, [x0]\n"
        "    ldr x1, [x0, #16]\n"
        "    msr tpidr_el0, x1\n"
        "    ldr x0, [x0, #8]\n"
        "    mov x29, #0\n"
        "    mov x30, #0\n"
        "    blr x9\n"
        "    brk #0\n"
        ".global __cp_begin\n"
        ".hidden __cp_begin\n"
        ".global __cp_end\n"
        ".hidden __cp_end\n"
        ".global __cp_cancel\n"
        ".hidden __cp_cancel\n"
        "__cp_begin:\n"
        "__cp_end:\n"
        "    ret\n"
        "__cp_cancel:\n"
        "    b __cancel\n");
#else
#error "WitOS threads: unsupported architecture"
#endif
