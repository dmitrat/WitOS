#ifndef WITOS_RANDOM_H
#define WITOS_RANDOM_H
#include "types.h"
#define WIT_RANDOM_KEY_BYTES 32U
#define WIT_RANDOM_BLOCK_BYTES 64U
/* Internal bootstrap/UP service. All calls are serialized with IF clear.
 * Initialization consumes and wipes the caller-owned seed. No weak fallback. */
int wit_random_initialize(WitU8 *seed);
int wit_random_fill(WitU8 *output,WitU32 bytes);
void wit_random_self_test(void);
/* Kernel diagnostics only; never a user-visible entropy/identity token. */
WitU64 wit_random_generation(void);
#endif
