#ifndef WITOS_USER_H
#define WITOS_USER_H
#include "x64.h"
#include "user_layout.h"
#include "witos/handles.h"
#include "witos/events.h"
#include "witos/memory.h"
#include "witos/pe.h"

_Static_assert(WIT_PE_MAX_SECTIONS == WIT_IMAGE_INFO_MAX_RANGES, "Image range capacities");
_Static_assert(WIT_USER_IMAGE_INFO_OFFSET + WIT_IMAGE_INFO_SIZE <= 4096, "Image information page bound");

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

typedef enum WitUserThreadState {
    WitThreadEmpty, WitThreadReady, WitThreadRunning, WitThreadWaiting, WitThreadExited
} WitUserThreadState;

typedef enum WitUserWaitKind {
    WitWaitNone, WitWaitJoin, WitWaitEvent, WitWaitSleep
} WitUserWaitKind;

typedef struct WitUserThread {
    WitUserThreadState State;
    WitU32 WaitingOn;
    WitU32 Joiner;
    WitUserWaitKind WaitKind;
    WitU64 WaitHandle;
    WitU64 Deadline;
    WitU32 MonotonicWait; /* 0: delivered PIT ticks; 1: monotonic counter. */
    WitU64 WaitOrder;
    WitU64 Handle;
    WitU64 StackBottom;
    WitU64 StackTop;
    WitU64 Tls;
    WitU64 CompilerTls;
    WitU64 ExitCode;
    WitInterruptContext *Context;
} WitUserThread;

typedef struct WitUserProcess {
    WitUserSpace Space;
    WitHandleTable Handles;
    WitEventTable Events;
    WitU32 Id;
    WitU32 Slot;
    WitUserState State;
    WitU32 Writes;
    WitU64 Ticks;
    WitU64 ExitCode;
    WitU64 ImageBase;
    WitU64 ImageEntry;
    WitU32 ImageSize;
    WitU32 TlsBytes;
    WitU8 TlsTemplate[WIT_PE_TLS_MAX_BYTES];
    WitU64 FaultVector;
    WitU64 FaultError;
    WitU64 FaultAddress;
    WitU64 FaultRip;
    WitU64 FaultCs;
    WitU64 FaultSs;
    WitUserThread Threads[WIT_USER_THREAD_CAPACITY];
    WitU32 CurrentThread;
    WitU32 FaultThread;
    WitU64 ThreadCreates;
    WitU64 ThreadSwitches;
    WitU64 ThreadTimerSwitches;
    WitU64 ThreadJoins;
    WitU64 ThreadReaps;
    WitU64 ThreadDeadlocks;
    WitU64 NextWaitOrder;
    WitU64 EventParks;
    WitU64 EventWakes;
    WitU64 WaitTimeouts;
    WitU64 WaitCloses;
    WitU64 IdleHalts;
    WitU64 IdleTicks;
} WitUserProcess;

int wit_user_space_create(WitUserSpace *space, WitPageAllocator *allocator);
int wit_user_space_map(WitUserSpace *space, WitU64 address, int writable, int executable);
WitU64 wit_user_space_physical(const WitUserSpace *space, WitU64 address, int write, int execute);
int wit_user_copy_from(const WitUserSpace *space, WitU64 address, WitU8 *buffer, WitU32 size);
int wit_user_copy_to(const WitUserSpace *space, WitU64 address, const WitU8 *buffer, WitU32 size);
WitU64 wit_user_memory_query(const WitUserSpace *space, WitU64 address, WitU64 size, WitU64 version);
void wit_user_space_destroy(WitUserSpace *space);
int wit_user_space_unmap_fixed(WitUserSpace *space, WitU64 address);
WitU64 wit_user_memory_reserve(WitUserSpace *space, WitU64 size, WitU64 alignment, WitU64 *result);
WitU64 wit_user_memory_commit(WitUserSpace *space, WitU64 address, WitU64 size, WitU64 protection);
WitU64 wit_user_memory_reset(WitUserSpace *space, WitU64 address, WitU64 size);
WitU64 wit_user_memory_decommit(WitUserSpace *space, WitU64 address, WitU64 size);
WitU64 wit_user_memory_protect(WitUserSpace *space, WitU64 address, WitU64 size, WitU64 protection);
WitU64 wit_user_memory_release(WitUserSpace *space, WitU64 address);
void wit_user_memory_self_test(WitPageAllocator *pages);
void wit_user_thread_self_test(WitPageAllocator *pages);
void wit_user_wait_self_test(WitPageAllocator *pages);
void wit_user_image_self_test(WitPageAllocator *pages);
void wit_user_bootstrap_self_test(WitPageAllocator *pages);
void wit_user_gc_self_test(WitPageAllocator *pages);
void wit_user_tls_self_test(WitPageAllocator *pages);
void wit_user_dynamic_tls_self_test(WitPageAllocator *pages);
WitU64 wit_user_thread_query(const WitUserProcess *process, WitU64 address, WitU64 size, WitU64 version);
void wit_user_pal_self_test(WitPageAllocator *pages);
void wit_user_pal_services_self_test(WitPageAllocator *pages);
int wit_user_capture_tls(WitUserProcess *process, const WitPeImage *image);
WitU64 wit_user_prepare_thread(WitUserProcess *process, WitU32 index, WitU64 entry, WitU64 argument);
WitU64 wit_virtual_kernel_root(void);

int wit_user_create(WitUserProcess *process, WitPageAllocator *allocator,
    WitU32 slot, const WitU8 *code, WitU32 code_size);
WitPeStatus wit_user_create_pe(WitUserProcess *process, WitPageAllocator *allocator,
    WitU32 slot, const WitU8 *file, WitU32 size, WitU64 base);
int wit_user_image_map(WitUserSpace *space, const WitU8 *file, const WitPeImage *plan, WitU64 base);
WitU64 wit_user_thread_create(WitUserProcess *process, WitU64 entry, WitU64 argument, WitU64 *result);
void wit_user_run(WitUserProcess *process);
void wit_user_destroy(WitUserProcess *process);
int wit_user_is_active(void);
void wit_user_wait_expire(WitUserProcess *process, WitU64 now);
void wit_user_wait_expire_time(WitUserProcess *process, WitU64 now);
WitU64 wit_user_sleep_until(WitUserProcess *process, WitU64 deadline, WitU64 now);
WitU64 wit_user_event_wait_until(WitUserProcess *process, WitU64 handle, WitU64 deadline, WitU64 now);
WitU64 wit_user_sleep(WitUserProcess *process, WitU64 deadline, WitU64 now);
WitU64 wit_user_event_wait(WitUserProcess *process, WitU64 handle, WitU64 deadline, WitU64 now);
WitU64 wit_user_event_set(WitUserProcess *process, WitU64 handle);
WitU64 wit_user_event_reset(WitUserProcess *process, WitU64 handle);
WitU64 wit_user_event_close(WitUserProcess *process, WitU64 handle);
WitInterruptContext *wit_x64_user_syscall(WitInterruptContext *context);
WitInterruptContext *wit_user_timer_tick(WitInterruptContext *context);
WIT_NORETURN void wit_user_fault(const WitExceptionFrame *frame, WitU64 address);

void wit_x64_run_user(WitInterruptContext *context, WitU64 root);
WIT_NORETURN void wit_x64_leave_user(void);
void wit_x64_set_kernel_stack(WitU64 top);
void wit_x64_set_user_tls(WitU64 address, WitU64 compiler_address);
#endif
