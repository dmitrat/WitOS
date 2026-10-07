#ifndef WITOS_MEMORY_OBJECT_H
#define WITOS_MEMORY_OBJECT_H
#include "handles.h"
#include "limits.h"

/* Memory objects (RFC 0011 section 7.2, plan step K5.1): pages a component owns without an address of their own,
 * mapped by MEMORY_OBJECT_MAP into the caller's address space at a chosen or a fixed address under a protection the
 * handle's rights allow. A mapping is a reservation and is released with MEMORY_RELEASE; the object lives while a
 * handle or a mapping refers to it. Anonymous objects are the one kind until device memory (K3). */
#define WIT_MEMORY_MAP_VERSION 1U
#define WIT_MEMORY_MAP_SIZE 56U
#define WIT_MEMORY_OBJECT_ANONYMOUS 1U

/* MEMORY_OBJECT_MAP: the object, the page-aligned window of it, the address (0: the kernel chooses one in the data
 * arena; otherwise a free page-aligned address of an arena), the protection (NONE, READ, READ|WRITE or
 * READ|EXECUTE) and the target process, WIT_PROCESS_SELF until processes arrive (K5.2). */
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

/* Kernel-internal: an object's pages and the handles and mappings that refer to it. */
typedef struct WitMemoryObject {
    WitU32 Live, Kind, References, PageCount;
    WitU64 Pages[WIT_MEMORY_OBJECT_PAGES];
} WitMemoryObject;

typedef struct WitMemoryObjectTable {
    WitMemoryObject Entries[WIT_MEMORY_OBJECT_CAPACITY];
    WitU32 Count, Limit;
} WitMemoryObjectTable;

void wit_memory_objects_initialize(WitMemoryObjectTable *table);
#endif
