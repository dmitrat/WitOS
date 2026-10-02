#ifndef WITOS_USER_H
#define WITOS_USER_H
#include "witos/arch.h"
#include "witos/arch_types.h"
#include "witos/user_layout.h"
#include "witos/handles.h"
#include "witos/files.h"
#include "witos/events.h"
#include "witos/memory.h"
#include "witos/pe.h"
#include "witos/virtual_gap.h"

_Static_assert(WIT_WAIT_ANY_CAPACITY == WIT_EVENT_CAPACITY, "Wait-any event capacity");
_Static_assert(WIT_PE_MAX_SECTIONS == WIT_IMAGE_INFO_MAX_RANGES, "Image range capacities");
_Static_assert(WIT_USER_IMAGE_INFO_OFFSET + WIT_IMAGE_INFO_SIZE <= 4096, "Image information page bound");

/* Private code-mapping backend; not enabled by ordinary memory syscalls. */
#define WIT_CODE_EXECUTE 4ULL
typedef WitVirtualRange WitUserReservation;
#define WIT_CODE_VIEW_CAPACITY 16U

typedef struct WitCodeView {
    WitU64 Destination, Source, Size, Protection;
} WitCodeView;

typedef struct WitUserSpace {
    WitPageAllocator *Allocator;
    WitU64 Root;
    WitU64 OwnedPages[WIT_RUNTIME_PAGE_CAPACITY];
    WitU64 OwnedVirtual[WIT_RUNTIME_PAGE_CAPACITY]; /* Zero for page tables. */
    WitU64 AliasVirtual[WIT_RUNTIME_PAGE_CAPACITY];
    WitU64 AliasPhysical[WIT_RUNTIME_PAGE_CAPACITY];
    WitU32 AliasCount;
    WitCodeView CodeViews[WIT_CODE_VIEW_CAPACITY];
    WitUserReservation Reservations[WIT_RUNTIME_RESERVATION_CAPACITY];
    WitVirtualRange LibraryRanges[WIT_LIBRARY_CAPACITY + 1];
    WitU32 OwnedCount, PageLimit, ReservationLimit;
    WitU64 FixedLimit;
} WitUserSpace;

typedef enum WitUserState {
    WitUserEmpty,
    WitUserReady,
    WitUserRunning,
    WitUserExited,
    WitUserFaulted,
    WitUserBudgetExpired,
    WitUserBadReturn
} WitUserState;

typedef enum WitUserThreadState {
    WitThreadEmpty,
    WitThreadReady,
    WitThreadRunning,
    WitThreadWaiting,
    WitThreadExited
} WitUserThreadState;

typedef enum WitUserWaitKind {
    WitWaitNone,
    WitWaitJoin,
    WitWaitEvent,
    WitWaitSleep,
    WitWaitEvents,
    WitWaitObjects
} WitUserWaitKind;

typedef struct WitUserThread {
    WitUserThreadState State;
    WitU32 Detached;
    WitU32 LibraryNotifications, LibraryPhase, LibraryRequired;
    WitU32 NativeId;
    WitU32 SuspendCount;
    WitUserExceptionInfo Exception;
    WitUserExceptionInfo ExceptionParents[WIT_EXCEPTION_MAX_DEPTH - 1];
    WitU32 ExceptionDepth;
    WitU32 NameLength;
    WitU16 Name[WIT_THREAD_NAME_CAPACITY];
    WitU32 WaitingOn;
    WitU32 Joiner;
    WitUserWaitKind WaitKind;
    WitU64 WaitHandle;
    WitU64 WaitHandles[WIT_WAIT_ANY_CAPACITY];
    WitU32 WaitCount;
    WitU32 WaitAll, WaitAlertable;
    WitUserApc Apcs[WIT_APC_CAPACITY];
    WitU32 ApcCount;
    WitU64 Deadline;
    WitU32 MonotonicWait; /* 0: delivered PIT ticks; 1: monotonic counter. */
    WitU64 WaitOrder;
    WitU64 Handle;
    WitU64 StackBottom;
    WitU64 StackTop;
    WitU64 Tls;
    WitU64 CompilerTls;
    WitU64 LibraryTls[WIT_LIBRARY_CAPACITY];
    WitU64 LibraryNotificationPage, LibraryNotificationHandles[2];
    WitU64 ExitCode;
    WitArchFrame *Context;
} WitUserThread;

