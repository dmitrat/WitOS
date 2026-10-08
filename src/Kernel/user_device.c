#include "user.h"
#include "witos/devices.h"
#include "witos/platform.h"

/* Devices from user mode (RFC 0011 section 7.7, plan step K3.1): the device table as a read-only memory object,
 * DEVICE_ACQUIRE over it, DEVICE_MEMORY of an acquired device, and the device handle's close, duplication and
 * transfer. A device handle's object is the descriptor index plus one; the component counts its references (handles
 * and handles in flight) and gives the descriptor back with the last. Every call validates everything before it
 * changes state; nothing here reads a descriptor's meaning. */

#define TABLE_RIGHTS (WIT_RIGHT_MAP | WIT_RIGHT_QUERY | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER | WIT_RIGHT_ACQUIRE)
#define DEVICE_RIGHTS (WIT_RIGHT_BIND | WIT_RIGHT_QUERY | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER)
#define REGION_RIGHTS (WIT_RIGHT_MAP | WIT_RIGHT_WRITE | WIT_RIGHT_QUERY | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER)

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

/* The owner token of a component: its identity, never zero, never another component's. */
static WitU64 owner_token(const WitUserProcess *p)
{
    return ((WitU64)p->Id << 32) | (p->Slot + 1);
}

/* The device table as a memory object of the component with the rights asked; the test harness and, after K4,
 * the root task's startup hand it over. Zero when the table has no slot or no handle. */
WitU64 wit_user_device_table_grant(WitUserProcess *p, WitU32 rights)
{
    WitU64 page = wit_devices_table_page();
    WitU32 object = 0;
    if (!page || (rights & ~(WitU32)TABLE_RIGHTS)) {
        return 0;
    }
    if (!wit_user_memory_object_adopt(p, WIT_MEMORY_OBJECT_TABLE, &page, 1, 0, &object)) {
        return 0;
    }
    const WitU64 handle = wit_handle_grant_object(&p->Handles, WIT_HANDLE_MEMORY_OBJECT, rights, object);
    if (!handle) {
        wit_user_memory_object_release(object);
        return 0;
    }
    return handle;
}

static WitU64 get(WitUserProcess *p, WitU64 handle, WitU32 rights, WitU32 *index, WitU32 *granted)
{
    WitU64 object = 0;
    *index = 0;
    *granted = 0;
    const WitU64 status = wit_handle_check(&p->Handles, handle, WIT_HANDLE_DEVICE, rights);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!wit_handle_describe(&p->Handles, handle, WIT_HANDLE_DEVICE, &object, granted) ||
        !object ||
        object > WIT_DEVICE_CAPACITY ||
        wit_devices_owner((WitU32)object - 1) != owner_token(p)) {
        return WIT_STATUS_BAD_HANDLE;
    }
    *index = (WitU32)object - 1;
    return WIT_STATUS_OK;
}

static void release_device(WitUserProcess *p, WitU32 index)
{
    require(index < WIT_DEVICE_CAPACITY && p->DeviceReferences[index], "Released device reference has no device");
    if (--p->DeviceReferences[index] == 0) {
        wit_devices_release(index, owner_token(p));
    }
}

WitU64 wit_user_device_acquire(WitUserProcess *p, WitU64 table, WitU64 index, WitU64 reserved, WitU64 *result)
{
    WitU64 object = 0;
    WitU32 granted = 0;
    *result = 0;
    if (reserved) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitU64 status = wit_handle_check(&p->Handles, table, WIT_HANDLE_MEMORY_OBJECT, WIT_RIGHT_ACQUIRE);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!wit_handle_describe(&p->Handles, table, WIT_HANDLE_MEMORY_OBJECT, &object, &granted) ||
        wit_user_memory_object_kind(object) != WIT_MEMORY_OBJECT_TABLE) {
        return WIT_STATUS_BAD_HANDLE;
    }
    if (index >= wit_devices_count()) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (wit_devices_owner((WitU32)index)) {
        return WIT_STATUS_BUSY;
    }
    if (!wit_handles_free_count(&p->Handles)) {
        return WIT_STATUS_NO_MEMORY;
    }
    require(wit_devices_acquire((WitU32)index, owner_token(p)), "Free device could not be acquired");
    const WitU64 handle = wit_handle_grant_object(&p->Handles, WIT_HANDLE_DEVICE, DEVICE_RIGHTS, index + 1);
    require(handle != 0, "Device handle grant failed after the free slot check");
    p->DeviceReferences[index] = 1;
    *result = handle;
    return WIT_STATUS_OK;
}

