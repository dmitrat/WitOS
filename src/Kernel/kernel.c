#include "witos/boot.h"
#include "witos/memory.h"
#include "witos/virtual.h"
#include "witos/platform.h"
#include "build_info.h"

static WitPageAllocator physical_pages;
void wit_user_self_test(WitPageAllocator *pages);

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

    wit_console_write("WitOS 0.0.40 (compiler stack probes)\n");
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

    wit_console_write("[TEST-PASS] Boot.Contract\n");
    wit_platform_initialize();
    wit_console_write("CPU: x86_64\nUsable memory: ");
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
    wit_platform_clock_initialize(boot);
    wit_memory_self_test(boot, &physical_pages);
    wit_virtual_self_test(&physical_pages);
    wit_virtual_fault_test();
    wit_platform_fault_test();
    wit_scheduler_self_test();
    wit_user_self_test(&physical_pages);

    wit_console_write("Kernel initialized.\nHello from WitOS.\n");
    wit_console_write("[TEST-PASS] Boot.Hello\n");

#ifdef WITOS_TEST_HANG
    for (;;) { }
#else
    wit_platform_finish(0x10);
#endif
}
