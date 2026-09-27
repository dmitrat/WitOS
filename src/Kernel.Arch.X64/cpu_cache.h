#ifndef WITOS_X64_CPU_CACHE_H
#define WITOS_X64_CPU_CACHE_H
#include "witos/types.h"
/* Architecture-private CPUID reader, also used by deterministic decoder tests. */
typedef void (*WitCpuidReader)(WitU32 leaf, WitU32 subleaf, WitU32 registers[4]);
WitU64 wit_x64_decode_cache(WitCpuidReader read);
WitU64 wit_x64_cache_size(void);
void wit_x64_cache_self_test(void);
#endif
