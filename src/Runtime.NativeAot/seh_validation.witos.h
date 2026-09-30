#ifndef WITOS_SEH_VALIDATION_H
#define WITOS_SEH_VALIDATION_H
#include "unwind_validation.witos.h"
#define WIT_SEH_SCOPE_CAPACITY 128U
struct WitSehScope { WitU32 Begin,End,Handler,Target; };
struct WitSehTable { const WitU8* Entries;WitU32 Count; };
enum WitSehValidation { WitSehValid,WitSehBadRange,WitSehBadFormat,WitSehQuota };
struct WitSehGsData { WitU64 Address;WitU32 Flags; };
// Validates the scope table and complete trailing GS payload before callbacks.
WitSehValidation wit_seh_gs_validate(const WitUserImageInfo*,WitU32,WitU64,WitSehGsData*);
// C-specific scope-table prefix. Caller must retain immutable image lifetime
// and verify the actual language-handler identity before dispatching it.
WitSehValidation wit_seh_validate(const WitUserImageInfo*,WitU32,WitU64,WitSehTable*);
bool wit_seh_scope(const WitSehTable*,WitU32,WitSehScope*);
#endif
