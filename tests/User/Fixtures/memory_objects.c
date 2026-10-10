#include "fixture.h"

/* Memory object fixture over the memory objects of ABI-1 (RFC 0011 v3 section 7.2, plan steps K5.1 and K8.3):
 * MEMORY_OBJECT_CREATE, MEMORY_OBJECT_MAP at chosen and fixed addresses under every protection, views that share pages,
 * protection changes within the handle's rights, attenuated handles, an object that outlives its handle and ends with
 * its last mapping, a dual mapping that runs published code, an object moved through a channel, the object, page and
 * mapping quotas, and an exit with everything live. */

#define READ_WRITE (WIT_MEMORY_READ | WIT_MEMORY_WRITE)
#define READ_EXECUTE (WIT_MEMORY_READ | WIT_MEMORY_EXECUTE)
#define FIXED_DATA (WIT_USER_MEMORY_BASE + 0x100000ULL)
#define FIXED_CODE (WIT_USER_CODE_BASE + 0x10000ULL)

FIXTURE_CALL WitU64 create(WitU64 bytes, WitU64 expected)
{
    return fixture_expect(WIT_CALL_MEMORY_OBJECT_CREATE, bytes, 0, 0, expected);
}

FIXTURE_CALL void release(WitU64 base)
{
    fixture_expect(WIT_CALL_MEMORY_RELEASE, base, 0, 0, WIT_STATUS_OK);
}

FIXTURE_CALL void protect(WitU64 base, WitU64 bytes, WitU64 protection, WitU64 expected)
{
    fixture_expect(WIT_CALL_MEMORY_PROTECT, base, bytes, protection, expected);
}

static void request_map(WitMemoryMapRequest *request, WitU64 object)
{
    request->Version = WIT_MEMORY_MAP_VERSION;
    request->Size = sizeof(*request);
    request->Object = object;
    request->Offset = 0;
    request->Bytes = 4096;
    request->Address = 0;
    request->Protection = WIT_MEMORY_READ;
    request->Flags = 0;
    request->Target = WIT_PROCESS_SELF;
}

