#include "witos/arch.h"
#include "witos/boot.h"

/* Host harnesses link the kernel PE parser and validate images as the x64 kernel does: its machine, with unwind
 * metadata validation. */
const WitArchIdentity *wit_arch_identity(void)
{
    static const WitArchIdentity identity = {WIT_ARCH_X64, 0x8664, WIT_ARCH_PE_UNWIND, "x64", "x86_64"};
    return &identity;
}
