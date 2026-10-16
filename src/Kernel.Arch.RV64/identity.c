#include "witos/arch.h"
#include "witos/boot.h"

/* The PE machine of a RISC-V 64 UEFI image is 0x5064; the firmware loads the PE32+ the tool converts the kernel's ELF
 * into (plan step V1.1). */
static const WitArchIdentity identity = {WIT_ARCH_RISCV64, 0x5064, 0, "riscv64", "riscv64"};

const WitArchIdentity *wit_arch_identity(void)
{
    return &identity;
}
