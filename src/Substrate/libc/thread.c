#include "witos_libc.h"
#include <errno.h>

/* The thread pointer (plan step S1.1). musl sets it once for the main thread (__init_tp) and once per new thread;
 * on x64 it is the FS base, which the kernel sets through THREAD_SET_TLS from the thread's next return to user
 * mode, the return of this very call. On ARM64 musl's own src/thread/aarch64/__set_thread_area.s writes TPIDR_EL0,
 * which the kernel preserves, so nothing of WitOS is needed there. */
#if defined(__x86_64__)
__attribute__((__visibility__("hidden"))) int __set_thread_area(void *p)
{
    WitU64 result = 0;
    const WitU64 status = wit_syscall(WIT_CALL_THREAD_SET_TLS, (WitU64)p, 0, 0, &result);
    return status == WIT_STATUS_OK ? 0 : (int)__wit_errno(status);
}
#endif
