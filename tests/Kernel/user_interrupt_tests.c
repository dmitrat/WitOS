#include "user.h"
#include "witos/devices.h"
#include "witos/platform.h"
#include "protocol.h"
#include "self_test.h"
#include "user_interrupt_image.h"
#include "user_virtio_image.h"

/* Interrupts and DMA (RFC 0011 section 7.7, plan step K3.2) from user mode on both ISAs. The fixture binds the
 * virtio block function's line to an event and checks the binding's rights, uniqueness, acknowledgement and
 * lifetime; pins an anonymous object for the device and checks the ranges and the object's lifetime under the pin;
 * and drives the device as a virtio-blk driver over the modern PCI transport: a block read into pinned memory
 * completes with the bound interrupt, and the sector read is the FAT boot sector of the boot disk. */

static WitUserProcess process;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void create(WitPageAllocator *pages, WitU64 mode)
{
    WitUserTestConfig *info;
    const int driver = mode == WIT_INTERRUPT_TEST_VIRTIO;
    require(wit_test_create_fixture(&process, pages, 0, driver ? wit_user_virtio_image : wit_user_interrupt_image,
                driver ? sizeof(wit_user_virtio_image) : sizeof(wit_user_interrupt_image)),
        "Interrupt test process creation failed");
    if (driver) {
        /* The driver waits for the device; the host's block I/O takes wall-clock time the budget must cover. */
        process.TickLimit = WIT_RUNTIME_TICK_BUDGET;
    }
    info = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    info->Mode = mode;
    info->TableHandle = wit_user_device_table_grant(
        &process, WIT_RIGHT_MAP | WIT_RIGHT_QUERY | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER | WIT_RIGHT_ACQUIRE);
    require(info->TableHandle != 0, "Device table handle grant failed");
}

void wit_user_interrupt_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    static const char *names[] = {"InterruptBind", "DmaPin", "VirtioBlock"};
    require(wit_devices_count() >= 1, "The board published no device: the fixture needs the virtio block function");
    for (WitU64 mode = 0; mode < sizeof(names) / sizeof(names[0]); ++mode) {
        create(pages, mode);
        const WitU32 owned = process.Space.OwnedCount;
        wit_user_run(&process);
        if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("Interrupt test mode/state/code: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write(" last status: "); /* The fixture records the status its failed check saw. */
            wit_console_write_u64(*(const WitU64 *)wit_user_space_physical(&process.Space, WIT_USER_DATA + 1304, 0, 0));
            wit_console_write(" step: ");
            wit_console_write_u64(*(const WitU64 *)wit_user_space_physical(&process.Space, WIT_USER_DATA + 1312, 0, 0));
            wit_console_write("\n");
            wit_panic("User interrupt test failed");
        }
        if (mode == WIT_INTERRUPT_TEST_VIRTIO) {
            wit_console_write("Interrupts delivered: ");
            wit_console_write_u64(process.InterruptsDelivered);
            wit_console_write("\n");
            require(process.InterruptsDelivered >= 1, "The block read completed without the bound interrupt");
        }
        /* Every binding, pin, device, object and mapping ended with the component's last handle, and the component
         * owns the pages it owned before. */
        require(process.Handles.Count == 0 &&
                wit_memory_objects_live() == 0 &&
                process.Events.Count == 0 &&
                wit_channels_live() == 0 &&
                process.Space.AliasCount == 0 &&
                process.Space.OwnedCount == owned,
            "Interrupt scenario left objects, mappings or pages behind");
        require(wit_user_interrupts_bound() == 0, "A binding survived the scenario");
        for (WitU32 i = 0; i < WIT_DEVICE_CAPACITY; ++i) {
            require(
                !process.DeviceReferences[i] && !wit_devices_owner(i), "A device stayed acquired after the scenario");
        }
        for (WitU32 i = 0; i < WIT_PIN_CAPACITY; ++i) {
            require(!process.Pins[i].Live, "A pin survived the scenario");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Interrupt scenario leaked physical pages");
        wit_console_write("[TEST-PASS] User.");
        wit_console_write(names[mode]);
        wit_console_write("\n");
    }
}
