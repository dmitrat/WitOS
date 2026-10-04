#include "cxx_runtime.h"
extern "C" {
#include "bootstrap.h"
}

/* The C++ runtime's platform functions in the guest (P6.4.f, P6.4.g): the native component's fail-fast and the
 * statics lock, which yields while another thread initializes a static. The guest's native heap
 * (Runtime.NativeAot/native_new.witos.cpp) is the allocator. */
namespace WitCxx {

namespace {
volatile WitU32 statics;
} // namespace

void Fatal()
{
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

void StaticsLock()
{
    wit_native_lock(&statics);
}

void StaticsUnlock()
{
    wit_native_unlock(&statics);
}

void StaticsWait()
{
    wit_native_unlock(&statics);
    if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, 0) != WIT_STATUS_OK) {
        Fatal();
    }
    wit_native_lock(&statics);
}

void StaticsNotify() {}

} // namespace WitCxx
