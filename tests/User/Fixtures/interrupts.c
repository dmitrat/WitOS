#include "fixture.h"
#include "witos/dma.h"
#include "witos/message_interrupt.h"

/* Interrupt and DMA fixture over the devices of ABI-1 (RFC 0011 v3 section 7.7, plan steps K3.2 and K8.3):
 * INTERRUPT_BIND of the virtio block function's line to an event, the binding's rights, uniqueness, acknowledgement and
 * lifetime, INTERRUPT_BIND_MESSAGE's targets where the board has them and its refusal where it has none (H1.2b),
 * DEVICE_PORT's refusals on every board and a read of the i8042's status where the architecture has ports (H5.2), and
 * DMA_PIN of an anonymous object for the device with its ranges and refusals. The driver that uses both is the virtio
 * fixture. */

#define RANGES 8U

static WitU64 acquire(WitU64 table, WitU64 index, WitU64 expected)
{
    return fixture_expect(WIT_CALL_DEVICE_ACQUIRE, table, index, 0, expected);
}

static WitU64 bind(WitU64 device, WitU64 line, WitU64 event, WitU64 expected)
{
    return fixture_expect(WIT_CALL_INTERRUPT_BIND, device, line, event, expected);
}

static void pin_request(WitDmaPinRequest *request, WitU64 device, WitU64 object, WitU64 offset, WitU64 bytes,
    WitU32 capacity, WitDmaRange *ranges)
{
    request->Version = WIT_DMA_PIN_VERSION;
    request->Size = sizeof(*request);
    request->Device = device;
    request->Object = object;
    request->Offset = offset;
    request->Bytes = bytes;
    request->Ranges = (WitU64)ranges;
    request->RangeCapacity = capacity;
    request->Flags = 0;
}

static WitU64 pin(
    WitU64 device, WitU64 object, WitU64 offset, WitU64 bytes, WitU32 capacity, WitDmaRange *ranges, WitU64 expected)
{
    WitDmaPinRequest request;
    pin_request(&request, device, object, offset, bytes, capacity, ranges);
    return fixture_expect(WIT_CALL_DMA_PIN, (WitU64)&request, sizeof(request), 0, expected);
}

static void bind_test(WitU64 table, const volatile WitDeviceDescriptor *block, WitU32 index, WitU64 device)
{
    /* The block function has a line; an auto-reset event with WAIT and SIGNAL receives it. */
    fixture_check(block->LineCount >= 1, 20);
    const WitU64 event = fixture_expect(WIT_CALL_EVENT_CREATE, 0, 0, 0, WIT_STATUS_OK);
    const WitU64 binding = bind(device, 0, event, WIT_STATUS_OK);
    /* One binding per line; a line beyond the descriptor; an event without SIGNAL; a device without BIND. */
    bind(device, 0, event, WIT_STATUS_BUSY);
    bind(device, 5, event, WIT_STATUS_INVALID_ARGUMENT);
    const WitU64 waiter = fixture_duplicate(event, WIT_RIGHT_WAIT, WIT_STATUS_OK);
    bind(device, 0, waiter, WIT_STATUS_DENIED);
    fixture_close(waiter);
    const WitU64 query = fixture_duplicate(device, WIT_RIGHT_QUERY, WIT_STATUS_OK);
    bind(query, 0, event, WIT_STATUS_DENIED);
    fixture_close(query);
    /* Acknowledgement needs ACK and no extra arguments; the line can be acknowledged when nothing is pending. */
    fixture_expect(WIT_CALL_INTERRUPT_ACK, binding, 0, 0, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_INTERRUPT_ACK, binding, 1, 0, WIT_STATUS_INVALID_ARGUMENT);
    const WitU64 reader = fixture_duplicate(binding, WIT_RIGHT_QUERY, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_INTERRUPT_ACK, reader, 0, 0, WIT_STATUS_DENIED);
    fixture_close(reader);
    /* The binding holds the event and the device: both handles can go, the device stays acquired until the binding
     * ends with its last handle. */
    fixture_close(event);
    fixture_close(device);
    acquire(table, index, WIT_STATUS_BUSY);
    fixture_close(binding);
    fixture_close(acquire(table, index, WIT_STATUS_OK));
}

