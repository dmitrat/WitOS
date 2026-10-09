#ifndef WITOS_VIRTUAL_H
#define WITOS_VIRTUAL_H

#include "memory.h"

/* Internal, single-address-space bootstrap API. Scratch mappings are NX. */
#define WIT_VM_SCRATCH_BASE 0x0000008000000000ULL
#define WIT_VM_SCRATCH_SIZE 0x1000000ULL

const WitU8 *wit_virtual_boot_storage(void);
/* The kernel's view of a physical page a user mapping shows (S6.2): usable memory through the identity map, a page of
 * the boot package through the storage window; zero for anything the kernel does not map, a device region among them,
 * which the kernel then never copies from or to. */
WitU8 *wit_virtual_view(WitU64 physical);
/* The root task image the boot contract carries, mapped read-only beside the package; zero without one (K4). */
const WitU8 *wit_virtual_root_task(void);
void wit_virtual_initialize(const WitBootInfo *boot, WitPageAllocator *allocator);
int wit_virtual_map(WitU64 virtual_address, WitU64 physical_address, int writable);
int wit_virtual_protect(WitU64 virtual_address, int writable);
int wit_virtual_unmap(WitU64 virtual_address);
void wit_virtual_self_test(WitPageAllocator *allocator);
void wit_virtual_fault_test(void);

#endif
