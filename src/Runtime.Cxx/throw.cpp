#include "cxx_runtime.h"

/* Throwing and per-thread exception state of the C++ runtime (P6.4.e). */
extern "C" const unsigned char __ImageBase;

namespace WitCxx {

namespace {
__declspec(thread) ThreadState state;
}

ThreadState &Thread()
{
    return state;
}

void Raise(const ULONG_PTR *arguments)
{
    RaiseException(CXX_EXCEPTION, EXCEPTION_NONCONTINUABLE, 4, arguments);
    Fatal();
}

void Unwind(u64 frame, u64 ip, EXCEPTION_RECORD *record, CONTEXT *context, PUNWIND_HISTORY_TABLE history)
{
    RtlUnwindEx((void *)frame, (void *)ip, record, nullptr, context, history);
}

const ActiveCatch *CurrentCatch()
{
    for (u32 i = state.CatchCount; i > 0; --i) {
        if (!state.Catches[i - 1].Leaving) {
            return &state.Catches[i - 1];
        }
    }
    return nullptr;
}

void ForgetLeft(void *object)
{
    u32 kept = 0;
    for (u32 i = 0; i < state.CatchCount; ++i) {
        if (!state.Catches[i].Leaving || state.Catches[i].Leaving != object) {
            state.Catches[kept++] = state.Catches[i];
        }
    }
    state.CatchCount = kept;
}

void Destroy(void *object, const ThrowInfo *info, u64 imageBase)
{
    for (u32 i = 0; i < state.CatchCount; ++i) {
        if (!state.Catches[i].Leaving && state.Catches[i].Object == object) {
            return; // A catch that caught the rethrown object inside this one still uses it.
        }
    }
    if (object && info->Destroy) {
        ((void(__cdecl *)(void *))(imageBase + (u64)(u32)info->Destroy))(object);
    }
}

} // namespace WitCxx

using namespace WitCxx;

/* throw with an object, or the rethrow of the current exception when both are null. An exception counts as
 * uncaught from here until its catch handler becomes active. */
extern "C" __declspec(noreturn) void __stdcall _CxxThrowException(void *object, _ThrowInfo *raised) noexcept(false)
{
    const ThrowInfo *info = (const ThrowInfo *)raised; // the compiler's own declaration names an opaque type
    u64 base;
    if (!object && !info) {
        const ActiveCatch *current = CurrentCatch();
        if (!current) {
            Fatal(); // No current exception: std::terminate.
        }
        object = current->Object;
        info = current->Info;
        base = current->ImageBase;
    } else {
        if (!info) {
            Fatal();
        }
        base = (u64)&__ImageBase; // Linked into each module, the runtime throws the ThrowInfo of its own module.
    }
    ForgetLeft(object); // Records an earlier exception at this address left behind.
    ++state.Uncaught;
    const ULONG_PTR arguments[4] = {
        (info->Attributes & TI_PURE) ? PURE_MAGIC : MAGIC1, (ULONG_PTR)object, (ULONG_PTR)info, base};
    Raise(arguments);
}

/* std::terminate for the compiler, as when an exception leaves a noexcept function; no terminate handler yet. */
extern "C" __declspec(noreturn) void __cdecl __std_terminate()
{
    Fatal();
}

extern "C" int __cdecl __uncaught_exceptions()
{
    return state.Uncaught;
}
