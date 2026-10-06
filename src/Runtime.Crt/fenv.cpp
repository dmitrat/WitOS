#include <fenv.h>
#include <float.h>
#include <xmmintrin.h>
#include "crt.h"

/* The floating-point environment (P6.4.k3a3b) is MXCSR's, as in UCRT on x64: _controlfp_s and fesetround change the
 * SSE control and status register only, never the x87 control word, which x64 code does not use. CoreCLR sets the
 * rounding of each thread it starts with _controlfp_s; OpenLibm's fma and fmaf read and set the rounding through
 * fenv.h, whose feraiseexcept is UCRT's inline function, raising exceptions by division, not a library function. */
namespace WitCrt {
namespace {

constexpr unsigned FLAGS = 0x3F;
constexpr unsigned DENORMALS_ARE_ZERO = 0x40;
constexpr unsigned MASK_SHIFT = 7; // an exception's mask is its flag shifted left by 7
constexpr unsigned ROUNDING_SHIFT = 13;
constexpr unsigned ROUNDING = 3u << ROUNDING_SHIFT;
constexpr unsigned FLUSH_TO_ZERO = 0x8000;

/* The abstract exception bits of float.h and fenv.h, and MXCSR's flags, in the same order. */
struct Exception {
    unsigned Abstract;
    unsigned Flag;
};

constexpr Exception EXCEPTIONS[] = {{_EM_INVALID, 0x01}, {_EM_DENORMAL, 0x02}, {_EM_ZERODIVIDE, 0x04},
    {_EM_OVERFLOW, 0x08}, {_EM_UNDERFLOW, 0x10}, {_EM_INEXACT, 0x20}};

/* The control word of float.h that MXCSR holds: exception masks, rounding and denormal control. The precision and
 * infinity controls belong to the x87 and do not exist here. */
unsigned Control(unsigned mxcsr)
{
    unsigned control = 0;
    for (const Exception &exception : EXCEPTIONS) {
        if (mxcsr & (exception.Flag << MASK_SHIFT)) {
            control |= exception.Abstract;
        }
    }
    control |= ((mxcsr & ROUNDING) >> ROUNDING_SHIFT) << 8; // _RC_NEAR, _RC_DOWN, _RC_UP, _RC_CHOP in MXCSR's order
    const bool flush = mxcsr & FLUSH_TO_ZERO;
    const bool zero = mxcsr & DENORMALS_ARE_ZERO;
    if (flush && zero) {
        control |= _DN_FLUSH;
    } else if (zero) {
        control |= _DN_FLUSH_OPERANDS_SAVE_RESULTS;
    } else if (flush) {
        control |= _DN_SAVE_OPERANDS_FLUSH_RESULTS;
    }
    return control;
}

/* MXCSR with the given control word, keeping its flags and its other bits. */
unsigned Mxcsr(unsigned control, unsigned mxcsr)
{
    for (const Exception &exception : EXCEPTIONS) {
        const unsigned mask = exception.Flag << MASK_SHIFT;
        mxcsr = control & exception.Abstract ? mxcsr | mask : mxcsr & ~mask;
    }
    mxcsr = (mxcsr & ~ROUNDING) | (((control & _MCW_RC) >> 8) << ROUNDING_SHIFT);
    mxcsr &= ~(FLUSH_TO_ZERO | DENORMALS_ARE_ZERO);
    switch (control & _MCW_DN) {
    case _DN_FLUSH:
        return mxcsr | FLUSH_TO_ZERO | DENORMALS_ARE_ZERO;
    case _DN_FLUSH_OPERANDS_SAVE_RESULTS:
        return mxcsr | DENORMALS_ARE_ZERO;
    case _DN_SAVE_OPERANDS_FLUSH_RESULTS:
        return mxcsr | FLUSH_TO_ZERO;
    default:
        return mxcsr;
    }
}

} // namespace

/* As in UCRT on x64, a value bit under the mask beyond the controls float.h defines is an invalid parameter, and
 * otherwise the call succeeds. It ignores the x87 precision and infinity controls and the denormal exception's mask,
 * which only _control87 changes. A changed control word clears MXCSR's exception flags, as UCRT's does. */
int Controlfp_s(unsigned *current, unsigned value, unsigned mask)
{
    if (value & mask & ~(_MCW_EM | _MCW_RC | _MCW_PC | _MCW_IC | _MCW_DN)) {
        InvalidParameter();
    }
    mask &= (_MCW_EM & ~_EM_DENORMAL) | _MCW_RC | _MCW_DN;
    unsigned mxcsr = _mm_getcsr();
    const unsigned control = Control(mxcsr);
    const unsigned changed = (control & ~mask) | (value & mask);
    if (changed != control) {
        mxcsr = Mxcsr(changed, mxcsr) & ~FLAGS;
        _mm_setcsr(mxcsr);
    }
    if (current) {
        *current = Control(mxcsr);
    }
    return 0;
}

int Fegetround()
{
    return int(Control(_mm_getcsr()) & _MCW_RC); // FE_TONEAREST and the rest are the _RC_ values
}

/* Unlike _controlfp_s, it changes the rounding bits alone and keeps the exception flags, as UCRT's does. */
int Fesetround(int round)
{
    if (round & ~_MCW_RC) {
        return 1;
    }
    _mm_setcsr((_mm_getcsr() & ~ROUNDING) | ((unsigned(round) >> 8) << ROUNDING_SHIFT));
    return 0;
}

} // namespace WitCrt

#ifndef WITCRT_REFERENCE
extern "C" errno_t __cdecl _controlfp_s(unsigned *current, unsigned value, unsigned mask)
{
    return WitCrt::Controlfp_s(current, value, mask);
}

extern "C" int __cdecl fegetround(void)
{
    return WitCrt::Fegetround();
}

extern "C" int __cdecl fesetround(int round)
{
    return WitCrt::Fesetround(round);
}
#endif