/* References retain terminal identity/exit metadata, never reaped user pages. */
typedef struct WitUserThreadReference {
    WitU64 Handle, ThreadId, ExitCode;
    WitU32 Rights, Exited;
} WitUserThreadReference;

typedef struct WitUserStackLease {
    WitU64 Token, OwnerId, ThreadId;
} WitUserStackLease;

typedef struct WitUserLibrary {
    WitU64 Token, Base, ImageBytes, FileOffset, FileBytes;
    WitU32 EntryRva, UnwindRva, UnwindBytes, References;
    WitU64 NameOffset;
    WitU32 NameBytes, Dependencies, Readers;
    WitU64 AttachOrder;
} WitUserLibrary;

typedef struct WitUserLibraryReader {
    WitU64 Token, ModuleToken;
    WitU32 Slot;
} WitUserLibraryReader;

typedef struct WitUserLibraryLifecycle {
    WitU64 Token, Owner, Address, Origin;
    WitU32 Mask, Attach, ReaderRelease, Shutdown, ThreadNotify, ThreadReserved, Count, Order[WIT_LIBRARY_CAPACITY];
} WitUserLibraryLifecycle;

typedef struct WitUserLibraryTls {
    WitU32 Bytes;
    WitU8 Data[WIT_PE_TLS_MAX_BYTES];
} WitUserLibraryTls;

typedef struct WitUserProcess {
    WitUserSpace Space;
    WitHandleTable Handles;
    WitFileTable Files;
    WitUserLibrary Libraries[WIT_LIBRARY_CAPACITY];
    WitUserLibraryTls LibraryTls[WIT_LIBRARY_CAPACITY];
    WitUserLibraryLifecycle LibraryLifecycle;
    WitU64 NextLibraryAttach;
    WitU32 LibraryShutdown;
    WitUserLibraryReader LibraryReaders[WIT_LIBRARY_READER_CAPACITY];
    WitEventTable Events;
    WitU32 Id;
    WitU32 Slot;
    WitUserState State;
    WitU32 Writes;
    WitU32 RandomRequests;
    WitU64 RandomBytes;
    WitU64 Ticks, TickLimit;
    WitU64 ExitCode;
    WitU64 ImageBase;
    WitU64 ImageEntry;
    WitU32 ImageSize;
    WitU32 TlsBytes;
    WitU8 TlsTemplate[WIT_PE_TLS_MAX_BYTES];
    WitU64 FaultVector;
    WitU64 FaultError;
    WitU64 FaultAddress;
    WitArchFaultState FaultState;
    WitUserStackLease StackLeases[WIT_STACK_LEASE_CAPACITY];
    WitUserThread Threads[WIT_USER_THREAD_CAPACITY];
    WitUserThreadReference ThreadReferences[WIT_RUNTIME_HANDLE_CAPACITY];
    WitU64 ExceptionCallback;
    WitU32 RequireThreadCompletion;
    WitU64 AbruptThreadId, AbruptThreadCode, OrderlyThreadExits;
    WitU64 MemoryCommitFailures;
    WitU64 ForeignObjectWaitSuspends;
    WitU64 ReferenceThreadCapacityFailures;
    WitU64 HardwareNullReads, HardwareNullWrites, HardwareDivideFaults, HardwareIllegalFaults, ExceptionContinuations;
    WitU64 FatalOwner;
    WitUserFatalInfo Fatal;
    WitU32 FatalArmed;
    WitU32 CurrentThread;
    WitU32 FaultThread;
    WitU64 ProcessWriteBarriers;
    WitU64 ThreadCreates;
    WitU64 ThreadSwitches;
    WitU64 ThreadTimerSwitches;
    WitU64 ThreadJoins;
    WitU64 ThreadReaps;
    WitU64 DetachedCreates;
    WitU64 DetachedReaps;
    WitU64 ThreadDeadlocks;
    WitU64 NextWaitOrder;
    WitU32 MemoryPressureLow;
    WitU64 MemoryPressureEvents[WIT_RUNTIME_EVENT_CAPACITY];
    WitU64 EventParks;
    WitU64 EventWakes;
    WitU64 WaitTimeouts;
    WitU64 WaitCloses;
    WitU64 IdleHalts;
    WitU64 IdleTicks;
} WitUserProcess;

