#ifndef WITOS_TYPES_H
#define WITOS_TYPES_H

#ifdef __cplusplus
#define WIT_STATIC_ASSERT static_assert
#else
#define WIT_STATIC_ASSERT _Static_assert
#endif

typedef unsigned char WitU8;
typedef unsigned short WitU16;
typedef unsigned int WitU32;
typedef unsigned long long WitU64;

WIT_STATIC_ASSERT(sizeof(WitU8) == 1, "WitU8 width");
WIT_STATIC_ASSERT(sizeof(WitU16) == 2, "WitU16 width");
WIT_STATIC_ASSERT(sizeof(WitU32) == 4, "WitU32 width");
WIT_STATIC_ASSERT(sizeof(WitU64) == 8, "WitU64 width");

/* The ABI headers are read by the kernel's MSVC build and by layer 2's clang build (plan step T1). */
#if defined(_MSC_VER)
#define WIT_NORETURN __declspec(noreturn)
#else
#define WIT_NORETURN __attribute__((noreturn))
#endif

#endif
