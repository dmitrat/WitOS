#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <stdlib.h>
#include <errno.h>
#include <limits.h>
#pragma function(memcpy, strlen, strcmp)

/* The native C locale is fixed: no locale service or managed allocation.
 * Static compiler TLS is available before dynamic C++ TLS constructors. */
[[msvc::no_tls_guard]] static __declspec(thread) int native_errno;

extern "C" int *__cdecl _errno(void)
{
    return &native_errno;
}

extern "C" decltype(&_errno) const __imp__errno = &_errno;

extern "C" void *__cdecl memcpy(void *destination, const void *source, size_t count)
{
    auto to = (volatile unsigned char *)destination;
    auto from = (const volatile unsigned char *)source;
    for (size_t i = 0; i < count; ++i) {
        to[i] = from[i];
    }
    return destination;
}

extern "C" size_t __cdecl strlen(const char *string)
{
    const volatile char *p = string;
    size_t length = 0;
    while (p[length]) {
        ++length;
    }
    return length;
}

extern "C" int __cdecl strcmp(const char *first, const char *second)
{
    for (;;) {
        const auto a = (unsigned char)*first++, b = (unsigned char)*second++;
        if (a != b || !a) {
            return (int)a - (int)b;
        }
    }
}

static unsigned char lower(unsigned char value)
{
    return value >= 'A' && value <= 'Z' ? (unsigned char)(value + ('a' - 'A')) : value;
}

extern "C" int __cdecl _stricmp(const char *first, const char *second)
{
    for (;;) {
        const auto a = lower((unsigned char)*first++), b = lower((unsigned char)*second++);
        if (a != b || !a) {
            return (int)a - (int)b;
        }
    }
}

static int digit(unsigned char value)
{
    if (value >= '0' && value <= '9') {
        return value - '0';
    }
    value = lower(value);
    return value >= 'a' && value <= 'z' ? value - 'a' + 10 : 36;
}

static unsigned long long parse_unsigned(const char *string, char **end, int base, unsigned long long maximum)
{
    if (end) {
        *end = (char *)string;
    }
    if (!string || base < 0 || base == 1 || base > 36) {
        errno = EINVAL;
        return 0;
    }
    const char *p = string;
    while (*p == ' ' || (*p >= '\t' && *p <= '\r')) {
        ++p;
    }
    const bool negative = *p == '-';
    if (*p == '-' || *p == '+') {
        ++p;
    }
    if ((base == 0 || base == 16) && p[0] == '0' && (p[1] == 'x' || p[1] == 'X') && digit((unsigned char)p[2]) < 16) {
        base = 16;
        p += 2;
    }
    if (!base) {
        base = p[0] == '0' ? 8 : 10;
    }
    const char *first = p;
    unsigned long long value = 0;
    bool overflow = false;
    for (int n; (n = digit((unsigned char)*p)) < base; ++p) {
        if (value > (maximum - (unsigned)n) / (unsigned)base) {
            overflow = true;
        } else if (!overflow) {
            value = value * (unsigned)base + (unsigned)n;
        }
    }
    if (p == first) {
        return 0;
    }
    if (end) {
        *end = (char *)p;
    }
    if (overflow) {
        errno = ERANGE;
        return maximum;
    }
    return negative ? (0ULL - value) & maximum : value;
}

extern "C" unsigned long long __cdecl strtoull(const char *string, char **end, int base)
{
    return parse_unsigned(string, end, base, ULLONG_MAX);
}

extern "C" unsigned long __cdecl strtoul(const char *string, char **end, int base)
{
    static_assert(sizeof(unsigned long) == 4, "WitOS Windows-codegen native CRT uses 32-bit unsigned long");
    return (unsigned long)parse_unsigned(string, end, base, ULONG_MAX);
}
