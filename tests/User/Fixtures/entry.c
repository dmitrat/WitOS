#include "fixture.h"

/* User isolation and memory fixture (plan steps M2 and K8.3): the first component a self-test kernel runs. It checks the
 * state a component starts in (user mode, zero SIMD state and thread register, zero data pages), the call boundary
 * (an unknown call, handle rights and kinds, buffers the kernel refuses whole, a buffer across two pages, registers
 * that survive a call), the faults the kernel contains (kernel memory, privileged instructions, data execution, the
 * stack's guard pages, read-only code and startup block, a peer's page, address zero, an undefined instruction, a
 * stack outside its owning range at the return), the preemption of a thread whose flags, registers and SIMD state must
 * survive, and the life of dynamic memory (reserve, commit, protect, decommit, release, quota rollback, zero fill).
 * The traps and the register checks are the assembly below; everything else is C. */

#define SENTINEL 0x55AA001100220033ULL
#define DATA_QWORDS ((WIT_USER_DATA_END - WIT_USER_DATA) / 8)
#define READ_WRITE (WIT_MEMORY_READ | WIT_MEMORY_WRITE)
#define RESERVATION 0x800000000ULL /* 32 GiB */
#define NONCANONICAL 0x0000800000000000ULL
#if defined(__x86_64__)
#define RET_INSTRUCTION 0xC3U
#else
#define RET_INSTRUCTION 0xD65F03C0U
#endif

static const char message[] = "Hello from ring 3.\n";

#if defined(__x86_64__)
/* C reached by a jump from the entry's assembly, with the stack pointer 16-byte aligned and no return address. */
#define JUMPED __attribute__((force_align_arg_pointer))
#else
#define JUMPED
#endif

#define ASSEMBLY __attribute__((visibility("hidden")))
/* A system call with the sentinel in the registers a call must keep (RBX, R12-R15 and XMM6 on x64; X19-X22 and both
 * halves of V8 on ARM64): its status, or ~0 when one came back changed; the value lands in *value. */
ASSEMBLY WitU64 entry_preserved_call(WitU64 number, WitU64 a0, WitU64 a1, WitU64 a2, WitU64 *value);
/* A read and a byte write the kernel is to refuse, and the instructions it traps; none returns where a fault is due. */
ASSEMBLY WitU64 entry_read(WitU64 address);
ASSEMBLY void entry_write(WitU64 address, WitU64 value);
ASSEMBLY WIT_NORETURN void entry_trap_privileged(void);
ASSEMBLY WIT_NORETURN void entry_trap_register(void);
ASSEMBLY WIT_NORETURN void entry_trap_undefined(void);
/* A system call with the stack pointer outside the thread's stacks: the kernel refuses the return. */
ASSEMBLY WIT_NORETURN void entry_bad_return(WitU64 number);
/* Spins with the sentinel in a GPR, a SIMD register (and TPIDR_EL0 on ARM64) and equal condition flags; a lost value
 * fails. */
ASSEMBLY WIT_NORETURN void entry_preemption_state(void);
ASSEMBLY WIT_NORETURN void entry_main(const WitUserStartup *startup);
ASSEMBLY WIT_NORETURN void entry_initial_failed(void);
ASSEMBLY WIT_NORETURN void entry_state_lost(void);

