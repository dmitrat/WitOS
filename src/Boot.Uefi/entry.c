#include "uefi.h"
#include "witos/boot.h"
#include "witos/platform.h"

/* Fixed boot buffers avoid allocator/GC/filesystem dependencies at M0.
 * The image and all firmware-owned memory remain reserved after handoff. */
static WitU64 raw_memory_map[16384];
static WitMemoryRegion memory_regions[WIT_MAX_MEMORY_REGIONS];
static WitBootInfo boot_info;
void wit_boot_describe_image(WitBootInfo *boot);
void wit_boot_entropy(EfiBootServicesPrefix*,WitBootInfo*);

EfiStatus efi_main(EfiHandle image, EfiSystemTable *system)
{
    EfiBootServicesPrefix *services;

    wit_console_initialize();
    wit_console_write("[BOOT] UEFI x64 adapter\n");
    if (system == 0 || system->Header.Signature != EFI_SYSTEM_TABLE_SIGNATURE ||
        system->Header.HeaderSize < sizeof(EfiSystemTable) ||
        system->BootServices == 0) {
        wit_panic("Invalid UEFI system table");
    }
    services = system->BootServices;
    if (services->Header.Signature != EFI_BOOT_SERVICES_SIGNATURE ||
        services->Header.HeaderSize < sizeof(EfiBootServicesPrefix) ||
        services->GetMemoryMap == 0 || services->ExitBootServices == 0) {
        wit_panic("Invalid UEFI boot services");
    }

    wit_boot_describe_image(&boot_info);
    wit_boot_entropy(services,&boot_info);
    for (WitU32 attempt = 0; attempt < 3; ++attempt) {
        WitU64 size = sizeof(raw_memory_map);
        WitU64 key = 0;
        WitU64 descriptor_size = 0;
        WitU32 descriptor_version = 0;
        EfiStatus status = services->GetMemoryMap(
            &size, raw_memory_map, &key, &descriptor_size, &descriptor_version);

        if (status != EFI_SUCCESS || descriptor_size < sizeof(EfiMemoryDescriptor) ||
            descriptor_version != 1 || size == 0 || size > sizeof(raw_memory_map) ||
            size % descriptor_size != 0 || size / descriptor_size > WIT_MAX_MEMORY_REGIONS) {
            wit_panic("Unsupported UEFI memory map");
        }

        boot_info.MemoryRegionCount = (WitU32)(size / descriptor_size);
        for (WitU32 i = 0; i < boot_info.MemoryRegionCount; ++i) {
            const EfiMemoryDescriptor *source = (const EfiMemoryDescriptor *)(
                (const WitU8 *)raw_memory_map + i * descriptor_size);
            if (source->NumberOfPages == 0 || source->NumberOfPages > ~0ULL / 4096ULL) {
                wit_panic("Invalid UEFI memory length");
            }
            memory_regions[i].Base = source->PhysicalStart;
            memory_regions[i].Length = source->NumberOfPages * 4096ULL;
            memory_regions[i].Kind = source->Type == EFI_CONVENTIONAL_MEMORY
                ? WIT_MEMORY_USABLE : WIT_MEMORY_RESERVED;
            memory_regions[i].Reserved = 0;
        }

        /* Do not allocate or call firmware between GetMemoryMap and exit.
         * A stale key is retried with a fresh map, even after partial exit. */
        status = services->ExitBootServices(image, key);
        if (status == EFI_SUCCESS) {
            wit_disable_interrupts();
            boot_info.Magic = WIT_BOOT_MAGIC;
            boot_info.Version = WIT_BOOT_VERSION;
            boot_info.Size = sizeof(WitBootInfo);
            boot_info.Architecture = WIT_ARCH_X64;
            boot_info.MemoryRegions = memory_regions;
            boot_info.Flags = WIT_BOOT_SERVICES_EXITED;
#ifdef WITOS_TEST_OVERLAPPING_MAP
            if (boot_info.MemoryRegionCount > 1) {
                memory_regions[1].Base = memory_regions[0].Base;
            }
#endif
#ifdef WITOS_TEST_INVALID_BOOTINFO
            boot_info.Version = 0;
#endif
            wit_console_write("[BOOT] ExitBootServices OK\n");
            wit_platform_enter(&boot_info);
        }
        if (status != EFI_INVALID_PARAMETER) {
            wit_panic("ExitBootServices failed");
        }
    }
    wit_panic("ExitBootServices exhausted retries");
}
