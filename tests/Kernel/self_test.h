#ifndef WITOS_SELF_TEST_H
#define WITOS_SELF_TEST_H

#include "witos/boot.h"
#include "witos/memory.h"

/* Entry points of the kernel self-tests, linked only into WITOS_SELFTEST kernels. */
void wit_kernel_self_test(const WitBootInfo *boot, WitPageAllocator *pages);
void wit_arch_fault_self_test(void);
void wit_arch_scheduler_self_test(void);
/* The UTC clock (K6): plausible at boot, monotonic, settable within its range. */
void wit_clock_self_test(void);
/* The processor table (K7.1): the boot processor first and online, the others present. */
void wit_processor_self_test(void);
/* The processors as the kernel runs them (K7.2): every present one online, fences and invalidations acknowledged. */
void wit_smp_self_test(void);

/* User isolation tests shared by every architecture; the architecture's kernel_tests.c runs its other user
 * suites between the two halves, and the end restores the page count that the beginning returned. */
WitU64 wit_user_isolation_begin_self_test(WitPageAllocator *pages);
void wit_user_isolation_end_self_test(WitPageAllocator *pages, WitU64 before);

/* Processes (K5.2c): a component creates, places, starts, waits for, queries and kills created processes. */
void wit_user_process_self_test(WitPageAllocator *pages);

/* A mechanism fixture from its flat image (K8.4c): validated whole, created with the fixture profile, its startup
 * descriptor holding the kernel log; zero when the image or the creation fails. */
struct WitUserProcess;
int wit_test_create_fixture(
    struct WitUserProcess *process, WitPageAllocator *pages, WitU32 slot, const WitU8 *image, WitU32 size);

/* Whether a component ended in a contained fault; a fault a test accepts this way counts toward the summary. */
int wit_test_faulted(const struct WitUserProcess *process);

/* Last line of the self-tests: "[TEST-SUMMARY] faults=<contained> checked=<accepted>"; the host requires both to
 * equal the number of [USER-FAULT] lines, so no test suite needs a hand-counted fault total. */
void wit_test_summary(void);

#endif
