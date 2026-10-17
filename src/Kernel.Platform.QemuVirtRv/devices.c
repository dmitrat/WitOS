#include "witos/boot.h"
#include "witos/device.h"
#include "witos/platform.h"
#include "virt_rv.h"

/* QEMU virt (riscv64) device enumeration (plan steps V1.2 and V1.3): the PCI functions behind the board's generic ECAM
 * bridge, with their INTx lines as PLIC sources from the device tree's interrupt map (pci.c), then the firmware's
 * framebuffer of the boot contract (C2.1). */

WitU32 wit_platform_devices(const WitBootInfo *boot, WitDeviceDescriptor *table, WitU32 capacity)
{
    WitU32 count = wit_virt_rv_pci_devices(boot, table, capacity);
    if (count < capacity && wit_device_describe_framebuffer(boot, &table[count])) {
        ++count;
    }
    return count;
}
