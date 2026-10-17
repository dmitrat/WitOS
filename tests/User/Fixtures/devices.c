#include "fixture.h"
#include "witos/process.h"

/* Device fixture over the devices of ABI-1 (RFC 0011 v3 section 7.7, plan steps K3.1 and K8.3): the device table as a
 * read-only memory object, DEVICE_ACQUIRE over it and DEVICE_MEMORY of the acquired device's regions. The fixture knows
 * what the kernel does not: the identity of the board's virtio block function (vendor 0x1AF4, device 0x1001) and that
 * its first region is the function's configuration space, whose first words repeat that identity. The move test (D1.2,
 * ABI-1 1.2) hands the device to a child process: refused while the sender keeps a reference, delivered with the holder
 * when the message carries the sender's last one. */

#define VIRTIO_BLOCK FIXTURE_VIRTIO_BLOCK
#define READ_WRITE (WIT_MEMORY_READ | WIT_MEMORY_WRITE)
#define READ_EXECUTE (WIT_MEMORY_READ | WIT_MEMORY_EXECUTE)
#define CHILD_CODE (WIT_USER_CODE_BASE + 0x10000ULL)
#define CHILD_STACK (WIT_USER_MEMORY_BASE + 0x100000ULL)
#define CHILD_STACK_BYTES 16384ULL

static WitU64 acquire(WitU64 table, WitU64 index, WitU64 expected)
{
    return fixture_expect(WIT_CALL_DEVICE_ACQUIRE, table, index, 0, expected);
}

static WitU64 region(WitU64 device, WitU64 index, WitU64 expected)
{
    return fixture_expect(WIT_CALL_DEVICE_MEMORY, device, index, 0, expected);
}

static void release(WitU64 base)
{
    fixture_expect(WIT_CALL_MEMORY_RELEASE, base, 0, 0, WIT_STATUS_OK);
}

static void table_test(WitU64 table, const volatile WitDeviceDescriptor *block, WitU32 index)
{
    /* The table is read-only: a writable view is refused by the handle's rights, and so is an executable one. */
    fixture_map(table, 0, 4096, READ_WRITE, 0, WIT_STATUS_DENIED);
    fixture_map(table, 0, 4096, READ_EXECUTE, 0, WIT_STATUS_DENIED);
    /* The block function's first region is its configuration space; its words repeat the identity of the table. */
    fixture_check(block->RegionCount >= 1 && (block->Regions[0].Flags & WIT_DEVICE_REGION_CONFIG), 10);
    WitU64 device = acquire(table, index, WIT_STATUS_OK);
    const WitU64 object = region(device, 0, WIT_STATUS_OK);
    const WitU64 view = fixture_map(object, 0, 4096, WIT_MEMORY_READ, 0, WIT_STATUS_OK);
    const volatile WitU32 *config = (const volatile WitU32 *)view;
    fixture_check(config[0] == VIRTIO_BLOCK, 11);
    fixture_check(config[2] == block->Identity[1], 12); /* class and revision */
    /* A writable view is within the region handle's rights; an executable one never is; the view is not committable. */
    const WitU64 writable = fixture_map(object, 0, 4096, READ_WRITE, 0, WIT_STATUS_OK);
    fixture_check(*(const volatile WitU32 *)writable == VIRTIO_BLOCK, 13);
    release(writable);
    fixture_map(object, 0, 4096, READ_EXECUTE, 0, WIT_STATUS_DENIED);
    fixture_expect(WIT_CALL_MEMORY_COMMIT, view, 4096, READ_WRITE, WIT_STATUS_DENIED);
    fixture_expect(WIT_CALL_MEMORY_PROTECT, view, 4096, READ_EXECUTE, WIT_STATUS_DENIED);
    /* The region object outlives the device handle and the view outlives the object's handle; the device is free
     * again only when every handle to it is gone. */
    fixture_close(device);
    device = acquire(table, index, WIT_STATUS_OK);
    fixture_close(object);
    fixture_check(config[0] == VIRTIO_BLOCK, 14);
    /* A call's buffer in a device region is refused: the kernel has no view of device memory (S6.2). */
    fixture_expect(WIT_CALL_MEMORY_OBJECT_MAP, view, sizeof(WitMemoryMapRequest), 0, WIT_STATUS_BAD_ADDRESS);
    release(view);
    fixture_close(device);
}

