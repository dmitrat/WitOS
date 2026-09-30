#ifndef WITOS_MATH_BITS_H
#define WITOS_MATH_BITS_H
#include <stdint.h>
/* Representation access for the pinned binary64 algorithm, not new math. */
typedef uint32_t u_int32_t;
typedef union WitDoubleBits { double value; uint64_t bits; } WitDoubleBits;
#define OLM_DLLEXPORT
#define __ieee754_log wit_ieee754_log
#define EXTRACT_WORDS(hi,lo,x) do { WitDoubleBits b; b.value=(x); (hi)=(int32_t)(b.bits>>32); (lo)=(uint32_t)b.bits; } while (0)
#define GET_HIGH_WORD(hi,x) do { WitDoubleBits b; b.value=(x); (hi)=(int32_t)(b.bits>>32); } while (0)
#define SET_HIGH_WORD(x,hi) do { WitDoubleBits b; b.value=(x); b.bits=(b.bits&UINT64_C(0xffffffff))|((uint64_t)(uint32_t)(hi)<<32); (x)=b.value; } while (0)
#endif
