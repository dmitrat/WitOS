#ifndef WITOS_X64_H
#define WITOS_X64_H

#include "witos/boot.h"
#include "user_layout.h"

#define WIT_X64_STORAGE_BASE 0xFFFFA00000000000ULL
#define WIT_X64_STORAGE_SLOT 320U
WIT_STATIC_ASSERT(((WIT_X64_STORAGE_BASE>>39)&511)==WIT_X64_STORAGE_SLOT,"Storage PML4 slot");
#define WIT_KERNEL_STACK_SIZE 65536U
#define WIT_EMERGENCY_STACK_SIZE 32768U
#define WIT_KERNEL_STACK_REGION_SIZE (WIT_KERNEL_STACK_SIZE + 8192U)
#define WIT_EMERGENCY_STACK_REGION_SIZE (WIT_EMERGENCY_STACK_SIZE + 8192U)
#define WIT_STACK_GUARD_COUNT (8U + 4U * WIT_USER_THREAD_CAPACITY)
#define WIT_PAGE_FAULT_PROBE 0x0000400000000000ULL

extern WitU8 wit_x64_kernel_stack[WIT_KERNEL_STACK_REGION_SIZE];
extern WitU8 wit_x64_double_fault_stack[WIT_EMERGENCY_STACK_REGION_SIZE];
extern WitU8 wit_x64_worker_stacks[2][WIT_KERNEL_STACK_REGION_SIZE];
extern WitU8 wit_x64_user_kernel_stacks[2][WIT_USER_THREAD_CAPACITY][WIT_KERNEL_STACK_REGION_SIZE];
void wit_x64_context_profile_self_test(void);
void wit_x64_context_copy_self_test(void);
int wit_x64_context_profile_supported(void);
WitU32 wit_x64_mxcsr_mask(void);
void wit_x64_stack_guards(WitU64 guards[WIT_STACK_GUARD_COUNT]);
void wit_x64_timer_start(void);
void wit_x64_timer_stop(void);
WitU64 wit_x64_clock_ticks(void);
#define WIT_X64_HPET_BASE 0xFED00000ULL
void wit_x64_map_hpet(const WitBootInfo *boot);
WitU64 wit_x64_monotonic_read(void);
WitU64 wit_x64_monotonic_frequency(void);
void wit_x64_idle_once(void);
void wit_x64_idle_resume(void);
extern WitU64 wit_x64_isr_table[256];

#pragma pack(push, 1)
typedef struct WitDescriptorPointer {
    WitU16 Limit;
    WitU64 Base;
} WitDescriptorPointer;

typedef struct WitInterruptGate {
    WitU16 OffsetLow;
    WitU16 Selector;
    WitU8 Ist;
    WitU8 Attributes;
    WitU16 OffsetMiddle;
    WitU32 OffsetHigh;
    WitU32 Reserved;
} WitInterruptGate;

typedef struct WitTaskState {
    WitU32 Reserved0;
    WitU64 Rsp[3];
    WitU64 Reserved1;
    WitU64 Ist[7];
    WitU64 Reserved2;
    WitU16 Reserved3;
    WitU16 IoMapBase;
} WitTaskState;
#pragma pack(pop)

/* The stubs normalize the vector/error prefix. Long-mode hardware supplies
 * RIP, CS, RFLAGS, interrupted RSP and SS, including same-privilege faults. */
typedef struct WitExceptionFrame {
    WitU64 Vector;
    WitU64 Error;
    WitU64 Rip;
    WitU64 Cs;
    WitU64 Rflags;
    WitU64 Rsp;
    WitU64 Ss;
} WitExceptionFrame;

_Static_assert(sizeof(WitDescriptorPointer) == 10, "x64 descriptor pointer");
_Static_assert(sizeof(WitInterruptGate) == 16, "x64 IDT gate");
_Static_assert(sizeof(WitTaskState) == 104, "x64 TSS");
_Static_assert(sizeof(WitExceptionFrame) == 56, "x64 normalized exception frame");

typedef struct __declspec(align(16)) WitInterruptContext {
    WitU8 FxState[512];
    WitU64 R15, R14, R13, R12, R11, R10, R9, R8;
    WitU64 Rsi, Rdi, Rbp, Rdx, Rcx, Rbx, Rax;
    WitU64 Rip, Cs, Rflags, Rsp, Ss;
} WitInterruptContext;

_Static_assert(sizeof(WitInterruptContext) == 672, "x64 interrupt context layout");
WitInterruptContext *wit_x64_timer_interrupt(WitInterruptContext *context);
void wit_x64_fxsave(void *state);
void wit_x64_process_write_barrier(void);
WitU64 wit_x64_read_flags(void);
WIT_NORETURN void wit_x64_worker(WitU32 index);
void wit_x64_load_tables(const WitDescriptorPointer *gdt, const WitDescriptorPointer *idt);
WitU64 wit_x64_stack_pointer(void);
WIT_NORETURN void wit_x64_exception(const WitExceptionFrame *frame, WitU64 fault_address);
void wit_x64_trigger_breakpoint(void);
void wit_x64_trigger_divide_error(void);
void wit_x64_trigger_invalid_opcode(void);
void wit_x64_trigger_general_protection(void);
void wit_x64_trigger_page_fault(void);
void wit_x64_trigger_double_fault(void);

#endif
