#include "cxx_runtime.h"
extern "C" {
#include "bootstrap.h"
}

/* The C++ runtime's platform function in the guest (P6.4.f): the native component's fail-fast. */
namespace WitCxx {

void Fatal()
{
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

} // namespace WitCxx