#if defined(__x86_64__)
__asm__(".section .text.entry,\"ax\",@progbits\n"
        ".globl wit_user_start\n"
        "wit_user_start:\n"
        "    mov %cs, %ax\n"
        "    and $3, %eax\n"
        "    cmp $3, %eax\n"
        "    jne entry_initial_failed\n"
        "    movq %xmm0, %rax\n"
        "    test %rax, %rax\n"
        "    jne entry_initial_failed\n"
        "    jmp entry_main\n"
        ".text\n"
        ".globl entry_preserved_call\n.hidden entry_preserved_call\n"
        "entry_preserved_call:\n"
        "    push %rbx\n"
        "    push %r12\n"
        "    push %r13\n"
        "    push %r14\n"
        "    push %r15\n"
        "    movabs $0x55AA001100220033, %rbx\n"
        "    mov %rbx, %r12\n"
        "    mov %rbx, %r13\n"
        "    mov %rbx, %r14\n"
        "    mov %rbx, %r15\n"
        "    movq %rbx, %xmm6\n"
        "    mov %rdi, %rax\n"
        "    mov %rsi, %rdi\n"
        "    mov %rdx, %rsi\n"
        "    mov %rcx, %rdx\n"
        "    syscall\n"
        "    mov %rdx, (%r8)\n"
        "    movq %xmm6, %rcx\n"
        "    cmp %rbx, %rcx\n"
        "    jne 1f\n"
        "    cmp %rbx, %r12\n"
        "    jne 1f\n"
        "    cmp %rbx, %r13\n"
        "    jne 1f\n"
        "    cmp %rbx, %r14\n"
        "    jne 1f\n"
        "    cmp %rbx, %r15\n"
        "    jne 1f\n"
        "    movabs $0x55AA001100220033, %rcx\n"
        "    cmp %rcx, %rbx\n"
        "    je 2f\n"
        "1:\n"
        "    mov $-1, %rax\n"
        "2:\n"
        "    pop %r15\n"
        "    pop %r14\n"
        "    pop %r13\n"
        "    pop %r12\n"
        "    pop %rbx\n"
        "    ret\n"
        ".globl entry_read\n.hidden entry_read\n"
        "entry_read:\n"
        "    mov (%rdi), %rax\n"
        "    ret\n"
        ".globl entry_write\n.hidden entry_write\n"
        "entry_write:\n"
        "    mov %sil, (%rdi)\n"
        "    ret\n"
        ".globl entry_trap_privileged\n.hidden entry_trap_privileged\n"
        "entry_trap_privileged:\n"
        "    cli\n"
        "    ud2\n"
        ".globl entry_trap_register\n.hidden entry_trap_register\n"
        "entry_trap_register:\n"
        "    mov $0xF4, %dx\n"
        "    mov $0x10, %eax\n"
        "    out %eax, %dx\n"
        "    ud2\n"
        ".globl entry_trap_undefined\n.hidden entry_trap_undefined\n"
        "entry_trap_undefined:\n"
        "    ud2\n"
        ".globl entry_bad_return\n.hidden entry_bad_return\n"
        "entry_bad_return:\n"
        "    mov %rdi, %rax\n"
        "    movabs $0x0000800000000000, %rsp\n"
        "    syscall\n"
        "1:\n"
        "    pause\n"
        "    jmp 1b\n"
        ".globl entry_preemption_state\n.hidden entry_preemption_state\n"
        "entry_preemption_state:\n"
        "    movabs $0x55AA001100220033, %r12\n"
        "    movq %r12, %xmm6\n"
        "    cmp %r12, %r12\n"
        "1:\n" /* every instruction keeps ZF=1 until the next equal comparison sets it again */
        "    jne entry_state_lost\n"
        "    movq %xmm6, %rax\n"
        "    cmp %rax, %r12\n"
        "    pause\n"
        "    jmp 1b\n");
