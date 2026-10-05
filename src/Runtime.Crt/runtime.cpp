#include <math.h>
#include <stdint.h>
#include <string.h>
#include "crt.h"

/* The rest of the subset (P6.4.h, P6.4.i): the invalid-parameter end, ceilf, terminate, abort, _invoke_watson and
 * _fltused, the marker of floating-point code that a C runtime defines (the NativeAOT overlay has its own in
 * native_math.witos.cpp; a module links one). The STL's number facets (P6.4.i3) also call frexp and the
 * classifications _dclass and _ldclass, exact as in UCRT, and strtod and strtof, which, like the floating-point
 * conversions of printf, end the process as unimplemented. */
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

} // namespace WitCrt

#ifndef WITCRT_REFERENCE
extern "C" {
int _fltused = 0x9875;
}

#pragma function(ceilf, _dclass, _ldclass)

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

/* Parsing floating-point text is not implemented (P6.4.i3). */
extern "C" double __cdecl strtod(const char *, char **)
{
    WitCrt::InvalidParameter();
}

extern "C" float __cdecl strtof(const char *, char **)
{
    WitCrt::InvalidParameter();
}

/* std::terminate without terminate handlers, like the C++ runtime's __std_terminate. */
extern "C" __declspec(noreturn) void __cdecl terminate() noexcept
{
    WitCrt::Platform::Fatal();
}
#endif
