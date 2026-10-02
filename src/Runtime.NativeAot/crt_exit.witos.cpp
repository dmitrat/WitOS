#include "native_process.h"
#include "library_lifecycle.h"
#include <stdlib.h>

// Define the actual CRT signature, without linking a Windows CRT implementation.
// State and callbacks are component-private; no compiler TLS stores authority.
using ExitCallback = void (__cdecl *)(void);
static ExitCallback callbacks[WIT_NATIVE_EXIT_MAX_CALLBACKS];
static volatile WitU32 gate;
static WitU32 count, phase; // 0 accepting, 1 TLS cleanup, 2 callbacks, 3 thread notification, 4 stopped
static WitU64 owner;
static WIT_NORETURN void fatal() { wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT); }
static WitUserThreadInfo current()
{
    WitUserThreadInfo info;
    if (wit_native_call(WIT_CALL_THREAD_QUERY, (WitU64)&info, sizeof(info), WIT_THREAD_INFO_VERSION, nullptr) != WIT_STATUS_OK ||
        info.Version != WIT_THREAD_INFO_VERSION || info.Size != sizeof(info) || !info.ThreadId) fatal();
    return info;
}
static void lock()
{
    while (!wit_native_try_lock(&gate))
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) fatal();
}
static bool code(ExitCallback callback)
{
    return wit_native_image_range(wit_native_process_image(), (WitU64)callback, 1,
        WIT_IMAGE_INFO_EXECUTE, WIT_IMAGE_INFO_WRITE, 1) != 0;
}
extern "C" int __cdecl atexit(ExitCallback callback)
{
    if (!code(callback)) return -1;
    const auto info = current();
    lock();
    if (phase >= 3 || (phase && owner != info.ThreadId) || count == WIT_NATIVE_EXIT_MAX_CALLBACKS) {
        wit_native_unlock(&gate);
        return -1;
    }
    callbacks[count++] = callback;
    wit_native_unlock(&gate);
    return 0;
}
extern "C" void wit_native_process_shutdown(void)
{
    const auto info = current();
    if (!info.CompilerTls) fatal();
    lock();
    if (phase == 4 && owner == info.ThreadId) { wit_native_unlock(&gate); return; }
    if (phase) { wit_native_unlock(&gate); fatal(); }
    owner = info.ThreadId;
    phase = 1;
    wit_native_unlock(&gate);
    // Native C++ TLS destructors precede CRT exit callbacks. They may register
    // process cleanup on this thread. No ThreadStore detach is synthesized here.
    wit_native_tls_leave();
    lock();
    phase = 2;
    WitU32 invoked = 0;
    for (;;) {
        if (!count) {
            phase = 3;
            wit_native_unlock(&gate);
            // Runtime process-exit callbacks must publish shutdown before the
            // thread notification reaches a future real RuntimeThreadShutdown.
            wit_native_thread_notify_exit();
            // Keep DLL code alive through runtime detach/cleanup notifications.
            if(wit_native_library_shutdown()!=WIT_STATUS_OK)fatal();
            lock();
            phase = 4;
            wit_native_unlock(&gate);
            return;
        }
        if (++invoked > 2 * WIT_NATIVE_EXIT_MAX_CALLBACKS) { wit_native_unlock(&gate); fatal(); }
        const auto callback = callbacks[--count];
        callbacks[count] = nullptr;
        wit_native_unlock(&gate);
        if (!code(callback)) fatal();
        callback(); // Pop first; never hold the gate across user code or waits.
        lock();
    }
}
extern "C" WIT_NORETURN void wit_native_process_exit(WitU64 code)
{
    wit_native_process_shutdown();
    (void)wit_native_call(WIT_CALL_EXIT, code, 0, 0, nullptr);
    fatal();
}
