#ifndef WITOS_TESTS_FIXTURE_H
#define WITOS_TESTS_FIXTURE_H
#include "witos/user_abi.h"
#include "witos/user_layout.h"
#include "witos/syscall.h"
#include "witos/channels.h"
#include "witos/device.h"
#include "witos/memory_object.h"
#include "witos/thread_info.h"
#include "witos/thread_reference.h"
#include "witos/wait_objects.h"
#include "protocol.h"

/* What the mechanism fixtures share (plan step K8.3). A fixture is one C source for both ISAs, compiled by the pinned
 * clang for the layer-2 triple and linked by lld into the component's code window at WIT_USER_CODE
 * (tests/User/Fixtures/fixture.ld): code and constants in one executable segment, no writable data, the entry
 * first. Its state lives in the component's data page at fixed offsets the kernel self-test reads: the status of a
 * failed check at 1304 and the number of checks passed at 1312. The first thread enters wit_user_start with the
 * startup block at WIT_USER_INFO in its argument register, as the kernel prepares a component's first thread. */

#define FIXTURE_FAILED_STATUS (*(volatile WitU64 *)(WIT_USER_DATA + 1304))
#define FIXTURE_CHECKS (*(volatile WitU64 *)(WIT_USER_DATA + 1312))
#define FIXTURE_FAILURE_EXIT_CODE 241U

/* Calls a fixture makes many times stay calls, so that a fixture stays small; inline keeps an unused one quiet. */
#define FIXTURE_CALL static inline __attribute__((noinline))

#if defined(__x86_64__)
/* A function the kernel enters directly, with no return address and the stack pointer 16-byte aligned: the first
 * thread's entry and a thread's. */
#define FIXTURE_ENTRY __attribute__((section(".text.entry"), force_align_arg_pointer, noreturn))
#define FIXTURE_THREAD __attribute__((force_align_arg_pointer, noreturn))
#else
#define FIXTURE_ENTRY __attribute__((section(".text.entry"), noreturn))
#define FIXTURE_THREAD __attribute__((noreturn))
#endif

/* The stack of a fixture's thread: four pages of a reservation of its own. */
#define FIXTURE_STACK_BYTES (4 * 4096ULL)

/* A thread's entry: its argument in the argument register (the base of its stack's reservation for
 * fixture_thread_create). */
typedef void (*FixtureThread)(WitU64 argument);

/* What a thread of fixture_thread finds through its argument when its creator gives it more than a stack: the base of
 * its stack's reservation, its index and a word the creator or the thread uses, often the thread's TLS. */
typedef struct FixtureThreadRecord {
    WitU64 Stack, Index, Value, Reserved;
} FixtureThreadRecord;

static inline WIT_NORETURN void fixture_exit(WitU64 code)
{
    WitU64 result;
    for (;;) {
        wit_syscall(WIT_CALL_PROCESS_EXIT, code, 0, 0, &result);
    }
}

static inline WIT_NORETURN void fixture_failed(WitU64 status)
{
    FIXTURE_FAILED_STATUS = status;
    fixture_exit(FIXTURE_FAILURE_EXIT_CODE);
}

/* A system call whose status must be the expected one; the check is counted and the result returned. */
FIXTURE_CALL WitU64 fixture_expect(WitU64 number, WitU64 a0, WitU64 a1, WitU64 a2, WitU64 expected)
{
    WitU64 result = 0;
    const WitU64 status = wit_syscall(number, a0, a1, a2, &result);
    ++FIXTURE_CHECKS;
    if (status != expected) {
        fixture_failed(status);
    }
    return result;
}

/* A condition with the code a failure reports in place of a status. */
static inline void fixture_check(int condition, WitU64 code)
{
    if (!condition) {
        fixture_failed(code);
    }
}

/* The startup block's test extension (tests/User/protocol.h). */
static inline const WitUserTestConfig *fixture_config(const WitUserStartup *startup)
{
    return (const WitUserTestConfig *)startup;
}

/* MEMORY_OBJECT_MAP of a window of an object into the caller, at an address or where the kernel chooses (zero). */
FIXTURE_CALL WitU64 fixture_map(
    WitU64 object, WitU64 offset, WitU64 bytes, WitU32 protection, WitU64 address, WitU64 expected)
{
    WitMemoryMapRequest request;
    request.Version = WIT_MEMORY_MAP_VERSION;
    request.Size = sizeof(request);
    request.Object = object;
    request.Offset = offset;
    request.Bytes = bytes;
    request.Address = address;
    request.Protection = protection;
    request.Flags = 0;
    request.Target = WIT_PROCESS_SELF;
    return fixture_expect(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&request, sizeof(request), 0, expected);
}

