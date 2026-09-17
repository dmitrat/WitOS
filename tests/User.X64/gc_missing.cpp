#include "gcenv.witos.h"
/* Reset semantics are still unported; time support must not hide this boundary. */
extern "C" WitU64 wit_native_main(const WitUserStartup*)
{
    return GCToOSInterface::VirtualReset(nullptr, 0, false) ? 0 : 1;
}