static void message_request(WitMessageBindRequest *request, WitU64 device, WitU64 event)
{
    request->Version = WIT_MESSAGE_BIND_VERSION;
    request->Size = sizeof(*request);
    request->Device = device;
    request->Event = event;
    request->Flags = 0;
    request->Reserved = 0;
    request->Address = 0;
    request->Data = 0;
    request->Reserved1 = 0;
}

static WitU64 bind_message(WitMessageBindRequest *request, WitU64 device, WitU64 event, WitU64 expected)
{
    message_request(request, device, event);
    return fixture_expect(WIT_CALL_INTERRUPT_BIND_MESSAGE, (WitU64)request, sizeof(*request), 0, expected);
}

static void message_test(WitU64 device)
{
    WitMessageBindRequest request;
    const WitU64 features = fixture_expect(WIT_CALL_QUERY, 0, 0, 0, WIT_STATUS_OK) >> 32;
    const WitU64 event = fixture_expect(WIT_CALL_EVENT_CREATE, 0, 0, 0, WIT_STATUS_OK);
    if (!(features & WIT_ABI_FEATURE_MESSAGE_INTERRUPTS)) {
        /* A board without message interrupts refuses the call whole. */
        bind_message(&request, device, event, WIT_STATUS_UNSUPPORTED);
        fixture_close(event);
        return;
    }
    /* Refusals: a foreign version, a wrong size, flags, an event without SIGNAL, a device without BIND. */
    message_request(&request, device, event);
    request.Version = 2;
    fixture_expect(WIT_CALL_INTERRUPT_BIND_MESSAGE, (WitU64)&request, sizeof(request), 0, WIT_STATUS_UNSUPPORTED);
    request.Version = WIT_MESSAGE_BIND_VERSION;
    fixture_expect(
        WIT_CALL_INTERRUPT_BIND_MESSAGE, (WitU64)&request, sizeof(request) - 8, 0, WIT_STATUS_INVALID_ARGUMENT);
    request.Flags = 1;
    fixture_expect(WIT_CALL_INTERRUPT_BIND_MESSAGE, (WitU64)&request, sizeof(request), 0, WIT_STATUS_INVALID_ARGUMENT);
    const WitU64 waiter = fixture_duplicate(event, WIT_RIGHT_WAIT, WIT_STATUS_OK);
    bind_message(&request, device, waiter, WIT_STATUS_DENIED);
    fixture_close(waiter);
    const WitU64 query = fixture_duplicate(device, WIT_RIGHT_QUERY, WIT_STATUS_OK);
    bind_message(&request, query, event, WIT_STATUS_DENIED);
    fixture_close(query);
    /* Two targets: an address and data written into each request, and two different messages. */
    WitMessageBindRequest second;
    const WitU64 first_binding = bind_message(&request, device, event, WIT_STATUS_OK);
    fixture_check(request.Address != 0 && request.Data != 0 && request.Reserved1 == 0, 30);
    const WitU64 second_binding = bind_message(&second, device, event, WIT_STATUS_OK);
    fixture_check(second.Address != 0 && second.Data != request.Data, 31);
    /* A message is an edge: an acknowledgement succeeds and does nothing; the bindings end with their handles. */
    fixture_expect(WIT_CALL_INTERRUPT_ACK, first_binding, 0, 0, WIT_STATUS_OK);
    fixture_close(first_binding);
    fixture_close(second_binding);
    fixture_close(event);
}

static WitU64 port(
    WitU64 device, WitU32 region, WitU32 operation, WitU32 width, WitU32 offset, WitU32 value, WitU64 expected)
{
    WitDevicePortRequest request = {
        WIT_DEVICE_PORT_VERSION, sizeof(request), region, operation, width, offset, value, 0};
    return fixture_expect(WIT_CALL_DEVICE_PORT, device, (WitU64)&request, sizeof(request), expected);
}

/* DEVICE_PORT (ABI-1 1.7, H5.2): on every board a memory region is refused, so are a region beyond the descriptor and a
 * malformed request, and a device without BIND; where the architecture has a port space, QUERY says so and the board's
 * i8042, when it describes one, answers a read of its status port, refuses an access beyond its one byte, and binds its
 * keyboard's edge line. */