void wit_user_exception_initialize(WitUserProcess *);
void wit_user_exception_clear(WitUserThread *);
WitU64 wit_user_exception_begin(WitUserProcess *, WitU64, WitU64, WitU64, WitU64 *);
WitU64 wit_user_exception_register(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_exception_query(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_exception_continue(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_exception_unwind(WitUserProcess *, WitU64, WitU64, WitU64);
int wit_user_exception_deliver(WitUserProcess *, WitArchFrame *, WitU64, WitU64, WitU64);
WitArchFrame *wit_user_exception_trap(WitArchFrame *, WitU64, WitU64, WitU64);
void wit_user_context_snapshot(WitThreadContext *, const WitUserThread *, const WitArchFrame *);
WitU64 wit_user_context_validate(WitUserProcess *, WitUserThread *, const WitThreadContext *, int);
void wit_user_stack_leases_initialize(WitUserProcess *);
void wit_user_stack_leases_exit(WitUserProcess *, WitU64);
int wit_user_stack_leases_owned(const WitUserProcess *, WitU64);
int wit_user_stack_leased(const WitUserProcess *, WitU64, int);
WitU64 wit_user_stack_lease_acquire(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_stack_lease_query(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_stack_lease_release(WitUserProcess *, WitU64);
WitU64 wit_user_thread_context_metadata(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_thread_context_set(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_thread_context_restore(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_thread_context_get(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_cpu_context_query(WitUserProcess *, WitU64, WitU64, WitU64);
void wit_user_thread_name_clear(WitUserThread *);
WitU64 wit_user_thread_name_set(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_thread_name_query(WitUserProcess *, WitU64, WitU64, WitU64);
void wit_user_suspend_deadline_self_test(void);
WitU64 wit_user_thread_suspend(WitUserProcess *, WitU64, int, WitU64 *);
void wit_user_wait_complete(WitUserThread *, WitU64, WitU64);
void wit_user_wait_objects_changed(WitUserProcess *);
void wit_user_wait_handle_closed(WitUserProcess *, WitU64);
void wit_user_apc_initialize(WitUserThread *);
WitU64 wit_user_apc_queue(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_apc_dequeue(WitUserProcess *, WitU64, WitU64);
WitU64 wit_user_objects_poll(WitUserProcess *, const WitU64 *, WitU32, int, int, WitU64 *);
WitU64 wit_user_object_wait(WitUserProcess *, WitU64, WitU64, WitU64, WitU64, WitU64 *);
WitU64 wit_user_reference_target(WitUserProcess *, WitU64, WitU32, WitUserThread **);
WitU64 wit_user_reference_signaled(WitUserProcess *, WitU64, int *);
void wit_user_references_initialize(WitUserProcess *);
void wit_user_references_exit(WitUserProcess *, WitU64, WitU64);
WitU64 wit_user_reference_duplicate(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_reference_query(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_reference_close(WitUserProcess *, WitU64);

int wit_user_space_create_profile(WitUserSpace *, WitPageAllocator *, int);
int wit_user_space_create(WitUserSpace *space, WitPageAllocator *allocator);
int wit_user_space_map(WitUserSpace *space, WitU64 address, int writable, int executable);
WitU64 wit_user_space_physical(const WitUserSpace *space, WitU64 address, int write, int execute);
int wit_user_copy_from(const WitUserSpace *space, WitU64 address, WitU8 *buffer, WitU32 size);
WitU64 wit_user_console_write(WitUserProcess *, WitU64, WitU64, WitU64);
int wit_user_buffer_readable(const WitUserSpace *, WitU64, WitU32);
int wit_user_buffer_writable(const WitUserSpace *, WitU64, WitU32);
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
void wit_user_native_id_self_test(void);
void wit_user_runtime_unwind_metadata_self_test(WitPageAllocator *);
void wit_user_wait_self_test(WitPageAllocator *pages);
void wit_user_image_self_test(WitPageAllocator *pages);
void wit_user_bootstrap_self_test(WitPageAllocator *pages);
void wit_user_gc_self_test(WitPageAllocator *pages);
void wit_user_tls_self_test(WitPageAllocator *pages);
void wit_user_dynamic_tls_self_test(WitPageAllocator *pages);
WitU64 wit_user_thread_query(const WitUserProcess *process, WitU64 address, WitU64 size, WitU64 version);
void wit_user_pal_self_test(WitPageAllocator *pages);
void wit_user_pal_services_self_test(WitPageAllocator *pages);
void wit_user_wait_any_self_test(WitPageAllocator *pages);
void wit_user_pressure_self_test(WitPageAllocator *pages);
int wit_user_capture_tls(WitUserProcess *process, const WitPeImage *image);
WitU64 wit_user_thread_create_reference(WitUserProcess *process, WitU64 input, WitU64 size, WitU64 *result);
WitU64 wit_user_prepare_thread(WitUserProcess *process, WitU32 index, WitU64 entry, WitU64 argument, WitU64 flags);
WitU64 wit_virtual_kernel_root(void);

int wit_user_create(
    WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot, const WitU8 *code, WitU32 code_size);
WitPeStatus wit_user_create_pe_profile(
    WitUserProcess *, WitPageAllocator *, WitU32, const WitU8 *, WitU32, WitU64, const char *, WitU32);
WitPeStatus wit_user_create_named_pe(WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot,
    const WitU8 *file, WitU32 size, WitU64 base, const char *resource_name);
WitPeStatus wit_user_create_pe(
    WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot, const WitU8 *file, WitU32 size, WitU64 base);
int wit_user_image_map(WitUserSpace *space, const WitU8 *file, const WitPeImage *plan, WitU64 base);
WitU64 wit_user_thread_create(WitUserProcess *process, WitU64 entry, WitU64 argument, WitU64 *result);
WitU64 wit_user_thread_create_flags(
    WitUserProcess *process, WitU64 entry, WitU64 argument, WitU64 flags, WitU64 *result);
void wit_user_pal_module_self_test(WitPageAllocator *pages);
void wit_user_pal_environment_self_test(WitPageAllocator *pages);
void wit_user_process_exit_self_test(WitPageAllocator *pages);
void wit_user_runtime_config_self_test(WitPageAllocator *pages);
void wit_user_runtime_boot_test(WitPageAllocator *pages);
void wit_user_pal_background_self_test(WitPageAllocator *pages);
void wit_user_pal_error_self_test(WitPageAllocator *pages);
void wit_user_run(WitUserProcess *process);
void wit_user_destroy(WitUserProcess *process);
int wit_user_is_active(void);
void wit_user_pressure_update(WitUserProcess *process);
WitU64 wit_user_pressure_create(WitUserProcess *process, WitU64 *handle);
WitU64 wit_user_event_notify(WitUserProcess *process, WitU64 handle, int signaled);
void wit_user_wait_expire(WitUserProcess *process, WitU64 now);
void wit_user_wait_expire_time(WitUserProcess *process, WitU64 now);
WitU64 wit_user_sleep_until(WitUserProcess *process, WitU64 deadline, WitU64 now);
WitU64 wit_user_event_wait_any_until(
    WitUserProcess *process, WitU64 address, WitU64 count, WitU64 deadline, WitU64 now, WitU64 *index);
WitU64 wit_user_event_wait_until(WitUserProcess *process, WitU64 handle, WitU64 deadline, WitU64 now);
WitU64 wit_user_sleep(WitUserProcess *process, WitU64 deadline, WitU64 now);
WitU64 wit_user_event_wait(WitUserProcess *process, WitU64 handle, WitU64 deadline, WitU64 now);
WitU64 wit_user_event_set(WitUserProcess *process, WitU64 handle);
WitU64 wit_user_event_reset(WitUserProcess *process, WitU64 handle);
WitU64 wit_user_event_close(WitUserProcess *process, WitU64 handle);
WitArchFrame *wit_user_syscall(WitArchFrame *frame, WitU64 call, WitU64 argument0, WitU64 argument1, WitU64 argument2);
WitArchFrame *wit_user_timer_tick(WitArchFrame *frame);
WIT_NORETURN void wit_user_fault(
    const void *trap, WitU64 trap_size, WitU64 vector, WitU64 error, WitU64 address, const WitArchFaultState *state);

WitU64 wit_user_code_call(WitUserProcess *, WitU64, WitU64, WitU64, WitU64 *);
WitU64 wit_user_code_reserve(WitUserSpace *, WitU64, WitU64, WitU64, WitU64, WitU64 *);
WitU64 wit_user_code_validate(WitUserSpace *, WitU64, WitU64, WitU64);
WitU64 wit_user_code_reset_sparse(WitUserSpace *, WitU64, WitU64);
WitU64 wit_user_code_map_sparse(WitUserSpace *, WitU64, WitU64, WitU64, WitU64);
WitU64 wit_user_code_alias(WitUserSpace *, WitU64, WitU64, WitU64, WitU64);
WitU64 wit_user_code_protect(WitUserSpace *, WitU64, WitU64, WitU64);
WitU64 wit_user_code_publish(WitUserSpace *, WitU64, WitU64);
void wit_user_code_self_test(WitPageAllocator *);

WitU64 wit_user_library_call(WitUserProcess *, WitU64, WitU64, WitU64, WitU64 *);
WitU64 wit_user_library_release(WitUserSpace *, WitU64);
void wit_user_library_initialize(WitUserProcess *);
void wit_user_library_collect(WitUserProcess *);
int wit_user_library_tls_install(WitUserProcess *, WitU32, const WitPeImage *, WitU64);
void wit_user_library_tls_remove(WitUserProcess *, WitU32);
int wit_user_library_tls_create_thread(WitUserProcess *, WitU32);
void wit_user_library_tls_reap_thread(WitUserProcess *, WitU32);
void wit_user_library_tls_refresh(WitUserProcess *);
WitU32 wit_user_library_reachable(const WitUserProcess *);
WitU64 wit_user_library_thread_admission(const WitUserProcess *, WitU64);
WitU64 wit_user_library_thread_notify(WitUserProcess *, const WitLibraryRequest *);
WitU64 wit_user_library_begin_lifecycle(WitUserProcess *, WitU32, int, WitU64, int, WitU64, WitU64 *);
WitU64 wit_user_library_finish_lifecycle(WitUserProcess *, const WitLibraryRequest *);
WitU64 wit_user_library_shutdown(WitUserProcess *, const WitLibraryRequest *);
WitU64 wit_user_library_release_plan(WitUserProcess *, WitUserLibrary *, WitU64, int, WitU32, WitU64 *);
WitU64 wit_user_library_reader_call(WitUserProcess *, const WitLibraryRequest *, WitU64 *);
WitU64 wit_user_storage_query(WitUserProcess *, WitU64, WitU64, WitU64, WitU64 *);
WitU64 wit_user_file_call(WitUserProcess *, WitU64, WitU64, WitU64, WitU64 *);
void wit_user_file_self_test(WitPageAllocator *);
#endif
