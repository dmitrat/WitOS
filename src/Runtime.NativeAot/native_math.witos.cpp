#include <math.h>
#include <errno.h>
#include <stdint.h>
#pragma function(log)
extern "C" double wit_ieee754_log(double);
extern "C" { int _fltused = 0x9875; }
extern "C" double __cdecl log(double value)
{
    union { double value; uint64_t bits; } input = { value };
    const uint64_t absolute = input.bits & UINT64_C(0x7fffffffffffffff);
    if (!absolute) errno = ERANGE;
    else if ((input.bits >> 63) && absolute <= UINT64_C(0x7ff0000000000000)) errno = EDOM;
    return wit_ieee754_log(value);
}
