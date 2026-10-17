#include "witos/arch.h"
#include "witos/arch_types.h"
#include "rv64.h"
#include "protocol.h"
#include "user_arch_tests.h"

/* RISC-V parts of the shared user-mode tests (plan step V1.3). Faults are given by the exception cause (scause, which
 * the record carries as both the vector and the error) and the trap value. A load from a page U-mode may not reach is
 * a load page fault (13), a store a store page fault (15), an instruction fetch an instruction page fault (12): kernel
 * pages lack the U bit, unmapped and no-access pages are invalid, a read-only page lacks W and a data page lacks X.
 * A CSR write or read of a supervisor register and an undefined instruction are illegal instructions (2). The trap
 * value of a page fault is the faulting address; of an illegal instruction the instruction's bits, which no case
 * compares. */

#define ILLEGAL 2U
#define INSTRUCTION_PAGE 12U
#define LOAD_PAGE 13U
#define STORE_PAGE 15U

const WitUserFaultCase wit_test_user_faults[] = {
    {WIT_TEST_KERNEL_READ, "User.KernelRead", LOAD_PAGE, LOAD_PAGE, (WitU64)&wit_test_kernel_canary, 1, 0},
    {WIT_TEST_KERNEL_WRITE, "User.KernelWrite", STORE_PAGE, STORE_PAGE, (WitU64)&wit_test_kernel_canary, 1, 0},
    {WIT_TEST_PRIVILEGED_CLI, "User.PrivilegedMask", ILLEGAL, ILLEGAL, 0, 0, 0},
    {WIT_TEST_PRIVILEGED_PORT, "User.PrivilegedRegister", ILLEGAL, ILLEGAL, 0, 0, 0},
    {WIT_TEST_EXECUTE_DATA, "User.Nx", INSTRUCTION_PAGE, INSTRUCTION_PAGE, WIT_USER_DATA, 1, 0},
    {WIT_TEST_GUARD_LOW, "User.GuardLow", STORE_PAGE, STORE_PAGE, WIT_USER_STACK_BOTTOM - 1, 1, 0},
    {WIT_TEST_GUARD_HIGH, "User.GuardHigh", STORE_PAGE, STORE_PAGE, WIT_USER_STACK_TOP, 1, 0},
    {WIT_TEST_WRITE_CODE, "User.WriteCode", STORE_PAGE, STORE_PAGE, WIT_USER_IMAGE_BASE, 1, 0},
    {WIT_TEST_WRITE_INFO, "User.WriteInfo", STORE_PAGE, STORE_PAGE, WIT_USER_INFO, 1, 0},
    {WIT_TEST_NULL_READ, "User.NullRead", LOAD_PAGE, LOAD_PAGE, 0, 1, 0},
    {WIT_TEST_INVALID_OPCODE, "User.InvalidOpcode", ILLEGAL, ILLEGAL, 0, 0, 0},
    {WIT_TEST_MEMORY_RESERVED, "User.MemoryReservedFault", LOAD_PAGE, LOAD_PAGE, WIT_USER_MEMORY_BASE, 1, 0},
    {WIT_TEST_MEMORY_DECOMMITTED, "User.MemoryDecommittedFault", LOAD_PAGE, LOAD_PAGE, WIT_USER_MEMORY_BASE, 1, 0},
    {WIT_TEST_MEMORY_RELEASED, "User.MemoryReleasedFault", LOAD_PAGE, LOAD_PAGE, WIT_USER_MEMORY_BASE, 1, 0},
    {WIT_TEST_MEMORY_READONLY, "User.MemoryReadOnlyFault", STORE_PAGE, STORE_PAGE, WIT_USER_MEMORY_BASE, 1, 0},
    {WIT_TEST_MEMORY_NOACCESS, "User.MemoryNoAccessFault", LOAD_PAGE, LOAD_PAGE, WIT_USER_MEMORY_BASE, 1, 0},
    {WIT_TEST_MEMORY_NX, "User.MemoryNxFault", INSTRUCTION_PAGE, INSTRUCTION_PAGE, WIT_USER_MEMORY_BASE, 1, 1}};
const WitU32 wit_test_user_fault_count = sizeof(wit_test_user_faults) / sizeof(wit_test_user_faults[0]);
const WitUserFaultCase wit_test_user_peer_fault = {
    WIT_TEST_PEER_READ, "User.PeerMemory", LOAD_PAGE, LOAD_PAGE, WIT_USER_PEER_PAGE, 1, 0};

const WitUserFaultCase wit_test_thread_faults[] = {
    {WIT_THREAD_TEST_FAULT, "User.ThreadFault", ILLEGAL, ILLEGAL, 0, 0, 0},
    {WIT_THREAD_TEST_GUARD_LOW, "User.ThreadGuardLow", STORE_PAGE, STORE_PAGE, WIT_THREAD_TEST_STACK - 1, 1, 0},
    {WIT_THREAD_TEST_GUARD_HIGH, "User.ThreadGuardHigh", STORE_PAGE, STORE_PAGE,
        WIT_THREAD_TEST_STACK + WIT_THREAD_TEST_STACK_BYTES, 1, 0}};
const WitU32 wit_test_thread_fault_count = sizeof(wit_test_thread_faults) / sizeof(wit_test_thread_faults[0]);
const WitUserFaultCase wit_test_exception_fault = {
    WIT_EXCEPTION_TEST_REJECT, "User.ExceptionReject", LOAD_PAGE, LOAD_PAGE, 0, 1, 0};

static WitRv64Frame model_frames[WIT_USER_THREAD_CAPACITY];

WitArchFrame *wit_test_model_frame(WitU32 index)
{
    return &model_frames[index];
}

int wit_test_user_fault_state(const WitArchFaultState *state, WitU64 *pc)
{
    *pc = state->Sepc;
    return (state->Sstatus & WIT_RV64_SSTATUS_SPP) == 0;
}