#else
__asm__(".section .text.entry,\"ax\",%progbits\n"
        ".globl wit_user_start\n"
        "wit_user_start:\n"
        "    fmov x9, d0\n"
        "    cbnz x9, entry_initial_failed\n"
        "    mov x9, v0.d[1]\n"
        "    cbnz x9, entry_initial_failed\n"
        "    mrs x9, tpidr_el0\n"
        "    cbnz x9, entry_initial_failed\n"
        "    movz x9, #0x0033\n"
        "    movk x9, #0x0022, lsl #16\n"
        "    movk x9, #0x0011, lsl #32\n"
        "    movk x9, #0x55AA, lsl #48\n"
        "    msr tpidr_el0, x9\n"
        "    b entry_main\n"
        ".text\n"
        ".globl entry_preserved_call\n.hidden entry_preserved_call\n"
        "entry_preserved_call:\n"
        "    stp x19, x20, [sp, #-48]!\n"
        "    stp x21, x22, [sp, #16]\n"
        "    str d8, [sp, #32]\n"
        "    movz x19, #0x0033\n"
        "    movk x19, #0x0022, lsl #16\n"
        "    movk x19, #0x0011, lsl #32\n"
        "    movk x19, #0x55AA, lsl #48\n"
        "    mov x20, x19\n"
        "    mov x21, x19\n"
        "    mov x22, x19\n"
        "    fmov d8, x19\n"
        "    mov v8.d[1], x19\n"
        "    mov x8, x0\n"
        "    mov x0, x1\n"
        "    mov x1, x2\n"
        "    mov x2, x3\n"
        "    svc #0\n"
        "    str x1, [x4]\n"
        "    fmov x9, d8\n"
        "    mov x10, v8.d[1]\n"
        "    cmp x9, x19\n"
        "    ccmp x10, x19, #0, eq\n"
        "    ccmp x20, x19, #0, eq\n"
        "    ccmp x21, x19, #0, eq\n"
        "    ccmp x22, x19, #0, eq\n"
        "    b.ne 1f\n"
        "    movz x9, #0x0033\n"
        "    movk x9, #0x0022, lsl #16\n"
        "    movk x9, #0x0011, lsl #32\n"
        "    movk x9, #0x55AA, lsl #48\n"
        "    cmp x9, x19\n"
        "    b.eq 2f\n"
        "1:\n"
        "    mov x0, #-1\n"
        "2:\n"
        "    ldr d8, [sp, #32]\n"
        "    ldp x21, x22, [sp, #16]\n"
        "    ldp x19, x20, [sp], #48\n"
        "    ret\n"
        ".globl entry_read\n.hidden entry_read\n"
        "entry_read:\n"
        "    ldr x0, [x0]\n"
        "    ret\n"
        ".globl entry_write\n.hidden entry_write\n"
        "entry_write:\n"
        "    strb w1, [x0]\n"
        "    ret\n"
        ".globl entry_trap_privileged\n.hidden entry_trap_privileged\n"
        "entry_trap_privileged:\n"
        "    msr daifset, #2\n" /* EL0 may not mask interrupts (SCTLR_EL1.UMA is clear) */
        "    udf #0\n"
        ".globl entry_trap_register\n.hidden entry_trap_register\n"
        "entry_trap_register:\n"
        "    mrs x9, ttbr1_el1\n" /* EL1 system registers are undefined at EL0 */
        "    udf #0\n"
        ".globl entry_trap_undefined\n.hidden entry_trap_undefined\n"
        "entry_trap_undefined:\n"
        "    udf #0\n"
        ".globl entry_bad_return\n.hidden entry_bad_return\n"
        "entry_bad_return:\n"
        "    mov x8, x0\n"
        "    movz x9, #0x8000, lsl #32\n"
        "    mov sp, x9\n"
        "    svc #0\n"
        "1:\n"
        "    yield\n"
        "    b 1b\n"
        ".globl entry_preemption_state\n.hidden entry_preemption_state\n"
        "entry_preemption_state:\n"
        "    mrs x19, tpidr_el0\n" /* the sentinel since the entry */
        "    fmov d8, x19\n"
        "    mov v8.d[1], x19\n"
        "    cmp x19, x19\n"
        "1:\n" /* equal comparisons keep Z and C set */
        "    b.ne entry_state_lost\n"
        "    b.lo entry_state_lost\n"
        "    fmov x9, d8\n"
        "    cmp x9, x19\n"
        "    b.ne entry_state_lost\n"
        "    mov x9, v8.d[1]\n"
        "    cmp x9, x19\n"
        "    b.ne entry_state_lost\n"
        "    mrs x9, tpidr_el0\n"
        "    cmp x9, x19\n"
        "    yield\n"
        "    b 1b\n");
#endif

JUMPED void entry_initial_failed(void)
{
    fixture_failed(0x100);
}

JUMPED void entry_state_lost(void)
{
    fixture_failed(0x101);
}

FIXTURE_CALL WitU64 preserved(WitU64 number, WitU64 a0, WitU64 a1, WitU64 a2, WitU64 expected)
{
    WitU64 value = 0;
    ++FIXTURE_CHECKS;
    const WitU64 status = entry_preserved_call(number, a0, a1, a2, &value);
    if (status != expected) {
        fixture_failed(status);
    }
    return value;
}