static void authority_test(WitU64 table, const volatile WitDeviceDescriptor *block, WitU32 index)
{
    /* A table handle without ACQUIRE acquires nothing; indexes and reserved arguments are checked first. */
    const WitU64 reader = fixture_duplicate(table, WIT_RIGHT_MAP | WIT_RIGHT_QUERY, WIT_STATUS_OK);
    acquire(reader, index, WIT_STATUS_DENIED);
    fixture_close(reader);
    acquire(table, 0xFFFF, WIT_STATUS_INVALID_ARGUMENT);
    fixture_expect(WIT_CALL_DEVICE_ACQUIRE, table, index, 1, WIT_STATUS_INVALID_ARGUMENT);
    /* One holder at a time; a region index beyond the descriptor, a window beyond the region (ABI-1 1.3: the
     * configuration page has one page, so window 1 lies beyond it) and a port region are refused. */
    WitU64 device = acquire(table, index, WIT_STATUS_OK);
    acquire(table, index, WIT_STATUS_BUSY);
    region(device, 99, WIT_STATUS_INVALID_ARGUMENT);
    fixture_expect(WIT_CALL_DEVICE_MEMORY, device, 0, 1, WIT_STATUS_INVALID_ARGUMENT);
    for (WitU32 i = 0; i < block->RegionCount; ++i) {
        if (block->Regions[i].Flags & WIT_DEVICE_REGION_PORT) {
            region(device, i, WIT_STATUS_UNSUPPORTED);
        }
    }
    /* A device handle without BIND maps no region; the device stays held through it. */
    const WitU64 query = fixture_duplicate(device, WIT_RIGHT_QUERY, WIT_STATUS_OK);
    region(query, 0, WIT_STATUS_DENIED);
    fixture_close(device);
    acquire(table, index, WIT_STATUS_BUSY);
    fixture_close(query);
    /* The device moves through a channel: in flight it is held, received it works, and a dropped message frees it. */
    WitU64 ends[2] = {0, 0}, moved = 0, received = 0;
    fixture_expect(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, WIT_STATUS_OK);
    device = acquire(table, index, WIT_STATUS_OK);
    moved = device;
    fixture_send_handle(ends[0], &moved, WIT_STATUS_OK);
    region(device, 0, WIT_STATUS_BAD_HANDLE);
    acquire(table, index, WIT_STATUS_BUSY);
    fixture_receive_handle(ends[1], &received, WIT_STATUS_OK);
    fixture_close(region(received, 0, WIT_STATUS_OK));
    moved = received;
    fixture_send_handle(ends[0], &moved, WIT_STATUS_OK);
    fixture_close(ends[1]); /* the receiving endpoint: its queue is dropped with the device handle */
    fixture_close(acquire(table, index, WIT_STATUS_OK));
    fixture_close(ends[0]);
}

/* The child of the move test (D1.2), copied into its code object: position-independent, the child-local endpoint handle
 * in the first argument register. It receives a message of one handle, maps the received device's first region with
 * DEVICE_MEMORY, sends the two statuses back (99 for a DEVICE_MEMORY it did not reach) and sleeps until the fixture
 * kills it, spending none of the tick budget it took from the fixture. */
#define CHILD_MESSAGE_VERSION 1
#define CHILD_SEND 71
#define CHILD_RECEIVE 72
#define CHILD_DEVICE_MEMORY 81
#define CHILD_SLEEP_UNTIL 54
_Static_assert(CHILD_MESSAGE_VERSION == WIT_CHANNEL_MESSAGE_VERSION &&
        CHILD_SEND == WIT_CALL_CHANNEL_SEND &&
        CHILD_RECEIVE == WIT_CALL_CHANNEL_RECEIVE &&
        CHILD_DEVICE_MEMORY == WIT_CALL_DEVICE_MEMORY &&
        CHILD_SLEEP_UNTIL == WIT_CALL_SLEEP_UNTIL &&
        sizeof(WitChannelMessage) == 40,
    "The child's constants follow ABI-1");
#define STRINGIZE(x) #x
#define STRING(x) STRINGIZE(x)
#define UNREACHED 99U

