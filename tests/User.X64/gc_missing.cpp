#include "gcenv.witos.h"
/* Process-wide GC rendezvous remains unported; reset must not hide it. */
extern "C" WitU64 wit_native_main(const WitUserStartup*)
{
    GCToOSInterface::FlushProcessWriteBuffers();
    return 0;
}
