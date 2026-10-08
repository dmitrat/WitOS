#include "witos/user_abi.h"
#include "witos/root.h"
#include "witos/user_layout.h"
#include "witos/memory_object.h"
#include "witos/device.h"
#include "witos/channels.h"
#include "witos/syscall.h"

/* The root task fixture (RFC 0011 v3 section 7.11, plan steps K4 and T1): the first component the kernel starts
 * from the boot disk's flat image, with nothing but its startup descriptor (witos/root.h) in the argument register.
 * One C source for both ISAs, compiled by the pinned clang for the architecture's triple and linked by lld at the
 * image window (tests/User/root.ld): no libc, no runtime, the ABI-1 transport of witos/syscall.h alone. It checks
 * the descriptor, writes to the kernel log through the log handle, maps the first page of the boot package and
 * checks its magic, maps the device table and checks that the board published devices, reads UTC and sets it through
 * the clock capability (K6), delegates the log over a channel (S5.2), then exits with zero; a failed check exits
 * with 241. The data page holds the status of a
 * failed check at 1304 and the count of checks at 1312 for the kernel self-test's diagnostics. */

#define STRINGIZE(x) #x
#define STRING(x) STRINGIZE(x)
#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#define ENTRY_ATTRIBUTES __attribute__((force_align_arg_pointer))
#else
#define ISA_NAME "aarch64"
#define ENTRY_ATTRIBUTES
#endif

#define FAILED_STATUS ((volatile WitU64 *)(WIT_USER_DATA + 1304))
#define CHECKS_PASSED ((volatile WitU64 *)(WIT_USER_DATA + 1312))
#define FAILURE_EXIT_CODE 241U
#define PACKAGE_MAGIC 0x31304B4150544957ULL /* "WITPAK01" */
#define UTC_2026 1767225600000000000ULL /* 2026-01-01 */
#define UTC_2030 1893456000000000000ULL /* 2030-01-01 */

static const char started[] = "[ROOT] started by clang " STRING(__clang_major__) "." STRING(__clang_minor__) "." STRING(
    __clang_patchlevel__) " for " ISA_NAME "\n";
static const char mapped[] = "[ROOT] package and devices \n";
static const char delegated[] = "[ROOT] log delegated\n";

static WIT_NORETURN void exit_process(WitU64 code)
{
    WitU64 result;
    for (;;) {
        wit_syscall(WIT_CALL_PROCESS_EXIT, code, 0, 0, &result);
    }
}

static WIT_NORETURN void failed(WitU64 status)
{
    *FAILED_STATUS = status;
    exit_process(FAILURE_EXIT_CODE);
}

/* A system call whose status must be the expected one; the check is counted. */
static WitU64 expect(WitU64 number, WitU64 a0, WitU64 a1, WitU64 a2, WitU64 expected)
{
    WitU64 result = 0;
    const WitU64 status = wit_syscall(number, a0, a1, a2, &result);
    ++*CHECKS_PASSED;
    if (status != expected) {
        failed(status);
    }
    return result;
}

static void check(int condition, WitU64 code)
{
    if (!condition) {
        failed(code);
    }
}

static void log_line(const WitRootStartup *startup, const char *text, WitU64 length)
{
    const WitU64 written =
        expect(WIT_CALL_DEBUG_WRITE, startup->Handles[WIT_ROOT_HANDLE_LOG], (WitU64)text, length, WIT_STATUS_OK);
    check(written == length, 1);
}

/* A channel message that carries one handle and no bytes. */
static void handle_message(WitChannelMessage *message, WitU64 *handle)
{
    message->Version = WIT_CHANNEL_MESSAGE_VERSION;
    message->Size = sizeof(*message);
    message->Data = 0;
    message->Handles = (WitU64)handle;
    message->Bytes = 0;
    message->HandleCount = 1;
    message->Flags = 0;
    message->Reserved = 0;
}

/* MEMORY_OBJECT_MAP of the first bytes of an object at an address the kernel chooses. */
static WitU64 map_object(WitU64 handle, WitU64 bytes, WitU32 protection, WitU64 expected)
{
    WitMemoryMapRequest request;
    request.Version = WIT_MEMORY_MAP_VERSION;
    request.Size = sizeof(request);
    request.Object = handle;
    request.Offset = 0;
    request.Bytes = bytes;
    request.Address = 0;
    request.Protection = protection;
    request.Flags = 0;
    request.Target = WIT_PROCESS_SELF;
    return expect(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&request, sizeof(request), 0, expected);
}

