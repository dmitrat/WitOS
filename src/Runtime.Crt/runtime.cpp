#include <math.h>
#include <stdint.h>
#include <string.h>
#include "crt.h"

/* The rest of the subset (P6.4.h): the invalid-parameter end, ceilf, terminate and _fltused, the marker of floating-point
 * code that a C runtime defines (the NativeAOT overlay has its own in native_math.witos.cpp; a module links one). */
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

} // namespace WitCrt

#ifndef WITCRT_REFERENCE
extern "C" {
int _fltused = 0x9875;
}

#pragma function(ceilf)

extern "C" float __cdecl ceilf(float value)
{
    return WitCrt::Ceilf(value);
}

/* std::terminate without terminate handlers, like the C++ runtime's __std_terminate. */
extern "C" __declspec(noreturn) void __cdecl terminate() noexcept
{
    WitCrt::Platform::Fatal();
}
#endif