__attribute__((visibility("hidden"))) extern const WitU8 device_child_begin[], device_child_end[];
#if defined(__x86_64__)
__asm__(
    ".text\n"
    ".globl device_child_begin\n.hidden device_child_begin\n"
    "device_child_begin:\n"
    "    mov %rdi, %r12\n"
    "    sub $192, %rsp\n"
    "    movl $" STRING(
        CHILD_MESSAGE_VERSION) ", (%rsp)\n"
                               "    movl $40, 4(%rsp)\n"
                               "    lea 64(%rsp), %rax\n"
                               "    mov %rax, 8(%rsp)\n"
                               "    lea 128(%rsp), %rax\n"
                               "    mov %rax, 16(%rsp)\n"
                               "    movl $16, 24(%rsp)\n"
                               "    movl $1, 28(%rsp)\n"
                               "    movq $0, 32(%rsp)\n"
                               "    movq $0, 128(%rsp)\n"
                               "    mov %r12, %rdi\n"
                               "    mov %rsp, %rsi\n"
                               "    mov $40, %edx\n"
                               "    mov $" STRING(
                                   CHILD_RECEIVE) ", %eax\n"
                                                  "    syscall\n"
                                                  "    mov %rax, 160(%rsp)\n"
                                                  "    movq $99, 168(%rsp)\n"
                                                  "    test %rax, %rax\n"
                                                  "    jne 1f\n"
                                                  "    mov 128(%rsp), %rdi\n"
                                                  "    xor %esi, %esi\n"
                                                  "    xor %edx, %edx\n"
                                                  "    mov $" STRING(
                                                      CHILD_DEVICE_MEMORY) ", %eax\n"
                                                                           "    syscall\n"
                                                                           "    mov %rax, 168(%rsp)\n"
                                                                           "1:\n"
                                                                           "    lea 160(%rsp), %rax\n"
                                                                           "    mov %rax, 8(%rsp)\n"
                                                                           "    movq $0, 16(%rsp)\n"
                                                                           "    movl $16, 24(%rsp)\n"
                                                                           "    movl $0, 28(%rsp)\n"
                                                                           "    mov %r12, %rdi\n"
                                                                           "    mov %rsp, %rsi\n"
                                                                           "    mov $40, %edx\n"
                                                                           "    mov $" STRING(
                                                                               CHILD_SEND) ", %eax\n"
                                                                                           "    syscall\n"
                                                                                           "2:\n"
                                                                                           "    mov $-1, %rdi\n"
                                                                                           "    xor %esi, %esi\n"
                                                                                           "    xor %edx, %edx\n"
                                                                                           "    mov $" STRING(
                                                                                               CHILD_SLEEP_UNTIL) ", "
                                                                                                                  "%eax"
                                                                                                                  "\n"
                                                                                                                  "    "
                                                                                                                  "sysc"
                                                                                                                  "all"
                                                                                                                  "\n"
                                                                                                                  "    "
                                                                                                                  "jmp "
                                                                                                                  "2b\n"
                                                                                                                  ".glo"
                                                                                                                  "bl "
                                                                                                                  "devi"
                                                                                                                  "ce_"
                                                                                                                  "chil"
                                                                                                                  "d_"
                                                                                                                  "end"
                                                                                                                  "\n."
                                                                                                                  "hidd"
                                                                                                                  "en "
                                                                                                                  "devi"
                                                                                                                  "ce_"
                                                                                                                  "chil"
                                                                                                                  "d_"
                                                                                                                  "end"
                                                                                                                  "\n"
                                                                                                                  "devi"
                                                                                                                  "ce_"
                                                                                                                  "chil"
                                                                                                                  "d_"
                                                                                                                  "end:"
                                                                                                                  "\n");
