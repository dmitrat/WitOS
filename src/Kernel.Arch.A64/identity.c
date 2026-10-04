#include "witos/arch.h"
#include "witos/boot.h"

/* No unwind validator decodes ARM64 .pdata before A3, so images with an exception directory are refused. */
static const WitArchIdentity identity = {WIT_ARCH_ARM64, 0xAA64, 0, "arm64", "aarch64"};

const WitArchIdentity *wit_arch_identity(void)
{
    return &identity;
}
