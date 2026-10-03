#ifndef WITOS_SELF_TEST_H
#define WITOS_SELF_TEST_H

#include "witos/boot.h"
#include "witos/memory.h"

/* Entry points of the kernel self-tests, linked only into WITOS_SELFTEST kernels. */
void wit_kernel_self_test(const WitBootInfo *boot, WitPageAllocator *pages);
void wit_arch_fault_self_test(void);
void wit_arch_scheduler_self_test(void);

/* User isolation tests shared by every architecture; the architecture's kernel_tests.c runs its other user
 * suites between the two halves, and the end restores the page count that the beginning returned. */
WitU64 wit_user_isolation_begin_self_test(WitPageAllocator *pages);
void wit_user_isolation_end_self_test(WitPageAllocator *pages, WitU64 before);

#endif