FIXTURE_CALL WitU64 write(WitU64 handle, WitU64 buffer, WitU64 bytes, WitU64 expected)
{
    return fixture_expect(WIT_CALL_DEBUG_WRITE, handle, buffer, bytes, expected);
}

static WitU64 try_write(WitU64 handle, WitU64 expected)
{
    return write(handle, (WitU64)message, sizeof(message) - 1, expected);
}

FIXTURE_CALL void check_zero(WitU64 base, WitU64 qwords, WitU64 code)
{
    for (WitU64 i = 0; i < qwords; ++i) {
        fixture_check(((const volatile WitU64 *)base)[i] == 0, code);
    }
}

static WIT_NORETURN void run_at(WitU64 address)
{
#if defined(__x86_64__)
    *(volatile WitU8 *)address = (WitU8)RET_INSTRUCTION;
#else
    *(volatile WitU32 *)address = RET_INSTRUCTION;
#endif
    ((void (*)(void))address)();
    fixture_failed(0x102);
}

static void normal_test(const WitUserTestConfig *config)
{
    const WitU64 console = config->Startup.ConsoleHandle;
    fixture_expect(0xFFFF, 0, 0, 0, WIT_STATUS_UNSUPPORTED);
    try_write(config->ReadOnlyHandle, WIT_STATUS_DENIED);
    try_write(config->SelfHandle, WIT_STATUS_WRONG_TYPE);
    try_write(config->ForeignHandle, WIT_STATUS_BAD_HANDLE);
    try_write(0, WIT_STATUS_BAD_HANDLE);
    /* A buffer beyond the bounds, above user space, in the kernel and past the data pages is refused whole. */
    write(console, (WitU64)message, WIT_DEBUG_WRITE_MAX + 1, WIT_STATUS_TOO_LARGE);
    write(console, ~1ULL, 8, WIT_STATUS_BAD_ADDRESS);
    write(console, config->KernelProbe, 8, WIT_STATUS_BAD_ADDRESS);
    write(console, WIT_USER_DATA_END - 2, 8, WIT_STATUS_BAD_ADDRESS);
    fixture_check(write(console, 0, 0, WIT_STATUS_OK) == 0, 10);
    /* A readable code buffer, with the registers a call keeps, and a buffer across the two data pages. */
    fixture_check(preserved(WIT_CALL_DEBUG_WRITE, console, (WitU64)message, sizeof(message) - 1, WIT_STATUS_OK) ==
            sizeof(message) - 1,
        11);
    *(volatile WitU32 *)(WIT_USER_DATA + 4092) = 0x736F7263U; /* "cros" */
    *(volatile WitU32 *)(WIT_USER_DATA + 4096) = 0x0A677073U; /* "spg\n" */
    fixture_check(write(console, WIT_USER_DATA + 4092, 8, WIT_STATUS_OK) == 8, 12);
    /* A closed handle is gone for every call. */
    fixture_close(console);
    try_write(console, WIT_STATUS_BAD_HANDLE);
    fixture_expect(WIT_CALL_HANDLE_CLOSE, console, 0, 0, WIT_STATUS_BAD_HANDLE);
#if defined(__aarch64__)
    WitU64 thread_register;
    __asm__ volatile("mrs %0, tpidr_el0" : "=r"(thread_register));
    fixture_check(thread_register == SENTINEL, 13); /* every return to EL0 kept TPIDR_EL0 */
#endif
    *(volatile WitU64 *)WIT_USER_DATA = config->InstanceId;
}

/* A buffer in the reservation that the kernel cannot read is refused whole. */
static void bad_buffer(WitU64 console, WitU64 base)
{
    fixture_check(write(console, base, 8, WIT_STATUS_BAD_ADDRESS) == 0, 20);
}

FIXTURE_CALL void memory(WitU64 number, WitU64 a0, WitU64 a1, WitU64 a2, WitU64 expected)
{
    fixture_expect(number, a0, a1, a2, expected);
}

