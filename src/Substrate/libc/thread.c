#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/thread_info.h"
#include "witos/thread_reference.h"
#include <errno.h>
#include <sched.h>
#include <string.h>

/* Threads (plan step S2): musl's pthreads over the kernel's threads. musl prepares a thread's stack and TLS itself
 * and calls __clone with the start function, the stack pointer, the thread pointer and two words: the parent's copy
 * of the thread id (CLONE_PARENT_SETTID) and the word the kernel zeroes when the thread is gone
 * (CLONE_CHILD_CLEARTID: the thread list lock, which the exiting thread holds until then). Here a thread is a
 * THREAD_CREATE of the one form, started suspended so that its id is in place before it runs, entered through a
 * trampoline that installs the thread pointer where the kernel cannot (TPIDR_EL0 on ARM64) and calls the start
 * function; a thread's exit is THREAD_EXIT with the exit request naming that word and the futex event behind it, so
 * that the kernel serves the clear and the wake after the thread stopped running — never from the thread itself,
 * whose stack a joiner may then free. Thread ids are the library's own small integers; the main thread is 1. */

#define THREADS 64
#define MAIN_TID 1

typedef struct Thread {
    WitU64 Identity; /* the kernel's thread identity, from THREAD_QUERY */
    int Tid;
    int *ClearWord; /* CLONE_CHILD_CLEARTID: zeroed by the kernel at the exit */
} Thread;

typedef struct Launch {
    int (*Function)(void *);
    void *Argument;
    void *ThreadPointer;
} Launch;

static Thread threads[THREADS];
static int next_tid = MAIN_TID + 1;
static int *main_clear_word;

void __wit_thread_entry(void); /* the trampoline below */

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

static Thread *find_current(void)
{
    const WitU64 identity = current_identity();
    for (unsigned i = 0; i < THREADS; ++i) {
        if (threads[i].Tid && threads[i].Identity == identity) {
            return &threads[i];
        }
    }
    return 0;
}

long __wit_gettid(void)
{
    const Thread *thread = find_current();
    return thread ? thread->Tid : MAIN_TID;
}

/* Whether a word is one the exit request clears (musl: the thread list lock, named by every thread and by
 * set_tid_address): its waiters share the event the kernel sets (futex.c). */
int __wit_is_exit_word(const volatile void *address)
{
    if (!address) {
        return 0;
    }
    if (address == (const volatile void *)main_clear_word) {
        return 1;
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
    if (thread) {
        thread->ClearWord = address;
        return thread->Tid;
    }
    main_clear_word = address;
    return MAIN_TID;
}

/* The exit of the calling thread (SYS_exit, or __unmapself with the stack's reservation): the kernel clears the
 * thread's word and sets the exit word's event once the thread no longer runs. */
long __wit_thread_exit(long code, WitU64 reservation)
{
    WitThreadExitRequest request;
    WitU64 result = 0;
    Thread *thread = find_current();
    int *word = thread ? thread->ClearWord : main_clear_word;
    request.Version = WIT_THREAD_EXIT_VERSION;
    request.Size = sizeof(request);
    request.ClearAddress = (WitU64)word;
    request.Event = word ? __wit_futex_exit_event() : 0;
    if (thread) {
        thread->Tid = 0; /* the record is free once the kernel has the exit; the identity stays in the kernel */
        thread->Identity = 0;
        thread->ClearWord = 0;
    }
    const WitU64 status = wit_syscall(WIT_CALL_THREAD_EXIT, (WitU64)code, reservation, (WitU64)&request, &result);
    return __wit_errno(status); /* a refused exit returns to the caller, which loops on SYS_exit */
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
    for (unsigned i = 0; i < THREADS; ++i) {
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
    record->Identity = info.ThreadId;
    record->Tid = tid;
    record->ClearWord = child_tid;
    if (parent_tid) {
        *parent_tid = tid;
    }
    status = wit_syscall(WIT_CALL_THREAD_RESUME, handle, 0, 0, &result);
    wit_syscall(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, &result); /* the library tracks the thread itself */
    if (status != WIT_STATUS_OK) {
        record->Tid = 0;
        return (int)__wit_errno(status);
    }
    return tid;
}

/* musl's __unmapself(base, size): the exiting detached thread's stack goes with the thread. */
void __unmapself(void *base, size_t size)
{
    (void)size;
    for (;;) {
        __wit_thread_exit(0, (WitU64)base);
    }
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
        "    ud2\n");

/* musl sets the main thread's pointer once (__init_tp); on x64 that is the FS base, which the kernel sets. */
__attribute__((__visibility__("hidden"))) int __set_thread_area(void *p)
{
    WitU64 result = 0;
    const WitU64 status = wit_syscall(WIT_CALL_THREAD_SET_TLS, (WitU64)p, 0, 0, &result);
    return status == WIT_STATUS_OK ? 0 : (int)__wit_errno(status);
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
        "    brk #0\n");
#else
#error "WitOS threads: unsupported architecture"
#endif
