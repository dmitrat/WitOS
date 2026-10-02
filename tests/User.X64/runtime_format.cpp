#include "pal.witos.h"
#include "tls.h"
#include "protocol.h"
#include <stdio.h>
#include <stdarg.h>
#include <errno.h>
extern "C" void wit_math_set_rounding(unsigned);

static int print(unsigned __int64 options, char *output, size_t capacity, size_t count, const char *format, ...)
{
    va_list args;
    va_start(args, format);
    const int result = __stdio_common_vsnprintf_s(options, output, capacity, count, format, nullptr, args);
    va_end(args);
    return result;
}

static bool equal(const char *a, const char *b)
{
    while (*a && *a == *b) {
        ++a;
        ++b;
    }
    return *a == *b;
}

static bool cases(int saved)
{
    char text[128];
    errno = saved;
    if (print(0, text, sizeof(text), _TRUNCATE, "Committing %zd bytes (%.3f mb) element#%d", size_t(65536), 0.0625,
            3) != 43 ||
        !equal(text, "Committing 65536 bytes (0.063 mb) element#3")) {
        return false;
    }
    if (print(32, text, sizeof(text), _TRUNCATE, "%.3f %.0f", 0.0625, 2.5) != 7 || !equal(text, "0.062 2")) {
        return false;
    }
    if (print(0, text, sizeof(text), _TRUNCATE, "[%+08d][%#08x][%.0d][%#.0o]", -17, 0x12u, 0, 0u) != 25 ||
        !equal(text, "[-0000017][0x000012][][0]")) {
        return false;
    }
    for (unsigned mode = 0; mode < 4; ++mode) {
        wit_math_set_rounding(mode);
        const char *expected = mode == 2 ? "0.063" : "0.062";
        if (print(32, text, sizeof(text), _TRUNCATE, "%.3f", 0.0625) != 5 || !equal(text, expected)) {
            wit_math_set_rounding(0);
            return false;
        }
        if (print(0, text, sizeof(text), _TRUNCATE, "%.3f", 0.0625) != 5 || !equal(text, "0.063")) {
            wit_math_set_rounding(0);
            return false;
        }
    }
    wit_math_set_rounding(0);
    for (auto &c : text) {
        c = char(0x5a);
    }
    if (print(0, text, 8, 3, "abcdef") != -1 || errno != saved || !equal(text, "abc") || text[4] != 0x5a) {
        return false;
    }
    if (print(0, text, 4, _TRUNCATE, "abcdef") != -1 || errno != saved || !equal(text, "abc") || text[4] != 0x5a) {
        return false;
    }
    if (print(0, text, 4, 4, "abcdef") != -1 || errno != ERANGE || text[0]) {
        return false;
    }
    errno = saved;
    int target = 17;
    if (print(0, text, sizeof(text), _TRUNCATE, "%n", &target) != -1 || errno != EINVAL || target != 17 || text[0]) {
        return false;
    }
    errno = saved;
    if (print(0, nullptr, 0, 0, "unused") != 0 || errno != saved) {
        return false;
    }
    if (print(0, text, sizeof(text), _TRUNCATE, "bad%") != -1 || errno != EINVAL || text[0]) {
        return false;
    }
    errno = saved;
    if (print(0, text, sizeof(text), _TRUNCATE, "[%*.*s][%I64d][%ld]", 5, 2, "abcd", INT64_MIN, -9L) < 0 ||
        !equal(text, "[   ab][-9223372036854775808][-9]") ||
        errno != saved) {
        return false;
    }
    return true;
}

static WitU64 worker(WitU64 index)
{
    const DWORD error = DWORD(1500 + index);
    const int saved = int(1600 + index);
    SetLastError(error);
    errno = saved;
    for (unsigned i = 0; i < 2; ++i) {
        if (!cases(saved) ||
            wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK ||
            errno != saved ||
            GetLastError() != error) {
            return 2201;
        }
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" bool wit_test_format(bool threads)
{
    SetLastError(0x73481234);
    errno = 79;
    if (!cases(79)) {
        return false;
    }
    WitU64 arena = 0;
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, 8192, 4096, 0, &arena) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_COMMIT, arena, 4096, 3, nullptr) != WIT_STATUS_OK) {
        return false;
    }
    char *edge = (char *)(arena + 4093);
    edge[0] = 'x';
    edge[1] = 'y';
    edge[2] = 0;
    if (wit_native_call(WIT_CALL_MEMORY_PROTECT, arena, 4096, WIT_MEMORY_READ, nullptr) != WIT_STATUS_OK) {
        return false;
    }
    char text[16];
    if (print(0, text, sizeof(text), _TRUNCATE, "%s", edge) != 2 || !equal(text, "xy")) {
        return false;
    }
    if (wit_native_call(WIT_CALL_MEMORY_PROTECT, arena, 4096, 3, nullptr) != WIT_STATUS_OK) {
        return false;
    }
    char *output = (char *)(arena + 4088);
    if (print(0, output, 8, _TRUNCATE, "%010d", 42) != -1 || !equal(output, "0000000") || errno != 79) {
        return false;
    }
    if (wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) {
        return false;
    }
    if (threads) {
        WitU64 handles[3], result;
        for (WitU64 i = 0; i < 3; ++i) {
            if (wit_native_thread_create(worker, i, &handles[i]) != WIT_STATUS_OK) {
                return false;
            }
        }
        for (unsigned i = 0; i < 3; ++i) {
            if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &result) != WIT_STATUS_OK ||
                result != WIT_TEST_EXIT_CODE) {
                return false;
            }
        }
    }
    return errno == 79 && GetLastError() == 0x73481234;
}
