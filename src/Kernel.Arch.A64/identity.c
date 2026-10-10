#include "witos/arch.h"
#include "witos/boot.h"

static const WitArchIdentity identity = {WIT_ARCH_ARM64, 0xAA64, 0, "arm64", "aarch64"};

const WitArchIdentity *wit_arch_identity(void)
{
    return &identity;
}
