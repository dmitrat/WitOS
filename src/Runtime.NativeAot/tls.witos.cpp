#include "tls.h"

/* The MSVC compiler emits initializer pointers into .CRT$XD* and references
 * these helper symbols. No Windows CRT implementation is linked. */
typedef void(__cdecl *TlsFunction)(void);
#pragma section(".CRT$XDA", read)
#pragma section(".CRT$XDZ", read)
extern "C" {
__declspec(allocate(".CRT$XDA")) TlsFunction const wit_tls_initializers_begin = nullptr;
__declspec(allocate(".CRT$XDZ")) TlsFunction const wit_tls_initializers_end = nullptr;
[[msvc::no_tls_guard]] __declspec(thread) bool __tls_guard = false;
}
static const WitUserImageInfo *image;
static volatile WitU32 initialized;
static __declspec(thread) WitU32 phase;
static __declspec(thread) WitU32 count;
static __declspec(thread) TlsFunction destructors[WIT_NATIVE_TLS_MAX_DESTRUCTORS];
// Storage belongs to this thread's kernel-managed compiler TLS page. Identity is
// always queried from the kernel; neither FS nor GS data supplies authority.
static __declspec(thread) WitNativeThreadExitCallback exit_callback;
static __declspec(thread) void *exit_context;
static __declspec(thread) WitU64 exit_owner;
static __declspec(thread) WitNativeThreadExitCallback cleanup_callback;
static __declspec(thread) void *cleanup_context;
static __declspec(thread) WitU64 cleanup_owner;
static __declspec(thread) WitU32 exit_phase; // 0 empty, 1 registered, 2 invoking, 3 completed

