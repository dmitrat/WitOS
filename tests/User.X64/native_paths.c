#include "path.h"
#pragma optimize("", off)

static WitU32 length(const char *value)
{
    WitU32 size = 0;
    while (value[size]) {
        ++size;
    }
    return size;
}

static int equal(const WitNativePath *value, const char *expected)
{
    WitU32 size = length(expected);
    if (value->Bytes != size || value->Text[size]) {
        return 0;
    }
    for (WitU32 i = 0; i < size; ++i) {
        if (value->Text[i] != expected[i]) {
            return 0;
        }
    }
    return 1;
}

static int unchanged(const WitNativePath *value)
{
    for (WitU32 i = 0; i < sizeof(*value); ++i) {
        if (((const WitU8 *)value)[i] != 0xA5) {
            return 0;
        }
    }
    return 1;
}

#define CHECK(value, code) \
    do { \
        if (!(value)) return code; \
    } while (0)

static WIT_NORETURN void directory_worker(WitU64 argument)
{
    (void)argument;
    const WitU64 status = wit_native_cwd_set("/app", 4);
    wit_native_call(WIT_CALL_THREAD_EXIT, status == WIT_STATUS_OK ? 42 : 2499, 0, 0, 0);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

WitU64 wit_native_paths_test(void)
{
    WitNativePath value;
    CHECK(wit_native_cwd_get(&value) == WIT_STATUS_OK && equal(&value, "/"), 2401);
    CHECK(wit_native_cwd_set("/app", 4) == WIT_STATUS_OK &&
            wit_native_cwd_get(&value) == WIT_STATUS_OK &&
            equal(&value, "/app"),
        2402);
    CHECK(wit_native_path_full("./CoreClrProbe.dll", 18, &value) == WIT_STATUS_OK &&
            equal(&value, "/app/CoreClrProbe.dll"),
        2403);
    CHECK(wit_native_path_full("../app//./CoreClrProbe.dll", 26, &value) == WIT_STATUS_OK &&
            equal(&value, "/app/CoreClrProbe.dll"),
        2404);
    const char mixed[] = {'a', 'p', 'p', 92, '.', 92, '.', '.', 92, 'p'};
    CHECK(wit_path_resolve("/", 1, mixed, sizeof(mixed), &value) == WIT_STATUS_OK && equal(&value, "/p"), 2405);
    CHECK(wit_native_cwd_set("CoreClrProbe.dll", 16) == WIT_STATUS_WRONG_TYPE &&
            wit_native_cwd_get(&value) == WIT_STATUS_OK &&
            equal(&value, "/app"),
        2406);
    CHECK(wit_native_cwd_set("../missing", 10) == WIT_STATUS_NOT_FOUND &&
            wit_native_cwd_get(&value) == WIT_STATUS_OK &&
            equal(&value, "/app"),
        2407);
    CHECK(wit_native_path_full("../../../../p", 13, &value) == WIT_STATUS_OK && equal(&value, "/p"), 2408);
    for (WitU32 i = 0; i < sizeof(value); ++i) {
        ((WitU8 *)&value)[i] = 0xA5;
    }
    CHECK(wit_native_path_full("CoreClrProbe.dll/", 17, &value) == WIT_STATUS_WRONG_TYPE && unchanged(&value), 2409);
    CHECK(wit_native_path_full("missing", 7, &value) == WIT_STATUS_NOT_FOUND && unchanged(&value), 2410);
    CHECK(wit_native_path_resolve("C:/p", 4, &value) == WIT_STATUS_INVALID_ARGUMENT && unchanged(&value), 2411);
    const WitU8 invalid[] = {0xC0, 0xAF, '/', '.', '.', '/', 'p'};
    CHECK(wit_native_path_resolve((const char *)invalid, sizeof(invalid), &value) == WIT_STATUS_INVALID_ARGUMENT &&
            unchanged(&value),
        2412);
    CHECK(wit_native_path_resolve("", 0, &value) == WIT_STATUS_INVALID_ARGUMENT && unchanged(&value), 2413);
    CHECK(wit_native_cwd_set("..", 2) == WIT_STATUS_OK &&
            wit_native_cwd_get(&value) == WIT_STATUS_OK &&
            equal(&value, "/"),
        2414);
    WitU64 worker = 0, result = 0;
    CHECK(
        wit_native_call(WIT_CALL_THREAD_CREATE_SIMPLE, (WitU64)directory_worker, 0, 0, &worker) == WIT_STATUS_OK, 2415);
    CHECK(wit_native_call(WIT_CALL_THREAD_JOIN, worker, 0, 0, &result) == WIT_STATUS_OK && result == 42, 2416);
    CHECK(wit_native_cwd_get(&value) == WIT_STATUS_OK && equal(&value, "/app"), 2417);
    CHECK(wit_native_cwd_set("/", 1) == WIT_STATUS_OK, 2418);
    return 42;
}
