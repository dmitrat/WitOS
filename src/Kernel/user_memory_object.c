#include "user.h"
#include "witos/platform.h"

/* Memory objects (RFC 0011 section 7.2): MEMORY_OBJECT_CREATE and MEMORY_OBJECT_MAP, the object's close,
 * duplication and transfer, and the release of a mapping through MEMORY_RELEASE. Every call validates its whole
 * request before it allocates or maps; a failed creation owns no page, a failed mapping leaves no reservation. The
 * address space keeps the mappings (user_space.c); this module keeps the objects and counts their references. */

#define OBJECT_RIGHTS \
    (WIT_RIGHT_MAP | WIT_RIGHT_WRITE | WIT_RIGHT_EXECUTE | WIT_RIGHT_QUERY | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER)

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

void wit_memory_objects_initialize(WitMemoryObjectTable *table)
{
    table->Count = 0;
    table->Limit = WIT_MEMORY_OBJECT_CAPACITY;
    for (WitU32 i = 0; i < WIT_MEMORY_OBJECT_CAPACITY; ++i) {
        table->Entries[i].Live = 0;
        table->Entries[i].Kind = 0;
        table->Entries[i].References = 0;
        table->Entries[i].PageCount = 0;
        table->Entries[i].Owned = 0;
        table->Entries[i].Device = 0;
    }
}

static WitMemoryObject *slot(WitUserProcess *p, WitU64 object)
{
    if (!object || object > p->MemoryObjects.Limit || !p->MemoryObjects.Entries[object - 1].Live) {
        return 0;
    }
    return &p->MemoryObjects.Entries[object - 1];
}

/* The object behind a live handle of the kind with the rights, its object number and the handle's rights. */
static WitU64 get(
    WitUserProcess *p, WitU64 handle, WitU32 rights, WitMemoryObject **result, WitU64 *object, WitU32 *granted)
{
    *result = 0;
    *object = 0;
    *granted = 0;
    const WitU64 status = wit_handle_check(&p->Handles, handle, WIT_HANDLE_MEMORY_OBJECT, rights);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!wit_handle_describe(&p->Handles, handle, WIT_HANDLE_MEMORY_OBJECT, object, granted) ||
        !(*result = slot(p, *object))) {
        return WIT_STATUS_BAD_HANDLE;
    }
    return WIT_STATUS_OK;
}

static void release_object(WitUserProcess *p, WitU64 object)
{
    WitMemoryObject *entry = slot(p, object);
    require(entry != 0 && entry->References != 0, "Released memory object reference has no object");
    if (--entry->References == 0) {
        if (entry->Owned) {
            wit_user_space_free_pages(&p->Space, entry->Pages, entry->PageCount);
        }
        entry->Live = 0;
        entry->Kind = 0;
        entry->PageCount = 0;
        entry->Owned = 0;
        entry->Device = 0;
        --p->MemoryObjects.Count;
    }
}