ENTRY_ATTRIBUTES WIT_NORETURN void wit_user_start(const WitRootStartup *startup)
{
    const WitU64 features = WIT_ABI_FEATURE_CHANNELS | WIT_ABI_FEATURE_DEVICES;
    check(startup->Version == WIT_ROOT_STARTUP_VERSION, 2);
    check(startup->Size == WIT_ROOT_STARTUP_SIZE, 3);
    check(startup->AbiVersion == WIT_ABI_VERSION, 4);
    check((startup->Features & features) == features, 5);
    check(startup->MemoryBase == WIT_USER_MEMORY_BASE, 6);
    check(startup->CodeBase == WIT_USER_CODE_BASE, 7);
    check(startup->PackageBytes != 0, 8);
    check(startup->HandleCount >= 4, 9); /* the log, the package, the device table and the clock */
    log_line(startup, started, sizeof(started) - 1);

    /* The boot package: its first page is the package header with the magic; a writable view is refused. */
    const WitU64 package = startup->Handles[WIT_ROOT_HANDLE_PACKAGE];
    const WitU64 header = map_object(package, 4096, WIT_MEMORY_READ, WIT_STATUS_OK);
    check(*(const volatile WitU64 *)header == PACKAGE_MAGIC, 10);
    map_object(package, 4096, WIT_MEMORY_READ | WIT_MEMORY_WRITE, WIT_STATUS_DENIED);
    expect(WIT_CALL_MEMORY_RELEASE, header, 4096, 0, WIT_STATUS_OK); /* a mapping's own size releases it whole */
    /* Code loads from the package (S5.1): an executable view is granted and published, never a writable one. */
    const WitU64 code = map_object(package, 4096, WIT_MEMORY_READ | WIT_MEMORY_EXECUTE, WIT_STATUS_OK);
    expect(WIT_CALL_CODE_PUBLISH, code, 4096, 0, WIT_STATUS_OK);
    expect(WIT_CALL_MEMORY_RELEASE, code, 0, 0, WIT_STATUS_OK);

    /* The device table: a version 1 table with at least one descriptor. */
    const WitU64 table = map_object(startup->Handles[WIT_ROOT_HANDLE_DEVICES], 4096, WIT_MEMORY_READ, WIT_STATUS_OK);
    const volatile WitDeviceTable *devices = (const volatile WitDeviceTable *)table;
    check(devices->Version == WIT_DEVICE_TABLE_VERSION, 101);
    check(devices->Count != 0, 102);
    expect(WIT_CALL_MEMORY_RELEASE, table, 0, 0, WIT_STATUS_OK);
    log_line(startup, mapped, sizeof(mapped) - 1);

    /* UTC (K6): its frequency, a plausible reading, a set through the clock capability that the next reading
     * continues from, and the refusals of another handle and of the monotonic clock. */
    const WitU64 clock = startup->Handles[WIT_ROOT_HANDLE_CLOCK];
    check(expect(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_UTC, 0, 0, WIT_STATUS_OK) == 1000000000ULL, 11);
    check(expect(WIT_CALL_CLOCK_READ, WIT_CLOCK_UTC, 0, 0, WIT_STATUS_OK) >= UTC_2026, 12);
    expect(WIT_CALL_CLOCK_SET, clock, WIT_CLOCK_UTC, UTC_2030, WIT_STATUS_OK);
    check(expect(WIT_CALL_CLOCK_READ, WIT_CLOCK_UTC, 0, 0, WIT_STATUS_OK) >= UTC_2030, 13);
    expect(WIT_CALL_CLOCK_SET, startup->Handles[WIT_ROOT_HANDLE_LOG], WIT_CLOCK_UTC, UTC_2030, WIT_STATUS_WRONG_TYPE);
    expect(WIT_CALL_CLOCK_SET, clock, WIT_CLOCK_MONOTONIC, UTC_2030, WIT_STATUS_INVALID_ARGUMENT);

    /* The kernel log is delegable (S5.2): a duplicate with WRITE and TRANSFER moves over a channel and writes from
     * where it arrives; a duplicate without TRANSFER stays, and one without DUPLICATE has no duplicates. */
    const WitU64 log = startup->Handles[WIT_ROOT_HANDLE_LOG];
    WitU64 ends[2] = {0, 0}, movable = 0, fixed = 0, received = 0, again = 0;
    WitChannelMessage message;
    expect(WIT_CALL_HANDLE_DUPLICATE, log, (WitU64)&movable, WIT_RIGHT_WRITE | WIT_RIGHT_TRANSFER, WIT_STATUS_OK);
    expect(WIT_CALL_HANDLE_DUPLICATE, log, (WitU64)&fixed, WIT_RIGHT_WRITE, WIT_STATUS_OK);
    expect(WIT_CALL_HANDLE_DUPLICATE, movable, (WitU64)&again, 0, WIT_STATUS_DENIED);
    expect(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, WIT_STATUS_OK);
    handle_message(&message, &fixed);
    expect(WIT_CALL_CHANNEL_SEND, ends[0], (WitU64)&message, sizeof(message), WIT_STATUS_DENIED);
    handle_message(&message, &movable);
    expect(WIT_CALL_CHANNEL_SEND, ends[0], (WitU64)&message, sizeof(message), WIT_STATUS_OK);
    expect(WIT_CALL_DEBUG_WRITE, movable, (WitU64)delegated, sizeof(delegated) - 1, WIT_STATUS_BAD_HANDLE);
    handle_message(&message, &received);
    check(
        expect(WIT_CALL_CHANNEL_RECEIVE, ends[1], (WitU64)&message, sizeof(message), WIT_STATUS_OK) == 1ULL << 32, 14);
    check(expect(WIT_CALL_DEBUG_WRITE, received, (WitU64)delegated, sizeof(delegated) - 1, WIT_STATUS_OK) ==
            sizeof(delegated) - 1,
        15);
    expect(WIT_CALL_HANDLE_CLOSE, received, 0, 0, WIT_STATUS_OK);
    expect(WIT_CALL_HANDLE_CLOSE, fixed, 0, 0, WIT_STATUS_OK);
    expect(WIT_CALL_HANDLE_CLOSE, ends[0], 0, 0, WIT_STATUS_OK);
    expect(WIT_CALL_HANDLE_CLOSE, ends[1], 0, 0, WIT_STATUS_OK);
    exit_process(0); /* a root task exits with zero; the kernel treats anything else as failure */
}
