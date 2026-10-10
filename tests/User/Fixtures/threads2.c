#include "fixture.h"

/* Thread form and transport fixture (RFC 0011 v3 sections 7.3 and 6.1, plan steps K5.2a and K8.3). Every call travels
 * through the layer-2 transport (witos/syscall.h): SYSCALL with the SysV registers on x64, SVC on ARM64. A thread's
 * argument arrives in the first argument register of the SysV convention alone (K8.4b); THREAD_SET_TLS changes the
 * first thread's FS base on x64
 * and is UNSUPPORTED on ARM64, whose TPIDR_EL0 belongs to EL0; a stack of the fixture's own is reserved and committed;
 * THREAD_CREATE with the version 2 request starts a worker on it with a TLS base of the fixture's choosing; the worker
 * checks its argument, stack and TLS, changes its TLS where the ISA lets it, and exits naming the reservation with an
 * exit request (S2.1); the creator joins it and finds the reservation released, the word zeroed and the event set.
 * Broken requests are refused whole. The entry points are assembly that hands the registers the kernel set to C. */

#define STACK_BYTES 65536ULL
#define ARGUMENT 0x77U
#define EXIT_CODE 42U
#define FAILED_EXIT_CODE 9U
#define TLS_FIRST (WIT_USER_DATA + 2048)
#define TLS_SECOND (WIT_USER_DATA + 2560)
#define STACK_BASE (*(volatile WitU64 *)(WIT_USER_DATA + 3072))
#define EXIT_REQUEST ((volatile WitThreadExitRequest *)(WIT_USER_DATA + 3080))
#define EXIT_WORD (*(volatile WitU64 *)(WIT_USER_DATA + 3112))
#define EXIT_EVENT (*(volatile WitU64 *)(WIT_USER_DATA + 3120))
#define KERNEL_ADDRESS 0xFFFF800000000000ULL

#if defined(__x86_64__)
#define C_ENTRY __attribute__((force_align_arg_pointer, noreturn, used))
#else
#define C_ENTRY __attribute__((noreturn, used))
#endif

/* The entry points the kernel enters: wit_user_start hands the startup block (RDI, x0) to threads2_main; worker hands
 * its argument and its stack pointer to threads2_worker. */
__attribute__((visibility("hidden"))) void worker(void);
#if defined(__x86_64__)
__asm__(".section .text.entry,\"ax\",@progbits\n"
        ".globl wit_user_start\n"
        "wit_user_start:\n"
        "    jmp threads2_main\n"
        ".text\n"
        ".globl worker\n.hidden worker\n"
        "worker:\n"
        "    mov %rsp, %rsi\n"
        "    jmp threads2_worker\n");
#else
__asm__(".section .text.entry,\"ax\",%progbits\n"
        ".globl wit_user_start\n"
        "wit_user_start:\n"
        "    b threads2_main\n"
        ".text\n"
        ".globl worker\n.hidden worker\n"
        "worker:\n"
        "    mov x1, sp\n"
        "    b threads2_worker\n");
#endif

static WitU64 tls_read(void)
{
    WitU64 value;
#if defined(__x86_64__)
    __asm__ volatile("mov %%fs:0, %0" : "=r"(value));
#else
    __asm__ volatile("mrs %0, tpidrro_el0" : "=r"(value));
#endif
    return value;
}

static WIT_NORETURN void worker_failed(void)
{
    WitU64 result;
    for (;;) {
        wit_syscall(WIT_CALL_THREAD_EXIT, FAILED_EXIT_CODE, 0, 0, &result);
    }
}

static void worker_check(int condition)
{
    if (!condition) {
        worker_failed();
    }
}

static WitU64 worker_call(WitU64 number, WitU64 a0, WitU64 a1, WitU64 a2)
{
    WitU64 result;
    return wit_syscall(number, a0, a1, a2, &result);
}

