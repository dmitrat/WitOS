#include "x64.h"
#include "protocol.h"
#include "user_arch_tests.h"

/* x64 parts of the shared user-mode tests: page faults (vector 14) with their error code and CR2, general
 * protection (13) and invalid opcode (6). */

const WitUserFaultCase wit_test_user_faults[] = {
    {WIT_TEST_KERNEL_READ, "User.KernelRead", 14, 5, (WitU64)&wit_test_kernel_canary, 1, 0},
    {WIT_TEST_KERNEL_WRITE, "User.KernelWrite", 14, 7, (WitU64)&wit_test_kernel_canary, 1, 0},
    {WIT_TEST_PRIVILEGED_CLI, "User.PrivilegedCli", 13, 0, 0, 0, 0},
    {WIT_TEST_PRIVILEGED_PORT, "User.PrivilegedPort", 13, 0, 0, 0, 0},
    {WIT_TEST_EXECUTE_DATA, "User.Nx", 14, 21, WIT_USER_DATA, 1, 0},
    {WIT_TEST_GUARD_LOW, "User.GuardLow", 14, 6, WIT_USER_STACK_BOTTOM - 1, 1, 0},
    {WIT_TEST_GUARD_HIGH, "User.GuardHigh", 14, 6, WIT_USER_STACK_TOP, 1, 0},
    {WIT_TEST_WRITE_CODE, "User.WriteCode", 14, 7, WIT_USER_CODE, 1, 0},
    {WIT_TEST_WRITE_INFO, "User.WriteInfo", 14, 7, WIT_USER_INFO, 1, 0},
    {WIT_TEST_NULL_READ, "User.NullRead", 14, 4, 0, 1, 0},
    {WIT_TEST_INVALID_OPCODE, "User.InvalidOpcode", 6, 0, 0, 0, 0},
    {WIT_TEST_MEMORY_RESERVED, "User.MemoryReservedFault", 14, 4, WIT_USER_MEMORY_BASE, 1, 0},
    {WIT_TEST_MEMORY_DECOMMITTED, "User.MemoryDecommittedFault", 14, 4, WIT_USER_MEMORY_BASE, 1, 0},
    {WIT_TEST_MEMORY_RELEASED, "User.MemoryReleasedFault", 14, 4, WIT_USER_MEMORY_BASE, 1, 0},
    {WIT_TEST_MEMORY_READONLY, "User.MemoryReadOnlyFault", 14, 7, WIT_USER_MEMORY_BASE, 1, 0},
    {WIT_TEST_MEMORY_NOACCESS, "User.MemoryNoAccessFault", 14, 4, WIT_USER_MEMORY_BASE, 1, 0},
    {WIT_TEST_MEMORY_NX, "User.MemoryNxFault", 14, 21, WIT_USER_MEMORY_BASE, 1, 1}};
const WitU32 wit_test_user_fault_count = sizeof(wit_test_user_faults) / sizeof(wit_test_user_faults[0]);
const WitUserFaultCase wit_test_user_peer_fault = {
    WIT_TEST_PEER_READ, "User.PeerMemory", 14, 4, WIT_USER_PEER_PAGE, 1, 0};

const WitUserFaultCase wit_test_thread_faults[] = {{WIT_THREAD_TEST_FAULT, "User.ThreadFault", 6, 0, 0, 0, 0},
    {WIT_THREAD_TEST_GUARD_LOW, "User.ThreadGuardLow", 14, 6, WIT_USER_STACK_BOTTOM + WIT_USER_THREAD_STRIDE - 1, 1, 0},
    {WIT_THREAD_TEST_GUARD_HIGH, "User.ThreadGuardHigh", 14, 6, WIT_USER_STACK_TOP + WIT_USER_THREAD_STRIDE, 1, 0}};
const WitU32 wit_test_thread_fault_count = sizeof(wit_test_thread_faults) / sizeof(wit_test_thread_faults[0]);
const WitUserFaultCase wit_test_exception_fault = {WIT_EXCEPTION_TEST_REJECT, "User.ExceptionReject", 14, 4, 0, 1, 0};

/* x64 validates x64 unwind metadata, so a malformed exception directory is invalid, not unsupported. */
const WitU16 wit_test_foreign_machine = 0xAA64;
const WitPeStatus wit_test_exception_directory_status = WitPeInvalidImage;
const WitUserFaultCase wit_test_image_faults[3] = {
    {0, "write", 14, 7, 0, 0, 0}, {0, "execute", 14, 21, 0, 0, 0}, {0, "read", 14, 4, 0, 0, 0}};

static WitInterruptContext model_frames[WIT_USER_THREAD_CAPACITY];

WitArchFrame *wit_test_model_frame(WitU32 index)
{
    return &model_frames[index];
}

int wit_test_user_fault_state(const WitArchFaultState *state, WitU64 *pc)
{
    *pc = state->Rip;
    return state->Cs == WIT_USER_CS && state->Ss == WIT_USER_SS;
}
