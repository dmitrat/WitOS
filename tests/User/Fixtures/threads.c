#include "fixture.h"

/* Thread fixture over the ABI-1 thread family of RFC 0011 v3 (plan steps K1.2 and K8.3): THREAD_CREATE of the one form
 * on the fixture's own stacks, a join as OBJECT_WAIT on the thread handle followed by THREAD_QUERY for the exit code and
 * HANDLE_CLOSE, a closed handle that detaches a live thread, a reaped slot that comes back with another handle, the
 * capacity of a component's threads, and a thread's faults: an undefined instruction, the guard below and above its
 * stack, a stack pointer on another thread's stack at the return, and a thread that ends the process. Two workers
 * preempt each other while each checks that its TLS, its SIMD registers, a word below its stack pointer and its own
 * floating-point rounding mode survive; that part is the assembly of threads_worker. */

#define RECORDS ((volatile FixtureThreadRecord *)(WIT_USER_DATA + 0x100))
#define FLAGS ((volatile WitU64 *)WIT_USER_DATA) /* a worker's start at its index, the first worker's end at 3 */
#define RELEASE_FLAG (*(volatile WitU64 *)(WIT_USER_DATA + 0x200))
#define FOREIGN_HANDLE 0xFFFFFFFF00010001ULL
_Static_assert(FIXTURE_STACK_BYTES == WIT_THREAD_TEST_STACK_BYTES, "The fault worker's stack is the kernel test's");

#define ASSEMBLY __attribute__((visibility("hidden")))
/* The preemption worker: its record in the argument register; it ends in threads_worker_done. */
ASSEMBLY void threads_worker(WitU64 record);
ASSEMBLY WIT_NORETURN void threads_worker_done(volatile FixtureThreadRecord *record);
ASSEMBLY WIT_NORETURN void threads_worker_failed(void);
/* A byte write, an undefined instruction and a system call with the stack pointer at sp; none returns where a fault is
 * due. */
ASSEMBLY void threads_write(WitU64 address);
ASSEMBLY WIT_NORETURN void threads_trap_undefined(void);
ASSEMBLY WIT_NORETURN void threads_call_on(WitU64 number, WitU64 sp);

#if defined(__x86_64__)
__asm__(".text\n"
        ".globl threads_worker\n.hidden threads_worker\n"
        "threads_worker:\n"
        /* No call or prologue has touched the new stack: zero SIMD registers and the whole stack zero. */
        "    mov %rdi, %rbx\n"
        "    movq %xmm0, %rax\n"
        "    test %rax, %rax\n"
        "    jne threads_worker_failed\n"
        "    movq %xmm6, %rax\n"
        "    test %rax, %rax\n"
        "    jne threads_worker_failed\n"
        "    mov (%rbx), %rdi\n"
        "    mov $2048, %ecx\n"
        "    xor %eax, %eax\n"
        "    repe scasq\n"
        "    jne threads_worker_failed\n"
        /* The TLS base is the record's word: zero at first, then the index, which the record shows. */
        "    mov 8(%rbx), %r12\n"
        "    cmpq $0, %fs:0\n"
        "    jne threads_worker_failed\n"
        "    mov %r12, %fs:0\n"
        "    cmp 16(%rbx), %r12\n"
        "    jne threads_worker_failed\n"
        "    movq %r12, %xmm6\n"
        "    movabs $0x123456789ABCDEF0, %r13\n"
        "    add %r12, %r13\n"
        "    movq %r13, %xmm0\n"
        "    mov %r13, -16(%rsp)\n"
        "    mov $0x1F80, %eax\n"
        "    cmp $1, %r12\n"
        "    jne 1f\n"
        "    mov $0x3F80, %eax\n" /* round toward zero in the first worker only */
        "1:\n"
        "    mov %eax, -24(%rsp)\n"
        "    ldmxcsr -24(%rsp)\n"
        "    movabs $0x0000008000008000, %rdi\n"
        "    cmp $3, %r12\n"
        "    jae 4f\n"
        "    movq $1, (%rdi,%r12,8)\n"
        "2:\n"
        "    cmp %r12, %fs:0\n"
        "    jne threads_worker_failed\n"
        "    movq %xmm6, %rax\n"
        "    cmp %rax, %r12\n"
        "    jne threads_worker_failed\n"
        "    movq %xmm0, %rax\n"
        "    cmp %rax, %r13\n"
        "    jne threads_worker_failed\n"
        "    cmp %r13, -16(%rsp)\n"
        "    jne threads_worker_failed\n"
        "    stmxcsr -32(%rsp)\n"
        "    mov -24(%rsp), %eax\n"
        "    cmp %eax, -32(%rsp)\n"
        "    jne threads_worker_failed\n"
        "    incq %fs:8\n"
        "    cmp $1, %r12\n"
        "    jne 3f\n"
        "    cmpq $0, 16(%rdi)\n"
        "    je 2b\n"
        "    movq $1, 24(%rdi)\n"
        "    jmp 4f\n"
        "3:\n"
        "    cmpq $0, 24(%rdi)\n"
        "    je 2b\n"
        "4:\n"
        "    mov %rbx, %rdi\n"
        "    jmp threads_worker_done\n"
        ".globl threads_write\n.hidden threads_write\n"
        "threads_write:\n"
        "    movb $1, (%rdi)\n"
        "    ret\n"
        ".globl threads_trap_undefined\n.hidden threads_trap_undefined\n"
        "threads_trap_undefined:\n"
        "    ud2\n"
        ".globl threads_call_on\n.hidden threads_call_on\n"
        "threads_call_on:\n"
        "    mov %rdi, %rax\n"
        "    mov %rsi, %rsp\n"
        "    syscall\n"
        "1:\n"
        "    pause\n"
        "    jmp 1b\n");
