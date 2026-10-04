#include "library.h"
#include "tls_callback_order.h"
#pragma optimize("", off)
#define CHECK(v, c) \
    do { \
        if (!(v)) return c; \
    } while (0)

/* The guest half of the TLS callback order (P6.4.b): the scenario of tests/WitOS.Dev.Tests/Native/LibraryTlsCallbacks.c
 * without the thread that predates the load, which the kernel does not admit yet. The trace is formatted the same way
 * and must equal WIT_TLS_CALLBACK_ORDER, which the tool generates from the Windows reference. */
#define WHO_MAIN 6ULL
#define WHO_AFTER 7ULL
#define WHO_BEFORE 8ULL
#define WHO_STEP 9ULL
#define EVENT_CAPACITY 64U
#define TEXT_CAPACITY 512U

static WitU64 record, sink_read, identify, marker_set;
static WitU64 events[EVENT_CAPACITY];
static char text[TEXT_CAPACITY];

static WitU64 symbol(WitU64 handle, const char *name, WitU64 *address)
{
    WitU32 length = 0;
    while (name[length]) {
        ++length;
    }
    return wit_native_library_symbol(handle, name, length, 0, address);
}

static void step(WitU32 number)
{
    ((void (*)(WitU64))record)(WHO_STEP << 56 | (WitU64)number << 48);
}

static WitU64 child_body(void)
{
    CHECK(wit_native_library_thread_enter() == WIT_STATUS_OK, 3601);
    ((void (*)(WitU64))identify)(WHO_AFTER);
    ((void (*)(int))marker_set)(9);
    CHECK(wit_native_library_thread_leave() == WIT_STATUS_OK, 3602);
    return 42;
}

static WIT_NORETURN void child(WitU64 unused)
{
    (void)unused;
    const WitU64 code = child_body();
    (void)wit_native_call(WIT_CALL_THREAD_COMPLETE, code, 0, 0, 0);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

static void put(WitU32 *at, char value)
{
    if (*at + 1 < TEXT_CAPACITY) {
        text[(*at)++] = value;
    }
}

static void put_number(WitU32 *at, WitU32 value)
{
    char digits[10];
    WitU32 count = 0;
    do {
        digits[count++] = (char)('0' + value % 10);
        value /= 10;
    } while (value);
    while (count) {
        put(at, digits[--count]);
    }
}

static char label(WitU32 count, WitU32 address)
{
    for (WitU32 i = 0; i < count; ++i) {
        const WitU64 who = events[i] >> 56;
        if (who >= WHO_MAIN && who <= WHO_BEFORE && (WitU32)events[i] == address) {
            return "TNP"[who - WHO_MAIN];
        }
    }
    return 'X';
}

/* The scenario threads only, as the TRACE line of the Windows reference. */
static void format(WitU32 count)
{
    WitU32 at = 0;
    for (WitU32 i = 0; i < count; ++i) {
        const WitU64 who = events[i] >> 56;
        const WitU32 reason = (WitU32)(events[i] >> 48 & 0xFF), value = (WitU32)(events[i] >> 32 & 0xFFFF);
        const char thread = label(count, (WitU32)events[i]);
        if (who < WHO_MAIN && thread == 'X') {
            continue;
        }
        if (at) {
            put(&at, ' ');
        }
        if (who == WHO_STEP) {
            put(&at, 'M');
            put_number(&at, reason);
            continue;
        }
        put(&at, who >= WHO_MAIN ? "TNP"[who - WHO_MAIN] : "?ABE"[who < 4 ? who : 0]);
        if (who < WHO_MAIN) {
            put_number(&at, reason);
        }
        put(&at, ':');
        put_number(&at, value);
        if (who < WHO_MAIN) {
            put(&at, '@');
            put(&at, thread);
        }
    }
    text[at] = 0;
}

static int same(const char *a, const char *b)
{
    WitU32 i = 0;
    while (a[i] && a[i] == b[i]) {
        ++i;
    }
    return a[i] == b[i];
}

WitU64 wit_native_library_tls_callbacks_test(void)
{
    const char sinkName[] = "/native/tlssink.dll", libraryName[] = "/native/tlscallbacks.dll";
    WitU64 sink = 0, library = 0, thread = 0, result = 0;
    WitU64 status = wit_native_library_load(sinkName, sizeof(sinkName) - 1, &sink);
    CHECK(status == WIT_STATUS_OK, 3620 + status);
    CHECK(symbol(sink, "SinkRecord", &record) == WIT_STATUS_OK && symbol(sink, "SinkRead", &sink_read) == WIT_STATUS_OK,
        3604);
    step(1);
    step(2);
    status = wit_native_library_load(libraryName, sizeof(libraryName) - 1, &library);
    CHECK(status == WIT_STATUS_OK, 3640 + status);
    CHECK(symbol(library, "TlsMarkerRecord", &identify) == WIT_STATUS_OK &&
            symbol(library, "TlsMarkerSet", &marker_set) == WIT_STATUS_OK,
        3606);
    step(3);
    ((void (*)(WitU64))identify)(WHO_MAIN);
    ((void (*)(int))marker_set)(5);
    CHECK(wit_native_call(WIT_CALL_THREAD_CREATE, (WitU64)child, 0, WIT_THREAD_LIBRARY_NOTIFICATIONS, &thread) ==
            WIT_STATUS_OK,
        3607);
    CHECK(wit_native_call(WIT_CALL_THREAD_JOIN, thread, 0, 0, &result) == WIT_STATUS_OK && result == 42, 3608);
    step(4);
    step(5);
    CHECK(wit_native_library_unload(library) == WIT_STATUS_OK, 3609);
    step(6);
    const WitU32 count = ((WitU32 (*)(WitU64 *, WitU32))sink_read)(events, EVENT_CAPACITY);
    CHECK(count <= EVENT_CAPACITY, 3610);
    format(count);
    CHECK(wit_native_library_unload(sink) == WIT_STATUS_OK, 3611);
    CHECK(same(text, WIT_TLS_CALLBACK_ORDER), 3612);
    return 42;
}
