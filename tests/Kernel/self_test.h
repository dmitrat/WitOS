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

/* Processes (K5.2c): a component creates, places, starts, waits for, queries and kills created processes. */
void wit_user_process_self_test(WitPageAllocator *pages);

/* Whether a component ended in a contained fault; a fault a test accepts this way counts toward the summary. */
struct WitUserProcess;
int wit_test_faulted(const struct WitUserProcess *process);

/* Last line of the self-tests: "[TEST-SUMMARY] faults=<contained> checked=<accepted>"; the host requires both to
 * equal the number of [USER-FAULT] lines, so no test suite needs a hand-counted fault total. */
void wit_test_summary(void);

#endif