#elif defined(__aarch64__)
__asm__(
    ".text\n"
    ".globl device_child_begin\n.hidden device_child_begin\n"
    "device_child_begin:\n"
    "    mov x19, x0\n"
    "    sub sp, sp, #192\n"
    "    mov w9, #" STRING(
        CHILD_MESSAGE_VERSION) "\n"
                               "    str w9, [sp]\n"
                               "    mov w9, #40\n"
                               "    str w9, [sp, #4]\n"
                               "    add x9, sp, #64\n"
                               "    str x9, [sp, #8]\n"
                               "    add x9, sp, #128\n"
                               "    str x9, [sp, #16]\n"
                               "    mov w9, #16\n"
                               "    str w9, [sp, #24]\n"
                               "    mov w9, #1\n"
                               "    str w9, [sp, #28]\n"
                               "    str xzr, [sp, #32]\n"
                               "    str xzr, [sp, #128]\n"
                               "    mov x0, x19\n"
                               "    mov x1, sp\n"
                               "    mov x2, #40\n"
                               "    mov x8, #" STRING(
                                   CHILD_RECEIVE) "\n"
                                                  "    svc #0\n"
                                                  "    str x0, [sp, #160]\n"
                                                  "    mov x9, #99\n"
                                                  "    str x9, [sp, #168]\n"
                                                  "    cbnz x0, 1f\n"
                                                  "    ldr x0, [sp, #128]\n"
                                                  "    mov x1, #0\n"
                                                  "    mov x2, #0\n"
                                                  "    mov x8, #" STRING(
                                                      CHILD_DEVICE_MEMORY) "\n"
                                                                           "    svc #0\n"
                                                                           "    str x0, [sp, #168]\n"
                                                                           "1:\n"
                                                                           "    add x9, sp, #160\n"
                                                                           "    str x9, [sp, #8]\n"
                                                                           "    str xzr, [sp, #16]\n"
                                                                           "    mov w9, #16\n"
                                                                           "    str w9, [sp, #24]\n"
                                                                           "    str wzr, [sp, #28]\n"
                                                                           "    mov x0, x19\n"
                                                                           "    mov x1, sp\n"
                                                                           "    mov x2, #40\n"
                                                                           "    mov x8, #" STRING(
                                                                               CHILD_SEND) "\n"
                                                                                           "    svc #0\n"
                                                                                           "2:\n"
                                                                                           "    mov x0, #-1\n"
                                                                                           "    mov x1, #0\n"
                                                                                           "    mov x2, #0\n"
                                                                                           "    mov x8, #" STRING(
                                                                                               CHILD_SLEEP_UNTIL) "\n"
                                                                                                                  "    "
                                                                                                                  "svc "
                                                                                                                  "#0\n"
                                                                                                                  "    "
                                                                                                                  "b "
                                                                                                                  "2b\n"
                                                                                                                  ".glo"
                                                                                                                  "bl "
                                                                                                                  "devi"
                                                                                                                  "ce_"
                                                                                                                  "chil"
                                                                                                                  "d_"
                                                                                                                  "end"
                                                                                                                  "\n."
                                                                                                                  "hidd"
                                                                                                                  "en "
                                                                                                                  "devi"
                                                                                                                  "ce_"
                                                                                                                  "chil"
                                                                                                                  "d_"
                                                                                                                  "end"
                                                                                                                  "\n"
                                                                                                                  "devi"
                                                                                                                  "ce_"
                                                                                                                  "chil"
                                                                                                                  "d_"
                                                                                                                  "end:"
                                                                                                                  "\n");
