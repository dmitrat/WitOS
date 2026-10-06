#pragma once
/* Forced ahead of every OpenLibm source of the UCRT subset (P6.4.k3a3c). The functions OpenLibm defines and calls get
 * private names, so they meet neither UCRT's in the in-process differential nor the subset's own exports, which wrap
 * them with UCRT's errno in math.cpp. ldexp is scalbn, as upstream's alias makes it; math.cpp supplies sqrt and the
 * two classifications whose upstream files also need long double's layout. The GCC built-ins that OpenLibm's header
 * names for its constants are written as MSVC's own header writes them. */

#define acos wit_openlibm_acos
#define acosh wit_openlibm_acosh
#define asin wit_openlibm_asin
#define asinh wit_openlibm_asinh
#define atan wit_openlibm_atan
#define atan2 wit_openlibm_atan2
#define atanh wit_openlibm_atanh
#define cbrt wit_openlibm_cbrt
#define ceil wit_openlibm_ceil
#define copysign wit_openlibm_copysign
#define cos wit_openlibm_cos
#define cosh wit_openlibm_cosh
#define exp wit_openlibm_exp
#define expm1 wit_openlibm_expm1
#define fabs wit_openlibm_fabs
#define floor wit_openlibm_floor
#define fma wit_openlibm_fma
#define fmaf wit_openlibm_fmaf
#define fmod wit_openlibm_fmod
#define frexp wit_openlibm_frexp
#define ilogb wit_openlibm_ilogb
#define ldexp wit_openlibm_scalbn
#define log wit_openlibm_log
#define log10 wit_openlibm_log10
#define log1p wit_openlibm_log1p
#define log2 wit_openlibm_log2
#define modf wit_openlibm_modf
#define nextafter wit_openlibm_nextafter
#define pow wit_openlibm_pow
#define round wit_openlibm_round
#define scalbn wit_openlibm_scalbn
#define sin wit_openlibm_sin
#define sinh wit_openlibm_sinh
#define sqrt wit_openlibm_sqrt
#define tan wit_openlibm_tan
#define tanh wit_openlibm_tanh
#define __isfinite wit_openlibm_isfinite
#define __isnormal wit_openlibm_isnormal

#define __builtin_huge_val() (1e300 * 1e300)
#define __builtin_huge_valf() ((float)(1e300 * 1e300))
#define __builtin_inff() ((float)(1e300 * 1e300))
