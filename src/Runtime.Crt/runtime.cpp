#include <math.h>
#include <stdint.h>
#include <string.h>
#include "crt.h"

/* The rest of the subset (P6.4.h, P6.4.i): the invalid-parameter end, ceilf, terminate, abort, _invoke_watson and
 * _fltused, the marker of floating-point code that a C runtime defines (the NativeAOT overlay has its own in
 * native_math.witos.cpp; a module links one). The STL's number facets (P6.4.i3) also call frexp, fabs, abs, llabs
 * and the classifications _dclass, _ldclass, _dtest and _ldtest, exact as in UCRT, and strtod and strtof, which, like
 * the floating-point conversions of printf, end the process as unimplemented. */
namespace WitCrt {

void InvalidParameter()
{
    Platform::Fatal(); // UCRT's default handler ends the process too; no handler can be installed here
}

float Ceilf(float value)
{
    uint32_t bits;
    memcpy(&bits, &value, sizeof(bits));
    const int exponent = int((bits >> 23) & 0xFF) - 127;
    if (exponent == 128) {
        return value + value; // infinite, or a NaN made quiet
    }
    if (exponent >= 23) {
        return value; // no fraction
    }
    if (exponent < 0) {
        if (!(bits & 0x7FFFFFFF)) {
            return value; // a signed zero
        }
        return bits >> 31 ? -0.0f : 1.0f;
    }
    const uint32_t fraction = 0x007FFFFFu >> exponent;
    if (!(bits & fraction)) {
        return value;
    }
    if (!(bits >> 31)) {
        bits += 0x00800000u >> exponent;
    }
    bits &= ~fraction;
    memcpy(&value, &bits, sizeof(value));
    return value;
}

double Frexp(double value, int *exponent)
{
    if (!exponent) {
        InvalidParameter();
    }
    uint64_t bits;
    memcpy(&bits, &value, sizeof(bits));
    int biased = int((bits >> 52) & 0x7FF);
    if (biased == 0x7FF) {
        *exponent = -1; // as UCRT reports infinities and NaNs, a signaling NaN made quiet
        return value + value;
    }
    *exponent = 0;
    if (!(bits & ~(1ULL << 63))) {
        return value; // a signed zero
    }
    if (!biased) {
        // A subnormal m * 2^-1074: shift m until its leading bit is the implicit one, then 0.1m * 2^(-1021 - shift).
        int shift = 0;
        uint64_t mantissa = bits & ((1ULL << 52) - 1);
        while (!(mantissa & (1ULL << 52))) {
            mantissa <<= 1;
            ++shift;
        }
        *exponent = -1021 - shift;
        bits = (bits & (1ULL << 63)) | (1022ULL << 52) | (mantissa & ((1ULL << 52) - 1));
        memcpy(&value, &bits, sizeof(value));
        return value;
    }
    *exponent = biased - 1022;
    bits = (bits & ~(0x7FFULL << 52)) | (1022ULL << 52);
    memcpy(&value, &bits, sizeof(value));
    return value;
}

double Fabs(double value)
{
    uint64_t bits;
    memcpy(&bits, &value, sizeof(bits));
    bits &= ~(1ULL << 63); // a NaN keeps its payload
    memcpy(&value, &bits, sizeof(value));
    return value;
}

long long Llabs(long long value)
{
    return value < 0 ? (long long)(0ULL - (unsigned long long)value) : value;
}

short Dclass(double value)
{
    uint64_t bits;
    memcpy(&bits, &value, sizeof(bits));
    const uint64_t exponent = (bits >> 52) & 0x7FF, mantissa = bits & ((1ULL << 52) - 1);
    if (exponent == 0x7FF) {
        return mantissa ? FP_NAN : FP_INFINITE;
    }
    if (!exponent) {
        return mantissa ? FP_SUBNORMAL : FP_ZERO;
    }
    return FP_NORMAL;
}

/* getenv (P6.4.k3a2): a narrow copy of the process's environment, made at the first call in the platform's ANSI code
 * page, as UCRT makes the narrow environment of a library; later changes to the process's environment are not in it,
 * as in UCRT, which only _putenv updates. Like UCRT's, the copy leaves out the entries that begin with '=' (the current
 * directories of drives); a name matches without regard to ASCII case. The copy lives as long as the module. */
namespace {
Platform::Lock environmentLock;
char *environment;
bool environmentMade;

void Swap(char *first, char *second, size_t width)
{
    for (size_t i = 0; i < width; ++i) {
        const char value = first[i];
        first[i] = second[i];
        second[i] = value;
    }
}

void Sift(char *base, size_t root, size_t count, size_t width, int(__cdecl *compare)(const void *, const void *))
{
    for (;;) {
        size_t child = 2 * root + 1;
        if (child >= count) {
            return;
        }
        if (child + 1 < count && compare(base + child * width, base + (child + 1) * width) < 0) {
            ++child;
        }
        if (compare(base + root * width, base + child * width) >= 0) {
            return;
        }
        Swap(base + root * width, base + child * width, width);
        root = child;
    }
}
} // namespace

char *Getenv(const char *name)
{
    if (!name) {
        InvalidParameter();
    }
    size_t length = 0;
    while (name[length]) {
        if (++length >= 32767) { // _MAX_ENV
            InvalidParameter();
        }
    }
    Platform::Acquire(environmentLock);
    if (!environmentMade) {
        environment = Platform::NarrowEnvironment();
        environmentMade = true;
    }
    char *result = nullptr;
    for (char *entry = environment; entry && *entry; entry += strlen(entry) + 1) {
        if (*entry != '=' && !Strnicmp(entry, name, length) && entry[length] == '=') {
            result = entry + length + 1;
            break;
        }
    }
    Platform::Release(environmentLock);
    return result;
}

/* qsort (P6.4.k3a2) as a heap sort: the order of equal elements is unspecified, as the C standard leaves it. */
void Qsort(void *base, size_t count, size_t width, int(__cdecl *compare)(const void *, const void *))
{
    if ((!base && count) || !width || !compare) {
        InvalidParameter();
    }
    auto *bytes = static_cast<char *>(base);
    for (size_t i = count / 2; i-- > 0;) {
        Sift(bytes, i, count, width, compare);
    }
    for (size_t end = count; end-- > 1;) {
        Swap(bytes, bytes + end * width, width);
        Sift(bytes, 0, end, width, compare);
    }
}

} // namespace WitCrt

