#ifndef WITOS_USER_H
#define WITOS_USER_H
#include "x64.h"
#include "user_layout.h"
#include "witos/handles.h"
#include "witos/memory.h"

typedef struct WitUserReservation {
    WitU64 Base;
    WitU64 Size;
} WitUserReservation;

typedef struct WitUserSpace {
    WitPageAllocator *Allocator;
    WitU64 Root;
    WitU64 OwnedPages[WIT_USER_PAGE_CAPACITY];
    WitU64 OwnedVirtual[WIT_USER_PAGE_CAPACITY]; /* Zero for page tables. */
    WitUserReservation Reservations[WIT_USER_RESERVATION_CAPACITY];
    WitU32 OwnedCount;
} WitUserSpace;

typedef enum WitUserState {
    WitUserEmpty, WitUserReady, WitUserRunning, WitUserExited,
    WitUserFaulted, WitUserBudgetExpired, WitUserBadReturn
} WitUserState;

typedef struct WitUserProcess {
    WitUserSpace Space;
    WitHandleTable Handles;
    WitU32 Id;
    WitU32 Slot;
    WitUserState State;
    WitU32 Writes;
    WitU64 Ticks;
    WitU64 ExitCode;
    WitU64 FaultVector;
    WitU64 FaultError;
    WitU64 FaultAddress;
    WitU64 FaultRip;
    WitU64 FaultCs;
    WitU64 FaultSs;
    WitInterruptContext *InitialContext;
} WitUserProcess;

int wit_user_space_create(WitUserSpace *space, WitPageAllocator *allocator);
int wit_user_space_map(WitUserSpace *space, WitU64 address, int writable, int executable);
WitU64 wit_user_space_physical(const WitUserSpace *space, WitU64 address, int write, int execute);
int wit_user_copy_from(const WitUserSpace *space, WitU64 address, WitU8 *buffer, WitU32 size);
void wit_user_space_destroy(WitUserSpace *space);
WitU64 wit_user_memory_reserve(WitUserSpace *space, WitU64 size, WitU64 alignment, WitU64 *result);
WitU64 wit_user_memory_commit(WitUserSpace *space, WitU64 address, WitU64 size, WitU64 protection);
WitU64 wit_user_memory_decommit(WitUserSpace *space, WitU64 address, WitU64 size);
WitU64 wit_user_memory_protect(WitUserSpace *space, WitU64 address, WitU64 size, WitU64 protection);
WitU64 wit_user_memory_release(WitUserSpace *space, WitU64 address);
void wit_user_memory_self_test(WitPageAllocator *pages);
WitU64 wit_virtual_kernel_root(void);

int wit_user_create(WitUserProcess *process, WitPageAllocator *allocator,
    WitU32 slot, const WitU8 *code, WitU32 code_size);
void wit_user_run(WitUserProcess *process);
void wit_user_destroy(WitUserProcess *process);
int wit_user_is_active(void);
WitInterruptContext *wit_x64_user_syscall(WitInterruptContext *context);
WitInterruptContext *wit_user_timer_tick(WitInterruptContext *context);
WIT_NORETURN void wit_user_fault(const WitExceptionFrame *frame, WitU64 address);

void wit_x64_run_user(WitInterruptContext *context, WitU64 root);
WIT_NORETURN void wit_x64_leave_user(void);
void wit_x64_set_kernel_stack(WitU64 top);
#endif
