#include "user.h"
#include "witos/platform.h"
#include "witos/boot.h"

/* Memory objects (RFC 0011 section 7.2): MEMORY_OBJECT_CREATE and MEMORY_OBJECT_MAP, the object's close,
 * duplication and transfer, and the release of a mapping through MEMORY_RELEASE. Every call validates its whole
 * request before it allocates or maps; a failed creation owns no page, a failed mapping leaves no reservation. The
 * address space keeps the mappings (user_space.c); this module keeps the objects and counts their references.
 *
 * The table is the kernel's (K5.2b): an object has one number in every process, so a handle in flight between
 * processes and a mapping into another process (K5.2c) name the same object. An anonymous object owns its pages,
 * taken from the kernel's page allocator; its creator is charged for them, and for the object's slot in its quota of
 * live objects, while the creator lives. A process's references end with the process: its handles and the
 * capabilities in flight in its channels at its exit, its mappings at its teardown; what its objects were charged
 * for is orphaned at the teardown, and the object lives on with the references other processes hold. */

#define OBJECT_RIGHTS \
    (WIT_RIGHT_MAP | WIT_RIGHT_WRITE | WIT_RIGHT_EXECUTE | WIT_RIGHT_QUERY | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER)

static WitMemoryObjectTable objects;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

/* The physical page at an index of an object: from its page array or, for an object over extents, from them. */
static WitU64 object_page(const WitMemoryObject *entry, WitU32 index)
{
    if (!entry->Extents) {
        return entry->Pages[index];
    }
    WitU64 skipped = 0;
    for (WitU32 i = 0; i < entry->ExtentCount; ++i) {
        const WitU64 pages = entry->Extents[i].Length / 4096;
        if (index - skipped < pages) {
            return entry->Extents[i].Base + (WitU64)(index - skipped) * 4096;
        }
        skipped += pages;
    }
    wit_panic("Memory object page beyond its extents");
}

static WitMemoryObject *slot(WitU64 object)
{
    if (!object || object > WIT_MEMORY_OBJECT_TABLE_CAPACITY || !objects.Entries[object - 1].Live) {
        return 0;
    }
    return &objects.Entries[object - 1];
}

WitU32 wit_memory_objects_live(void)
{
    return objects.Count;
}

WitU32 wit_memory_objects_charged(const WitUserProcess *p)
{
    WitU32 count = 0;
    for (WitU32 i = 0; i < WIT_MEMORY_OBJECT_TABLE_CAPACITY; ++i) {
        if (objects.Entries[i].Live && objects.Entries[i].Creator == p) {
            ++count;
        }
    }
    return count;
}

/* A free entry for an object the process creates, within its quota of live objects; zero when none. */
static WitMemoryObject *reserve_entry(const WitUserProcess *p, WitU32 *object)
{
    *object = 0;
    if (wit_memory_objects_charged(p) >= WIT_MEMORY_OBJECT_CAPACITY) {
        return 0;
    }
    for (WitU32 i = 0; i < WIT_MEMORY_OBJECT_TABLE_CAPACITY; ++i) {
        if (!objects.Entries[i].Live) {
            *object = i + 1;
            return &objects.Entries[i];
        }
    }
    return 0;
}

/* Publishes a reserved entry with its one reference, the creator's. */
static void publish(WitMemoryObject *entry, WitUserProcess *p, WitU32 kind, WitU32 pages, WitU32 owned, WitU32 device,
    const WitBootStorageExtent *extents, WitU32 extent_count)
{
    entry->Kind = kind;
    entry->PageCount = pages;
    entry->References = 1;
    entry->Owned = owned;
    entry->Device = device;
    entry->Extents = extents;
    entry->ExtentCount = extent_count;
    entry->Creator = p;
    entry->Allocator = p->Space.Allocator;
    entry->Live = 1;
    ++objects.Count;
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
        !(*result = slot(*object))) {
        return WIT_STATUS_BAD_HANDLE;
    }
    return WIT_STATUS_OK;
}

