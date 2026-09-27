#include "gcenv.witos.h"
/* CPU cache discovery remains unported; memory barriers must not hide it. */
extern "C" WitU64 wit_native_main(const WitUserStartup*)
{
    return GCToOSInterface::GetCacheSizePerLogicalCpu(true);
}
