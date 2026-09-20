#include <minipal/mutex.h>
#if defined(_DEBUG) || defined(DACCESS_COMPILE)
#error The initial WitOS Crst adapter supports the native Release runtime only.
#endif
#include "Crst.h"
extern "C" {
#include "bootstrap.h"
}

/* Upstream Crst::Init is void and its inline InitNoThrow always returns true.
 * Do not conceal a bounded-registry failure behind that successful return. */
void CrstStatic::Init(CrstType type, CrstFlags flags)
{
    (void)type; (void)flags; // The pinned Release implementation also ignores these.
    if (!minipal_mutex_init(&m_Lock)) wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}
void CrstStatic::Destroy() { minipal_mutex_destroy(&m_Lock); }
void CrstStatic::Enter(CrstStatic* lock) { minipal_mutex_enter(&lock->m_Lock); }
void CrstStatic::Leave(CrstStatic* lock) { minipal_mutex_leave(&lock->m_Lock); }
