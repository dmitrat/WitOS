#include "fixture.h"
#include "witos/process.h"
#include "witos/thread_info.h"

/* Process fixture (RFC 0011 v3 section 7.8, plan steps K5.2c and K8.3). A channel is created and one end goes to a new
 * process through PROCESS_CREATE; the child's program is written into a memory object through a writable view,
 * published through an executable view of the fixture's own, and mapped executable into the child at a fixed code
 * address beside a stack object; THREAD_CREATE version 3 starts the child's first thread with the child-local endpoint
 * handle as its argument; the child sends eight bytes over it and exits with 42. The fixture waits for the process,
 * queries it, receives the message, then creates and kills a second child, and a third spins until its tick budget, the
 * fixture's, ends it, which PROCESS_QUERY reports as BUDGET, on a stack of two objects it takes as one
 * (STACK_ADJACENT, ABI-1 1.1). Refusals are checked whole. */

#define FIXED_CODE (WIT_USER_CODE_BASE + 0x10000ULL)
#define FIXED_DATA (WIT_USER_MEMORY_BASE + 0x100000ULL)
#define READ_WRITE (WIT_MEMORY_READ | WIT_MEMORY_WRITE)
#define READ_EXECUTE (WIT_MEMORY_READ | WIT_MEMORY_EXECUTE)
#define STACK_BYTES 16384ULL
#define MESSAGE 0x1122334455667788ULL
#define CHILD_EXIT 42U

/* The child's constants as the assembly spells them, which takes no C suffix. */
#define CHILD_MESSAGE_VERSION 1
#define CHILD_SEND 71
#define CHILD_PROCESS_EXIT 1
_Static_assert(CHILD_MESSAGE_VERSION == WIT_CHANNEL_MESSAGE_VERSION &&
        CHILD_SEND == WIT_CALL_CHANNEL_SEND &&
        CHILD_PROCESS_EXIT == WIT_CALL_PROCESS_EXIT &&
        sizeof(WitChannelMessage) == 40,
    "The child's constants follow ABI-1");
#define STRINGIZE(x) #x
#define STRING(x) STRINGIZE(x)
#define CHILD_MESSAGE_VERSION_TEXT STRING(CHILD_MESSAGE_VERSION)
#define CHILD_SEND_TEXT STRING(CHILD_SEND)
#define CHILD_PROCESS_EXIT_TEXT STRING(CHILD_PROCESS_EXIT)

/* The child's program, copied into its code object: position-independent, the child-local endpoint handle in the first
 * argument register. It sends eight bytes over the endpoint from its stack and exits with 42 (43 on a failure). */
__attribute__((visibility("hidden"))) extern const WitU8 process_child_begin[], process_child_spin[],
    process_child_end[];
#if defined(__x86_64__)
__asm__(".text\n"
        ".globl process_child_begin\n.hidden process_child_begin\n"
        "process_child_begin:\n"
        "    mov %rdi, %r12\n"
        "    sub $128, %rsp\n"
        "    movl $" CHILD_MESSAGE_VERSION_TEXT ", (%rsp)\n"
        "    movl $40, 4(%rsp)\n"
        "    lea 64(%rsp), %rax\n"
        "    mov %rax, 8(%rsp)\n"
        "    movq $0, 16(%rsp)\n"
        "    movl $8, 24(%rsp)\n"
        "    movl $0, 28(%rsp)\n"
        "    movq $0, 32(%rsp)\n"
        "    movabs $0x1122334455667788, %rax\n"
        "    mov %rax, 64(%rsp)\n"
        "    mov %r12, %rdi\n"
        "    mov %rsp, %rsi\n"
        "    mov $40, %edx\n"
        "    mov $" CHILD_SEND_TEXT ", %eax\n"
        "    syscall\n"
        "    mov $42, %edi\n"
        "    test %eax, %eax\n"
        "    je 1f\n"
        "    mov $43, %edi\n"
        "1:\n"
        "    xor %esi, %esi\n"
        "    xor %edx, %edx\n"
        "    mov $" CHILD_PROCESS_EXIT_TEXT ", %eax\n"
        "    syscall\n"
        "    ud2\n"
        ".globl process_child_spin\n.hidden process_child_spin\n"
        "process_child_spin:\n"
        "    jmp process_child_spin\n"
        ".globl process_child_end\n.hidden process_child_end\n"
        "process_child_end:\n");
