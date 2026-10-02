#include "pal.witos.h"
#include "diagnostics.h"
#include <stdlib.h>

/* Single bounded write: no heap, compiler TLS, CRT strlen, locks or cleanup.
 * A bad caller pointer can fault during the scan; no output precedes it.
 * The kernel validates the complete byte range again before emitting it. */
static bool try_print(const char *message)
{
    const WitU64 console = wit_native_process_console();
    if (!console || !message) {
        return false;
    }
    const volatile char *bytes = message;
    WitU64 length = 0;
    while (length <= WIT_ABI_MAX_WRITE && bytes[length]) {
        ++length;
    }
    if (length > WIT_ABI_MAX_WRITE) {
        return false;
    }
    WitU64 written = 0;
    return wit_native_call(WIT_CALL_WRITE, console, (uintptr_t)message, length, &written) == WIT_STATUS_OK &&
        written == length;
}

void PalPrintFatalError(const char *message)
{
    if (!try_print(message)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
}

extern "C" int __cdecl _purecall(void)
{
    (void)try_print("[NATIVE-FATAL] pure virtual call\n");
    wit_native_fail_fast(WIT_NATIVE_PURECALL_EXIT);
}

extern "C" __declspec(noreturn) void __cdecl __report_rangecheckfailure(void)
{
    (void)try_print("[NATIVE-FATAL] compiler range check\n");
    wit_native_fail_fast(WIT_NATIVE_RANGECHECK_EXIT);
}
