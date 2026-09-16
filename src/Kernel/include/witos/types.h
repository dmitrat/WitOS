#ifndef WITOS_TYPES_H
#define WITOS_TYPES_H

typedef unsigned char WitU8;
typedef unsigned short WitU16;
typedef unsigned int WitU32;
typedef unsigned long long WitU64;

_Static_assert(sizeof(WitU8) == 1, "WitU8 width");
_Static_assert(sizeof(WitU16) == 2, "WitU16 width");
_Static_assert(sizeof(WitU32) == 4, "WitU32 width");
_Static_assert(sizeof(WitU64) == 8, "WitU64 width");

#define WIT_NORETURN __declspec(noreturn)

#endif