#elif defined(__aarch64__)
__asm__(".text\n"
        ".globl process_child_begin\n.hidden process_child_begin\n"
        "process_child_begin:\n"
        "    mov x19, x0\n"
        "    sub sp, sp, #128\n"
        "    mov w9, #" CHILD_MESSAGE_VERSION_TEXT "\n"
        "    str w9, [sp]\n"
        "    mov w9, #40\n"
        "    str w9, [sp, #4]\n"
        "    add x9, sp, #64\n"
        "    str x9, [sp, #8]\n"
        "    str xzr, [sp, #16]\n"
        "    mov w9, #8\n"
        "    str w9, [sp, #24]\n"
        "    str wzr, [sp, #28]\n"
        "    str xzr, [sp, #32]\n"
        "    movz x9, #0x7788\n"
        "    movk x9, #0x5566, lsl #16\n"
        "    movk x9, #0x3344, lsl #32\n"
        "    movk x9, #0x1122, lsl #48\n"
        "    str x9, [sp, #64]\n"
        "    mov x0, x19\n"
        "    mov x1, sp\n"
        "    mov x2, #40\n"
        "    mov x8, #" CHILD_SEND_TEXT "\n"
        "    svc #0\n"
        "    mov x9, #42\n"
        "    cbz x0, 1f\n"
        "    mov x9, #43\n"
        "1:\n"
        "    mov x0, x9\n"
        "    mov x1, #0\n"
        "    mov x2, #0\n"
        "    mov x8, #" CHILD_PROCESS_EXIT_TEXT "\n"
        "    svc #0\n"
        "    udf #0\n"
        ".globl process_child_spin\n.hidden process_child_spin\n"
        "process_child_spin:\n"
        "    b process_child_spin\n"
        ".globl process_child_end\n.hidden process_child_end\n"
        "process_child_end:\n");
#else
__asm__(".text\n"
        ".globl process_child_begin\n.hidden process_child_begin\n"
        "process_child_begin:\n"
        "    mv s1, a0\n"
        "    addi sp, sp, -128\n"
        "    li t0, " CHILD_MESSAGE_VERSION_TEXT "\n"
        "    sw t0, 0(sp)\n"
        "    li t0, 40\n"
        "    sw t0, 4(sp)\n"
        "    addi t0, sp, 64\n"
        "    sd t0, 8(sp)\n"
        "    sd zero, 16(sp)\n"
        "    li t0, 8\n"
        "    sw t0, 24(sp)\n"
        "    sw zero, 28(sp)\n"
        "    sd zero, 32(sp)\n"
        "    li t0, 0x1122334455667788\n"
        "    sd t0, 64(sp)\n"
        "    mv a0, s1\n"
        "    mv a1, sp\n"
        "    li a2, 40\n"
        "    li a7, " CHILD_SEND_TEXT "\n"
        "    ecall\n"
        "    li t0, 42\n"
        "    beqz a0, 1f\n"
        "    li t0, 43\n"
        "1:\n"
        "    mv a0, t0\n"
        "    li a1, 0\n"
        "    li a2, 0\n"
        "    li a7, " CHILD_PROCESS_EXIT_TEXT "\n"
        "    ecall\n"
        "    unimp\n"
        ".globl process_child_spin\n.hidden process_child_spin\n"
        "process_child_spin:\n"
        "    j process_child_spin\n"
        ".globl process_child_end\n.hidden process_child_end\n"
        "process_child_end:\n");
#endif

/* PROCESS_CREATE of a child with the endpoint; the child-local endpoint handle in *local. */
static WitU64 process_create(WitU64 endpoint, WitU64 *local, WitU64 expected)
{
    WitProcessCreateRequest request;
    request.Version = WIT_PROCESS_CREATE_VERSION;
    request.Size = sizeof(request);
    request.Endpoint = endpoint;
    request.Pages = 0;
    request.Flags = 0;
    request.Reserved = 0;
    return fixture_expect(WIT_CALL_PROCESS_CREATE, (WitU64)&request, sizeof(request), (WitU64)local, expected);
}

static void process_query(WitU64 process, WitProcessInfo *info)
{
    info->Version = WIT_PROCESS_INFO_VERSION;
    info->Size = sizeof(*info);
    fixture_expect(WIT_CALL_PROCESS_QUERY, process, (WitU64)info, sizeof(*info), WIT_STATUS_OK);
}

static WitU64 thread_create3_at(WitU64 process, WitU64 entry, WitU64 sp, WitU64 argument, WitU32 flags, WitU64 expected)
{
    WitThreadCreateRequest3 request;
    request.Version = WIT_THREAD_CREATE_VERSION_3;
    request.Size = sizeof(request);
    request.Entry = entry;
    request.Argument = argument;
    request.StackPointer = sp;
    request.TlsBase = 0;
    request.Process = process;
    request.Flags = flags;
    request.Reserved = 0;
    return fixture_expect(WIT_CALL_THREAD_CREATE, (WitU64)&request, sizeof(request), 0, expected);
}

