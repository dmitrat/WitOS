#include "uefi.h"
#include "witos/boot.h"
#include "witos/platform.h"
static WitU8 seed[WIT_BOOT_SEED_BYTES];

void wit_boot_entropy(EfiBootServicesPrefix *services, WitBootInfo *boot)
{
    static const EfiGuid guid = {0x3152bca5, 0xeade, 0x433d, {0x86, 0x2e, 0xc0, 0x1c, 0xdc, 0x29, 0x1f, 0x44}};
    EfiRngProtocol *rng = 0;
    if (!services->LocateProtocol ||
        services->LocateProtocol(&guid, 0, (void **)&rng) != EFI_SUCCESS ||
        !rng ||
        !rng->GetRng) {
        wit_panic("UEFI RNG unavailable");
    }
    if (rng->GetRng(rng, 0, sizeof(seed), seed) != EFI_SUCCESS) {
        wit_panic("UEFI RNG failed");
    }
    boot->EntropySeed = seed;
    boot->EntropySize = sizeof(seed);
    boot->EntropyReserved = 0;
    wit_console_write("[BOOT] Cryptographic seed acquired\n");
}