#else
__asm__(".text\n"
        ".globl threads_worker\n.hidden threads_worker\n"
        "threads_worker:\n"
        "    mov x23, x0\n"
        "    fmov x9, d0\n"
        "    cbnz x9, threads_worker_failed\n"
        "    fmov x9, d8\n"
        "    cbnz x9, threads_worker_failed\n"
        "    mrs x9, fpcr\n"
        "    cbnz x9, threads_worker_failed\n"
        "    ldr x25, [x23]\n"
        "    mov x10, #2048\n"
        "1:\n"
        "    ldr x9, [x25], #8\n"
        "    cbnz x9, threads_worker_failed\n"
        "    subs x10, x10, #1\n"
        "    b.ne 1b\n"
        "    ldr x20, [x23, #8]\n"
        "    mrs x22, tpidrro_el0\n"
        "    add x9, x23, #16\n"
        "    cmp x9, x22\n"
        "    b.ne threads_worker_failed\n"
        "    ldr x9, [x22]\n"
        "    cbnz x9, threads_worker_failed\n"
        "    str x20, [x22]\n"
        "    fmov d8, x20\n"
        "    movz x24, #0xDEF0\n"
        "    movk x24, #0x9ABC, lsl #16\n"
        "    movk x24, #0x5678, lsl #32\n"
        "    movk x24, #0x1234, lsl #48\n"
        "    add x24, x24, x20\n"
        "    fmov d0, x24\n"
        "    stur x24, [sp, #-16]\n" /* below SP: no exception entry may touch the EL0 stack */
        "    mov x26, #0\n"
        "    cmp x20, #1\n"
        "    b.ne 2f\n"
        "    mov x26, #0x00C00000\n" /* round toward zero in the first worker only */
        "2:\n"
        "    msr fpcr, x26\n"
        "    movz x25, #0x8000\n"
        "    movk x25, #0x80, lsl #32\n"
        "    cmp x20, #3\n"
        "    b.hs 5f\n"
        "    mov x9, #1\n"
        "    str x9, [x25, x20, lsl #3]\n"
        "3:\n"
        "    mrs x9, tpidrro_el0\n"
        "    cmp x9, x22\n"
        "    b.ne threads_worker_failed\n"
        "    ldr x9, [x22]\n"
        "    cmp x9, x20\n"
        "    b.ne threads_worker_failed\n"
        "    fmov x9, d8\n"
        "    cmp x9, x20\n"
        "    b.ne threads_worker_failed\n"
        "    fmov x9, d0\n"
        "    cmp x9, x24\n"
        "    b.ne threads_worker_failed\n"
        "    ldur x9, [sp, #-16]\n"
        "    cmp x9, x24\n"
        "    b.ne threads_worker_failed\n"
        "    mrs x9, fpcr\n"
        "    cmp x9, x26\n"
        "    b.ne threads_worker_failed\n"
        "    ldr x9, [x22, #8]\n"
        "    add x9, x9, #1\n"
        "    str x9, [x22, #8]\n"
        "    cmp x20, #1\n"
        "    b.ne 4f\n"
        "    ldr x9, [x25, #16]\n"
        "    cbz x9, 3b\n"
        "    mov x9, #1\n"
        "    str x9, [x25, #24]\n"
        "    b 5f\n"
        "4:\n"
        "    ldr x9, [x25, #24]\n"
        "    cbz x9, 3b\n"
        "5:\n"
        "    mov x0, x23\n"
        "    b threads_worker_done\n"
        ".globl threads_write\n.hidden threads_write\n"
        "threads_write:\n"
        "    mov w9, #1\n"
        "    strb w9, [x0]\n"
        "    ret\n"
        ".globl threads_trap_undefined\n.hidden threads_trap_undefined\n"
        "threads_trap_undefined:\n"
        "    udf #0\n"
        ".globl threads_call_on\n.hidden threads_call_on\n"
        "threads_call_on:\n"
        "    mov x8, x0\n"
        "    mov sp, x1\n"
        "    svc #0\n"
        "1:\n"
        "    yield\n"
        "    b 1b\n");
