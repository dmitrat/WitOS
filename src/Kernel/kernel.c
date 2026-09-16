#include "witos/boot.h"
#include "witos/platform.h"
#include "build_info.h"

WIT_NORETURN void wit_panic(const char *reason)
{
    wit_console_write("[PANIC] ");
    wit_console_write(reason);
    wit_console_write("\n");
    wit_platform_finish(0x11);
}

WIT_NORETURN void wit_kernel_entry(const WitBootInfo *boot)
{
    WitU64 usable = 0;

    wit_console_write("WitOS 0.0.1 (M0)\n");
    wit_console_write("Build: " WITOS_BUILD_ID " | x64 | Debug\n");
    wit_console_write("[TEST-BEGIN] Boot.Contract\n");

    if (boot == 0 || boot->Magic != WIT_BOOT_MAGIC ||
        boot->Version != WIT_BOOT_VERSION || boot->Size != sizeof(WitBootInfo) ||
        boot->Architecture != WIT_ARCH_X64 ||
        (boot->Flags & WIT_BOOT_SERVICES_EXITED) == 0 ||
        boot->MemoryRegions == 0 || boot->MemoryRegionCount == 0 ||
        boot->MemoryRegionCount > WIT_MAX_MEMORY_REGIONS) {
        wit_panic("Invalid WitBootInfo");
    }

    for (WitU32 i = 0; i < boot->MemoryRegionCount; ++i) {
        const WitMemoryRegion *region = &boot->MemoryRegions[i];
        if (region->Length == 0 || region->Base > ~0ULL - region->Length ||
            (region->Base & 4095ULL) != 0 || (region->Length & 4095ULL) != 0 ||
            region->Kind > WIT_MEMORY_USABLE || region->Reserved != 0) {
            wit_panic("Invalid memory region");
        }
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

    wit_console_write("[TEST-PASS] Boot.Contract\n");
    wit_console_write("CPU: x86_64\nUsable memory: ");
    wit_console_write_u64(usable / (1024ULL * 1024ULL));
    wit_console_write(" MiB\nMemory regions: ");
    wit_console_write_u64(boot->MemoryRegionCount);
    wit_console_write("\nKernel initialized.\nHello from WitOS.\n");
    wit_console_write("[TEST-PASS] Boot.Hello\n");

#ifdef WITOS_TEST_HANG
    /* The test runner must classify a guest that never exits as a timeout. */
    for (;;) { }
#else
    wit_platform_finish(0x10);
#endif
}