static void basic_test(void)
{
    WitMemoryMapRequest request;
    WitU64 objects[WIT_MEMORY_OBJECT_CAPACITY];
    /* Creation validates the size and the flags before it takes pages. */
    create(0, WIT_STATUS_INVALID_ARGUMENT);
    create(4097, WIT_STATUS_INVALID_ARGUMENT);
    fixture_expect(WIT_CALL_MEMORY_OBJECT_CREATE, 4096, 1, 0, WIT_STATUS_INVALID_ARGUMENT);
    const WitU64 object = create(8192, WIT_STATUS_OK);
    /* The first view lies at an address the kernel chose in the data arena. The map request: a foreign version, a
     * target that is no handle of ours, a wrong size, WRITE with EXECUTE, a window beyond the object and an empty
     * window are refused before any reservation is taken. */
    volatile WitU64 *const first = (volatile WitU64 *)fixture_map(object, 0, 8192, READ_WRITE, 0, WIT_STATUS_OK);
    fixture_check((WitU64)first >= WIT_USER_MEMORY_BASE, 10);
    request_map(&request, object);
    request.Version = 2;
    fixture_expect(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&request, sizeof(request), 0, WIT_STATUS_UNSUPPORTED);
    request.Version = WIT_MEMORY_MAP_VERSION;
    request.Target = 0;
    fixture_expect(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&request, sizeof(request), 0, WIT_STATUS_BAD_HANDLE);
    request.Target = WIT_PROCESS_SELF;
    fixture_expect(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&request, sizeof(request) - 1, 0, WIT_STATUS_INVALID_ARGUMENT);
    fixture_map(object, 0, 4096, WIT_MEMORY_WRITE | WIT_MEMORY_EXECUTE, 0, WIT_STATUS_INVALID_ARGUMENT);
    fixture_map(object, 4096, 8192, WIT_MEMORY_READ, 0, WIT_STATUS_TOO_LARGE);
    fixture_map(object, 0, 0, WIT_MEMORY_READ, 0, WIT_STATUS_INVALID_ARGUMENT);
    /* Two views share the pages; a protection change within the rights takes effect on both. */
    first[0] = 0x1122;
    first[512] = 0x3344;
    volatile WitU64 *const second =
        (volatile WitU64 *)fixture_map(object, 4096, 4096, WIT_MEMORY_READ, 0, WIT_STATUS_OK);
    fixture_check(second[0] == 0x3344, 11);
    protect((WitU64)second, 4096, READ_WRITE, WIT_STATUS_OK);
    second[0] = 0x5566;
    fixture_check(first[512] == 0x5566, 12);
    /* An attenuated handle maps read-only views only, and its views cannot be made writable or committed. */
    const WitU64 reader = fixture_duplicate(object, WIT_RIGHT_MAP | WIT_RIGHT_QUERY, WIT_STATUS_OK);
    fixture_map(reader, 0, 4096, READ_WRITE, 0, WIT_STATUS_DENIED);
    const WitU64 view = fixture_map(reader, 0, 4096, WIT_MEMORY_READ, 0, WIT_STATUS_OK);
    fixture_check(*(volatile WitU64 *)view == 0x1122, 13);
    protect(view, 4096, READ_WRITE, WIT_STATUS_DENIED);
    fixture_expect(WIT_CALL_MEMORY_COMMIT, view, 4096, READ_WRITE, WIT_STATUS_DENIED);
    release(view);
    fixture_close(reader);
    /* A fixed address must be free; a free one is taken as asked. */
    fixture_map(object, 0, 4096, WIT_MEMORY_READ, (WitU64)first, WIT_STATUS_BUSY);
    fixture_check(fixture_map(object, 0, 4096, WIT_MEMORY_READ, FIXED_DATA, WIT_STATUS_OK) == FIXED_DATA, 14);
    release(FIXED_DATA);
    /* The object outlives its handle and ends with its last mapping; the object quota is whole again afterward. */
    fixture_close(object);
    fixture_check(first[0] == 0x1122 && second[0] == 0x5566, 15);
    release((WitU64)first);
    release((WitU64)second);
    for (WitU32 i = 0; i < WIT_MEMORY_OBJECT_CAPACITY; ++i) {
        objects[i] = create(4096, WIT_STATUS_OK);
    }
    create(4096, WIT_STATUS_NO_MEMORY);
    for (WitU32 i = 0; i < WIT_MEMORY_OBJECT_CAPACITY; ++i) {
        fixture_close(objects[i]);
    }
}

/* A function that returns 0x1234, as each ISA encodes it. */
static void write_function(volatile WitU32 *code)
{
#if defined(__x86_64__)
    code[0] = 0x001234B8U; /* mov eax, 0x1234 */
    code[1] = 0x0000C300U; /* the last byte of the immediate, then ret */
#else
    code[0] = 0x52824680U; /* mov w0, #0x1234 */
    code[1] = 0xD65F03C0U; /* ret */
#endif
}

static void code_test(void)
{
    /* A writable view receives the instructions, an executable view at a fixed address of the code arena runs them
     * once published; publication through the writable view is refused. */
    const WitU64 object = create(4096, WIT_STATUS_OK);
    const WitU64 writable = fixture_map(object, 0, 4096, READ_WRITE, 0, WIT_STATUS_OK);
    const WitU64 executable = fixture_map(object, 0, 4096, READ_EXECUTE, FIXED_CODE, WIT_STATUS_OK);
    fixture_check(executable == FIXED_CODE, 20);
    write_function((volatile WitU32 *)writable);
    fixture_expect(WIT_CALL_CODE_PUBLISH, writable, 4096, 0, WIT_STATUS_DENIED);
    fixture_expect(WIT_CALL_CODE_PUBLISH, executable, 4096, 0, WIT_STATUS_OK);
    fixture_check(((WitU32 (*)(void))executable)() == 0x1234, 21);
    protect(writable, 4096, WIT_MEMORY_WRITE | WIT_MEMORY_EXECUTE, WIT_STATUS_INVALID_ARGUMENT);
    protect(writable, 4096, READ_EXECUTE, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_CODE_PUBLISH, writable, 4096, 0, WIT_STATUS_OK);
    fixture_check(((WitU32 (*)(void))writable)() == 0x1234, 22);
    release(executable);
    release(writable);
    fixture_close(object);
}