/* The worker: its argument, the stack pointer exactly the one requested, the TLS base its creator chose. */
C_ENTRY void threads2_worker(WitU64 argument, WitU64 sp)
{
    worker_check(argument == ARGUMENT);
    worker_check(sp == STACK_BASE + STACK_BYTES);
#if defined(__x86_64__)
    worker_check(tls_read() == 0x1234);
    worker_check(worker_call(WIT_CALL_THREAD_SET_TLS, TLS_SECOND, 0, 0) == WIT_STATUS_OK);
    worker_check(tls_read() == 0x5678);
#else
    /* TPIDRRO_EL0 holds the request's TLS base; TPIDR_EL0 is ours, preserved by the kernel across a yield. */
    worker_check(tls_read() == TLS_FIRST);
    __asm__ volatile("msr tpidr_el0, %0" ::"r"((WitU64)0x5678));
    worker_call(WIT_CALL_THREAD_YIELD, 0, 0, 0);
    WitU64 own;
    __asm__ volatile("mrs %0, tpidr_el0" : "=r"(own));
    worker_check(own == 0x5678);
#endif
    /* THREAD_EXIT naming something that is not a reservation returns instead of exiting. */
    worker_check(worker_call(WIT_CALL_THREAD_EXIT, 7, WIT_USER_DATA, 0) == WIT_STATUS_NOT_RESERVED);
    /* Exit requests refused whole (S2.1): a foreign version, a word outside user space, an unknown event. */
    EXIT_REQUEST->Version = 2;
    EXIT_REQUEST->Size = sizeof(WitThreadExitRequest);
    EXIT_REQUEST->ClearAddress = (WitU64)&EXIT_WORD;
    EXIT_REQUEST->Event = EXIT_EVENT;
    worker_check(
        worker_call(WIT_CALL_THREAD_EXIT, EXIT_CODE, STACK_BASE, (WitU64)EXIT_REQUEST) == WIT_STATUS_UNSUPPORTED);
    EXIT_REQUEST->Version = WIT_THREAD_EXIT_VERSION;
    EXIT_REQUEST->ClearAddress = KERNEL_ADDRESS;
    worker_check(
        worker_call(WIT_CALL_THREAD_EXIT, EXIT_CODE, STACK_BASE, (WitU64)EXIT_REQUEST) == WIT_STATUS_BAD_ADDRESS);
    EXIT_REQUEST->ClearAddress = (WitU64)&EXIT_WORD;
    EXIT_REQUEST->Event = 0x12345;
    worker_check(
        worker_call(WIT_CALL_THREAD_EXIT, EXIT_CODE, STACK_BASE, (WitU64)EXIT_REQUEST) == WIT_STATUS_BAD_HANDLE);
    EXIT_REQUEST->Event = EXIT_EVENT;
    worker_check(EXIT_WORD == 1); /* nothing of a refused request was served */
    /* The kernel releases the stack's reservation once this thread no longer runs on it, zeroes the word and sets the
     * event. */
    worker_call(WIT_CALL_THREAD_EXIT, EXIT_CODE, STACK_BASE, (WitU64)EXIT_REQUEST);
    worker_failed();
}

static WitU64 create(WitU64 entry, WitU64 sp, WitU64 tls, WitU32 version, WitU32 flags, WitU64 expected)
{
    WitThreadCreateRequest2 request;
    request.Version = version;
    request.Size = sizeof(request);
    request.Entry = entry;
    request.Argument = ARGUMENT;
    request.StackPointer = sp;
    request.TlsBase = tls;
    request.Flags = flags;
    request.Reserved = 0;
    return fixture_expect(WIT_CALL_THREAD_CREATE, (WitU64)&request, sizeof(request), 0, expected);
}