/* The last reference ends the object: owned pages go back to the allocator and off the creator's charge. */
static void release_object(WitU64 object)
{
    WitMemoryObject *entry = slot(object);
    require(entry != 0 && entry->References != 0, "Released memory object reference has no object");
    if (--entry->References != 0) {
        return;
    }
    if (entry->Owned) {
        for (WitU32 i = 0; i < entry->PageCount; ++i) {
            require(wit_page_free(entry->Allocator, entry->Pages[i]), "Memory object page ownership corrupted");
        }
        if (entry->Creator) {
            wit_user_space_uncharge(&entry->Creator->Space, entry->PageCount);
        }
    }
    entry->Live = 0;
    entry->Kind = 0;
    entry->PageCount = 0;
    entry->Owned = 0;
    entry->Device = 0;
    entry->Extents = 0;
    entry->ExtentCount = 0;
    entry->Creator = 0;
    entry->Allocator = 0;
    --objects.Count;
}

WitU64 wit_user_memory_object_create(WitUserProcess *p, WitU64 size, WitU64 flags, WitU64 reserved, WitU64 *result)
{
    WitU32 object = 0;
    *result = 0;
    if (flags || reserved || !size || (size & 4095)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (size / 4096 > WIT_MEMORY_OBJECT_PAGES) {
        return WIT_STATUS_TOO_LARGE;
    }
    const WitU32 pages = (WitU32)(size / 4096);
    WitMemoryObject *entry = reserve_entry(p, &object);
    if (!entry || !wit_handles_free_count(&p->Handles) || !wit_user_space_charge(&p->Space, pages)) {
        return WIT_STATUS_NO_MEMORY;
    }
    for (WitU32 i = 0; i < pages; ++i) {
        if (!wit_page_allocate(p->Space.Allocator, &entry->Pages[i])) {
            while (i) {
                require(wit_page_free(p->Space.Allocator, entry->Pages[--i]), "Memory object rollback lost a page");
            }
            wit_user_space_uncharge(&p->Space, pages);
            return WIT_STATUS_NO_MEMORY;
        }
        for (WitU32 k = 0; k < 512; ++k) {
            ((WitU64 *)entry->Pages[i])[k] = 0;
        }
    }
    const WitU64 handle = wit_handle_grant_object(&p->Handles, WIT_HANDLE_MEMORY_OBJECT, OBJECT_RIGHTS, object);
    require(handle != 0, "Object handle grant failed after the free slot check");
    publish(entry, p, WIT_MEMORY_OBJECT_ANONYMOUS, pages, 1, 0, 0, 0);
    *result = handle;
    return WIT_STATUS_OK;
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
    /* The target: the caller, or a live process a handle with MANAGE names (K5.2c); the mapping is the target's. */
    WitUserProcess *target = 0;
    WitU64 status = wit_user_process_target(p, request.Target, WIT_RIGHT_MANAGE, &target);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    status = get(p, request.Object, WIT_RIGHT_MAP, &entry, &object, &granted);
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
        request.Bytes > (WitU64)entry->PageCount * 4096 - request.Offset ||
        request.Bytes / 4096 > WIT_MEMORY_OBJECT_PAGES) {
        return WIT_STATUS_TOO_LARGE; /* One mapping covers at most the pages of one anonymous object. */
    }
    WitU64 window[WIT_MEMORY_OBJECT_PAGES];
    for (WitU64 i = 0; i < request.Bytes / 4096; ++i) {
        window[i] = object_page(entry, (WitU32)(request.Offset / 4096 + i));
    }
    const WitU64 mapped = wit_user_space_map_object(&target->Space, request.Address, request.Bytes, window,
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
        release_object(object);
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
    release_object(object);
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

/* A reference a dropped message carried (user_channel.c), a pin held (user_dma.c) or a grant lost. */
void wit_user_memory_object_release(WitU64 object)
{
    release_object(object);
}

/* An object over pages the component does not own (a device region, the device table; K3.1): one reference, the
 * caller's. Returns the object number, zero when the table or the component's quota is full. */
int wit_user_memory_object_adopt(
    WitUserProcess *p, WitU32 kind, const WitU64 *pages, WitU32 count, WitU32 device, WitU32 *object)
{
    *object = 0;
    if (!count || count > WIT_MEMORY_OBJECT_PAGES || kind == WIT_MEMORY_OBJECT_ANONYMOUS) {
        return 0;
    }
    WitMemoryObject *entry = reserve_entry(p, object);
    if (!entry) {
        return 0;
    }
    for (WitU32 k = 0; k < count; ++k) {
        entry->Pages[k] = pages[k];
    }
    publish(entry, p, kind, count, 0, device, 0, 0);
    return 1;
}

/* An object over physical extents the component does not own (the boot package, K4): one reference, the caller's;
 * the page count is the size rounded up to pages. */
int wit_user_memory_object_adopt_extents(
    WitUserProcess *p, WitU32 kind, const WitBootStorageExtent *extents, WitU32 count, WitU64 bytes, WitU32 *object)
{
    *object = 0;
    if (!extents || !count || !bytes || kind == WIT_MEMORY_OBJECT_ANONYMOUS) {
        return 0;
    }
    WitMemoryObject *entry = reserve_entry(p, object);
    if (!entry) {
        return 0;
    }
    publish(entry, p, kind, (WitU32)((bytes + 4095) / 4096), 0, 0, extents, count);
    return 1;
}

WitU32 wit_user_memory_object_kind(WitU64 object)
{
    const WitMemoryObject *entry = slot(object);
    return entry ? entry->Kind : 0;
}

int wit_user_memory_object_handle(WitUserProcess *p, WitU64 handle)
{
    return wit_handle_check(&p->Handles, handle, WIT_HANDLE_MEMORY_OBJECT, 0) != WIT_STATUS_WRONG_TYPE;
}

/* For pins (K3.2): a reference beyond handles and mappings, and the pages of an object. */
void wit_user_memory_object_retain(WitU64 object)
{
    WitMemoryObject *entry = slot(object);
    require(entry != 0, "Retained memory object reference has no object");
    ++entry->References;
}

WitU64 wit_user_memory_object_pages(WitU64 object, WitU32 index, int physical)
{
    const WitMemoryObject *entry = slot(object);
    require(entry != 0 && (!physical || index < entry->PageCount), "Memory object page query out of range");
    return physical ? object_page(entry, index) : entry->PageCount;
}

/* The component's end (K5.2b). At its exit, before the handle table is wiped: every handle to an object releases
 * its reference. At its teardown: every mapping releases its reference, and the objects it created and still live
 * lose their creator, so that nothing charges a record about to be reused. */
void wit_user_memory_objects_release_handles(WitUserProcess *p)
{
    for (WitU32 i = 0; i < p->Handles.Limit; ++i) {
        const WitHandleEntry *entry = &p->Handles.Entries[i];
        if (entry->Live && entry->Kind == WIT_HANDLE_MEMORY_OBJECT) {
            release_object(entry->Object);
        }
    }
}

void wit_user_memory_objects_release_mappings(WitUserProcess *p)
{
    WitU32 mapped[WIT_RUNTIME_RESERVATION_CAPACITY];
    const WitU32 count = wit_user_space_take_mapped_objects(&p->Space, mapped, WIT_RUNTIME_RESERVATION_CAPACITY);
    for (WitU32 i = 0; i < count; ++i) {
        release_object(mapped[i]);
    }
}

void wit_memory_objects_orphan(WitUserProcess *p)
{
    for (WitU32 i = 0; i < WIT_MEMORY_OBJECT_TABLE_CAPACITY; ++i) {
        WitMemoryObject *entry = &objects.Entries[i];
        if (!entry->Live || entry->Creator != p) {
            continue;
        }
        if (entry->Owned) {
            wit_user_space_uncharge(&p->Space, entry->PageCount);
        }
        entry->Creator = 0;
    }
    require(p->Space.ChargedPages == 0, "Memory object charge survived its objects");
}