WitU64 wit_user_memory_object_create(WitUserProcess *p, WitU64 size, WitU64 flags, WitU64 reserved, WitU64 *result)
{
    *result = 0;
    if (flags || reserved || !size || (size & 4095)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (size / 4096 > WIT_MEMORY_OBJECT_PAGES) {
        return WIT_STATUS_TOO_LARGE;
    }
    for (WitU32 i = 0; i < p->MemoryObjects.Limit; ++i) {
        WitMemoryObject *entry = &p->MemoryObjects.Entries[i];
        if (entry->Live) {
            continue;
        }
        const WitU32 pages = (WitU32)(size / 4096);
        if (!wit_user_space_allocate_pages(&p->Space, entry->Pages, pages)) {
            return WIT_STATUS_NO_MEMORY;
        }
        const WitU64 handle = wit_handle_grant_object(&p->Handles, WIT_HANDLE_MEMORY_OBJECT, OBJECT_RIGHTS, i + 1);
        if (!handle) {
            wit_user_space_free_pages(&p->Space, entry->Pages, pages);
            return WIT_STATUS_NO_MEMORY;
        }
        entry->Kind = WIT_MEMORY_OBJECT_ANONYMOUS;
        entry->PageCount = pages;
        entry->References = 1;
        entry->Owned = 1;
        entry->Device = 0;
        entry->Live = 1;
        ++p->MemoryObjects.Count;
        *result = handle;
        return WIT_STATUS_OK;
    }
    return WIT_STATUS_NO_MEMORY;
}

static int valid_protection(WitU32 protection)
{
    return protection == WIT_MEMORY_NONE ||
        protection == WIT_MEMORY_READ ||
        protection == (WIT_MEMORY_READ | WIT_MEMORY_WRITE) ||
        protection == (WIT_MEMORY_READ | WIT_MEMORY_EXECUTE);
}

WitU64 wit_user_memory_object_map(WitUserProcess *p, WitU64 address, WitU64 size, WitU64 reserved, WitU64 *result)
{
    WitMemoryMapRequest request;
    WitMemoryObject *entry;
    WitU64 object = 0;
    WitU32 granted = 0;
    *result = 0;
    if (reserved || size != sizeof(request)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_copy_from(&p->Space, address, (WitU8 *)&request, sizeof(request))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request.Version != WIT_MEMORY_MAP_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (request.Size != sizeof(request) || request.Flags || !valid_protection(request.Protection)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (request.Target != WIT_PROCESS_SELF) {
        return WIT_STATUS_UNSUPPORTED; /* Another process arrives with K5.2. */
    }
    const WitU64 status = get(p, request.Object, WIT_RIGHT_MAP, &entry, &object, &granted);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (((request.Protection & WIT_MEMORY_WRITE) && !(granted & WIT_RIGHT_WRITE)) ||
        ((request.Protection & WIT_MEMORY_EXECUTE) && !(granted & WIT_RIGHT_EXECUTE))) {
        return WIT_STATUS_DENIED;
    }
    if (!request.Bytes || (request.Bytes & 4095) || (request.Offset & 4095) || (request.Address & 4095)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (request.Offset > (WitU64)entry->PageCount * 4096 ||
        request.Bytes > (WitU64)entry->PageCount * 4096 - request.Offset) {
        return WIT_STATUS_TOO_LARGE;
    }
    const WitU64 mapped =
        wit_user_space_map_object(&p->Space, request.Address, request.Bytes, &entry->Pages[request.Offset / 4096],
            request.Protection, (WitU32)object, granted, entry->Kind == WIT_MEMORY_OBJECT_DEVICE, result);
    if (mapped != WIT_STATUS_OK) {
        return mapped;
    }
    ++entry->References;
    return WIT_STATUS_OK;
}

/* MEMORY_RELEASE: a mapping of an object releases the reservation and the object's reference; a plain reservation
 * goes its ordinary way. */
WitU64 wit_user_memory_unmap(WitUserProcess *p, WitU64 address)
{
    WitU64 object = 0;
    if (!wit_user_space_mapping_object(&p->Space, address, &object)) {
        return wit_user_memory_release(&p->Space, address);
    }
    const WitU64 status = wit_user_memory_release(&p->Space, address);
    if (status == WIT_STATUS_OK) {
        release_object(p, object);
    }
    return status;
}

WitU64 wit_user_memory_object_close(WitUserProcess *p, WitU64 handle)
{
    WitMemoryObject *entry;
    WitU64 object = 0;
    WitU32 granted = 0;
    const WitU64 status = get(p, handle, 0, &entry, &object, &granted);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    require(wit_handle_close(&p->Handles, handle) == WIT_STATUS_OK, "Memory object handle close failed");
    release_object(p, object);
    return WIT_STATUS_OK;
}

WitU64 wit_user_memory_object_duplicate(WitUserProcess *p, WitU64 source, WitU64 output, WitU64 requested)
{
    WitMemoryObject *entry;
    WitU64 object = 0, handle = 0;
    WitU32 granted = 0;
    if (requested & ~(WitU64)OBJECT_RIGHTS) {
        return WIT_STATUS_UNSUPPORTED;
    }
    const WitU64 status = get(p, source, WIT_RIGHT_DUPLICATE, &entry, &object, &granted);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    const WitU32 rights = requested ? (WitU32)requested : granted;
    if ((rights & granted) != rights) {
        return WIT_STATUS_DENIED;
    }
    if (!wit_user_buffer_writable(&p->Space, output, sizeof(handle))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    handle = wit_handle_grant_object(&p->Handles, WIT_HANDLE_MEMORY_OBJECT, rights, object);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    ++entry->References;
    require(wit_user_copy_to(&p->Space, output, (const WitU8 *)&handle, sizeof(handle)),
        "Validated duplicate output changed");
    return WIT_STATUS_OK;
}

/* A reference a dropped message carried (user_channel.c). */
void wit_user_memory_object_release(WitUserProcess *p, WitU64 object)
{
    release_object(p, object);
}

/* An object over pages the component does not own (a device region, the device table; K3.1): one reference, the
 * caller's. Returns the object number, zero when the table is full. */
int wit_user_memory_object_adopt(
    WitUserProcess *p, WitU32 kind, const WitU64 *pages, WitU32 count, WitU32 device, WitU32 *object)
{
    *object = 0;
    if (!count || count > WIT_MEMORY_OBJECT_PAGES || kind == WIT_MEMORY_OBJECT_ANONYMOUS) {
        return 0;
    }
    for (WitU32 i = 0; i < p->MemoryObjects.Limit; ++i) {
        WitMemoryObject *entry = &p->MemoryObjects.Entries[i];
        if (entry->Live) {
            continue;
        }
        for (WitU32 k = 0; k < count; ++k) {
            entry->Pages[k] = pages[k];
        }
        entry->Kind = kind;
        entry->PageCount = count;
        entry->References = 1;
        entry->Owned = 0;
        entry->Device = device;
        entry->Live = 1;
        ++p->MemoryObjects.Count;
        *object = i + 1;
        return 1;
    }
    return 0;
}

WitU32 wit_user_memory_object_kind(WitUserProcess *p, WitU64 object)
{
    const WitMemoryObject *entry = slot(p, object);
    return entry ? entry->Kind : 0;
}

int wit_user_memory_object_handle(WitUserProcess *p, WitU64 handle)
{
    return wit_handle_check(&p->Handles, handle, WIT_HANDLE_MEMORY_OBJECT, 0) != WIT_STATUS_WRONG_TYPE;
}