static WIT_NORETURN void fatal()
{
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

static bool range(WitU64 address, WitU64 size, WitU32 required, WitU32 forbidden)
{
    return wit_native_image_range(image, address, size, required, forbidden, 1) != 0;
}

extern "C" int wit_native_tls_code_pointer(WitU64 address)
{
    return initialized == 2 && range(address, 1, WIT_IMAGE_INFO_EXECUTE, WIT_IMAGE_INFO_WRITE);
}

static bool current_thread(WitUserThreadInfo &info)
{
    return wit_native_thread_info(&info) && info.CompilerTls;
}

extern "C" WitU64 wit_native_thread_on_exit(WitNativeThreadExitCallback callback, void *context)
{
    // Check publication and kernel-confirmed TLS before the first GS access.
    WitUserThreadInfo info;
    if (initialized != 2 || !current_thread(info)) {
        return WIT_STATUS_DENIED;
    }
    if (phase >= 3 || exit_phase >= 2) {
        return WIT_STATUS_CLOSED;
    }
    if (!phase) {
        return WIT_STATUS_DENIED;
    }
    if (!wit_native_tls_code_pointer((WitU64)callback)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (exit_phase) {
        return WIT_STATUS_BUSY;
    }
    exit_owner = info.ThreadId;
    exit_context = context;
    exit_callback = callback;
    exit_phase = 1;
    return WIT_STATUS_OK;
}

extern "C" WitU64 wit_native_thread_on_cleanup(WitNativeThreadExitCallback callback, void *context)
{
    WitUserThreadInfo info;
    if (initialized != 2 || !current_thread(info)) {
        return WIT_STATUS_DENIED;
    }
    if (phase >= 3 || exit_phase >= 2) {
        return WIT_STATUS_CLOSED;
    }
    if (!phase) {
        return WIT_STATUS_DENIED;
    }
    if (!wit_native_tls_code_pointer((WitU64)callback)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (cleanup_callback) {
        return WIT_STATUS_BUSY;
    }
    cleanup_owner = info.ThreadId;
    cleanup_context = context;
    cleanup_callback = callback;
    return WIT_STATUS_OK;
}

extern "C" void wit_native_thread_notify_exit(void)
{
    WitUserThreadInfo info;
    if (initialized != 2 || !current_thread(info)) {
        fatal();
    }
    if (phase != 4 || exit_phase == 2) {
        fatal();
    }
    if (exit_phase == 3) {
        return;
    }
    const auto callback = exit_callback;
    void *context = exit_context;
    if (exit_phase && (exit_owner != info.ThreadId || !wit_native_tls_code_pointer((WitU64)callback))) {
        fatal();
    }
    const auto cleanup = cleanup_callback;
    void *cleanup_data = cleanup_context;
    if (cleanup && (cleanup_owner != info.ThreadId || !wit_native_tls_code_pointer((WitU64)cleanup))) {
        fatal();
    }
    cleanup_callback = nullptr;
    cleanup_context = nullptr;
    cleanup_owner = 0;
    exit_callback = nullptr;
    exit_context = nullptr;
    exit_phase = 2; // Pop before calling; recursion and re-registration cannot repeat cleanup.
    if (callback) {
        callback(context); // No shared gate: callbacks may yield or park.
    }
    if (cleanup) {
        cleanup(cleanup_data); // Apartment/platform state outlives real runtime detach.
    }
    exit_phase = 3;
}

extern "C" void wit_native_tls_initialize(const WitUserStartup *startup)
{
    // Exactly once, on the startup thread, before publishing worker threads.
    if (!wit_native_claim_startup(&initialized) ||
        !startup ||
        startup->Version != WIT_ABI_VERSION ||
        startup->Size != sizeof(*startup) ||
        !startup->ImageInfo) {
        fatal();
    }
    // Native startup may publish the same checked image before TLS, for example
    // to configure immutable environment data needed by TLS constructors.
    if (!wit_native_process_image()) {
        wit_native_process_image_initialize(startup);
    } else if ((const WitUserImageInfo *)startup->ImageInfo != wit_native_process_image()) {
        fatal();
    }
    image = wit_native_process_image();
    const WitU64 first = (WitU64)&wit_tls_initializers_begin;
    const WitU64 last = (WitU64)&wit_tls_initializers_end;
    if (last <= first ||
        (last - first) % sizeof(TlsFunction) ||
        (last - first) / sizeof(TlsFunction) - 1 > WIT_NATIVE_TLS_MAX_INITIALIZERS ||
        !range(first, last - first + sizeof(TlsFunction), WIT_IMAGE_INFO_READ,
            WIT_IMAGE_INFO_WRITE | WIT_IMAGE_INFO_EXECUTE) ||
        wit_tls_initializers_begin ||
        wit_tls_initializers_end) {
        fatal();
    }
    for (WitU64 p = first + sizeof(TlsFunction); p < last; p += sizeof(TlsFunction)) {
        const auto callback = *(TlsFunction const *)p;
        if (callback && !range((WitU64)callback, 1, WIT_IMAGE_INFO_EXECUTE, WIT_IMAGE_INFO_WRITE)) {
            fatal();
        }
    }
    initialized = 2;
    wit_native_tls_enter();
}

extern "C" void wit_native_tls_enter(void)
{
    if (initialized != 2 || phase >= 3) {
        fatal();
    }
    if (phase) {
        return; // Includes constructor recursion: do not run twice.
    }
    phase = 1;
    __tls_guard = true; // Compiler-guarded cross-TU access is legal in constructors.
    const WitU64 last = (WitU64)&wit_tls_initializers_end;
    for (WitU64 p = (WitU64)&wit_tls_initializers_begin + sizeof(TlsFunction); p < last; p += sizeof(TlsFunction)) {
        const auto callback = *(TlsFunction const *)p;
        if (callback) {
            callback();
        }
    }
    phase = 2;
}

extern "C" int __cdecl __tlregdtor(TlsFunction callback)
{
    // MSVC-generated callers ignore registration failures. Exhaustion or bad
    // callbacks therefore fail the component instead of silently losing cleanup.
    if (initialized != 2 ||
        !phase ||
        phase == 4 ||
        count == WIT_NATIVE_TLS_MAX_DESTRUCTORS ||
        !wit_native_tls_code_pointer((WitU64)callback)) {
        fatal();
    }
    destructors[count++] = callback;
    return 0;
}

extern "C" void wit_native_tls_leave(void)
{
    if (phase == 4) {
        return;
    }
    if (initialized != 2 || phase == 1 || phase == 3) {
        fatal();
    }
    phase = 3;
    WitU32 invoked = 0;
    while (count) {
        if (++invoked > 2 * WIT_NATIVE_TLS_MAX_DESTRUCTORS) {
            fatal();
        }
        const auto callback = destructors[--count];
        destructors[count] = nullptr;
        if (!wit_native_tls_code_pointer((WitU64)callback)) {
            fatal();
        }
        callback(); // Pop before calling; callbacks may register bounded new work.
    }
    phase = 4;
}

extern "C" void __stdcall __dyn_tls_init(void *, unsigned long reason, void *) noexcept
{
    if (reason == 2) {
        wit_native_tls_enter();
    }
}

extern "C" void __cdecl __dyn_tls_on_demand_init() noexcept
{
    wit_native_tls_enter();
}

extern "C" void __stdcall __dyn_tls_dtor(void *, unsigned long reason, void *) noexcept
{
    if (reason == 0 || reason == 3) {
        wit_native_tls_leave();
    }
}

typedef void(__stdcall *TlsNotification)(void *, unsigned long, void *);
extern "C" const TlsNotification __dyn_tls_init_callback = __dyn_tls_init;
extern "C" const TlsNotification __dyn_tls_dtor_callback = __dyn_tls_dtor;
