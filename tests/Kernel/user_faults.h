#ifndef WITOS_USER_FAULTS_H
#define WITOS_USER_FAULTS_H

#include "user.h"

/* User faults of the shared isolation tests and what the architecture reports for each. */
typedef struct WitUserFaultCase {
    WitU64 Mode; /* WIT_TEST_* fixture mode. */
    const char *Name;
    WitU64 Vector; /* Fault class: the x64 vector or the ARM64 exception class. */
    WitU64 Error; /* The x64 error code or the ARM64 syndrome. */
    WitU64 Address; /* Faulting address, compared when CheckAddress is set. */
    WitU32 CheckAddress;
    WitU32 PcAtAddress; /* An instruction fetch from Address, which may lie outside the fixed image. */
} WitUserFaultCase;

/* Kernel word that the fixture probes; user mode must neither read nor change it. */
extern volatile WitU64 wit_test_kernel_canary;

/* Supplied by tests/Kernel.<isa>. */
extern const WitUserFaultCase wit_test_user_faults[];
extern const WitU32 wit_test_user_fault_count;
extern const WitUserFaultCase wit_test_user_peer_fault;

/* Nonzero when the fault was taken in user mode; stores the faulting program counter. */
int wit_test_user_fault_state(const WitArchFaultState *state, WitU64 *pc);

#endif