static void memory_test(WitU64 mode, WitU64 console)
{
    volatile WitU64 *const base =
        (volatile WitU64 *)fixture_expect(WIT_CALL_MEMORY_RESERVE, RESERVATION, 0x200000, 0, WIT_STATUS_OK);
    const WitU64 address = (WitU64)base;
    fixture_check(address == WIT_USER_MEMORY_BASE, 21);
    if (mode == WIT_TEST_MEMORY_RESERVED) {
        entry_read(address);
    }
    /* A commit that exhausts the page quota fails with no accessible prefix and allows a retry. */
    fixture_check(fixture_expect(WIT_CALL_MEMORY_COMMIT, address, WIT_USER_PAGE_CAPACITY * 4096ULL, READ_WRITE,
                      WIT_STATUS_NO_MEMORY) == 0,
        22);
    bad_buffer(console, address);
    memory(WIT_CALL_MEMORY_COMMIT, address, 8192, READ_WRITE, WIT_STATUS_OK);
    check_zero(address, 1024, 23);
    base[0] = 0x12345678;
    base[1023] = 0x76543210;
    if (mode == WIT_TEST_MEMORY_NX) {
        run_at(address);
    }
    /* Read-only, then no access, keeping the contents; an idempotent commit keeps NOACCESS and the contents. */
    memory(WIT_CALL_MEMORY_PROTECT, address, 8192, WIT_MEMORY_READ, WIT_STATUS_OK);
    if (mode == WIT_TEST_MEMORY_READONLY) {
        entry_write(address, 0);
    }
    fixture_check(base[0] == 0x12345678 && base[1023] == 0x76543210, 24);
    memory(WIT_CALL_MEMORY_PROTECT, address, 8192, 0, WIT_STATUS_OK);
    if (mode == WIT_TEST_MEMORY_NOACCESS) {
        entry_read(address);
    }
    bad_buffer(console, address);
    memory(WIT_CALL_MEMORY_COMMIT, address, 8192, READ_WRITE, WIT_STATUS_OK);
    bad_buffer(console, address);
    memory(WIT_CALL_MEMORY_PROTECT, address, 8192, READ_WRITE, WIT_STATUS_OK);
    fixture_check(base[0] == 0x12345678 && base[1023] == 0x76543210, 25);
    /* A range containing a hole fails without changing its mapped prefix. */
    memory(WIT_CALL_MEMORY_PROTECT, address, 12288, 0, WIT_STATUS_NOT_COMMITTED);
    base[0] = 0x1111;
    memory(WIT_CALL_MEMORY_DECOMMIT, address, 8192, 0, WIT_STATUS_OK);
    if (mode == WIT_TEST_MEMORY_DECOMMITTED) {
        entry_read(address);
    }
    bad_buffer(console, address);
    /* Physical memory committed with no access, then exposed zero-filled. */
    memory(WIT_CALL_MEMORY_COMMIT, address, 8192, 0, WIT_STATUS_OK);
    bad_buffer(console, address);
    memory(WIT_CALL_MEMORY_PROTECT, address, 8192, READ_WRITE, WIT_STATUS_OK);
    check_zero(address, 1024, 26);
    base[0] = 0x2222;
    memory(WIT_CALL_MEMORY_RELEASE, address, 0, 0, WIT_STATUS_OK); /* the whole reservation (S5.1) */
    if (mode == WIT_TEST_MEMORY_RELEASED) {
        entry_read(address);
    }
    bad_buffer(console, address);
    memory(WIT_CALL_MEMORY_COMMIT, address, 4096, READ_WRITE, WIT_STATUS_NOT_RESERVED);
    memory(WIT_CALL_MEMORY_RELEASE, address, 0, 0, WIT_STATUS_NOT_RESERVED);
    /* The arena's first address again, zero-filled. */
    fixture_check(fixture_expect(WIT_CALL_MEMORY_RESERVE, 8192, 4096, 0, WIT_STATUS_OK) == address, 27);
    memory(WIT_CALL_MEMORY_COMMIT, address, 8192, READ_WRITE, WIT_STATUS_OK);
    check_zero(address, 1024, 28);
    /* A dynamic buffer across a page boundary. */
    *(volatile WitU32 *)(address + 4092) = 0x746D656DU; /* "memt" */
    *(volatile WitU32 *)(address + 4096) = 0x0A747365U; /* "est\n" */
    fixture_check(write(console, address + 4092, 8, WIT_STATUS_OK) == 8, 29);
    /* Refusals: the fixed code, an unaligned range, an executable dynamic protection, a wrapped size, an alignment
     * below the size. */
    memory(WIT_CALL_MEMORY_PROTECT, WIT_USER_CODE, 4096, READ_WRITE, WIT_STATUS_BAD_ADDRESS);
    memory(WIT_CALL_MEMORY_DECOMMIT, address + 1, 4096, 0, WIT_STATUS_INVALID_ARGUMENT);
    memory(WIT_CALL_MEMORY_PROTECT, address, 4096, WIT_MEMORY_READ | WIT_MEMORY_EXECUTE, WIT_STATUS_INVALID_ARGUMENT);
    memory(WIT_CALL_MEMORY_COMMIT, address, ~4095ULL, READ_WRITE, WIT_STATUS_BAD_ADDRESS);
    fixture_check(fixture_expect(WIT_CALL_MEMORY_RESERVE, 4096, 12288, 0, WIT_STATUS_INVALID_ARGUMENT) == 0, 30);
    memory(WIT_CALL_MEMORY_RELEASE, address, 0, 0, WIT_STATUS_OK);
}

