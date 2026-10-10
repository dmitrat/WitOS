#include "fixture.h"
#include "witos/dma.h"

/* Interrupt and DMA fixture over the devices of ABI-1 (RFC 0011 v3 section 7.7, plan steps K3.2 and K8.3):
 * INTERRUPT_BIND of the virtio block function's line to an event, the binding's rights, uniqueness, acknowledgement and
 * lifetime, and DMA_PIN of an anonymous object for the device with its ranges and refusals. The driver that uses both
 * is the virtio fixture. */

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

FIXTURE_ENTRY void wit_user_start(const WitUserStartup *startup)
{
    const WitUserTestConfig *config = fixture_config(startup);
    fixture_check(startup->Version == WIT_ABI_VERSION, 1);
    const WitU64 table = config->TableHandle;
    WitU64 mapped = 0;
    WitU32 index = 0;
    const volatile WitDeviceDescriptor *block = fixture_find_device(table, FIXTURE_VIRTIO_BLOCK, &mapped, &index);
    const WitU64 device = acquire(table, index, WIT_STATUS_OK);
    if (config->Mode == WIT_INTERRUPT_TEST_BIND) {
        bind_test(table, block, index, device);
    } else if (config->Mode == WIT_INTERRUPT_TEST_DMA) {
        dma_test(device);
    } else {
        fixture_failed(5);
    }
    fixture_expect(WIT_CALL_MEMORY_RELEASE, mapped, 0, 0, WIT_STATUS_OK);
    fixture_close(table);
    fixture_exit(WIT_TEST_EXIT_CODE);
}
