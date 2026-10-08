#include "user.h"
#include "witos/devices.h"
#include "witos/platform.h"

/* Interrupts from user mode (RFC 0011 section 7.7, plan step K3.2): INTERRUPT_BIND ties a line of an acquired
 * device to an event of the component, INTERRUPT_ACK unmasks the line after the component handled it, and the
 * interrupt handle's close, duplication and transfer. A binding is one per line, kernel-wide, since lines are the
 * board's; it holds a reference to the device (the device stays acquired while bound) and to the event (the event
 * lives while bound, whatever handles the component closes). On an interrupt the kernel masks the line, sets the
 * event and resumes; the line stays masked until INTERRUPT_ACK or the binding's end. The kernel knows no device
 * protocol: what the interrupt means is the driver's business. */

#define INTERRUPT_RIGHTS (WIT_RIGHT_ACK | WIT_RIGHT_QUERY | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER)

typedef struct WitInterruptBinding {
    WitU32 Live, Line, Device; /* Device: the descriptor index plus one */
    WitU64 Owner; /* the owner token of the binding component (user_device.c) */
    WitU64 Event; /* the event object number in the owner's table */
} WitInterruptBinding;

static WitInterruptBinding bindings[WIT_INTERRUPT_CAPACITY];

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitInterruptBinding *slot(WitU64 object)
{
    if (!object || object > WIT_INTERRUPT_CAPACITY || !bindings[object - 1].Live) {
        return 0;
    }
    return &bindings[object - 1];
}

/* The binding behind a live handle of the component with the rights, and its number. */
static WitU64 get(
    WitUserProcess *p, WitU64 handle, WitU32 rights, WitInterruptBinding **result, WitU64 *object, WitU32 *granted)
{
    *result = 0;
    *object = 0;
    *granted = 0;
    const WitU64 status = wit_handle_check(&p->Handles, handle, WIT_HANDLE_INTERRUPT, rights);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!wit_handle_describe(&p->Handles, handle, WIT_HANDLE_INTERRUPT, object, granted) ||
        !(*result = slot(*object)) ||
        (*result)->Owner != wit_user_owner_token(p)) {
        return WIT_STATUS_BAD_HANDLE;
    }
    return WIT_STATUS_OK;
}

static void end_binding(WitUserProcess *p, WitU64 object)
{
    WitInterruptBinding *b = slot(object);
    require(b != 0 && b->Owner == wit_user_owner_token(p), "Ended binding is not the component's");
    wit_platform_line_mask(b->Line);
    wit_event_release(&p->Events, b->Event);
    wit_user_device_unreference(p, b->Device - 1);
    b->Live = 0;
    b->Line = 0;
    b->Device = 0;
    b->Owner = 0;
    b->Event = 0;
}

static void release_binding(WitUserProcess *p, WitU64 object)
{
    require(object != 0 && object <= WIT_INTERRUPT_CAPACITY && p->InterruptReferences[object - 1],
        "Released interrupt reference has no binding");
    if (--p->InterruptReferences[object - 1] == 0) {
        end_binding(p, object);
    }
}

WitU64 wit_user_interrupt_bind(WitUserProcess *p, WitU64 device, WitU64 line_index, WitU64 event, WitU64 *result)
{
    WitU32 device_index = 0, free = WIT_INTERRUPT_CAPACITY;
    WitU64 event_object = 0;
    WitU32 event_rights = 0;
    WitEvent *target;
    *result = 0;
    const WitU64 status = wit_user_device_index(p, device, WIT_RIGHT_BIND, &device_index);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    const WitDeviceDescriptor *d = wit_devices_descriptor(device_index);
    require(d != 0, "Acquired device has no descriptor");
    if (line_index >= d->LineCount) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitU32 line = d->Lines[line_index].Line;
    if (d->Lines[line_index].Kind != WIT_DEVICE_LINE_LEVEL || !wit_platform_line_valid(line)) {
        return WIT_STATUS_UNSUPPORTED;
    }
    const WitU64 event_status = wit_event_get(&p->Events, &p->Handles, event, WIT_RIGHT_SIGNAL, &target);
    if (event_status != WIT_STATUS_OK) {
        return event_status;
    }
    require(wit_handle_describe(&p->Handles, event, WIT_HANDLE_EVENT, &event_object, &event_rights),
        "Checked event handle has no description");
    for (WitU32 i = 0; i < WIT_INTERRUPT_CAPACITY; ++i) {
        if (bindings[i].Live && bindings[i].Line == line) {
            return WIT_STATUS_BUSY;
        }
        if (!bindings[i].Live && free == WIT_INTERRUPT_CAPACITY) {
            free = i;
        }
    }
    if (free == WIT_INTERRUPT_CAPACITY || !wit_handles_free_count(&p->Handles)) {
        return WIT_STATUS_NO_MEMORY;
    }
    const WitU64 handle = wit_handle_grant_object(&p->Handles, WIT_HANDLE_INTERRUPT, INTERRUPT_RIGHTS, free + 1);
    require(handle != 0, "Interrupt handle grant failed after the free slot check");
    wit_event_retain(&p->Events, event_object);
    wit_user_device_reference(p, device_index);
    bindings[free].Line = line;
    bindings[free].Device = device_index + 1;
    bindings[free].Owner = wit_user_owner_token(p);
    bindings[free].Event = event_object;
    bindings[free].Live = 1;
    p->InterruptReferences[free] = 1;
    wit_platform_line_unmask(line);
    *result = handle;
    return WIT_STATUS_OK;
}

