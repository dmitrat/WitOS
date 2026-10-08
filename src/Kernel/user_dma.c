#include "user.h"
#include "witos/dma.h"
#include "witos/platform.h"

/* DMA pinning from user mode (RFC 0011 section 7.7, plan step K3.2): DMA_PIN and DMA_UNPIN, the pin handle's
 * close, duplication and transfer. A pin is a reference to an anonymous memory object over a page-aligned window:
 * the object cannot end while pinned, its pages never move, and the physical ranges of the window are what the
 * holder of a device programs into it. Without an IOMMU the device holder is the trust boundary, so a pin needs the
 * device handle (BIND) as well as the object handle (MAP). */

#define PIN_RIGHTS (WIT_RIGHT_QUERY | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER)

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitUserPin *slot(WitUserProcess *p, WitU64 object)
{
    if (!object || object > WIT_PIN_CAPACITY || !p->Pins[object - 1].Live) {
        return 0;
    }
    return &p->Pins[object - 1];
}

static WitU64 get(WitUserProcess *p, WitU64 handle, WitU32 rights, WitUserPin **result, WitU64 *object, WitU32 *granted)
{
    *result = 0;
    *object = 0;
    *granted = 0;
    const WitU64 status = wit_handle_check(&p->Handles, handle, WIT_HANDLE_PIN, rights);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!wit_handle_describe(&p->Handles, handle, WIT_HANDLE_PIN, object, granted) || !(*result = slot(p, *object))) {
        return WIT_STATUS_BAD_HANDLE;
    }
    return WIT_STATUS_OK;
}

static void release_pin(WitUserProcess *p, WitU64 object)
{
    WitUserPin *pin = slot(p, object);
    require(pin != 0 && pin->References != 0, "Released pin reference has no pin");
    if (--pin->References == 0) {
        wit_user_memory_object_release(pin->Object);
        pin->Live = 0;
        pin->Object = 0;
        pin->PageFirst = 0;
        pin->PageCount = 0;
    }
}