static void transfer_test(void)
{
    /* An object moves through a channel: the sender's handle is gone, its view stays, the receiver's view reads. */
    WitU64 ends[2] = {0, 0}, moved = 0, received = 0;
    const WitU64 object = create(4096, WIT_STATUS_OK);
    const WitU64 view = fixture_map(object, 0, 4096, READ_WRITE, 0, WIT_STATUS_OK);
    *(volatile WitU64 *)view = 0x7777;
    fixture_expect(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, WIT_STATUS_OK);
    moved = object;
    fixture_send_handle(ends[0], &moved, WIT_STATUS_OK);
    fixture_map(object, 0, 4096, WIT_MEMORY_READ, 0, WIT_STATUS_BAD_HANDLE);
    fixture_receive_handle(ends[1], &received, WIT_STATUS_OK);
    const WitU64 other = fixture_map(received, 0, 4096, WIT_MEMORY_READ, 0, WIT_STATUS_OK);
    fixture_check(*(volatile WitU64 *)other == 0x7777, 30);
    release(view);
    release(other);
    fixture_close(received);
    fixture_close(ends[0]);
    fixture_close(ends[1]);
}

static void limits_test(void)
{
    WitU64 mappings[WIT_USER_RESERVATION_CAPACITY];
    /* An object beyond its page quota is refused; a second large object exceeds the component's pages and takes none;
     * the mappings of one object are bounded by the reservations. */
    create((WIT_MEMORY_OBJECT_PAGES + 1) * 4096ULL, WIT_STATUS_TOO_LARGE);
    const WitU64 large = create(WIT_MEMORY_OBJECT_PAGES * 4096ULL, WIT_STATUS_OK);
    create(WIT_MEMORY_OBJECT_PAGES * 4096ULL, WIT_STATUS_NO_MEMORY);
    fixture_close(large);
    fixture_close(create(WIT_MEMORY_OBJECT_PAGES * 4096ULL, WIT_STATUS_OK));
    const WitU64 object = create(4096, WIT_STATUS_OK);
    for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
        mappings[i] = fixture_map(object, 0, 4096, WIT_MEMORY_READ, 0, WIT_STATUS_OK);
    }
    fixture_map(object, 0, 4096, WIT_MEMORY_READ, 0, WIT_STATUS_NO_MEMORY);
    for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
        release(mappings[i]);
    }
    fixture_close(object);
}

static void exit_test(void)
{
    /* The component exits with everything live: an object behind a view, its duplicate handle in flight in a channel,
     * and a second object behind its handle alone. The kernel releases the handles and the capability at the exit, the
     * view at the teardown. */
    WitU64 ends[2] = {0, 0};
    const WitU64 object = create(4096, WIT_STATUS_OK);
    fixture_map(object, 0, 4096, READ_WRITE, 0, WIT_STATUS_OK);
    WitU64 moved = fixture_duplicate(object, 0, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, WIT_STATUS_OK);
    fixture_send_handle(ends[0], &moved, WIT_STATUS_OK);
    create(8192, WIT_STATUS_OK);
}

FIXTURE_ENTRY void wit_user_start(const WitRootStartup *startup)
{
    const WitUserTestConfig *config = fixture_config(startup);
    fixture_check(startup->AbiVersion == WIT_ABI_VERSION, 1);
    switch (config->Mode) {
    case WIT_MEMORY_OBJECT_TEST_BASIC:
        basic_test();
        break;
    case WIT_MEMORY_OBJECT_TEST_CODE:
        code_test();
        break;
    case WIT_MEMORY_OBJECT_TEST_TRANSFER:
        transfer_test();
        break;
    case WIT_MEMORY_OBJECT_TEST_LIMITS:
        limits_test();
        break;
    case WIT_MEMORY_OBJECT_TEST_EXIT:
        exit_test();
        break;
    default:
        fixture_failed(2);
    }
    fixture_exit(WIT_TEST_EXIT_CODE);
}
