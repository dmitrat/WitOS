#include "library.h"
#include "tls_callback_order.h"
#pragma optimize("", off)
#define CHECK(v, c) \
    do { \
        if (!(v)) return c; \
    } while (0)

/* The guest half of the DLL TLS references (P6.4.b-d): the scenarios of tests/WitOS.Dev.Tests/Native/
 * LibraryTlsCallbacks.c and LibraryTlsObjects.c, including the thread that already runs when the library loads. Each
 * trace is formatted the same way and must equal the Windows order the tool generates: WIT_TLS_CALLBACK_ORDER for the
 * callback library; WIT_TLS_CALLBACK_NOENTRY_ORDER for the same library linked without an entry point, which is loaded
 * while threads without notifications run and admits another; WIT_TLS_OBJECTS_ORDER for C++ thread_local objects with
 * the WitOS dynamic TLS support. */
#define WHO_MAIN 6ULL
#define WHO_AFTER 7ULL
#define WHO_BEFORE 8ULL
#define WHO_STEP 9ULL
#define EVENT_CAPACITY 64U
#define TEXT_CAPACITY 512U

static WitU64 record, sink_read, identify, marker_set, touch, ready, go;
static int notifications, objects;
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

/* What a thread does with the library once identified: set its TLS marker or touch its objects. */
static void act(int marker)
{
    if (objects) {
        (void)((int (*)(void))touch)();
    } else {
        ((void (*)(int))marker_set)(marker);
    }
}

static WitU64 child_body(void)
{
    CHECK(!notifications || wit_native_library_thread_enter() == WIT_STATUS_OK, 1);
    ((void (*)(WitU64))identify)(WHO_AFTER);
    act(9);
    CHECK(!notifications || wit_native_library_thread_leave() == WIT_STATUS_OK, 2);
    return 42;
}

/* Runs before the load and waits until main releases it after the thread started after the load has exited. */
static WitU64 before_body(void)
{
    CHECK(!notifications || wit_native_library_thread_enter() == WIT_STATUS_OK, 1);
    CHECK(wit_native_call(WIT_CALL_EVENT_SET, ready, 0, 0, 0) == WIT_STATUS_OK &&
            wit_native_wait_one(go, WIT_WAIT_INFINITE) == WIT_STATUS_OK,
        3);
    ((void (*)(WitU64))identify)(WHO_BEFORE);
    if (objects) {
        act(0);
    }
    CHECK(!notifications || wit_native_library_thread_leave() == WIT_STATUS_OK, 2);
    return 42;
}

static WIT_NORETURN void child(WitU64 before)
{
    const WitU64 code = before ? before_body() : child_body();
    (void)wit_native_call(WIT_CALL_THREAD_EXIT, code, 0, 0, 0);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

static WitU64 start(WitU64 before, WitU64 *thread)
{
    return wit_native_thread_start((WitU64)child, before, notifications ? WIT_THREAD_LIBRARY_NOTIFICATIONS : 0, thread);
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

/* The scenario threads only, as the TRACE line of the Windows reference: callbacks A, B and entry E by reason, object
 * construction C and destruction D by object and serial, thread names with their TLS marker unless objects. */
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
        put(&at, who >= WHO_MAIN ? "TNP"[who - WHO_MAIN] : "?ABECD"[who]);
        if (who >= WHO_MAIN && objects) {
            continue;
        }
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

/* One run of the scenario; failure codes are offsets from base. The object scenario marks the main thread's access
 * as its own step, so the later steps shift by one. */
static WitU64 scenario(const char *libraryName, WitU32 nameBytes, const char *expected, WitU64 base)
{
    const char sinkName[] = "/native/tlssink.dll";
    WitU64 sink = 0, library = 0, thread = 0, before = 0, result = 0;
    WitU64 status = wit_native_library_load(sinkName, sizeof(sinkName) - 1, &sink);
    CHECK(status == WIT_STATUS_OK, base + 20 + status);
    CHECK(symbol(sink, "SinkRecord", &record) == WIT_STATUS_OK && symbol(sink, "SinkRead", &sink_read) == WIT_STATUS_OK,
        base + 4);
    CHECK(wit_native_call(WIT_CALL_EVENT_CREATE, WIT_EVENT_MANUAL_RESET, 0, 0, &ready) == WIT_STATUS_OK &&
            wit_native_call(WIT_CALL_EVENT_CREATE, WIT_EVENT_MANUAL_RESET, 0, 0, &go) == WIT_STATUS_OK,
        base + 13);
    step(1);
    CHECK(start(1, &before) == WIT_STATUS_OK && wit_native_wait_one(ready, WIT_WAIT_INFINITE) == WIT_STATUS_OK,
        base + 14);
    step(2);
    status = wit_native_library_load(libraryName, nameBytes, &library);
    CHECK(status == WIT_STATUS_OK, base + 40 + status);
    CHECK(objects ? symbol(library, "TlsObjectsRecord", &identify) == WIT_STATUS_OK &&
                symbol(library, "TouchObjects", &touch) == WIT_STATUS_OK
                  : symbol(library, "TlsMarkerRecord", &identify) == WIT_STATUS_OK &&
                symbol(library, "TlsMarkerSet", &marker_set) == WIT_STATUS_OK,
        base + 6);
    step(3);
    ((void (*)(WitU64))identify)(WHO_MAIN);
    act(5);
    const WitU32 shift = objects ? 1U : 0U;
    if (objects) {
        step(4);
    }
    CHECK(start(0, &thread) == WIT_STATUS_OK, base + 7);
    CHECK(wit_native_thread_join(thread, &result) == WIT_STATUS_OK, base + 8);
    CHECK(result == 42, base + result);
    step(4 + shift);
    CHECK(wit_native_call(WIT_CALL_EVENT_SET, go, 0, 0, 0) == WIT_STATUS_OK &&
            wit_native_thread_join(before, &result) == WIT_STATUS_OK,
        base + 15);
    CHECK(result == 42, base + 60 + result);
    CHECK(wit_native_call(WIT_CALL_CLOSE, ready, 0, 0, 0) == WIT_STATUS_OK &&
            wit_native_call(WIT_CALL_CLOSE, go, 0, 0, 0) == WIT_STATUS_OK,
        base + 16);
    step(5 + shift);
    CHECK(wit_native_library_unload(library) == WIT_STATUS_OK, base + 9);
    step(6 + shift);
    const WitU32 count = ((WitU32 (*)(WitU64 *, WitU32))sink_read)(events, EVENT_CAPACITY);
    CHECK(count <= EVENT_CAPACITY, base + 10);
    format(count);
    CHECK(wit_native_library_unload(sink) == WIT_STATUS_OK, base + 11);
    CHECK(same(text, expected), base + 12);
    return 0;
}

WitU64 wit_native_library_tls_callbacks_test(void)
{
    const char library[] = "/native/tlscallbacks.dll", noEntry[] = "/native/tlsnoentry.dll",
               objectLibrary[] = "/native/tlsobjects.dll";
    notifications = 1;
    objects = 0;
    WitU64 code = scenario(library, sizeof(library) - 1, WIT_TLS_CALLBACK_ORDER, 3600);
    if (!code) {
        notifications = 0;
        code = scenario(noEntry, sizeof(noEntry) - 1, WIT_TLS_CALLBACK_NOENTRY_ORDER, 3700);
    }
    if (!code) {
        notifications = 1;
        objects = 1;
        code = scenario(objectLibrary, sizeof(objectLibrary) - 1, WIT_TLS_OBJECTS_ORDER, 3800);
    }
    return code ? code : 42;
}