static void port_test(WitU64 table, WitU64 mapped, WitU64 block)
{
    WitDevicePortRequest request = {WIT_DEVICE_PORT_VERSION, sizeof(request), 0, WIT_DEVICE_PORT_READ, 1, 0, 0, 0};
    const WitU64 features = fixture_expect(WIT_CALL_QUERY, 0, 0, 0, WIT_STATUS_OK) >> 32;
    port(block, 0, WIT_DEVICE_PORT_READ, 1, 0, 0, WIT_STATUS_UNSUPPORTED); /* the configuration page is memory */
    port(block, WIT_DEVICE_REGIONS, WIT_DEVICE_PORT_READ, 1, 0, 0, WIT_STATUS_INVALID_ARGUMENT);
    port(block, 0, WIT_DEVICE_PORT_READ, 3, 0, 0, WIT_STATUS_INVALID_ARGUMENT);
    port(block, 0, WIT_DEVICE_PORT_READ, 1, 0, 1, WIT_STATUS_INVALID_ARGUMENT);
    port(block, 0, 3, 1, 0, 0, WIT_STATUS_INVALID_ARGUMENT);
    request.Version = 2;
    fixture_expect(WIT_CALL_DEVICE_PORT, block, (WitU64)&request, sizeof(request), WIT_STATUS_UNSUPPORTED);
    request.Version = WIT_DEVICE_PORT_VERSION;
    fixture_expect(WIT_CALL_DEVICE_PORT, block, (WitU64)&request, sizeof(request) - 8, WIT_STATUS_INVALID_ARGUMENT);
    const WitU64 query = fixture_duplicate(block, WIT_RIGHT_QUERY, WIT_STATUS_OK);
    port(query, 0, WIT_DEVICE_PORT_READ, 1, 0, 0, WIT_STATUS_DENIED);
    fixture_close(query);
    const volatile WitDeviceTable *header = (const volatile WitDeviceTable *)mapped;
    const volatile WitDeviceDescriptor *descriptors =
        (const volatile WitDeviceDescriptor *)(mapped + WIT_DEVICE_TABLE_SIZE);
    WitU32 i8042 = header->Count;
    for (WitU32 i = 0; i < header->Count; ++i) {
        if (descriptors[i].Bus == WIT_DEVICE_BUS_LEGACY && descriptors[i].Identity[0] == WIT_LEGACY_DEVICE_I8042) {
            i8042 = i;
        }
    }
    if (!(features & WIT_ABI_FEATURE_DEVICE_PORTS)) {
        fixture_check(i8042 == header->Count, 40); /* no board without ports describes a port device */
        return;
    }
    if (i8042 == header->Count) {
        return;
    }
    fixture_check(descriptors[i8042].RegionCount == 2 &&
            descriptors[i8042].Regions[1].Base == 0x64 &&
            (descriptors[i8042].Regions[1].Flags & WIT_DEVICE_REGION_PORT) &&
            descriptors[i8042].Lines[0].Kind == WIT_DEVICE_LINE_EDGE &&
            descriptors[i8042].Lines[0].Line == 1,
        41);
    const WitU64 controller = acquire(table, i8042, WIT_STATUS_OK);
    port(controller, 1, WIT_DEVICE_PORT_READ, 1, 0, 0, WIT_STATUS_OK); /* the status register, whatever it holds */
    port(controller, 1, WIT_DEVICE_PORT_READ, 1, 1, 0, WIT_STATUS_INVALID_ARGUMENT);
    port(controller, 1, WIT_DEVICE_PORT_READ, 2, 0, 0, WIT_STATUS_INVALID_ARGUMENT);
    port(controller, 0, WIT_DEVICE_PORT_WRITE, 1, 0, 0x100, WIT_STATUS_INVALID_ARGUMENT);
    /* The keyboard's edge line binds as a level line does, once; the binding's end masks it again. */
    const WitU64 event = fixture_expect(WIT_CALL_EVENT_CREATE, 0, 0, 0, WIT_STATUS_OK);
    const WitU64 keyboard = bind(controller, 0, event, WIT_STATUS_OK);
    bind(controller, 0, event, WIT_STATUS_BUSY);
    fixture_close(keyboard);
    fixture_close(event);
    fixture_close(controller);
}

