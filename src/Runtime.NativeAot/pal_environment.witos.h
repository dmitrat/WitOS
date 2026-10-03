#ifndef WITOS_PAL_ENVIRONMENT_H
#define WITOS_PAL_ENVIRONMENT_H
#include <stdint.h>
#include <stddef.h>
#include "../Runtime.Native/native_limits.h"

/* Internal single-image startup contract; not a general environment SDK.
 * Call once after image publication and before constructors/workers.
 * The complete table and terminated strings must be initialized readonly data. */
struct WitPalEnvironmentEntry {
    const wchar_t *Name;
    const wchar_t *Value;
    uint32_t NameLength;
    uint32_t ValueLength;
};

bool wit_pal_environment_initialize(const WitPalEnvironmentEntry *entries, uint32_t count);
bool wit_pal_environment_is_ready();
extern "C" wchar_t *wit_pal_environment_strings();
extern "C" int wit_pal_environment_free(wchar_t *);
#endif
