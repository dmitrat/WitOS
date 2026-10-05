#include "crt.h"

/* Threads (P6.4.i2): _beginthreadex runs the procedure on a platform thread and _endthreadex ends the calling thread,
 * as UCRT does. The procedure's signature is the thread start routine's on x64, so no per-thread block of the C
 * runtime wraps it: the subset's per-thread state, errno, is thread-local storage. A failure maps the platform's error
 * to errno; a missing procedure is an invalid parameter. */
namespace WitCrt {

uintptr_t Beginthreadex(
    void *security, unsigned stack, _beginthreadex_proc_type start, void *argument, unsigned flags, unsigned *id)
{
    if (!start) {
        InvalidParameter();
    }
    return reinterpret_cast<uintptr_t>(Platform::CreateThread(security, stack, start, argument, flags, id));
}

void Endthreadex(unsigned code)
{
    Platform::ExitThread(code);
}

} // namespace WitCrt

#ifndef WITCRT_REFERENCE
extern "C" uintptr_t __cdecl _beginthreadex(
    void *security, unsigned stack, _beginthreadex_proc_type start, void *argument, unsigned flags, unsigned *id)
{
    return WitCrt::Beginthreadex(security, stack, start, argument, flags, id);
}

extern "C" void __cdecl _endthreadex(unsigned code)
{
    WitCrt::Endthreadex(code);
}
#endif
