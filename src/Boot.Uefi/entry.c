#include "uefi.h"
#include "witos/boot.h"
#include "witos/arch.h"
#include "witos/platform.h"

/* Fixed boot buffers avoid allocator/GC/filesystem dependencies at M0.
 * The image and all firmware-owned memory remain reserved after handoff. */
static WitU64 raw_memory_map[16384];
static WitMemoryRegion memory_regions[WIT_MAX_MEMORY_REGIONS];
static WitBootInfo boot_info;
void wit_boot_describe_image(WitBootInfo *boot);
void wit_boot_entropy(EfiBootServicesPrefix *, WitBootInfo *);
int wit_boot_storage(EfiHandle, EfiBootServicesPrefix *, WitBootInfo *);

/* The firmware tables the kernel's platform may enumerate devices from: the ACPI 2.0 RSDP and the flattened device
 * tree, found by their GUIDs in the configuration table; absent ones stay zero. The pointers are physical because
 * the firmware runs identity-mapped. */
static void firmware_tables(const EfiSystemTable *system, WitBootInfo *boot)
{
    static const EfiGuid acpi = {0x8868e871, 0xe4f1, 0x11d3, {0xbc, 0x22, 0x00, 0x80, 0xc7, 0x3c, 0x88, 0x81}};
    static const EfiGuid tree = {0xb1b621d5, 0xf19c, 0x41a5, {0x83, 0x0b, 0xd9, 0x15, 0x2c, 0x69, 0xaa, 0xe0}};
    const EfiConfigurationTable *tables = (const EfiConfigurationTable *)system->ConfigurationTable;
    boot->AcpiRsdp = 0;
    boot->DeviceTree = 0;
    if (tables == 0 || system->ConfigurationTableCount > 1024) {
        return;
    }
    for (WitU64 i = 0; i < system->ConfigurationTableCount; ++i) {
        const EfiGuid *g = &tables[i].VendorGuid;
        int same_acpi = g->A == acpi.A && g->B == acpi.B && g->C == acpi.C;
        int same_tree = g->A == tree.A && g->B == tree.B && g->C == tree.C;
        for (WitU32 k = 0; k < 8; ++k) {
            same_acpi = same_acpi && g->D[k] == acpi.D[k];
            same_tree = same_tree && g->D[k] == tree.D[k];
        }
        if (same_acpi && boot->AcpiRsdp == 0) {
            boot->AcpiRsdp = (WitU64)tables[i].VendorTable;
        }
        if (same_tree && boot->DeviceTree == 0) {
            boot->DeviceTree = (WitU64)tables[i].VendorTable;
        }
    }
    wit_console_write(boot->AcpiRsdp ? "[BOOT] ACPI tables published\n" : "[BOOT] No ACPI tables\n");
    wit_console_write(boot->DeviceTree ? "[BOOT] Device tree published\n" : "[BOOT] No device tree\n");
}

EfiStatus efi_main(EfiHandle image, EfiSystemTable *system)
{
    EfiBootServicesPrefix *services;

    wit_console_initialize();
    wit_console_write("[BOOT] UEFI ");
    wit_console_write(wit_arch_identity()->Name);
    wit_console_write(" adapter\n");
    if (system == 0 ||
        system->Header.Signature != EFI_SYSTEM_TABLE_SIGNATURE ||
        system->Header.HeaderSize < sizeof(EfiSystemTable) ||
        system->BootServices == 0) {
        wit_panic("Invalid UEFI system table");
    }
    services = system->BootServices;
    if (services->Header.Signature != EFI_BOOT_SERVICES_SIGNATURE ||
        services->Header.HeaderSize < sizeof(EfiBootServicesPrefix) ||
        services->GetMemoryMap == 0 ||
        services->ExitBootServices == 0) {
        wit_panic("Invalid UEFI boot services");
    }

    wit_boot_describe_image(&boot_info);
    firmware_tables(system, &boot_info);
    wit_boot_entropy(services, &boot_info);
    if (!wit_boot_storage(image, services, &boot_info)) {
        wit_panic("UEFI boot package unavailable or invalid");
    }
    for (WitU32 attempt = 0; attempt < 3; ++attempt) {
        WitU64 size = sizeof(raw_memory_map);
        WitU64 key = 0;
        WitU64 descriptor_size = 0;
        WitU32 descriptor_version = 0;
        EfiStatus status = services->GetMemoryMap(&size, raw_memory_map, &key, &descriptor_size, &descriptor_version);

        if (status != EFI_SUCCESS ||
            descriptor_size < sizeof(EfiMemoryDescriptor) ||
            descriptor_version != 1 ||
            size == 0 ||
            size > sizeof(raw_memory_map) ||
            size % descriptor_size != 0 ||
            size / descriptor_size > WIT_MAX_MEMORY_REGIONS) {
            wit_panic("Unsupported UEFI memory map");
        }

        boot_info.MemoryRegionCount = (WitU32)(size / descriptor_size);
        for (WitU32 i = 0; i < boot_info.MemoryRegionCount; ++i) {
            const EfiMemoryDescriptor *source =
                (const EfiMemoryDescriptor *)((const WitU8 *)raw_memory_map + i * descriptor_size);
            if (source->NumberOfPages == 0 || source->NumberOfPages > ~0ULL / 4096ULL) {
                wit_panic("Invalid UEFI memory length");
            }
            memory_regions[i].Base = source->PhysicalStart;
            memory_regions[i].Length = source->NumberOfPages * 4096ULL;
            memory_regions[i].Kind = source->Type == EFI_CONVENTIONAL_MEMORY ? WIT_MEMORY_USABLE : WIT_MEMORY_RESERVED;
            memory_regions[i].Reserved = 0;
        }

        /* Do not allocate or call firmware between GetMemoryMap and exit.
         * A stale key is retried with a fresh map, even after partial exit. */
        status = services->ExitBootServices(image, key);
        if (status == EFI_SUCCESS) {
            wit_arch_disable_interrupts();
            boot_info.Magic = WIT_BOOT_MAGIC;
            boot_info.Version = WIT_BOOT_VERSION;
            boot_info.Size = sizeof(WitBootInfo);
            boot_info.Architecture = wit_arch_identity()->BootArchitecture;
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
            wit_arch_enter(&boot_info);
        }
        if (status != EFI_INVALID_PARAMETER) {
            wit_panic("ExitBootServices failed");
        }
    }
    wit_panic("ExitBootServices exhausted retries");
}