static void dma_test(WitU64 device)
{
    WitDmaRange ranges[RANGES];
    WitDmaPinRequest request;
    /* Two pages of an anonymous object pinned for the device: the ranges cover them, page-aligned, and end with a zero
     * entry. */
    const WitU64 object = fixture_expect(WIT_CALL_MEMORY_OBJECT_CREATE, 8192, 0, 0, WIT_STATUS_OK);
    const WitU64 first = pin(device, object, 0, 8192, RANGES, ranges, WIT_STATUS_OK);
    fixture_check(ranges[0].Size + ranges[1].Size == 8192, 21); /* the second is zero when the pages are contiguous */
    fixture_check(ranges[0].Address != 0 && !(ranges[0].Address & 4095), 22);
    fixture_check(ranges[2].Size == 0, 23);
    /* Refusals: a window beyond the object, no capacity, a foreign version, a wrong size, a device region as the object,
     * a device without BIND, an object without MAP. */
    pin(device, object, 0, 12288, RANGES, ranges, WIT_STATUS_TOO_LARGE);
    pin(device, object, 4096, 8192, RANGES, ranges, WIT_STATUS_TOO_LARGE);
    pin(device, object, 0, 4096, 0, ranges, WIT_STATUS_INVALID_ARGUMENT);
    pin_request(&request, device, object, 0, 4096, RANGES, ranges);
    request.Version = 2;
    fixture_expect(WIT_CALL_DMA_PIN, (WitU64)&request, sizeof(request), 0, WIT_STATUS_UNSUPPORTED);
    request.Version = WIT_DMA_PIN_VERSION;
    fixture_expect(WIT_CALL_DMA_PIN, (WitU64)&request, sizeof(request) - 1, 0, WIT_STATUS_INVALID_ARGUMENT);
    const WitU64 region = fixture_expect(WIT_CALL_DEVICE_MEMORY, device, 0, 0, WIT_STATUS_OK);
    pin(device, region, 0, 4096, RANGES, ranges, WIT_STATUS_INVALID_ARGUMENT);
    fixture_close(region);
    const WitU64 query = fixture_duplicate(device, WIT_RIGHT_QUERY, WIT_STATUS_OK);
    pin(query, object, 0, 4096, RANGES, ranges, WIT_STATUS_DENIED);
    fixture_close(query);
    const WitU64 reader = fixture_duplicate(object, WIT_RIGHT_QUERY, WIT_STATUS_OK);
    pin(device, reader, 0, 4096, RANGES, ranges, WIT_STATUS_DENIED);
    fixture_close(reader);
    /* The pin ends with its handle; a second pin of the same pages is fine while the first lives. */
    const WitU64 second = pin(device, object, 4096, 4096, RANGES, ranges, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_DMA_UNPIN, first, 0, 0, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_DMA_UNPIN, first, 0, 0, WIT_STATUS_BAD_HANDLE);
    fixture_close(object); /* the object lives on by the second pin */
    fixture_expect(WIT_CALL_DMA_UNPIN, second, 0, 0, WIT_STATUS_OK);
    fixture_close(device);
}

FIXTURE_ENTRY void wit_user_start(const WitRootStartup *startup)
{
    const WitUserTestConfig *config = fixture_config(startup);
    fixture_check(startup->AbiVersion == WIT_ABI_VERSION, 1);
    const WitU64 table = config->TableHandle;
    WitU64 mapped = 0;
    WitU32 index = 0;
    const volatile WitDeviceDescriptor *block = fixture_find_device(table, FIXTURE_VIRTIO_BLOCK, &mapped, &index);
    const WitU64 device = acquire(table, index, WIT_STATUS_OK);
    if (config->Mode == WIT_INTERRUPT_TEST_BIND) {
        bind_test(table, block, index, device);
        const WitU64 again = acquire(table, index, WIT_STATUS_OK);
        message_test(again);
        port_test(table, mapped, again);
        fixture_close(again);
    } else if (config->Mode == WIT_INTERRUPT_TEST_DMA) {
        dma_test(device);
    } else {
        fixture_failed(5);
    }
    fixture_expect(WIT_CALL_MEMORY_RELEASE, mapped, 0, 0, WIT_STATUS_OK);
    fixture_close(table);
    fixture_exit(WIT_TEST_EXIT_CODE);
}
