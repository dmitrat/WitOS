#include "cxx_runtime.h"

/* The C++ runtime's platform function on Windows, for the hosted reference build (P6.4.e). */
namespace WitCxx {

void Fatal()
{
    __fastfail(FAST_FAIL_FATAL_APP_EXIT);
}

} // namespace WitCxx