#endif

#if defined(__x86_64__)
/* C reached by a jump from the worker's assembly, whose stack holds no return address. */
#define JUMPED __attribute__((force_align_arg_pointer))
#else
#define JUMPED
#endif

JUMPED void threads_worker_done(volatile FixtureThreadRecord *record)
{
    fixture_thread_exit(100 + record->Index, record->Stack);
}

JUMPED void threads_worker_failed(void)
{
    fixture_failed(0x100);
}

static FIXTURE_THREAD void capacity_worker(WitU64 argument)
{
    volatile FixtureThreadRecord *record = (volatile FixtureThreadRecord *)argument;
    while (!RELEASE_FLAG) {
        fixture_expect(WIT_CALL_THREAD_YIELD, 0, 0, 0, WIT_STATUS_OK);
    }
    fixture_thread_exit(record->Index, record->Stack);
}

/* A thread's fault on the stack of the kernel test's fixed address, by the mode it receives. */
static FIXTURE_THREAD void fault_worker(WitU64 mode)
{
    switch (mode) {
    case WIT_THREAD_TEST_GUARD_LOW:
        threads_write(WIT_THREAD_TEST_STACK - 1);
        break;
    case WIT_THREAD_TEST_GUARD_HIGH:
        threads_write(WIT_THREAD_TEST_STACK + WIT_THREAD_TEST_STACK_BYTES);
        break;
    case WIT_THREAD_TEST_BAD_RETURN:
        threads_call_on(WIT_CALL_QUERY, WIT_USER_STACK_TOP - 40); /* mapped, but the first thread's stack */
    case WIT_THREAD_TEST_PROCESS_EXIT:
        fixture_exit(WIT_TEST_EXIT_CODE);
    default:
        threads_trap_undefined();
    }
    fixture_failed(0x101);
}

static WitU64 start(FixtureThread entry, WitU32 index, WitU64 expected)
{
    volatile FixtureThreadRecord *record = &RECORDS[index];
    record->Index = index;
    record->Value = 0;
    record->Reserved = 0;
    return fixture_thread_start(entry, record, (WitU64)&record->Value, expected);
}

