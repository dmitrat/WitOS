#ifndef WITOS_X64_H
#define WITOS_X64_H

#include "witos/boot.h"

#define WIT_KERNEL_STACK_SIZE 65536U
#define WIT_EMERGENCY_STACK_SIZE 32768U
#define WIT_PAGE_FAULT_PROBE 0x0000400000000000ULL

extern WitU8 wit_x64_kernel_stack[WIT_KERNEL_STACK_SIZE];
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
