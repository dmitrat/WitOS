#include "witos/boot.h"
#include "witos/device.h"
#include "witos/platform.h"

/* QEMU virt (riscv64) device enumeration (plan step V1.2): the firmware's framebuffer of the boot contract alone. The
 * PCI functions behind the board's generic ECAM bridge, whose interrupt map names PLIC sources where the ARM64 board's
 * names GIC interrupts, come with the user space the drivers need (plan step V1.3); until then no bus is enumerated,
 * and the table says so rather than pretending. */

WitU32 wit_platform_devices(const WitBootInfo *boot, WitDeviceDescriptor *table, WitU32 capacity)
{
    WitU32 count = 0;
    wit_console_write("[DEVICES] No bus enumerated on riscv64 (plan step V1.3)\n");
    if (count < capacity && wit_device_describe_framebuffer(boot, &table[count])) {
        ++count;
    }
    return count;
}