/* A channel message of bytes and handles; zero counts leave the buffers out. */
static inline void fixture_message(WitChannelMessage *message, WitU64 data, WitU32 bytes, WitU64 *handles, WitU32 count)
{
    message->Version = WIT_CHANNEL_MESSAGE_VERSION;
    message->Size = sizeof(*message);
    message->Data = data;
    message->Handles = (WitU64)handles;
    message->Bytes = bytes;
    message->HandleCount = count;
    message->Flags = 0;
    message->Reserved = 0;
}

/* CHANNEL_SEND of one handle and no bytes. */
FIXTURE_CALL void fixture_send_handle(WitU64 endpoint, WitU64 *handle, WitU64 expected)
{
    WitChannelMessage message;
    fixture_message(&message, 0, 0, handle, 1);
    fixture_expect(WIT_CALL_CHANNEL_SEND, endpoint, (WitU64)&message, sizeof(message), expected);
}

/* CHANNEL_RECEIVE of at most one handle and no bytes into *handle. */
FIXTURE_CALL void fixture_receive_handle(WitU64 endpoint, WitU64 *handle, WitU64 expected)
{
    WitChannelMessage message;
    fixture_message(&message, 0, 0, handle, 1);
    fixture_expect(WIT_CALL_CHANNEL_RECEIVE, endpoint, (WitU64)&message, sizeof(message), expected);
}

/* HANDLE_DUPLICATE with the rights given; the new handle. */
FIXTURE_CALL WitU64 fixture_duplicate(WitU64 handle, WitU64 rights, WitU64 expected)
{
    WitU64 copy = 0;
    fixture_expect(WIT_CALL_HANDLE_DUPLICATE, handle, (WitU64)&copy, rights, expected);
    return copy;
}

FIXTURE_CALL void fixture_close(WitU64 handle)
{
    fixture_expect(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, WIT_STATUS_OK);
}

/* OBJECT_WAIT on the objects given until the absolute deadline; the winner's index. */
FIXTURE_CALL WitU64 fixture_wait(const WitU64 *handles, WitU32 count, WitU64 deadline, WitU64 expected)
{
    WitUserWaitRequest request;
    request.Version = WIT_WAIT_OBJECTS_VERSION;
    request.Size = sizeof(request);
    request.Handles = (WitU64)handles;
    request.Count = count;
    request.Flags = 0;
    request.Deadline = deadline;
    return fixture_expect(WIT_CALL_OBJECT_WAIT, (WitU64)&request, sizeof(request), 0, expected);
}

/* A thread's stack: a reservation of FIXTURE_STACK_BYTES, committed writable, at the address given or where the kernel
 * chooses (zero); its base. */
FIXTURE_CALL WitU64 fixture_stack(WitU64 address)
{
    const WitU64 stack = fixture_expect(WIT_CALL_MEMORY_RESERVE, FIXTURE_STACK_BYTES, 4096, address, WIT_STATUS_OK);
    fixture_expect(
        WIT_CALL_MEMORY_COMMIT, stack, FIXTURE_STACK_BYTES, WIT_MEMORY_READ | WIT_MEMORY_WRITE, WIT_STATUS_OK);
    return stack;
}

/* THREAD_CREATE of the one form (version 2): the entry with its argument on the stack of fixture_stack, at its top, with
 * the TLS base given. */
FIXTURE_CALL WitU64 fixture_thread(
    FixtureThread entry, WitU64 argument, WitU64 stack, WitU64 tls, WitU32 flags, WitU64 expected)
{
    WitThreadCreateRequest2 request;
    request.Version = WIT_THREAD_CREATE_VERSION_2;
    request.Size = sizeof(request);
    request.Entry = (WitU64)entry;
    request.Argument = argument;
    request.StackPointer = stack + FIXTURE_STACK_BYTES;
    request.TlsBase = tls;
    request.Flags = flags;
    request.Reserved = 0;
    return fixture_expect(WIT_CALL_THREAD_CREATE, (WitU64)&request, sizeof(request), 0, expected);
}

/* A thread on a stack of its own whose base is its argument and lands in *stack; the thread's exit names it for the
 * kernel to release (fixture_thread_exit). A refused creation releases the reservation. */