static void normal_test(const WitUserTestConfig *config)
{
    /* A wait on what is no thread or on a foreign handle; a creation with an entry outside the code or a reserved flag. */
    const WitU64 console = config->Startup.Handles[WIT_ROOT_HANDLE_LOG], foreign = FOREIGN_HANDLE;
    fixture_wait(&console, 1, WIT_WAIT_INFINITE, WIT_STATUS_WRONG_TYPE);
    fixture_wait(&foreign, 1, WIT_WAIT_INFINITE, WIT_STATUS_BAD_HANDLE);
    const WitU64 stack = fixture_stack(0);
    fixture_thread((FixtureThread)WIT_USER_DATA, 0, stack, 0, 0, WIT_STATUS_BAD_ADDRESS);
    fixture_check(fixture_thread(threads_worker, 0, stack, 0, 4, WIT_STATUS_INVALID_ARGUMENT) == 0, 10);
    fixture_expect(WIT_CALL_MEMORY_RELEASE, stack, 0, 0, WIT_STATUS_OK);
    /* Two workers preempt each other; each ends with 100 plus its index. */
    const WitU64 first = start(threads_worker, 1, WIT_STATUS_OK);
    const WitU64 second = start(threads_worker, 2, WIT_STATUS_OK);
    fixture_check(fixture_join(first) == 101, 11);
    fixture_check(fixture_join(second) == 102, 12);
    fixture_check(FLAGS[1] == 1 && FLAGS[2] == 1 && FLAGS[3] == 1, 13);
    /* A reaped slot comes back with a fresh stack and TLS and another handle. */
    const WitU64 third = start(threads_worker, 3, WIT_STATUS_OK);
    fixture_check(third != first, 14);
    fixture_wait(&first, 1, WIT_WAIT_INFINITE, WIT_STATUS_BAD_HANDLE);
    fixture_expect(WIT_CALL_THREAD_YIELD, 0, 0, 0, WIT_STATUS_OK);
    fixture_check(fixture_join(third) == 103, 15);
    fixture_wait(&third, 1, WIT_WAIT_INFINITE, WIT_STATUS_BAD_HANDLE);
    /* Closing the handle of a live thread detaches it; the thread runs to its exit and is reaped there. */
    const WitU64 fourth = start(threads_worker, 4, WIT_STATUS_OK);
    fixture_close(fourth);
    fixture_wait(&fourth, 1, WIT_WAIT_INFINITE, WIT_STATUS_BAD_HANDLE);
    fixture_expect(WIT_CALL_THREAD_YIELD, 0, 0, 0, WIT_STATUS_OK);
}

static void capacity_test(void)
{
    /* Three threads beside the first fill the component; a fourth is refused with no handle and no stack left. */
    WitU64 threads[3];
    for (WitU32 i = 0; i < 3; ++i) {
        threads[i] = start(capacity_worker, i + 1, WIT_STATUS_OK);
    }
    fixture_check(start(capacity_worker, 4, WIT_STATUS_NO_MEMORY) == 0, 20);
    RELEASE_FLAG = 1;
    for (WitU32 i = 0; i < 3; ++i) {
        fixture_check(fixture_join(threads[i]) == i + 1, 21);
    }
}

FIXTURE_ENTRY void wit_user_start(const WitRootStartup *startup)
{
    const WitUserTestConfig *config = fixture_config(startup);
    fixture_check(startup->AbiVersion == WIT_ABI_VERSION, 1);
    if (config->Mode == WIT_THREAD_TEST_NORMAL) {
        normal_test(config);
    } else if (config->Mode == WIT_THREAD_TEST_CAPACITY) {
        capacity_test();
    } else {
        /* The thread's fault ends the component before its join returns. */
        const WitU64 stack = fixture_stack(WIT_THREAD_TEST_STACK);
        const WitU64 thread = fixture_thread(fault_worker, config->Mode, stack, 0, 0, WIT_STATUS_OK);
        fixture_wait(&thread, 1, WIT_WAIT_INFINITE, WIT_STATUS_OK);
        fixture_failed(3);
    }
    fixture_exit(WIT_TEST_EXIT_CODE);
}