#else
__asm__(
    ".text\n"
    ".globl device_child_begin\n.hidden device_child_begin\n"
    "device_child_begin:\n"
    "    mv s1, a0\n"
    "    addi sp, sp, -192\n"
    "    li t0, " STRING(
        CHILD_MESSAGE_VERSION) "\n"
                               "    sw t0, 0(sp)\n"
                               "    li t0, 40\n"
                               "    sw t0, 4(sp)\n"
                               "    addi t0, sp, 64\n"
                               "    sd t0, 8(sp)\n"
                               "    addi t0, sp, 128\n"
                               "    sd t0, 16(sp)\n"
                               "    li t0, 16\n"
                               "    sw t0, 24(sp)\n"
                               "    li t0, 1\n"
                               "    sw t0, 28(sp)\n"
                               "    sd zero, 32(sp)\n"
                               "    sd zero, 128(sp)\n"
                               "    mv a0, s1\n"
                               "    mv a1, sp\n"
                               "    li a2, 40\n"
                               "    li a7, " STRING(
                                   CHILD_RECEIVE) "\n"
                                                  "    ecall\n"
                                                  "    sd a0, 160(sp)\n"
                                                  "    li t0, 99\n"
                                                  "    sd t0, 168(sp)\n"
                                                  "    bnez a0, 1f\n"
                                                  "    ld a0, 128(sp)\n"
                                                  "    li a1, 0\n"
                                                  "    li a2, 0\n"
                                                  "    li a7, " STRING(
                                                      CHILD_DEVICE_MEMORY) "\n"
                                                                           "    ecall\n"
                                                                           "    sd a0, 168(sp)\n"
                                                                           "1:\n"
                                                                           "    addi t0, sp, 160\n"
                                                                           "    sd t0, 8(sp)\n"
                                                                           "    sd zero, 16(sp)\n"
                                                                           "    li t0, 16\n"
                                                                           "    sw t0, 24(sp)\n"
                                                                           "    sw zero, 28(sp)\n"
                                                                           "    mv a0, s1\n"
                                                                           "    mv a1, sp\n"
                                                                           "    li a2, 40\n"
                                                                           "    li a7, " STRING(
                                                                               CHILD_SEND) "\n"
                                                                                           "    ecall\n"
                                                                                           "2:\n"
                                                                                           "    li a0, -1\n"
                                                                                           "    li a1, 0\n"
                                                                                           "    li a2, 0\n"
                                                                                           "    li a7, " STRING(
                                                                                               CHILD_SLEEP_UNTIL) "\n"
                                                                                                                  "    "
                                                                                                                  "ecal"
                                                                                                                  "l\n"
                                                                                                                  "    "
                                                                                                                  "j "
                                                                                                                  "2b\n"
                                                                                                                  ".glo"
                                                                                                                  "bl "
                                                                                                                  "devi"
                                                                                                                  "ce_"
                                                                                                                  "chil"
                                                                                                                  "d_"
                                                                                                                  "end"
                                                                                                                  "\n."
                                                                                                                  "hidd"
                                                                                                                  "en "
                                                                                                                  "devi"
                                                                                                                  "ce_"
                                                                                                                  "chil"
                                                                                                                  "d_"
                                                                                                                  "end"
                                                                                                                  "\n"
                                                                                                                  "devi"
                                                                                                                  "ce_"
                                                                                                                  "chil"
                                                                                                                  "d_"
                                                                                                                  "end:"
                                                                                                                  "\n");
#endif

/* A child process with the endpoint, the code and the stack, its first thread started with the child-local endpoint;
 * the process handle. */
static WitU64 child_start(WitU64 endpoint, WitU64 code, WitU64 stack)
{
    WitProcessCreateRequest create;
    WitThreadCreateRequest3 thread;
    WitMemoryMapRequest map;
    WitU64 local = 0;
    create.Version = WIT_PROCESS_CREATE_VERSION;
    create.Size = sizeof(create);
    create.Endpoint = endpoint;
    create.Pages = 0;
    create.Flags = 0;
    create.Reserved = 0;
    const WitU64 child =
        fixture_expect(WIT_CALL_PROCESS_CREATE, (WitU64)&create, sizeof(create), (WitU64)&local, WIT_STATUS_OK);
    map.Version = WIT_MEMORY_MAP_VERSION;
    map.Size = sizeof(map);
    map.Offset = 0;
    map.Flags = 0;
    map.Target = child;
    map.Object = code;
    map.Bytes = 4096;
    map.Address = CHILD_CODE;
    map.Protection = READ_EXECUTE;
    fixture_expect(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&map, sizeof(map), 0, WIT_STATUS_OK);
    map.Object = stack;
    map.Bytes = CHILD_STACK_BYTES;
    map.Address = CHILD_STACK;
    map.Protection = READ_WRITE;
    fixture_expect(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&map, sizeof(map), 0, WIT_STATUS_OK);
    thread.Version = WIT_THREAD_CREATE_VERSION_3;
    thread.Size = sizeof(thread);
    thread.Entry = CHILD_CODE;
    thread.Argument = local;
    thread.StackPointer = CHILD_STACK + CHILD_STACK_BYTES;
    thread.TlsBase = 0;
    thread.Process = child;
    thread.Flags = 0;
    thread.Reserved = 0;
    fixture_close(fixture_expect(WIT_CALL_THREAD_CREATE, (WitU64)&thread, sizeof(thread), 0, WIT_STATUS_OK));
    return child;
}

/* The child's two statuses, after waiting for its message on the fixture's end. */
static void child_statuses(WitU64 endpoint, WitU64 *statuses)
{
    WitChannelMessage message;
    statuses[0] = statuses[1] = 0;
    fixture_wait(&endpoint, 1, WIT_WAIT_INFINITE, WIT_STATUS_OK);
    fixture_message(&message, (WitU64)statuses, 16, 0, 0);
    fixture_check(
        fixture_expect(WIT_CALL_CHANNEL_RECEIVE, endpoint, (WitU64)&message, sizeof(message), WIT_STATUS_OK) == 16, 30);
}

