#include <emmintrin.h>
#include <errno.h>
#include <math.h>
#include <stdint.h>
#include <string.h>
#include "crt.h"

/* The mathematics CoreCLR calls (P6.4.k3a3c). The double functions are OpenLibm's (CrtMathSources), each float
 * function is its double function rounded once to float, which is all but always the correctly rounded result, and
 * fmaf is OpenLibm's own: a fused operation must round once. errno follows the IEEE exceptions an evaluation raises,
 * as UCRT sets it: EDOM for an invalid operation, ERANGE for a pole or an overflow, nothing for an underflow, and
 * nothing for a NaN argument; fma and fmaf set none, like UCRT's. */
#pragma fenv_access(on) // the flags are read around the evaluations

extern "C" {
#define WITCRT_OPENLIBM_UNARY(c, Subset) double wit_openlibm_##c(double);
#define WITCRT_OPENLIBM_BINARY(c, Subset) double wit_openlibm_##c(double, double);
WITCRT_MATH_UNARY(WITCRT_OPENLIBM_UNARY)
WITCRT_MATH_BINARY(WITCRT_OPENLIBM_BINARY)
double wit_openlibm_fma(double, double, double);
float wit_openlibm_fmaf(float, float, float);
double wit_openlibm_modf(double, double *);

/* What OpenLibm calls and math.cpp supplies: sqrt is SSE2's, correctly rounded as the standard requires, and the two
 * classifications are those of OpenLibm's files, whose long-double forms need a layout MSVC does not have. */
double wit_openlibm_sqrt(double x)
{
    return _mm_cvtsd_f64(_mm_sqrt_sd(_mm_set_sd(x), _mm_set_sd(x)));
}

int wit_openlibm_isfinite(double x)
{
    uint64_t bits;
    memcpy(&bits, &x, sizeof(bits));
    return ((bits >> 52) & 0x7FF) != 0x7FF;
}

int wit_openlibm_isnormal(double x)
{
    uint64_t bits;
    memcpy(&bits, &x, sizeof(bits));
    const unsigned exponent = unsigned(bits >> 52) & 0x7FF;
    return exponent != 0 && exponent != 0x7FF;
}
}

namespace WitCrt {
namespace {

// MXCSR's exception flags (math.h's names for the matherr kinds are macros).
constexpr unsigned INVALID_RAISED = 0x01;
constexpr unsigned POLE_RAISED = 0x04;
constexpr unsigned OVERFLOW_RAISED = 0x08;
constexpr unsigned FLAGS = 0x3F;

/* Evaluates with MXCSR's exception flags clear, sets errno from what the evaluation raised and keeps the flags raised
 * before it raised. */
template <typename Evaluation> auto Checked(bool nan, Evaluation evaluation)
{
    const unsigned saved = _mm_getcsr();
    _mm_setcsr(saved & ~FLAGS);
    const auto result = evaluation();
    const unsigned raised = _mm_getcsr() & FLAGS;
    _mm_setcsr(saved | raised);
    if (!nan) {
        if (raised & INVALID_RAISED) {
            errno = EDOM;
        } else if (raised & (POLE_RAISED | OVERFLOW_RAISED)) {
            errno = ERANGE;
        }
    }
    return result;
}

bool IsNan(double x)
{
    return x != x;
}

} // namespace

#define WITCRT_DEFINE_UNARY(c, Subset) \
    double Subset(double x) \
    { \
        return Checked(IsNan(x), [=] { return wit_openlibm_##c(x); }); \
    }
#define WITCRT_DEFINE_BINARY(c, Subset) \
    double Subset(double x, double y) \
    { \
        return Checked(IsNan(x) || IsNan(y), [=] { return wit_openlibm_##c(x, y); }); \
    }
#define WITCRT_DEFINE_UNARY_FLOAT(c, Subset, of) \
    float Subset(float x) \
    { \
        return Checked(IsNan(x), [=] { return float(wit_openlibm_##of(double(x))); }); \
    }
#define WITCRT_DEFINE_BINARY_FLOAT(c, Subset, of) \
    float Subset(float x, float y) \
    { \
        return Checked(IsNan(x) || IsNan(y), [=] { return float(wit_openlibm_##of(double(x), double(y))); }); \
    }
WITCRT_MATH_UNARY(WITCRT_DEFINE_UNARY)
WITCRT_MATH_BINARY(WITCRT_DEFINE_BINARY)
WITCRT_MATH_UNARY_FLOAT(WITCRT_DEFINE_UNARY_FLOAT)
WITCRT_MATH_BINARY_FLOAT(WITCRT_DEFINE_BINARY_FLOAT)

double Fma(double x, double y, double z)
{
    return wit_openlibm_fma(x, y, z);
}

float Fmaf(float x, float y, float z)
{
    return wit_openlibm_fmaf(x, y, z);
}

double Modf(double x, double *integer)
{
    return Checked(IsNan(x), [=] { return wit_openlibm_modf(x, integer); });
}

/* Both parts of a float are floats: the double split is exact. */
float Modff(float x, float *integer)
{
    double whole = 0;
    const float fraction = Checked(IsNan(x), [&] { return float(wit_openlibm_modf(double(x), &whole)); });
    *integer = float(whole);
    return fraction;
}

} // namespace WitCrt

#ifndef WITCRT_REFERENCE
#pragma function(acos, asin, atan, atan2, ceil, cos, cosh, exp, fma, fmod, log, log10, log2, pow, sin, sinh, sqrt)
#pragma function(tan, tanh, acosf, asinf, atanf, atan2f, cosf, coshf, expf, fmaf, fmodf, log10f, log2f, logf, powf)
#pragma function(sinf, sinhf, tanf, tanhf)

#define WITCRT_EXPORT_UNARY(c, Subset) \
    extern "C" double __cdecl c(double x) \
    { \
        return WitCrt::Subset(x); \
    }
#define WITCRT_EXPORT_BINARY(c, Subset) \
    extern "C" double __cdecl c(double x, double y) \
    { \
        return WitCrt::Subset(x, y); \
    }
#define WITCRT_EXPORT_UNARY_FLOAT(c, Subset, of) \
    extern "C" float __cdecl c(float x) \
    { \
        return WitCrt::Subset(x); \
    }
#define WITCRT_EXPORT_BINARY_FLOAT(c, Subset, of) \
    extern "C" float __cdecl c(float x, float y) \
    { \
        return WitCrt::Subset(x, y); \
    }
WITCRT_MATH_UNARY(WITCRT_EXPORT_UNARY)
WITCRT_MATH_BINARY(WITCRT_EXPORT_BINARY)
WITCRT_MATH_UNARY_FLOAT(WITCRT_EXPORT_UNARY_FLOAT)
WITCRT_MATH_BINARY_FLOAT(WITCRT_EXPORT_BINARY_FLOAT)

extern "C" double __cdecl fma(double x, double y, double z)
{
    return WitCrt::Fma(x, y, z);
}

extern "C" float __cdecl fmaf(float x, float y, float z)
{
    return WitCrt::Fmaf(x, y, z);
}

extern "C" double __cdecl modf(double x, double *integer)
{
    return WitCrt::Modf(x, integer);
}

extern "C" float __cdecl modff(float x, float *integer)
{
    return WitCrt::Modff(x, integer);
}
#endif