FIXTURE_CALL WitU64 fixture_thread_create(FixtureThread entry, WitU64 *stack, WitU32 flags, WitU64 expected)
{
    *stack = fixture_stack(0);
    const WitU64 thread = fixture_thread(entry, *stack, *stack, 0, flags, expected);
    if (expected != WIT_STATUS_OK) {
        fixture_expect(WIT_CALL_MEMORY_RELEASE, *stack, 0, 0, WIT_STATUS_OK);
    }
    return thread;
}

/* A thread whose argument is its record, which receives its stack before the thread starts. */
FIXTURE_CALL WitU64 fixture_thread_start(
    FixtureThread entry, volatile FixtureThreadRecord *record, WitU64 tls, WitU64 expected)
{
    record->Stack = fixture_stack(0);
    const WitU64 thread = fixture_thread(entry, (WitU64)record, record->Stack, tls, 0, expected);
    if (expected != WIT_STATUS_OK) {
        fixture_expect(WIT_CALL_MEMORY_RELEASE, record->Stack, 0, 0, WIT_STATUS_OK);
    }
    return thread;
}

/* An absolute monotonic deadline the given number of 10 ms scheduler ticks from now, through CLOCK_FREQUENCY. */
FIXTURE_CALL WitU64 fixture_deadline_ticks(WitU64 ticks)
{
    const WitU64 frequency = fixture_expect(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, WIT_STATUS_OK);
    return fixture_expect(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, WIT_STATUS_OK) + frequency / 100 * ticks;
}

/* THREAD_EXIT with the code, naming the stack the thread runs on for the kernel to release after it stops. */
static inline WIT_NORETURN void fixture_thread_exit(WitU64 code, WitU64 stack)
{
    WitU64 result;
    for (;;) {
        wit_syscall(WIT_CALL_THREAD_EXIT, code, stack, 0, &result);
    }
}

/* Waits for a thread through its handle, reads its exit code and closes the handle. */
FIXTURE_CALL WitU64 fixture_join(WitU64 thread)
{
    WitUserThreadInfo info;
    fixture_wait(&thread, 1, WIT_WAIT_INFINITE, WIT_STATUS_OK);
    info.Version = WIT_THREAD_INFO_VERSION;
    info.Size = sizeof(info);
    fixture_expect(WIT_CALL_THREAD_QUERY, thread, (WitU64)&info, sizeof(info), WIT_STATUS_OK);
    fixture_close(thread);
    return info.ExitCode;
}

/* A full memory barrier for a device's view of memory: MFENCE on x64, DSB SY on ARM64. */
static inline void fixture_device_barrier(void)
{
#if defined(__x86_64__)
    __asm__ volatile("mfence" ::: "memory");
#else
    __asm__ volatile("dsb sy" ::: "memory");
#endif
}

/* The virtio block function of the board, by the identity its bus reports (vendor 0x1AF4, device 0x1001): what the
 * device fixtures know and the kernel does not. */
#define FIXTURE_VIRTIO_BLOCK 0x10011AF4U

/* Maps the device table read-only, checks its header and finds the descriptor of the identity: the descriptor, its
 * index in *index and the table's mapping in *mapped. */
FIXTURE_CALL const volatile WitDeviceDescriptor *fixture_find_device(
    WitU64 table, WitU32 identity, WitU64 *mapped, WitU32 *index)
{
    *mapped = fixture_map(table, 0, 4096, WIT_MEMORY_READ, 0, WIT_STATUS_OK);
    const volatile WitDeviceTable *header = (const volatile WitDeviceTable *)*mapped;
    fixture_check(header->Version == WIT_DEVICE_TABLE_VERSION &&
            header->Size == WIT_DEVICE_TABLE_SIZE &&
            header->DescriptorSize == WIT_DEVICE_DESCRIPTOR_SIZE,
        2);
    fixture_check(header->Count != 0 && header->Count <= WIT_DEVICE_CAPACITY, 3);
    const volatile WitDeviceDescriptor *descriptors =
        (const volatile WitDeviceDescriptor *)(*mapped + WIT_DEVICE_TABLE_SIZE);
    for (*index = 0; *index < header->Count; ++*index) {
        if (descriptors[*index].Identity[0] == identity) {
            return &descriptors[*index];
        }
    }
    fixture_failed(4);
}

#endif
