#include "witos/boot.h"
#include "witos/devices.h"
#include "witos/storage.h"
#include "witos/memory.h"
#include "witos/random.h"
#include "witos/virtual.h"
#include "witos/arch.h"
#include "witos/platform.h"
#include "witos/user_abi.h"
#include "build_info.h"

static WitPageAllocator physical_pages;
#if defined(WITOS_SELFTEST)
void wit_kernel_self_test(const WitBootInfo *boot, WitPageAllocator *pages);
#endif

WIT_NORETURN void wit_panic(const char *reason)
{
    wit_console_write("[PANIC] ");
    wit_console_write(reason);
    wit_console_write("\n");
    wit_platform_finish(0x11);
}

static WIT_NORETURN void finish(void)
{
    wit_console_write("Kernel initialized.\nHello from WitOS.\n");
    wit_console_write("[TEST-PASS] Boot.Hello\n");

#ifdef WITOS_TEST_HANG
    for (;;) {
    }
#else
    wit_platform_finish(0x10);
#endif
}

/* The handoff must match this kernel's boot contract and describe a valid memory map; returns usable bytes. */
static WitU64 validate_contract(const WitBootInfo *boot, const WitArchIdentity *arch)
{
    WitU64 usable = 0;
    if (boot == 0 ||
        boot->Magic != WIT_BOOT_MAGIC ||
        boot->Version != WIT_BOOT_VERSION ||
        boot->Size != sizeof(WitBootInfo) ||
        boot->Architecture != arch->BootArchitecture ||
        (boot->Flags & WIT_BOOT_SERVICES_EXITED) == 0 ||
        boot->MemoryRegions == 0 ||
        boot->MemoryRegionCount == 0 ||
        boot->MemoryRegionCount > WIT_MAX_MEMORY_REGIONS) {
        wit_panic("Invalid WitBootInfo");
    }
    if (!wit_memory_map_valid(boot->MemoryRegions, boot->MemoryRegionCount)) {
        wit_panic("Invalid memory map");
    }
    for (WitU32 i = 0; i < boot->MemoryRegionCount; ++i) {
        const WitMemoryRegion *region = &boot->MemoryRegions[i];
        if (region->Kind == WIT_MEMORY_USABLE) {
            if (usable > ~0ULL - region->Length) {
                wit_panic("Memory size overflow");
            }
            usable += region->Length;
        }
    }
    if (usable == 0) {
        wit_panic("No usable memory");
    }
    return usable;
}

/* The loader's seed lives in a writable, non-executable kernel section; the generator consumes and erases it. */
static void consume_entropy(const WitBootInfo *boot)
{
    const WitU64 address = (WitU64)boot->EntropySeed;
    int owned = 0;
    if (boot->EntropySize != WIT_RANDOM_KEY_BYTES || boot->EntropyReserved || !address) {
        wit_panic("Invalid boot entropy");
    }
    for (WitU32 i = 0; i < boot->ImageSectionCount; ++i) {
        const WitImageSection *section = &boot->ImageSections[i];
        if ((section->Flags & WIT_IMAGE_WRITE) &&
            !(section->Flags & WIT_IMAGE_EXECUTE) &&
            address >= section->Base &&
            address - section->Base <= section->Length &&
            WIT_RANDOM_KEY_BYTES <= section->Length - (address - section->Base)) {
            owned = 1;
        }
    }
    if (!owned || !wit_random_initialize(boot->EntropySeed)) {
        wit_panic("Invalid boot entropy");
    }
    for (WitU32 i = 0; i < WIT_RANDOM_KEY_BYTES; ++i) {
        if (boot->EntropySeed[i]) {
            wit_panic("Boot seed not erased");
        }
    }
    wit_console_write("[TEST-PASS] Random.BootSeedConsumed\n");
#if defined(WITOS_SELFTEST)
    wit_random_self_test();
#endif
}

WIT_NORETURN void wit_kernel_entry(const WitBootInfo *boot)
{
    const WitArchIdentity *arch = wit_arch_identity();
    WitU64 usable = 0;

    /* Versions come from the ABI headers; the host runner checks this line. */
    wit_console_write("WitOS user ABI v");
    wit_console_write_u64(WIT_ABI_VERSION);
    wit_console_write(", boot ABI v");
    wit_console_write_u64(WIT_BOOT_VERSION);
    wit_console_write("\n");
    wit_console_write("Build: " WITOS_BUILD_ID " | ");
    wit_console_write(arch->Name);
    wit_console_write(" | Debug\n");
    wit_console_write("[TEST-BEGIN] Boot.Contract\n");
    usable = validate_contract(boot, arch);
    wit_console_write("[TEST-PASS] Boot.Contract\n");
    wit_arch_initialize();
    wit_console_write("CPU: ");
    wit_console_write(arch->Processor);
    wit_console_write("\nUsable memory: ");
    wit_console_write_u64(usable / (1024ULL * 1024ULL));
    wit_console_write(" MiB\nMemory regions: ");
    wit_console_write_u64(boot->MemoryRegionCount);
    wit_console_write("\n");

    if (!wit_pages_initialize(&physical_pages, boot->MemoryRegions, boot->MemoryRegionCount)) {
        wit_panic("Physical allocator initialization failed (limit: 4 GiB)");
    }
    wit_console_write("Free physical pages: ");
    wit_console_write_u64(wit_pages_free_count(&physical_pages));
    wit_console_write("\n");
    wit_virtual_initialize(boot, &physical_pages);
    if (!wit_storage_initialize(boot)) {
        wit_panic("Invalid readonly boot package");
    }
    consume_entropy(boot);
    wit_platform_clock_initialize(boot);
    wit_devices_initialize(boot, &physical_pages);
#if defined(WITOS_SELFTEST)
    wit_kernel_self_test(boot, &physical_pages);
#endif
    finish();
}
