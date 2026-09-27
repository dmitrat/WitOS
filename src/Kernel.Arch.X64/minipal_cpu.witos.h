#ifndef WITOS_MINIPAL_CPU_H
#define WITOS_MINIPAL_CPU_H
#include <minipal/cpufeatures.h>
extern "C" {
#include "bootstrap.h"
}
/* Private decoder hooks; no new kernel ABI or public resource API. */
int wit_x64_minipal_decode(WitU32 maximum, WitU32 ecx, WitU32 edx);
bool wit_x64_minipal_brand_match(const unsigned char* brand);
WitU32 wit_x64_minipal_leaf1_ecx();
#endif