static WitU64 thread_create3(WitU64 process, WitU64 sp, WitU64 argument, WitU64 expected)
{
    return thread_create3_at(process, FIXED_CODE, sp, argument, 0, expected);
}

/* MEMORY_OBJECT_MAP of an object's first bytes into a target process. */
static WitU64 map_into(WitU64 object, WitU64 bytes, WitU64 address, WitU32 protection, WitU64 target, WitU64 expected)
{
    WitMemoryMapRequest request;
    request.Version = WIT_MEMORY_MAP_VERSION;
    request.Size = sizeof(request);
    request.Object = object;
    request.Offset = 0;
    request.Bytes = bytes;
    request.Address = address;
    request.Protection = protection;
    request.Flags = 0;
    request.Target = target;
    return fixture_expect(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&request, sizeof(request), 0, expected);
}

FIXTURE_ENTRY void wit_user_start(const WitRootStartup *startup)
{
    WitU64 first[2] = {0, 0}, second[2] = {0, 0}, third[2] = {0, 0}, local = 0;
    WitProcessInfo info;
    fixture_check(startup->AbiVersion == WIT_ABI_VERSION, 1);
    /* QUERY reports the process family. */
    fixture_check((fixture_expect(WIT_CALL_QUERY, 0, 0, 0, WIT_STATUS_OK) >> 32) & WIT_ABI_FEATURE_PROCESSES, 2);
    /* A channel: the first end stays here, the second goes to the child. */
    fixture_expect(WIT_CALL_CHANNEL_CREATE, (WitU64)first, 0, 0, WIT_STATUS_OK);
    /* The code object; it is no endpoint, so PROCESS_CREATE refuses it. */
    const WitU64 code = fixture_expect(WIT_CALL_MEMORY_OBJECT_CREATE, 4096, 0, 0, WIT_STATUS_OK);
    process_create(code, &local, WIT_STATUS_WRONG_TYPE);
    /* The child, with the second end, which is gone from this table; live, no thread yet. */
    const WitU64 child = process_create(first[1], &local, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_HANDLE_CLOSE, first[1], 0, 0, WIT_STATUS_BAD_HANDLE);
    process_query(child, &info);
    fixture_check(info.State == WIT_PROCESS_STATE_LIVE && info.Threads == 0, 3);
    /* The child's program: written through a writable view, published through an executable view of our own. */
    const WitU64 writable = map_into(code, 4096, 0, READ_WRITE, WIT_PROCESS_SELF, WIT_STATUS_OK);
    for (WitU64 i = 0; i < (WitU64)(process_child_end - process_child_begin); ++i) {
        ((volatile WitU8 *)writable)[i] = ((const volatile WitU8 *)process_child_begin)[i];
    }
    map_into(code, 4096, FIXED_CODE, READ_EXECUTE, WIT_PROCESS_SELF, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_CODE_PUBLISH, FIXED_CODE, 4096, 0, WIT_STATUS_OK);
    /* A handle without MANAGE may not map into the child; the full handle maps the code at the same fixed address. */
    const WitU64 observer = fixture_duplicate(child, WIT_RIGHT_WAIT | WIT_RIGHT_QUERY, WIT_STATUS_OK);
    map_into(code, 4096, FIXED_CODE, READ_EXECUTE, observer, WIT_STATUS_DENIED);
    fixture_check(map_into(code, 4096, FIXED_CODE, READ_EXECUTE, child, WIT_STATUS_OK) == FIXED_CODE, 4);
    /* The stack object, mapped writable into the child. */
    const WitU64 stack = fixture_expect(WIT_CALL_MEMORY_OBJECT_CREATE, STACK_BYTES, 0, 0, WIT_STATUS_OK);
    map_into(stack, STACK_BYTES, FIXED_DATA, READ_WRITE, child, WIT_STATUS_OK);
    /* The first thread: a stack pointer outside the child's reservations is refused; the stack's top starts it. */
    thread_create3(child, FIXED_DATA + 2 * STACK_BYTES, local, WIT_STATUS_BAD_ADDRESS);
    const WitU64 thread = thread_create3(child, FIXED_DATA + STACK_BYTES, local, WIT_STATUS_OK);
    /* The process ends with the child's exit: its handle is ready, its state exited with 42, its thread gone. */
    fixture_wait(&child, 1, WIT_WAIT_INFINITE, WIT_STATUS_OK);
    process_query(child, &info);
    fixture_check(info.State == WIT_PROCESS_STATE_EXITED && info.Threads == 0 && info.ExitCode == CHILD_EXIT, 5);
    fixture_wait(&thread, 1, WIT_WAIT_INFINITE, WIT_STATUS_OK);
    /* The child's message arrived on the first end: eight bytes, no handle. */
    WitU64 data[2] = {0, 0};
    WitChannelMessage message;
    fixture_message(&message, (WitU64)data, 16, 0, 0);
    fixture_check(
        fixture_expect(WIT_CALL_CHANNEL_RECEIVE, first[0], (WitU64)&message, sizeof(message), WIT_STATUS_OK) == 8, 6);
    fixture_check(data[0] == MESSAGE, 7);
    /* A thread into the ended process is refused. */
    thread_create3(child, FIXED_DATA + STACK_BYTES, local, WIT_STATUS_CLOSED);
    /* A second child, killed before it has a thread: exited with the code given, its handle ready. */
    fixture_expect(WIT_CALL_CHANNEL_CREATE, (WitU64)second, 0, 0, WIT_STATUS_OK);
    const WitU64 killed = process_create(second[1], &local, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_PROCESS_KILL, killed, 7, 0, WIT_STATUS_OK);
    process_query(killed, &info);
    fixture_check(info.State == WIT_PROCESS_STATE_EXITED && info.ExitCode == 7, 8);
    fixture_wait(&killed, 1, WIT_WAIT_INFINITE, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_PROCESS_KILL, killed, 7, 0, WIT_STATUS_CLOSED);
    /* A third child spins until its tick budget, the fixture's, ends it: BUDGET, no thread left (ABI-1 1.1). Its stack
     * is two objects at adjacent addresses, which STACK_ADJACENT takes as one; an unknown flag is refused. */
    fixture_expect(WIT_CALL_CHANNEL_CREATE, (WitU64)third, 0, 0, WIT_STATUS_OK);
    const WitU64 spinner = process_create(third[1], &local, WIT_STATUS_OK);
    map_into(code, 4096, FIXED_CODE, READ_EXECUTE, spinner, WIT_STATUS_OK);
    map_into(stack, STACK_BYTES, FIXED_DATA, READ_WRITE, spinner, WIT_STATUS_OK);
    const WitU64 below = fixture_expect(WIT_CALL_MEMORY_OBJECT_CREATE, STACK_BYTES, 0, 0, WIT_STATUS_OK);
    map_into(below, STACK_BYTES, FIXED_DATA - STACK_BYTES, READ_WRITE, spinner, WIT_STATUS_OK);
    const WitU64 spin_entry = FIXED_CODE + (WitU64)(process_child_spin - process_child_begin);
    thread_create3_at(spinner, spin_entry, FIXED_DATA + STACK_BYTES, local, 4, WIT_STATUS_INVALID_ARGUMENT);
    const WitU64 spinning = thread_create3_at(
        spinner, spin_entry, FIXED_DATA + STACK_BYTES, local, WIT_THREAD_STACK_ADJACENT, WIT_STATUS_OK);
    WitUserThreadInfo spun;
    spun.Version = WIT_THREAD_INFO_VERSION;
    spun.Size = sizeof(spun);
    fixture_expect(WIT_CALL_THREAD_QUERY, spinning, (WitU64)&spun, sizeof(spun), WIT_STATUS_OK);
    fixture_check(spun.StackLow == FIXED_DATA - STACK_BYTES && spun.StackHigh == FIXED_DATA + STACK_BYTES, 10);
    fixture_wait(&spinner, 1, WIT_WAIT_INFINITE, WIT_STATUS_OK);
    process_query(spinner, &info);
    fixture_check(info.State == WIT_PROCESS_STATE_BUDGET && info.Threads == 0, 9);
    /* Everything goes: the process handles, the thread handle, the channels, the views and the objects. */
    fixture_close(killed);
    fixture_close(second[0]);
    fixture_close(spinning);
    fixture_close(spinner);
    fixture_close(third[0]);
    fixture_close(child);
    fixture_close(observer);
    fixture_close(thread);
    fixture_close(first[0]);
    fixture_expect(WIT_CALL_MEMORY_RELEASE, FIXED_CODE, 0, 0, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_MEMORY_RELEASE, writable, 0, 0, WIT_STATUS_OK);
    fixture_close(code);
    fixture_close(stack);
    fixture_close(below);
    fixture_exit(WIT_TEST_EXIT_CODE);
}
