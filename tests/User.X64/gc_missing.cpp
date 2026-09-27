#include "gcenv.witos.h"
/* Write-watch reset remains unported; CPU discovery must not hide it. */
extern "C" WitU64 wit_native_main(const WitUserStartup*)
{
    GCToOSInterface::ResetWriteWatch(nullptr, 0);
    return 0;
}
