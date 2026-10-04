#include <stdio.h>
#include <stdint.h>
#include <stdarg.h>
#include <string.h>
#include <errno.h>
#include <stdlib.h>
#include <initializer_list>
extern "C" int wit_native_vsnprintf_s(unsigned __int64, char *, size_t, size_t, const char *, _locale_t, va_list);
static unsigned tests;
static unsigned __int64 options;

static int check(size_t capacity, size_t maxCount, const char *fmt, ...)
{
    char a[512], b[512];
    memset(a, 0x5a, sizeof(a));
    memset(b, 0x5a, sizeof(b));
    va_list x, y;
    va_start(x, fmt);
    va_copy(y, x);
    errno = 71;
    int n = wit_native_vsnprintf_s(options, a, capacity, maxCount, fmt, nullptr, x), ne = errno;
    errno = 71;
    int m = __stdio_common_vsnprintf_s(options, b, capacity, maxCount, fmt, nullptr, y), me = errno;
    va_end(y);
    va_end(x);
    ++tests;
    if (n != m || ne != me || strcmp(a, b)) {
        printf("FAIL fmt=%s cap=%zu max=%zu ours=%d/%d [%s] CRT=%d/%d [%s]\n", fmt, capacity, maxCount, n, ne, a, m, me,
            b);
        return 1;
    }
    for (size_t i = capacity; i < 512; ++i) {
        if (a[i] != 0x5a) {
            puts("WRITE OOB");
            return 1;
        }
    }
    return 0;
}

static void invalid(const wchar_t *, const wchar_t *, const wchar_t *, unsigned, uintptr_t) {}

int main()
{
    _set_invalid_parameter_handler(invalid);
    for (unsigned __int64 selected : {0ULL, 4ULL, 32ULL, 36ULL}) {
        options = selected;
        for (size_t cap : {size_t(1), size_t(4), size_t(16), size_t(64), size_t(512)}) {
            for (size_t max : {size_t(0), size_t(3), size_t(15), size_t(63), _TRUNCATE}) {
                if (check(cap, max, "Committing %zd bytes (%.3f mb) for GC bookkeeping element#%d failed",
                        size_t(65536), 0.0625, 3)) {
                    return 1;
                }
                if (check(cap, max, "[%+08d][%#08x][%#o][%.0d][%#.0o]", -17, 0x12u, 0u, 0, 0u)) {
                    return 1;
                }
                if (check(cap, max, "[%*.*s][%-8c][%I64d][%ld]", 8, 3, "abcdef", 'Q', INT64_MIN, -2147483647L)) {
                    return 1;
                }
            }
        }
        uint64_t seed = 0x2937491283479182ULL;
        for (unsigned i = 0; i < 2048; ++i) {
            seed = seed * 6364136223846793005ULL + 1442695040888963407ULL;

            union {
                uint64_t bits;
                double value;
            } x = {seed};

            for (unsigned precision : {0U, 1U, 3U, 6U, 18U, 64U}) {
                if (check(512, _TRUNCATE, "%.*f", precision, x.value)) {
                    return 1;
                }
            }
        }
        for (double v : {0.0, -0.0, 0.0625, -0.0625, 2.5, -2.5, 3.5, -3.5}) {
            if (check(512, _TRUNCATE, "[%+#020.3f][%.0f]", v, v)) {
                return 1;
            }
        }
    }
    printf("PASS: %u secure formatting differential cases\n", tests);
    return 0;
}
