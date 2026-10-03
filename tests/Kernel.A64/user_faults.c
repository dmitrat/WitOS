#include "witos/arch.h"
#include "witos/arch_types.h"
#include "a64.h"
#include "protocol.h"
#include "user_faults.h"

/* ARM64 expectations of the shared isolation tests: the exception class (ESR_EL1.EC) and the whole syndrome.
 * Data aborts from EL0 are class 0x24 and instruction aborts 0x20; the fault status names a translation fault
 * (0b0001LL) or a permission fault (0b0011LL) at lookup level LL, and WnR (bit 6) marks a write. The kernel
 * branch carries EL0 no-access at level 0, so kernel addresses give permission faults. Privileged register
 * writes trap as class 0x18, undefined instructions are class 0. The pinned QEMU reports Op1 and Op2 of the
 * trapped MSR DAIFSet swapped: 0x620793E4, where the architecture gives 0x620CD3E4 (Op1 3, Op2 6). */

#define DATA 0x24U
#define INSTRUCTION 0x20U
#define UNDEFINED 0x00U
#define SYSTEM 0x18U

const WitUserFaultCase wit_test_user_faults[] = {
    {WIT_TEST_KERNEL_READ, "User.KernelRead", DATA, 0x9200000FU, (WitU64)&wit_test_kernel_canary, 1, 0},
    {WIT_TEST_KERNEL_WRITE, "User.KernelWrite", DATA, 0x9200004FU, (WitU64)&wit_test_kernel_canary, 1, 0},
    {WIT_TEST_PRIVILEGED_CLI, "User.PrivilegedMask", SYSTEM, 0x620793E4U, 0, 0, 0},
    {WIT_TEST_PRIVILEGED_PORT, "User.PrivilegedRegister", UNDEFINED, 0x02000000U, 0, 0, 0},
    {WIT_TEST_EXECUTE_DATA, "User.Nx", INSTRUCTION, 0x8200000FU, WIT_USER_DATA, 1, 0},
    {WIT_TEST_GUARD_LOW, "User.GuardLow", DATA, 0x92000047U, WIT_USER_STACK_BOTTOM - 1, 1, 0},
    {WIT_TEST_GUARD_HIGH, "User.GuardHigh", DATA, 0x92000047U, WIT_USER_STACK_TOP, 1, 0},
    {WIT_TEST_WRITE_CODE, "User.WriteCode", DATA, 0x9200004FU, WIT_USER_CODE, 1, 0},
    {WIT_TEST_WRITE_INFO, "User.WriteInfo", DATA, 0x9200004FU, WIT_USER_INFO, 1, 0},
    {WIT_TEST_NULL_READ, "User.NullRead", DATA, 0x92000006U, 0, 1, 0},
    {WIT_TEST_INVALID_OPCODE, "User.InvalidOpcode", UNDEFINED, 0x02000000U, 0, 0, 0},
    {WIT_TEST_MEMORY_RESERVED, "User.MemoryReservedFault", DATA, 0x92000004U, WIT_USER_MEMORY_BASE, 1, 0},
    {WIT_TEST_MEMORY_DECOMMITTED, "User.MemoryDecommittedFault", DATA, 0x92000004U, WIT_USER_MEMORY_BASE, 1, 0},
    {WIT_TEST_MEMORY_RELEASED, "User.MemoryReleasedFault", DATA, 0x92000004U, WIT_USER_MEMORY_BASE, 1, 0},
    {WIT_TEST_MEMORY_READONLY, "User.MemoryReadOnlyFault", DATA, 0x9200004FU, WIT_USER_MEMORY_BASE, 1, 0},
    {WIT_TEST_MEMORY_NOACCESS, "User.MemoryNoAccessFault", DATA, 0x92000007U, WIT_USER_MEMORY_BASE, 1, 0},
    {WIT_TEST_MEMORY_NX, "User.MemoryNxFault", INSTRUCTION, 0x8200000FU, WIT_USER_MEMORY_BASE, 1, 1}};
const WitU32 wit_test_user_fault_count = sizeof(wit_test_user_faults) / sizeof(wit_test_user_faults[0]);
const WitUserFaultCase wit_test_user_peer_fault = {
    WIT_TEST_PEER_READ, "User.PeerMemory", DATA, 0x92000007U, WIT_USER_PEER_PAGE, 1, 0};

int wit_test_user_fault_state(const WitArchFaultState *state, WitU64 *pc)
{
    *pc = state->Elr;
    return (state->Spsr & WIT_A64_SPSR_MODE) == 0;
}