JUMPED void entry_main(const WitUserStartup *startup)
{
    const WitUserTestConfig *config = fixture_config(startup);
    /* Zero data pages, before the fixture writes anything there. */
    check_zero(WIT_USER_DATA, DATA_QWORDS, 2);
    fixture_check(startup->Version == WIT_ABI_VERSION && startup->Size == WIT_ABI_STARTUP_SIZE, 1);
    /* QUERY: the ABI version in the low half of the value, the registers a call keeps intact. */
    fixture_check((WitU32)preserved(WIT_CALL_QUERY, 0, 0, 0, WIT_STATUS_OK) == WIT_ABI_VERSION, 3);
    switch (config->Mode) {
    case WIT_TEST_KERNEL_READ:
        entry_read(config->KernelProbe);
        break;
    case WIT_TEST_KERNEL_WRITE:
        entry_write(config->KernelProbe, 0);
        break;
    case WIT_TEST_PRIVILEGED_CLI:
        entry_trap_privileged();
    case WIT_TEST_PRIVILEGED_PORT:
        entry_trap_register();
    case WIT_TEST_EXECUTE_DATA:
        run_at(WIT_USER_DATA);
    case WIT_TEST_GUARD_LOW:
        entry_write(WIT_USER_STACK_BOTTOM - 1, 1);
        break;
    case WIT_TEST_GUARD_HIGH:
        entry_write(WIT_USER_STACK_TOP, 1);
        break;
    case WIT_TEST_WRITE_CODE:
        entry_write(WIT_USER_CODE, 0x90);
        break;
    case WIT_TEST_WRITE_INFO:
        entry_write(WIT_USER_INFO, 0);
        break;
    case WIT_TEST_PEER_READ:
        entry_read(WIT_USER_PEER_PAGE);
        break;
    case WIT_TEST_SPIN:
        for (;;) {
        }
    case WIT_TEST_NULL_READ:
        entry_read(0);
        break;
    case WIT_TEST_INVALID_OPCODE:
        entry_trap_undefined();
    case WIT_TEST_BAD_RETURN:
        entry_bad_return(WIT_CALL_QUERY);
    case WIT_TEST_PREEMPTION_STATE:
        entry_preemption_state();
    case WIT_TEST_NORMAL:
        normal_test(config);
        fixture_exit(WIT_TEST_EXIT_CODE);
    default:
        if (config->Mode >= WIT_TEST_MEMORY_LIFECYCLE) {
            memory_test(config->Mode, startup->ConsoleHandle);
            fixture_exit(WIT_TEST_EXIT_CODE);
        }
    }
    fixture_failed(0x103); /* a fault was due */
}