/* The child's end: killed, waited for, its handle closed with the fixture's end. */
static void child_end(WitU64 child, WitU64 endpoint)
{
    fixture_expect(WIT_CALL_PROCESS_KILL, child, 7, 0, WIT_STATUS_OK);
    fixture_wait(&child, 1, WIT_WAIT_INFINITE, WIT_STATUS_OK);
    fixture_close(child);
    fixture_close(endpoint);
}

static void move_test(WitU64 table, WitU32 index)
{
    WitU64 ends[2] = {0, 0}, statuses[2] = {0, 0};
    /* The child's program, written through a writable view and published through an executable view of our own. */
    const WitU64 code = fixture_expect(WIT_CALL_MEMORY_OBJECT_CREATE, 4096, 0, 0, WIT_STATUS_OK);
    const WitU64 writable = fixture_map(code, 0, 4096, READ_WRITE, 0, WIT_STATUS_OK);
    for (WitU64 i = 0; i < (WitU64)(device_child_end - device_child_begin); ++i) {
        ((volatile WitU8 *)writable)[i] = ((const volatile WitU8 *)device_child_begin)[i];
    }
    fixture_check(fixture_map(code, 0, 4096, READ_EXECUTE, CHILD_CODE, WIT_STATUS_OK) == CHILD_CODE, 31);
    fixture_expect(WIT_CALL_CODE_PUBLISH, CHILD_CODE, 4096, 0, WIT_STATUS_OK);
    const WitU64 stack = fixture_expect(WIT_CALL_MEMORY_OBJECT_CREATE, CHILD_STACK_BYTES, 0, 0, WIT_STATUS_OK);
    /* A device its sender still refers to stays queued: the child's receive is refused (K5.2c), the dropped message
     * gives it back with the child's end, and the sender's duplicate keeps it until closed. */
    WitU64 device = acquire(table, index, WIT_STATUS_OK);
    const WitU64 kept = fixture_duplicate(device, WIT_RIGHT_QUERY, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, WIT_STATUS_OK);
    fixture_send_handle(ends[0], &device, WIT_STATUS_OK);
    WitU64 child = child_start(ends[1], code, stack);
    child_statuses(ends[0], statuses);
    fixture_check(statuses[0] == WIT_STATUS_UNSUPPORTED && statuses[1] == UNREACHED, 32);
    child_end(child, ends[0]);
    acquire(table, index, WIT_STATUS_BUSY);
    fixture_close(kept);
    /* The sender's last reference moves (D1.2): the child becomes the holder and maps the device's configuration
     * region; the sender cannot acquire the device the child holds, and the device is free again at the child's end. */
    device = acquire(table, index, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, WIT_STATUS_OK);
    fixture_send_handle(ends[0], &device, WIT_STATUS_OK);
    child = child_start(ends[1], code, stack);
    child_statuses(ends[0], statuses);
    fixture_check(statuses[0] == WIT_STATUS_OK && statuses[1] == WIT_STATUS_OK, 33);
    acquire(table, index, WIT_STATUS_BUSY);
    child_end(child, ends[0]);
    fixture_close(acquire(table, index, WIT_STATUS_OK));
    release(CHILD_CODE);
    release(writable);
    fixture_close(code);
    fixture_close(stack);
}

FIXTURE_ENTRY void wit_user_start(const WitRootStartup *startup)
{
    const WitUserTestConfig *config = fixture_config(startup);
    fixture_check(startup->AbiVersion == WIT_ABI_VERSION, 1);
    /* The table, mapped read-only: its header, then the block function among its descriptors. */
    const WitU64 table = config->TableHandle;
    WitU64 mapped = 0;
    WitU32 index = 0;
    const volatile WitDeviceDescriptor *block = fixture_find_device(table, VIRTIO_BLOCK, &mapped, &index);
    if (config->Mode == WIT_DEVICE_TEST_TABLE) {
        table_test(table, block, index);
    } else if (config->Mode == WIT_DEVICE_TEST_AUTHORITY) {
        authority_test(table, block, index);
    } else if (config->Mode == WIT_DEVICE_TEST_MOVE) {
        move_test(table, index);
    } else {
        fixture_failed(5);
    }
    release(mapped);
    fixture_close(table);
    fixture_exit(WIT_TEST_EXIT_CODE);
}
