#ifndef WITOS_UEFI_H
#define WITOS_UEFI_H

#include "witos/types.h"

/* Minimal x64 UEFI ABI declarations; no UEFI types cross WitBootInfo.
 * Layout reference: UEFI specification, EFI_SYSTEM_TABLE/EFI_BOOT_SERVICES.
 * All functions use the Microsoft x64 calling convention. */
typedef WitU64 EfiStatus;
typedef void *EfiHandle;

#define EFI_SUCCESS 0ULL
#define EFI_INVALID_PARAMETER 0x8000000000000002ULL
#define EFI_CONVENTIONAL_MEMORY 7U
#define EFI_SYSTEM_TABLE_SIGNATURE 0x5453595320494249ULL
#define EFI_BOOT_SERVICES_SIGNATURE 0x56524553544F4F42ULL

typedef struct EfiTableHeader {
    WitU64 Signature;
    WitU32 Revision;
    WitU32 HeaderSize;
    WitU32 Crc32;
    WitU32 Reserved;
} EfiTableHeader;

typedef struct EfiMemoryDescriptor {
    WitU32 Type;
    WitU32 Padding;
    WitU64 PhysicalStart;
    WitU64 VirtualStart;
    WitU64 NumberOfPages;
    WitU64 Attributes;
} EfiMemoryDescriptor;

typedef struct EfiGuid { WitU32 A; WitU16 B,C; WitU8 D[8]; } EfiGuid;
typedef EfiStatus (*EfiLocateProtocol)(const EfiGuid*,void*,void**);
typedef struct EfiRngProtocol EfiRngProtocol;
struct EfiRngProtocol {
    EfiStatus (*GetInfo)(EfiRngProtocol*,WitU64*,EfiGuid*);
    EfiStatus (*GetRng)(EfiRngProtocol*,const EfiGuid*,WitU64,WitU8*);
};
typedef EfiStatus (*EfiGetMemoryMap)(WitU64 *, void *, WitU64 *, WitU64 *, WitU32 *);
typedef EfiStatus (*EfiExitBootServices)(EfiHandle, WitU64);

typedef struct EfiBootServicesPrefix {
    EfiTableHeader Header;
    void *BeforeGetMemoryMap[4];
    EfiGetMemoryMap GetMemoryMap;
    void *BeforeExitBootServices[21];
    EfiExitBootServices ExitBootServices;
    void *BeforeLocateProtocol[10];
    EfiLocateProtocol LocateProtocol;
} EfiBootServicesPrefix;

typedef struct EfiSystemTable {
    EfiTableHeader Header;
    void *FirmwareVendor;
    WitU32 FirmwareRevision;
    void *ConsoleFields[6];
    void *RuntimeServices;
    EfiBootServicesPrefix *BootServices;
    WitU64 ConfigurationTableCount;
    void *ConfigurationTable;
} EfiSystemTable;

_Static_assert(sizeof(EfiGuid) == 16, "UEFI GUID layout");
_Static_assert(sizeof(EfiTableHeader) == 24, "UEFI header layout");
_Static_assert(sizeof(EfiMemoryDescriptor) == 40, "UEFI descriptor layout");
_Static_assert(sizeof(EfiBootServicesPrefix) == 328, "UEFI services prefix layout");
_Static_assert(sizeof(EfiSystemTable) == 120, "UEFI system table layout");

#endif