WitU64 wit_user_device_memory(WitUserProcess *p, WitU64 device, WitU64 region, WitU64 reserved, WitU64 *result)
{
    WitU32 index = 0, granted = 0, object = 0;
    WitU64 pages[WIT_MEMORY_OBJECT_PAGES];
    *result = 0;
    if (reserved) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitU64 status = get(p, device, WIT_RIGHT_BIND, &index, &granted);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    const WitDeviceDescriptor *d = wit_devices_descriptor(index);
    require(d != 0, "Acquired device has no descriptor");
    if (region >= d->RegionCount) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitDeviceRegion *r = &d->Regions[region];
    if (!(r->Flags & WIT_DEVICE_REGION_MEMORY)) {
        return WIT_STATUS_UNSUPPORTED; /* Port I/O stays described, never mapped (RFC 0011 section 7.7). */
    }
    if (r->Size / 4096 > WIT_MEMORY_OBJECT_PAGES) {
        return WIT_STATUS_TOO_LARGE;
    }
    if (!wit_handles_free_count(&p->Handles)) {
        return WIT_STATUS_NO_MEMORY;
    }
    for (WitU64 i = 0; i < r->Size / 4096; ++i) {
        pages[i] = r->Base + i * 4096;
    }
    if (!wit_user_memory_object_adopt(
            p, WIT_MEMORY_OBJECT_DEVICE, pages, (WitU32)(r->Size / 4096), index + 1, &object)) {
        return WIT_STATUS_NO_MEMORY;
    }
    const WitU64 handle = wit_handle_grant_object(&p->Handles, WIT_HANDLE_MEMORY_OBJECT, REGION_RIGHTS, object);
    require(handle != 0, "Region handle grant failed after the free slot check");
    *result = handle;
    return WIT_STATUS_OK;
}

WitU64 wit_user_device_close(WitUserProcess *p, WitU64 handle)
{
    WitU32 index = 0, granted = 0;
    const WitU64 status = get(p, handle, 0, &index, &granted);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    require(wit_handle_close(&p->Handles, handle) == WIT_STATUS_OK, "Device handle close failed");
    release_device(p, index);
    return WIT_STATUS_OK;
}

WitU64 wit_user_device_duplicate(WitUserProcess *p, WitU64 source, WitU64 output, WitU64 requested)
{
    WitU32 index = 0, granted = 0;
    WitU64 handle = 0;
    if (requested & ~(WitU64)DEVICE_RIGHTS) {
        return WIT_STATUS_UNSUPPORTED;
    }
    const WitU64 status = get(p, source, WIT_RIGHT_DUPLICATE, &index, &granted);
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
    handle = wit_handle_grant_object(&p->Handles, WIT_HANDLE_DEVICE, rights, index + 1);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    ++p->DeviceReferences[index];
    require(wit_user_copy_to(&p->Space, output, (const WitU8 *)&handle, sizeof(handle)),
        "Validated duplicate output changed");
    return WIT_STATUS_OK;
}

/* A reference a dropped message carried (user_channel.c): the device object number is the index plus one. */
void wit_user_device_release(WitUserProcess *p, WitU64 object)
{
    require(object != 0 && object <= WIT_DEVICE_CAPACITY, "Dropped device reference is out of range");
    release_device(p, (WitU32)object - 1);
}

int wit_user_device_handle(WitUserProcess *p, WitU64 handle)
{
    return wit_handle_check(&p->Handles, handle, WIT_HANDLE_DEVICE, 0) != WIT_STATUS_WRONG_TYPE;
}

/* The component ends: every device it still holds is free again, whatever handles it had. */
void wit_user_devices_reset(WitUserProcess *p)
{
    for (WitU32 i = 0; i < WIT_DEVICE_CAPACITY; ++i) {
        if (p->DeviceReferences[i]) {
            p->DeviceReferences[i] = 0;
            if (wit_devices_owner(i) == owner_token(p)) {
                wit_devices_release(i, owner_token(p));
            }
        }
    }
}

/* For bindings and pins (K3.2): the component's token, an acquired device's index behind a handle with the rights,
 * and references to a device beyond its handles. */
WitU64 wit_user_owner_token(const WitUserProcess *p)
{
    return owner_token(p);
}

WitU64 wit_user_device_index(WitUserProcess *p, WitU64 handle, WitU32 rights, WitU32 *index)
{
    WitU32 granted = 0;
    return get(p, handle, rights, index, &granted);
}

void wit_user_device_reference(WitUserProcess *p, WitU32 index)
{
    require(index < WIT_DEVICE_CAPACITY && p->DeviceReferences[index], "Referenced device is not the component's");
    ++p->DeviceReferences[index];
}

void wit_user_device_unreference(WitUserProcess *p, WitU32 index)
{
    release_device(p, index);
}
