#include "pal.witos.h"
#include "tls.h"

struct BackgroundStart {
    BackgroundCallback Callback;
    void* Context;
};
static BackgroundStart starts[4];
static volatile WitU32 gate;
static void lock()
{
    while (!wit_native_try_lock(&gate))
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK)
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}
static WitU64 run(WitU64 slot)
{
    if (slot >= 4) wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    lock();
    const auto callback = starts[slot].Callback;
    void* context = starts[slot].Context;
    starts[slot].Callback = nullptr;
    wit_native_unlock(&gate);
    if (!wit_native_tls_code_pointer((WitU64)callback)) wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    return callback(context);
}
static bool start(BackgroundCallback callback, void* context)
{
    if (!wit_native_tls_code_pointer((WitU64)callback)) return false;
    lock();
    size_t slot = 0;
    while (slot < 4 && starts[slot].Callback) ++slot;
    if (slot == 4) { wit_native_unlock(&gate); return false; }
    starts[slot].Context = context;
    starts[slot].Callback = callback;
    // Creation cannot park. Serialize publication/rollback against child copy-out.
    const WitU64 status = wit_native_thread_create_detached(run, slot);
    if (status != WIT_STATUS_OK) starts[slot].Callback = nullptr;
    wit_native_unlock(&gate);
    return status == WIT_STATUS_OK;
}
bool PalStartBackgroundGCThread(BackgroundCallback callback, void* context) { return start(callback, context); }
bool PalStartFinalizerThread(BackgroundCallback callback, void* context)
{
    // This single-CPU prototype has one scheduling class; no priority boost is claimed.
    return start(callback, context);
}
bool PalStartEventPipeHelperThread(BackgroundCallback callback, void* context) { return start(callback, context); }
