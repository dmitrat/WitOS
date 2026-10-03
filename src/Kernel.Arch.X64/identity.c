#include "witos/arch.h"
#include "witos/boot.h"

static const WitArchIdentity identity = {WIT_ARCH_X64, 0x8664, WIT_ARCH_PE_UNWIND, "x64", "x86_64"};

const WitArchIdentity *wit_arch_identity(void)
{
    return &identity;
}
