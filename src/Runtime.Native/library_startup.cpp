/* The startup of a WitOS C++ library (P6.4.j3c), as vcruntime's DLL startup is for an MSVC DLL. A library links its own
 * copy of the C++ runtime, the C runtime and the STL, as an MSVC DLL links the static CRT, so each module has its own
 * GS cookie, static initializers, processor level, stream options and atexit table. The process's state is not the
 * module's: the image and the console come from the immutable startup descriptor every module can read, and the
 * environment and the current directory from the kernel.
 *
 * The entry point wit_library_cxx_entry wraps the dynamic TLS entry wit_library_dll_entry (library_dynamic_tls.cpp),
 * which initializes the loading thread's thread_local objects and calls DllMain. At the process attach it prepares the
 * module first, in vcruntime's order: the GS cookies, the process's context, the STL's processor level and the C
 * runtime's stream options, then the module's C and C++ initializers; a failing C initializer fails the load without
 * DllMain, after the callbacks registered so far ran. At the process detach DllMain runs first, then the module's
 * atexit callbacks, newest first, each popped before it is called; a callback may register another, within a bound.
 * Thread attach and detach go to the dynamic TLS entry alone. */
#include "native_security.h"
#include "witos/user_layout.h"

extern "C" int __stdcall wit_library_dll_entry(void *module, unsigned long reason, void *reserved);
extern "C" bool wit_library_owns_code(const void *address);
extern "C" void wit_cxx_initialize_isa(void);
extern "C" void wit_crt_initialize_stdio_options(void);
extern "C" int wit_cxx_run_initializers(void);

namespace {
using ExitCallback = void(__cdecl *)(void);

constexpr unsigned long ProcessDetach = 0;
constexpr unsigned long ProcessAttach = 1;

ExitCallback callbacks[WIT_NATIVE_EXIT_MAX_CALLBACKS];
volatile WitU32 gate;
WitU32 count, phase; // 0 before the attach, 1 accepting, 2 running the callbacks, 3 done
WitU64 owner; // the thread that runs the callbacks

[[noreturn]] void fatal()
{
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

WitU64 thread()
{
    WitUserThreadInfo info;
    if (!wit_native_thread_info(&info)) {
        fatal();
    }
    return info.ThreadId;
}

// The module's atexit callbacks, newest first; only the running thread may add one while they run.
void run_exit()
{
    const WitU64 self = thread();
    wit_native_lock(&gate);
    owner = self;
    phase = 2;
    for (WitU32 invoked = 0; count; ++invoked) {
        if (invoked == 2 * WIT_NATIVE_EXIT_MAX_CALLBACKS) {
            wit_native_unlock(&gate);
            fatal(); // unbounded re-registration
        }
        const ExitCallback callback = callbacks[--count];
        callbacks[count] = nullptr;
        wit_native_unlock(&gate);
        callback();
        wit_native_lock(&gate);
    }
    phase = 3;
    wit_native_unlock(&gate);
}
} // namespace

/* atexit in a library registers with the module, as the static CRT's does in a DLL: the callback runs when the
 * library detaches. It must be the module's own code; a full table or a module that already detached refuses it. */
extern "C" int __cdecl atexit(ExitCallback callback)
{
    if (!callback || !wit_library_owns_code((const void *)callback)) {
        return -1;
    }
    const WitU64 self = thread();
    wit_native_lock(&gate);
    if (!phase || phase == 3 || (phase == 2 && owner != self) || count == WIT_NATIVE_EXIT_MAX_CALLBACKS) {
        wit_native_unlock(&gate);
        return -1;
    }
    callbacks[count++] = callback;
    wit_native_unlock(&gate);
    return 0;
}

/* Threads a library starts: a thread's lifecycle (the main image's compiler TLS, the runtime's thread notifications)
 * belongs to the process entry, which a library cannot reach yet, so a library starts no thread. No entry point is
 * accepted, which makes thread creation fail (CreateThread with ERROR_INVALID_ADDRESS), and the start and exit paths
 * that would follow are unreachable. */
extern "C" int wit_native_tls_code_pointer(WitU64)
{
    return 0;
}

extern "C" void wit_native_tls_enter(void)
{
    fatal();
}

extern "C" void wit_native_tls_leave(void)
{
    fatal();
}

extern "C" void wit_native_thread_notify_exit(void)
{
    fatal();
}

extern "C" int __stdcall wit_library_cxx_entry(void *module, unsigned long reason, void *reserved)
{
    if (reason == ProcessAttach) {
        if (phase) {
            fatal(); // one attach per load
        }
        wit_native_security_initialize_system();
        wit_native_process_image_initialize((const WitUserStartup *)WIT_USER_INFO);
        wit_cxx_initialize_isa();
        wit_crt_initialize_stdio_options();
        wit_native_lock(&gate);
        phase = 1;
        wit_native_unlock(&gate);
        if (wit_cxx_run_initializers()) {
            run_exit();
            return 0;
        }
        return wit_library_dll_entry(module, reason, reserved);
    }
    if (reason == ProcessDetach) {
        const int result = wit_library_dll_entry(module, reason, reserved);
        run_exit();
        return result;
    }
    return wit_library_dll_entry(module, reason, reserved);
}
