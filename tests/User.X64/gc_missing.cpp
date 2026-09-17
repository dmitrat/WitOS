#include "gcenv.witos.h"
/* Deliberate negative link root: PIT ticks do not implement the GC performance clock. */
extern "C" WitU64 wit_native_main(const WitUserStartup*)
{
    return (WitU64)GCToOSInterface::QueryPerformanceFrequency();
}
