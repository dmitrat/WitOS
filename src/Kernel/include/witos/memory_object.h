#ifndef WITOS_MEMORY_OBJECT_H
#define WITOS_MEMORY_OBJECT_H
#include "handles.h"
#include "limits.h"

/* Memory objects (RFC 0011 section 7.2, plan step K5.1): pages a component owns without an address of their own,
 * mapped by MEMORY_OBJECT_MAP into the caller's address space at a chosen or a fixed address under a protection the
 * handle's rights allow. A mapping is a reservation and is released with MEMORY_RELEASE; the object lives while a
 * handle or a mapping refers to it. Anonymous objects own their pages; a device region and the device table are
 * pages the kernel or the board owns, mapped but never freed by the component (K3.1). */
#define WIT_MEMORY_MAP_VERSION 1U
#define WIT_MEMORY_MAP_SIZE 56U
#define WIT_MEMORY_OBJECT_ANONYMOUS 1U
#define WIT_MEMORY_OBJECT_DEVICE 2U /* a device's memory region: uncached, never executable (DEVICE_MEMORY, K3.1) */
#define WIT_MEMORY_OBJECT_TABLE 3U /* the kernel's device descriptor table: read-only (K3.1) */
#define WIT_MEMORY_OBJECT_PACKAGE 4U /* the boot package over its extents: read-only (K4) */

/* MEMORY_OBJECT_MAP: the object, the page-aligned window of it, the address (0: the kernel chooses one in the data
 * arena; otherwise a free page-aligned address of an arena), the protection (NONE, READ, READ|WRITE or
 * READ|EXECUTE) and the target process, WIT_PROCESS_SELF until processes arrive (K5.2c). */
typedef struct WitMemoryMapRequest {
    WitU32 Version, Size;
    WitU64 Object;
    WitU64 Offset;
    WitU64 Bytes;
    WitU64 Address;
    WitU32 Protection, Flags;
    WitU64 Target;
} WitMemoryMapRequest;

WIT_STATIC_ASSERT(sizeof(WitMemoryMapRequest) == WIT_MEMORY_MAP_SIZE, "Memory map request ABI");

/* Kernel-internal: an object's pages and the count of the handles, mappings, pins and capabilities in flight that
 * refer to it. The table is the kernel's (K5.2b), so one number names an object in every process. Owned pages are
 * the object's, from the kernel's page allocator, and are charged to the creator's page quota while the creator
 * lives (Creator; zero once it is torn down); the pages of a device region or the table are nobody's. Device is the
 * descriptor index plus one of a device region's object, whose mappings are uncached. */
struct WitBootStorageExtent;
struct WitUserProcess;
struct WitPageAllocator;

typedef struct WitMemoryObject {
    WitU32 Live, Kind, References, PageCount;
    WitU32 Owned, Device;
    WitU64 Pages[WIT_MEMORY_OBJECT_PAGES];
    /* An object over physical extents (the boot package, K4) has no page array: its pages are the extents' in order
     * and PageCount may exceed the array. */
    const struct WitBootStorageExtent *Extents;
    WitU32 ExtentCount, ExtentReserved;
    struct WitUserProcess *Creator;
    struct WitPageAllocator *Allocator;
} WitMemoryObject;

typedef struct WitMemoryObjectTable {
    WitMemoryObject Entries[WIT_MEMORY_OBJECT_TABLE_CAPACITY];
    WitU32 Count, Reserved;
} WitMemoryObjectTable;

/* The live objects kernel-wide, and those a process created and still live (its quota of WIT_MEMORY_OBJECT_CAPACITY). */
WitU32 wit_memory_objects_live(void);
WitU32 wit_memory_objects_charged(const struct WitUserProcess *process);
#endif
