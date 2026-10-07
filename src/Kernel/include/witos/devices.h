#ifndef WITOS_DEVICES_H
#define WITOS_DEVICES_H
#include "device.h"
#include "memory.h"

struct WitBootInfo;

/* Kernel-internal: the device descriptor table the platform filled at boot, published to components as a read-only
 * memory object, and the ownership of each descriptor (plan step K3.1). The kernel never reads a descriptor's
 * meaning; it hands out the regions a descriptor lists and keeps one owner per device. */
void wit_devices_initialize(const struct WitBootInfo *boot, WitPageAllocator *allocator);
/* The physical page holding the table (WitDeviceTable then the descriptors); zero before initialization. */
WitU64 wit_devices_table_page(void);
WitU32 wit_devices_count(void);
const WitDeviceDescriptor *wit_devices_descriptor(WitU32 index);
/* Acquire takes a free descriptor for the owner token (nonzero) and returns nonzero; release frees it. */
int wit_devices_acquire(WitU32 index, WitU64 owner);
void wit_devices_release(WitU32 index, WitU64 owner);
WitU64 wit_devices_owner(WitU32 index);
#endif