WitU64 wit_user_dma_pin(WitUserProcess *p, WitU64 address, WitU64 size, WitU64 reserved, WitU64 *result)
{
    WitDmaPinRequest request;
    WitDmaRange ranges[WIT_DMA_RANGES_MAX];
    WitU32 device_index = 0, object_rights = 0, free = WIT_PIN_CAPACITY, count = 0;
    WitU64 object = 0;
    *result = 0;
    if (reserved || size != sizeof(request)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_copy_from(&p->Space, address, (WitU8 *)&request, sizeof(request))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request.Version != WIT_DMA_PIN_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (request.Size != sizeof(request) ||
        request.Flags ||
        !request.RangeCapacity ||
        request.RangeCapacity > WIT_DMA_RANGES_MAX) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitU64 status = wit_user_device_index(p, request.Device, WIT_RIGHT_BIND, &device_index);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    const WitU64 object_status = wit_handle_check(&p->Handles, request.Object, WIT_HANDLE_MEMORY_OBJECT, WIT_RIGHT_MAP);
    if (object_status != WIT_STATUS_OK) {
        return object_status;
    }
    if (!wit_handle_describe(&p->Handles, request.Object, WIT_HANDLE_MEMORY_OBJECT, &object, &object_rights)) {
        return WIT_STATUS_BAD_HANDLE;
    }
    if (wit_user_memory_object_kind(object) != WIT_MEMORY_OBJECT_ANONYMOUS) {
        return WIT_STATUS_INVALID_ARGUMENT; /* A device's registers or the device table are not DMA memory. */
    }
    const WitU64 pages = wit_user_memory_object_pages(object, 0, 0);
    if (!request.Bytes || (request.Bytes & 4095) || (request.Offset & 4095)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (request.Offset > pages * 4096 || request.Bytes > pages * 4096 - request.Offset) {
        return WIT_STATUS_TOO_LARGE;
    }
    if (!wit_user_buffer_writable(&p->Space, request.Ranges, (WitU64)request.RangeCapacity * sizeof(WitDmaRange))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    const WitU32 first = (WitU32)(request.Offset / 4096), page_count = (WitU32)(request.Bytes / 4096);
    for (WitU32 i = 0; i < WIT_DMA_RANGES_MAX; ++i) {
        ranges[i].Address = 0;
        ranges[i].Size = 0;
    }
    for (WitU32 i = 0; i < page_count; ++i) {
        const WitU64 physical = wit_user_memory_object_pages(object, first + i, 1);
        if (count && ranges[count - 1].Address + ranges[count - 1].Size == physical) {
            ranges[count - 1].Size += 4096;
            continue;
        }
        if (count == request.RangeCapacity) {
            return WIT_STATUS_TOO_LARGE; /* More ranges than the caller can take: nothing is pinned. */
        }
        ranges[count].Address = physical;
        ranges[count].Size = 4096;
        ++count;
    }
    for (WitU32 i = 0; i < WIT_PIN_CAPACITY; ++i) {
        if (!p->Pins[i].Live) {
            free = i;
            break;
        }
    }
    if (free == WIT_PIN_CAPACITY || !wit_handles_free_count(&p->Handles)) {
        return WIT_STATUS_NO_MEMORY;
    }
    const WitU64 handle = wit_handle_grant_object(&p->Handles, WIT_HANDLE_PIN, PIN_RIGHTS, free + 1);
    require(handle != 0, "Pin handle grant failed after the free slot check");
    wit_user_memory_object_retain(object);
    p->Pins[free].Object = (WitU32)object;
    p->Pins[free].PageFirst = first;
    p->Pins[free].PageCount = page_count;
    p->Pins[free].References = 1;
    p->Pins[free].Live = 1;
    require(wit_user_copy_to(
                &p->Space, request.Ranges, (const WitU8 *)ranges, (WitU64)request.RangeCapacity * sizeof(WitDmaRange)),
        "Validated range output changed");
    *result = handle;
    return WIT_STATUS_OK;
}

WitU64 wit_user_dma_unpin(WitUserProcess *p, WitU64 handle, WitU64 reserved0, WitU64 reserved1)
{
    if (reserved0 || reserved1) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    return wit_user_pin_close(p, handle);
}

WitU64 wit_user_pin_close(WitUserProcess *p, WitU64 handle)
{
    WitUserPin *pin;
    WitU64 object = 0;
    WitU32 granted = 0;
    const WitU64 status = get(p, handle, 0, &pin, &object, &granted);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    require(wit_handle_close(&p->Handles, handle) == WIT_STATUS_OK, "Pin handle close failed");
    release_pin(p, object);
    return WIT_STATUS_OK;
}

WitU64 wit_user_pin_duplicate(WitUserProcess *p, WitU64 source, WitU64 output, WitU64 requested)
{
    WitUserPin *pin;
    WitU64 object = 0, handle = 0;
    WitU32 granted = 0;
    if (requested & ~(WitU64)PIN_RIGHTS) {
        return WIT_STATUS_UNSUPPORTED;
    }
    const WitU64 status = get(p, source, WIT_RIGHT_DUPLICATE, &pin, &object, &granted);
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
    handle = wit_handle_grant_object(&p->Handles, WIT_HANDLE_PIN, rights, object);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    ++pin->References;
    require(wit_user_copy_to(&p->Space, output, (const WitU8 *)&handle, sizeof(handle)),
        "Validated duplicate output changed");
    return WIT_STATUS_OK;
}

/* A reference a dropped message carried (user_channel.c). */
void wit_user_pin_release(WitUserProcess *p, WitU64 object)
{
    release_pin(p, object);
}

int wit_user_pin_handle(WitUserProcess *p, WitU64 handle)
{
    return wit_handle_check(&p->Handles, handle, WIT_HANDLE_PIN, 0) != WIT_STATUS_WRONG_TYPE;
}

/* The component ends or starts: no pin, and no reference of a pin to an object. */
void wit_user_pins_reset(WitUserProcess *p)
{
    for (WitU32 i = 0; i < WIT_PIN_CAPACITY; ++i) {
        if (p->Pins[i].Live) {
            wit_user_memory_object_release(p->Pins[i].Object); /* The pin's reference ends with the component. */
        }
        p->Pins[i].Live = 0;
        p->Pins[i].Object = 0;
        p->Pins[i].PageFirst = 0;
        p->Pins[i].PageCount = 0;
        p->Pins[i].References = 0;
    }
}
