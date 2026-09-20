#ifndef WITOS_PAL_ENVIRONMENT_H
#define WITOS_PAL_ENVIRONMENT_H
#include <stdint.h>
#include <stddef.h>

/* Internal single-image startup contract; not a general environment SDK.
 * Call once after image publication and before constructors/workers.
 * The complete table and terminated strings must be initialized readonly data. */
struct WitPalEnvironmentEntry {
    const wchar_t* Name;
    const wchar_t* Value;
    uint32_t NameLength;
    uint32_t ValueLength;
};
static constexpr uint32_t WIT_PAL_ENV_CAPACITY = 16;
static constexpr uint32_t WIT_PAL_ENV_NAME_MAX = 63;
static constexpr uint32_t WIT_PAL_ENV_VALUE_MAX = 1023;
bool wit_pal_environment_initialize(const WitPalEnvironmentEntry* entries, uint32_t count);
#endif