C_ENTRY void threads2_main(const WitUserStartup *startup)
{
    fixture_check(startup->Version == WIT_ABI_VERSION, 2);
    /* QUERY: the ABI version in the low half of the value. */
    fixture_check((WitU32)fixture_expect(WIT_CALL_QUERY, 0, 0, 0, WIT_STATUS_OK) == WIT_ABI_VERSION, 3);
    *(volatile WitU64 *)TLS_FIRST = 0x1234;
    *(volatile WitU64 *)TLS_SECOND = 0x5678;
#if defined(__x86_64__)
    /* THREAD_SET_TLS: the FS base of this thread moves to a block of the data page. */
    fixture_expect(WIT_CALL_THREAD_SET_TLS, TLS_FIRST, 0, 0, WIT_STATUS_OK);
    fixture_check(tls_read() == 0x1234, 4);
    fixture_expect(WIT_CALL_THREAD_SET_TLS, TLS_FIRST, 1, 0, WIT_STATUS_INVALID_ARGUMENT);
    fixture_expect(WIT_CALL_THREAD_SET_TLS, KERNEL_ADDRESS, 0, 0, WIT_STATUS_INVALID_ARGUMENT);
#else
    /* THREAD_SET_TLS is not this ISA's: TPIDR_EL0 belongs to EL0. */
    fixture_expect(WIT_CALL_THREAD_SET_TLS, TLS_FIRST, 0, 0, WIT_STATUS_UNSUPPORTED);
#endif
    /* A stack of our own: a reservation of 64 KiB, committed writable. */
    const WitU64 stack = fixture_expect(WIT_CALL_MEMORY_RESERVE, STACK_BYTES, 4096, 0, WIT_STATUS_OK);
    STACK_BASE = stack;
    fixture_expect(WIT_CALL_MEMORY_COMMIT, stack, STACK_BYTES, WIT_MEMORY_READ | WIT_MEMORY_WRITE, WIT_STATUS_OK);
    /* Refusals of the version 2 request: a stack pointer outside any reservation (the data page is a fixed mapping), a
     * foreign version, a flag, a TLS base outside user space, a stack pointer off alignment. */
    const WitU64 entry = (WitU64)worker, top = stack + STACK_BYTES;
    create(entry, WIT_USER_DATA, TLS_FIRST, WIT_THREAD_CREATE_VERSION_2, 0, WIT_STATUS_BAD_ADDRESS);
    create(entry, top, TLS_FIRST, 3, 0, WIT_STATUS_UNSUPPORTED);
    create(entry, top, TLS_FIRST, WIT_THREAD_CREATE_VERSION_2, 2, WIT_STATUS_INVALID_ARGUMENT);
    create(entry, top, KERNEL_ADDRESS, WIT_THREAD_CREATE_VERSION_2, 0, WIT_STATUS_INVALID_ARGUMENT);
    create(entry, top - 8, TLS_FIRST, WIT_THREAD_CREATE_VERSION_2, 0, WIT_STATUS_INVALID_ARGUMENT);
    /* A version 1 thread (this one, on the kernel's stack) may not name a reservation at its exit. */
    fixture_expect(WIT_CALL_THREAD_EXIT, 7, WIT_USER_DATA, 0, WIT_STATUS_INVALID_ARGUMENT);
    /* An event and a word for the worker's exit request (S2.1). */
    EXIT_EVENT = fixture_expect(WIT_CALL_EVENT_CREATE, 0, 0, 0, WIT_STATUS_OK);
    EXIT_WORD = 1;
    /* The worker on our stack with our TLS base; its argument is 0x77. */
    const WitU64 thread = create(entry, top, TLS_FIRST, WIT_THREAD_CREATE_VERSION_2, 0, WIT_STATUS_OK);
    fixture_check(fixture_join(thread) == EXIT_CODE, 5);
    /* The exit request was served: the word is zero and the event is set; the exit released the reservation. */
    fixture_check(EXIT_WORD == 0, 6);
    const WitU64 event = EXIT_EVENT;
    fixture_wait(&event, 1, WIT_WAIT_INFINITE, WIT_STATUS_OK);
    fixture_close(event);
    fixture_expect(WIT_CALL_MEMORY_RELEASE, stack, 0, 0, WIT_STATUS_NOT_RESERVED);
    fixture_exit(WIT_TEST_EXIT_CODE);
}
