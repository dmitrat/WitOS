#include "user.h"
#include "witos/devices.h"
#include "witos/platform.h"
#include "protocol.h"
#include "self_test.h"
#include "user_device_image.h"

/* Devices (RFC 0011 section 7.7, plan step K3.1) from user mode on both ISAs. The fixture maps the device table the
 * kernel handed it, finds the board's virtio block function by the identity the bus reports, acquires it, maps its
 * configuration region through DEVICE_MEMORY and reads the same identity through the device; then it checks the
 * authority: a table handle without ACQUIRE, a second acquisition, a region out of range, a port region, an
 * executable or writable mapping the handle's rights refuse, a device moved through a channel, and the descriptor
 * free again after the last handle. */

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
    require(wit_test_create_fixture(&process, pages, 0, wit_user_device_image, sizeof(wit_user_device_image)),
        "Device test process creation failed");
    info = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    info->Mode = mode;
    info->TableHandle = wit_user_device_table_grant(
        &process, WIT_RIGHT_MAP | WIT_RIGHT_QUERY | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER | WIT_RIGHT_ACQUIRE);
    require(info->TableHandle != 0, "Device table handle grant failed");
}

void wit_user_device_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    static const char *names[] = {"DeviceTable", "DeviceAuthority"};
    const WitU32 count = wit_devices_count();
    wit_console_write("Device descriptors: ");
    wit_console_write_u64(count);
    wit_console_write("\n");
    for (WitU32 i = 0; i < count; ++i) {
        const WitDeviceDescriptor *d = wit_devices_descriptor(i);
        wit_console_write("  device bus/address/identity/regions/lines: ");
        wit_console_write_u64(d->Bus);
        wit_console_write("/");
        wit_console_write_hex(d->Address);
        wit_console_write("/");
        wit_console_write_hex(d->Identity[0]);
        wit_console_write("/");
        wit_console_write_u64(d->RegionCount);
        wit_console_write("/");
        wit_console_write_u64(d->LineCount);
        wit_console_write("\n");
    }
    require(count >= 1, "The board published no device: the fixture needs the virtio block function");
    for (WitU64 mode = 0; mode < sizeof(names) / sizeof(names[0]); ++mode) {
        create(pages, mode);
        const WitU32 owned = process.Space.OwnedCount;
        wit_user_run(&process);
        if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("Device test mode/state/code: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write(" last status: "); /* The fixture records the status its failed check saw. */
            wit_console_write_u64(*(const WitU64 *)wit_user_space_physical(&process.Space, WIT_USER_DATA + 1304, 0, 0));
            wit_console_write("\n");
            wit_panic("User device test failed");
        }
        /* Every device went back with the component's last handle, every region object and mapping ended, and the
         * component owns the pages it owned before: the table's and the device's pages were never its own. */
        require(process.Handles.Count == 0 &&
                wit_memory_objects_live() == 0 &&
                wit_channels_live() == 0 &&
                process.Space.AliasCount == 0 &&
                process.Space.OwnedCount == owned,
            "Device scenario left objects, mappings or pages behind");
        for (WitU32 i = 0; i < WIT_DEVICE_CAPACITY; ++i) {
            require(
                !process.DeviceReferences[i] && !wit_devices_owner(i), "A device stayed acquired after the scenario");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Device scenario leaked physical pages");
        wit_console_write("[TEST-PASS] User.");
        wit_console_write(names[mode]);
        wit_console_write("\n");
    }
}
