#ifndef WITOS_USER_ARCH_TESTS_H
#define WITOS_USER_ARCH_TESTS_H

#include "user.h"

/* Architecture parts of the shared user-mode tests, supplied by tests/Kernel.<isa>. */

/* A user fault of the shared tests and what the architecture reports for it. */
typedef struct WitUserFaultCase {
    WitU64 Mode; /* WIT_TEST_* or WIT_THREAD_TEST_* fixture mode. */
    const char *Name;
    WitU64 Vector; /* Fault class: the x64 vector or the ARM64 exception class. */
    WitU64 Error; /* The x64 error code or the ARM64 syndrome. */
    WitU64 Address; /* Faulting address, compared when CheckAddress is set. */
    WitU32 CheckAddress;
    WitU32 PcAtAddress; /* An instruction fetch from Address, which may lie outside the fixed image. */
} WitUserFaultCase;

/* Kernel word that the fixture probes; user mode must neither read nor change it. */
extern volatile WitU64 wit_test_kernel_canary;

/* Faults of the isolation fixture, the peer-page fault and the faults of the second thread of the thread
 * fixture. */
extern const WitUserFaultCase wit_test_user_faults[];
extern const WitU32 wit_test_user_fault_count;
extern const WitUserFaultCase wit_test_user_peer_fault;
extern const WitUserFaultCase wit_test_thread_faults[];
extern const WitU32 wit_test_thread_fault_count;

/* Nonzero when the fault was taken in user mode; stores the faulting program counter. */
int wit_test_user_fault_state(const WitArchFaultState *state, WitU64 *pc);

/* Saved frames of the model threads of the wait tests, one per thread index. */
WitArchFrame *wit_test_model_frame(WitU32 index);

/* The component faulted as expected, in user mode, and lost its handles; check_pc also requires the faulting
 * program counter in the fixed image or, for an instruction fetch, at the expected address. */
int wit_test_user_fault_contained(const WitUserProcess *process, const WitUserFaultCase *expected, int check_pc);

#endif
