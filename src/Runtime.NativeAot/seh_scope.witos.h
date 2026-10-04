#ifndef WITOS_SEH_SCOPE_H
#define WITOS_SEH_SCOPE_H
#include <windows.h>
#include "seh_validation.witos.h"

enum WitSehAction {
    WitSehSearch,
    WitSehContinue,
    WitSehTarget,
    WitSehInvalid
};

struct WitSehDecision {
    WitU32 Index, Target;
};

struct WitSehCallbacks {
    void *Context;
    void (*Before)(void *);
    void (*After)(void *);
    WitU64 (*Invoke)(void *, WitU64, WitU64, WitU64);
};

// Caller retains the actual stack/image lifetime throughout callback execution.
WitSehAction wit_seh_search(const WitUserImageInfo *, WitU32, WitU64, WitU64, WitU64, const WitUnwindStackRange *,
    EXCEPTION_RECORD *, CONTEXT *, WitSehDecision *, const WitSehCallbacks * = nullptr);
// Advances the cursor before a finally call. Does not transfer to a target frame.
bool wit_seh_terminate(const WitUserImageInfo *, WitU32, WitU64, WitU64, WitU64, const WitUnwindStackRange *, bool,
    WitU64, DWORD *, const WitSehCallbacks * = nullptr);
#endif
