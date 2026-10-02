#include "witos/types.h"
extern "C" WitU64 wit_unwind_test_gs(volatile char *);

extern "C" __declspec(noinline) WitU64 wit_unwind_protected_frame()
{
    volatile char buffer[128];
    for (unsigned i = 0; i < 128; ++i) {
        buffer[i] = (char)i;
    }
    buffer[0] = 7;
    const WitU64 result = wit_unwind_test_gs(buffer);
    return buffer[0] == 7 ? result : 4001;
}
