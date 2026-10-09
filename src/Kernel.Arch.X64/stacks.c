#include "x64.h"

__declspec(align(4096)) WitU8 wit_x64_kernel_stack[WIT_KERNEL_STACK_REGION_SIZE];
__declspec(align(4096)) WitU8 wit_x64_double_fault_stack[WIT_EMERGENCY_STACK_REGION_SIZE];
__declspec(align(4096)) WitU8 wit_x64_worker_stacks[2][WIT_KERNEL_STACK_REGION_SIZE];

/* One kernel stack per thread slot of every registry slot (K5.2c): a thread's frame lives on its own stack. */
__declspec(align(4096)) WitU8
    wit_x64_user_kernel_stacks[WIT_PROCESS_CAPACITY][WIT_PROCESS_THREAD_CAPACITY][WIT_KERNEL_STACK_REGION_SIZE];

void wit_x64_stack_guards(WitU64 guards[WIT_STACK_GUARD_COUNT])
{
    guards[0] = (WitU64)wit_x64_kernel_stack;
    guards[1] = guards[0] + 4096 + WIT_KERNEL_STACK_SIZE;
    guards[2] = (WitU64)wit_x64_double_fault_stack;
    guards[3] = guards[2] + 4096 + WIT_EMERGENCY_STACK_SIZE;
    for (WitU32 i = 0; i < 2; ++i) {
        guards[4 + i * 2] = (WitU64)wit_x64_worker_stacks[i];
        guards[5 + i * 2] = guards[4 + i * 2] + 4096 + WIT_KERNEL_STACK_SIZE;
    }
    for (WitU32 i = 0; i < WIT_PROCESS_CAPACITY; ++i) {
        for (WitU32 t = 0; t < WIT_PROCESS_THREAD_CAPACITY; ++t) {
            const WitU32 index = 8 + (i * WIT_PROCESS_THREAD_CAPACITY + t) * 2;
            guards[index] = (WitU64)wit_x64_user_kernel_stacks[i][t];
            guards[index + 1] = guards[index] + 4096 + WIT_KERNEL_STACK_SIZE;
        }
    }
}