WitU64 wit_user_interrupt_ack(WitUserProcess *p, WitU64 handle, WitU64 reserved0, WitU64 reserved1)
{
    WitInterruptBinding *b;
    WitU64 object = 0;
    WitU32 granted = 0;
    if (reserved0 || reserved1) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitU64 status = get(p, handle, WIT_RIGHT_ACK, &b, &object, &granted);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    wit_platform_line_unmask(b->Line);
    return WIT_STATUS_OK;
}

/* A line was raised (the architecture masked and completed it): the bound event of the running component is set.
 * An unbound line, or one bound by another component, stays masked. */
void wit_user_interrupt_raised(WitUserProcess *p, WitU32 line)
{
    for (WitU32 i = 0; i < WIT_INTERRUPT_CAPACITY; ++i) {
        if (bindings[i].Live && bindings[i].Line == line) {
            if (p && bindings[i].Owner == wit_user_owner_token(p)) {
                wit_user_event_signal_object(p, bindings[i].Event);
                ++p->InterruptsDelivered;
            }
            return;
        }
    }
}

WitU64 wit_user_interrupt_close(WitUserProcess *p, WitU64 handle)
{
    WitInterruptBinding *b;
    WitU64 object = 0;
    WitU32 granted = 0;
    const WitU64 status = get(p, handle, 0, &b, &object, &granted);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    require(wit_handle_close(&p->Handles, handle) == WIT_STATUS_OK, "Interrupt handle close failed");
    release_binding(p, object);
    return WIT_STATUS_OK;
}

WitU64 wit_user_interrupt_duplicate(WitUserProcess *p, WitU64 source, WitU64 output, WitU64 requested)
{
    WitInterruptBinding *b;
    WitU64 object = 0, handle = 0;
    WitU32 granted = 0;
    if (requested & ~(WitU64)INTERRUPT_RIGHTS) {
        return WIT_STATUS_UNSUPPORTED;
    }
    const WitU64 status = get(p, source, WIT_RIGHT_DUPLICATE, &b, &object, &granted);
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
    handle = wit_handle_grant_object(&p->Handles, WIT_HANDLE_INTERRUPT, rights, object);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    ++p->InterruptReferences[object - 1];
    require(wit_user_copy_to(&p->Space, output, (const WitU8 *)&handle, sizeof(handle)),
        "Validated duplicate output changed");
    return WIT_STATUS_OK;
}

/* A reference a dropped message carried (user_channel.c). */
void wit_user_interrupt_release(WitUserProcess *p, WitU64 object)
{
    release_binding(p, object);
}

int wit_user_interrupt_handle(WitUserProcess *p, WitU64 handle)
{
    return wit_handle_check(&p->Handles, handle, WIT_HANDLE_INTERRUPT, 0) != WIT_STATUS_WRONG_TYPE;
}

/* The component ends: its bindings end, before its devices and events are reset. */
void wit_user_interrupts_reset(WitUserProcess *p)
{
    for (WitU32 i = 0; i < WIT_INTERRUPT_CAPACITY; ++i) {
        p->InterruptReferences[i] = 0;
        if (bindings[i].Live && bindings[i].Owner == wit_user_owner_token(p)) {
            end_binding(p, i + 1);
        }
    }
}

WitU32 wit_user_interrupts_bound(void)
{
    WitU32 count = 0;
    for (WitU32 i = 0; i < WIT_INTERRUPT_CAPACITY; ++i) {
        count += bindings[i].Live != 0;
    }
    return count;
}
