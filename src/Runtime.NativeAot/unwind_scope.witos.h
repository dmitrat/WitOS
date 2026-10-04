#ifndef WITOS_UNWIND_SCOPE_H
#define WITOS_UNWIND_SCOPE_H
#include "witos/user_abi.h"

/* Enclose the entire walk and all uses of returned original-stack pointers.
 * Foreign references must already be suspended. No implicit stop/resume.
 * Close explicitly before nonlocal context transfer; raw exits are kernel-cleaned. */
class WitNativeUnwindScope final {
    WitU64 status;
    WitU64 token;
    WitU64 ownerId;
    WitNativeUnwindScope *previous;

public:
    explicit WitNativeUnwindScope(WitU64 reference);
    ~WitNativeUnwindScope();
    WitNativeUnwindScope(const WitNativeUnwindScope &) = delete;
    WitNativeUnwindScope &operator=(const WitNativeUnwindScope &) = delete;
    WitNativeUnwindScope(WitNativeUnwindScope &&) = delete;
    WitNativeUnwindScope &operator=(WitNativeUnwindScope &&) = delete;

    WitU64 Status() const
    {
        return status;
    }

    WitU64 Close();
    // Reacquire only the executing thread after a scoped callback returns.
    WitU64 ReopenCurrent();
    // Uses only the innermost scope. Refusal preserves every output byte.
    static WitU64 Current(WitU64 stackPointer, WitStackLeaseInfo *output);
};
#endif