#ifndef WITCRT_REFERENCE
extern "C" {
int _fltused = 0x9875;
}

#pragma function(ceilf, _dclass, _ldclass, _dtest, _ldtest, abs, llabs, fabs)

extern "C" float __cdecl ceilf(float value)
{
    return WitCrt::Ceilf(value);
}

/* abort without signal handlers: UCRT's default reports the fault and ends the process too (P6.4.i). */
extern "C" __declspec(noreturn) void __cdecl abort()
{
    WitCrt::Platform::Fatal();
}

/* The end of a failed parameter check that headers and the STL call directly. */
extern "C" __declspec(noreturn) void __cdecl _invoke_watson(
    const wchar_t *, const wchar_t *, const wchar_t *, unsigned int, uintptr_t)
{
    WitCrt::Platform::Fatal();
}

extern "C" double __cdecl frexp(double value, int *exponent)
{
    return WitCrt::Frexp(value, exponent);
}

extern "C" short __cdecl _dclass(double value)
{
    return WitCrt::Dclass(value);
}

extern "C" short __cdecl _ldclass(long double value)
{
    return WitCrt::Dclass(double(value)); // long double is double on x64
}

extern "C" short __cdecl _dtest(double *value)
{
    return WitCrt::Dclass(*value);
}

extern "C" short __cdecl _ldtest(long double *value)
{
    return WitCrt::Dclass(double(*value));
}

extern "C" double __cdecl fabs(double value)
{
    return WitCrt::Fabs(value);
}

extern "C" int __cdecl abs(int value)
{
    return int(WitCrt::Llabs(value) & 0xFFFFFFFF); // INT_MIN stays itself
}

extern "C" long long __cdecl llabs(long long value)
{
    return WitCrt::Llabs(value);
}

/* Parsing floating-point text is not implemented (P6.4.i3). */
extern "C" double __cdecl strtod(const char *, char **)
{
    WitCrt::InvalidParameter();
}

extern "C" float __cdecl strtof(const char *, char **)
{
    WitCrt::InvalidParameter();
}

/* CoreCLR imports wcstod and scanf only from code it does not run here (P6.4.k3a4): wcstod with u16_strtod, which
 * only ilasm calls, and sscanf_s with the reading of PGO text files, which needs reading streams. Both are unimplemented
 * and end the process. */
extern "C" double __cdecl wcstod(const wchar_t *, wchar_t **)
{
    WitCrt::InvalidParameter();
}

extern "C" int __cdecl __stdio_common_vsscanf(unsigned __int64, const char *, size_t, const char *, _locale_t, va_list)
{
    WitCrt::InvalidParameter();
}

/* std::terminate without terminate handlers, like the C++ runtime's __std_terminate. */
extern "C" __declspec(noreturn) void __cdecl terminate() noexcept
{
    WitCrt::Platform::Fatal();
}

extern "C" char *__cdecl getenv(const char *name)
{
    return WitCrt::Getenv(name);
}

extern "C" void __cdecl qsort(void *base, size_t count, size_t width, int(__cdecl *compare)(const void *, const void *))
{
    WitCrt::Qsort(base, count, width, compare);
}

/* The invalid-parameter report of UCRT's inline functions, which ends the process like every invalid parameter. */
extern "C" void __cdecl _invalid_parameter_noinfo(void)
{
    WitCrt::InvalidParameter();
}
#endif
