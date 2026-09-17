#include "gcenv.witos.h"
/* Deliberate negative link root: the memory slice must not claim GC startup. */
extern "C" WitU64 wit_native_main(const WitUserStartup*)
{
    return GCToOSInterface::Initialize() ? 0 : 1;
}
