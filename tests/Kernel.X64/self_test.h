#ifndef WITOS_SELF_TEST_H
#define WITOS_SELF_TEST_H

#include "witos/boot.h"
#include "witos/memory.h"

/* Entry points of the kernel self-tests, linked only into WITOS_SELFTEST kernels. */
void wit_kernel_self_test(const WitBootInfo *boot, WitPageAllocator *pages);
void wit_arch_fault_self_test(void);
void wit_arch_scheduler_self_test(void);
void wit_user_self_test(WitPageAllocator *pages);

#endif
