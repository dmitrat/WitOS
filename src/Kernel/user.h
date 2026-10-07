#ifndef WITOS_USER_H
#define WITOS_USER_H
#include "witos/arch.h"
#include "witos/arch_types.h"
#include "witos/user_layout.h"
#include "witos/handles.h"
#include "witos/limits.h"
#include "witos/files.h"
#include "witos/events.h"
#include "witos/channels.h"
#include "witos/memory_object.h"
#include "witos/memory.h"
#include "witos/pe.h"
#include "witos/virtual_gap.h"

_Static_assert(WIT_WAIT_ANY_CAPACITY == WIT_EVENT_CAPACITY, "Wait-any event capacity");
_Static_assert(WIT_PE_MAX_SECTIONS == WIT_IMAGE_INFO_MAX_RANGES, "Image range capacities");
_Static_assert(WIT_USER_IMAGE_INFO_OFFSET + WIT_IMAGE_INFO_SIZE <= 4096, "Image information page bound");

/* Private code-mapping backend; not enabled by ordinary memory syscalls. */
#define WIT_CODE_EXECUTE 4ULL
typedef WitVirtualRange WitUserReservation;

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
    /* Per reservation: the memory object it maps (its nonzero number; zero for a plain reservation) and the rights
     * of the handle that mapped it, which bound its protection. */
    WitU32 MappedObjects[WIT_RUNTIME_RESERVATION_CAPACITY];
    WitU32 MappedRights[WIT_RUNTIME_RESERVATION_CAPACITY];
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
    WitWaitSleep,
    WitWaitObjects
} WitUserWaitKind;

/* A pending activation: the requester's callback and argument (RFC 0011 section 7.5). */
typedef struct WitUserActivation {
    WitU64 Callback, Argument;
} WitUserActivation;

typedef struct WitUserThread {
    WitUserThreadState State;
    WitU32 LibraryNotifications, LibraryPhase, LibraryRequired;
    WitU32 NativeId;
    WitU32 SuspendCount;
    WitUserExceptionInfo Exception;
    WitUserExceptionInfo ExceptionParents[WIT_EXCEPTION_MAX_DEPTH - 1];
    WitU32 ExceptionDepth;
    WitU32 NameLength;
    WitU16 Name[WIT_THREAD_NAME_CAPACITY];
    WitUserWaitKind WaitKind;
    WitU64 WaitHandle;
    WitU64 WaitHandles[WIT_WAIT_ANY_CAPACITY];
    WitU32 WaitCount;
    WitU32 WaitAll;
    WitUserActivation Activations[WIT_ACTIVATION_CAPACITY]; /* Pending, oldest first. */
    WitU32 ActivationCount;
    WitU64 Deadline; /* Absolute monotonic deadline of a parked wait or sleep; all ones waits forever. */
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
    WitU32 TlsCallbacksRva, TlsCallbackCount;
} WitUserLibrary;

/* A library with an entry point takes thread notifications and the detach; one with an entry point or PE TLS callbacks
 * takes the process attach. */
int wit_user_library_participates(const WitUserLibrary *library);
int wit_user_library_attaches(const WitUserLibrary *library);

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
    WitChannelTable Channels;
    WitMemoryObjectTable MemoryObjects;
    /* Per device descriptor: the handles of this component to it, in the table or in flight (K3.1). */
    WitU32 DeviceReferences[WIT_DEVICE_CAPACITY];
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
    /* The package file the main image came from (P6.4.j3b): its name's offset in the package, 0 bytes for none. */
    WitU32 ImageNameBytes;
    WitU64 ImageNameOffset;
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
    WitU32 RuntimeProfile; /* The full runtime profile: budget diagnostics only. */
    WitU64 AbruptThreadId, AbruptThreadCode, ThreadExits;
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
    WitU64 ThreadReaps;
    WitU64 NextWaitOrder;
    WitU32 MemoryPressureLow;
    WitU64 MemoryPressureEvents[WIT_RUNTIME_EVENT_CAPACITY];
    WitU64 EventParks;
    WitU64 EventWakes;
    /* Parks and wakes of waits on thread handles alone (joins); waits that include an event count above. */
    WitU64 ThreadWaitParks;
    WitU64 ThreadWaitWakes;
    WitU64 WaitTimeouts;
    WitU64 WaitCloses;
    WitU64 WaitInterruptions; /* Waits and sleeps an activation ended. */
    WitU64 ActivationDeliveries;
    WitU64 ChannelSends, ChannelReceives, ChannelDrops; /* Messages queued, delivered and dropped with an endpoint. */
    WitU64 IdleHalts;
    WitU64 IdleTicks;
    /* The state every module shares (P6.4.j3a): the environment's records and their final terminator, and the
     * current directory, canonical UTF-8 from '/'. */
    WitU32 EnvironmentVariables, EnvironmentUnits;
    WitU16 Environment[WIT_ENVIRONMENT_UNITS + 1];
    WitU32 DirectoryBytes;
    WitU8 Directory[WIT_PROCESS_PATH_BYTES];
} WitUserProcess;

void wit_user_exception_initialize(WitUserProcess *);
void wit_user_exception_clear(WitUserThread *);
WitU64 wit_user_exception_begin(WitUserProcess *, WitU64, WitU64, WitU64, WitU64 *);
WitU64 wit_user_exception_register(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_exception_query(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_exception_continue(WitUserProcess *, WitU64, WitU64, WitU64);
int wit_user_exception_deliver(WitUserProcess *, WitArchFrame *, WitU64, WitU64, WitU64);
/* Enters the fault callback with a record on the thread's user-returning frame and publishes the record; 0 leaves the
 * frame, the record and the token sequence unchanged (no callback, a delivery in flight, no room on the stack). */
int wit_user_exception_enter(WitUserProcess *, WitUserThread *, WitArchFrame *, WitUserExceptionInfo *);
WitArchFrame *wit_user_exception_trap(WitArchFrame *, WitU64, WitU64, WitU64);
/* Ends the component as a contained fault of the current thread described by the state. */
WIT_NORETURN void wit_user_fault_state(WitU64, WitU64, WitU64, const WitArchFaultState *);
void wit_user_context_snapshot(WitThreadContext *, const WitUserThread *, const WitArchFrame *);
WitU64 wit_user_context_validate(WitUserProcess *, WitUserThread *, const WitThreadContext *, int);
void wit_user_stack_leases_initialize(WitUserProcess *);
void wit_user_stack_leases_exit(WitUserProcess *, WitU64);
int wit_user_stack_leases_owned(const WitUserProcess *, WitU64);
int wit_user_stack_leased(const WitUserProcess *, WitU64, int);
WitU64 wit_user_stack_lease_acquire(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_stack_lease_query(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_stack_lease_release(WitUserProcess *, WitU64);
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
void wit_user_activations_clear(WitUserThread *);
WitU64 wit_user_thread_activate(WitUserProcess *, WitU64, WitU64, WitU64);
/* Delivers the oldest pending activation of the running thread before its frame returns to user mode: 1 when the
 * frame now enters the fault callback. An activation the thread's stack cannot take ends the component as a fault. */
int wit_user_activation_deliver(WitUserProcess *, WitUserThread *);
/* Ends the wait or sleep the thread is parked in with INTERRUPTED (an activation was delivered to it). */
void wit_user_wait_interrupt(WitUserProcess *, WitUserThread *);
WitU64 wit_user_objects_poll(WitUserProcess *, const WitU64 *, WitU32, int, int, WitU64 *);
WitU64 wit_user_object_wait(WitUserProcess *, WitU64, WitU64, WitU64, WitU64, WitU64 *);
/* The process-internal object wait on copied handles: consumes a ready object, times out or parks the current thread. */
WitU64 wit_user_wait_objects(WitUserProcess *, const WitU64 *, WitU32, int, WitU64, WitU64, WitU64 *);
WitU64 wit_user_reference_target(WitUserProcess *, WitU64, WitU32, WitUserThread **);
WitU64 wit_user_reference_signaled(WitUserProcess *, WitU64, int *);
void wit_user_references_initialize(WitUserProcess *);
void wit_user_references_exit(WitUserProcess *, WitU64, WitU64);
/* The record behind a thread handle, after the handle check for the rights. */
WitU64 wit_user_reference_describe(WitUserProcess *, WitU64, WitU32, const WitUserThreadReference **);
/* HANDLE_DUPLICATE of a thread handle, WIT_THREAD_SELF, an event handle or a channel endpoint. */
WitU64 wit_user_handle_duplicate(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_reference_close(WitUserProcess *, WitU64);
/* Free thread handle records, and a new handle with a record copied from a snapshot (a moved thread handle). */
WitU32 wit_user_reference_free_count(const WitUserProcess *);
WitU64 wit_user_reference_attach(WitUserProcess *, const WitUserThreadReference *, WitU64 *);

/* Channels (RFC 0011 section 7.6): the three calls, the endpoint's close and duplication, its readiness for
 * OBJECT_WAIT (a message queued or the peer closed) and whether a handle is an endpoint's. */
WitU64 wit_user_channel_create(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_channel_send(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_channel_receive(WitUserProcess *, WitU64, WitU64, WitU64, WitU64 *);
WitU64 wit_user_channel_close(WitUserProcess *, WitU64);
WitU64 wit_user_channel_duplicate(WitUserProcess *, WitU64, WitU64, WitU64);
WitU64 wit_user_channel_signaled(WitUserProcess *, WitU64, int *);
int wit_user_channel_handle(WitUserProcess *, WitU64);
/* A thread exited: the thread handles in flight in messages learn the exit as the records in the table do. */
void wit_user_channels_thread_exited(WitUserProcess *, WitU64, WitU64);

/* Memory objects (RFC 0011 section 7.2): creation, mapping, the release of a mapping or a plain reservation, the
 * object's close, duplication, the reference a dropped message held, and whether a handle is an object's. */
WitU64 wit_user_memory_object_create(WitUserProcess *, WitU64, WitU64, WitU64, WitU64 *);
WitU64 wit_user_memory_object_map(WitUserProcess *, WitU64, WitU64, WitU64, WitU64 *);
WitU64 wit_user_memory_unmap(WitUserProcess *, WitU64);
WitU64 wit_user_memory_object_close(WitUserProcess *, WitU64);
WitU64 wit_user_memory_object_duplicate(WitUserProcess *, WitU64, WitU64, WitU64);
void wit_user_memory_object_release(WitUserProcess *, WitU64);
int wit_user_memory_object_handle(WitUserProcess *, WitU64);
int wit_user_memory_object_adopt(WitUserProcess *, WitU32, const WitU64 *, WitU32, WitU32, WitU32 *);
WitU32 wit_user_memory_object_kind(WitUserProcess *, WitU64);

/* Devices (RFC 0011 section 7.7, K3.1): the table as a memory object of the component, acquisition, a region as
 * a memory object, the device handle's close, duplication, the reference a dropped message held, whether a handle
 * is a device's, and the release of every device at the component's end. */
WitU64 wit_user_device_table_grant(WitUserProcess *, WitU32);
WitU64 wit_user_device_acquire(WitUserProcess *, WitU64, WitU64, WitU64, WitU64 *);
WitU64 wit_user_device_memory(WitUserProcess *, WitU64, WitU64, WitU64, WitU64 *);
WitU64 wit_user_device_close(WitUserProcess *, WitU64);
WitU64 wit_user_device_duplicate(WitUserProcess *, WitU64, WitU64, WitU64);
void wit_user_device_release(WitUserProcess *, WitU64);
int wit_user_device_handle(WitUserProcess *, WitU64);
void wit_user_devices_reset(WitUserProcess *);

/* The address space's part: pages without an address of their own, a mapping of an object's pages as a reservation
 * at a chosen or fixed address, and the object a reservation maps. */
int wit_user_space_allocate_pages(WitUserSpace *, WitU64 *, WitU32);
void wit_user_space_free_pages(WitUserSpace *, const WitU64 *, WitU32);
WitU64 wit_user_space_map_object(
    WitUserSpace *, WitU64, WitU64, const WitU64 *, WitU64, WitU32, WitU32, WitU32, WitU64 *);
int wit_user_space_mapping_object(const WitUserSpace *, WitU64, WitU64 *);

WitU64 wit_user_space_take_table(WitUserSpace *space);
void wit_user_space_release_table(WitUserSpace *space, WitU64 page);
int wit_user_space_create_profile(WitUserSpace *, WitPageAllocator *, int);
int wit_user_space_create(WitUserSpace *space, WitPageAllocator *allocator);
int wit_user_space_map(WitUserSpace *space, WitU64 address, int writable, int executable);
WitU64 wit_user_space_physical(const WitUserSpace *space, WitU64 address, int write, int execute);
void wit_user_space_publish_code(WitUserSpace *space, WitU64 address, WitU64 size);
int wit_user_copy_from(const WitUserSpace *space, WitU64 address, WitU8 *buffer, WitU32 size);
WitU64 wit_user_debug_write(WitUserProcess *, WitU64, WitU64, WitU64, WitU64 *);
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

/* Kinds of the self-test memory journal; only runtime-boot kernels record it. */
#define WIT_MEMORY_JOURNAL_RESERVE 1U
#define WIT_MEMORY_JOURNAL_COMMIT 2U
#define WIT_MEMORY_JOURNAL_DECOMMIT 3U
#define WIT_MEMORY_JOURNAL_RESET 4U
#define WIT_MEMORY_JOURNAL_PROTECT 5U
#define WIT_MEMORY_JOURNAL_RELEASE 6U
#if defined(WITOS_TEST_RUNTIME_BOOT)
typedef struct WitMemoryJournalEntry {
    WitU64 Sequence;
    WitU32 Kind, Status;
    WitU64 Address, Size;
} WitMemoryJournalEntry;

/* The ring of the last operations; entry Sequence - 1 sits at index (Sequence - 1) % capacity. */
const WitMemoryJournalEntry *wit_user_memory_journal(WitU64 *count, WitU32 *capacity);
#endif
void wit_user_memory_self_test(WitPageAllocator *pages);
void wit_user_thread_self_test(WitPageAllocator *pages);
void wit_user_native_id_self_test(void);
void wit_user_runtime_unwind_metadata_self_test(WitPageAllocator *);
void wit_user_wait_self_test(WitPageAllocator *pages);
void wit_user_exception_self_test(WitPageAllocator *pages);
void wit_user_channel_self_test(WitPageAllocator *pages);
void wit_user_memory_object_self_test(WitPageAllocator *pages);
void wit_user_device_self_test(WitPageAllocator *pages);
void wit_user_image_self_test(WitPageAllocator *pages);
void wit_user_bootstrap_self_test(WitPageAllocator *pages);
void wit_user_gc_self_test(WitPageAllocator *pages);
void wit_user_tls_self_test(WitPageAllocator *pages);
void wit_user_dynamic_tls_self_test(WitPageAllocator *pages);
/* THREAD_QUERY by handle or WIT_THREAD_SELF; the caller's Version and Size in the buffer select the record. */
WitU64 wit_user_thread_query(WitUserProcess *process, WitU64 handle, WitU64 address, WitU64 size);
/* The WIT_THREAD_CONTEXT_* flags of a thread's context. */
WitU32 wit_user_context_flags(const WitUserThread *target);
void wit_user_pal_self_test(WitPageAllocator *pages);
void wit_user_pal_services_self_test(WitPageAllocator *pages);
void wit_user_wait_any_self_test(WitPageAllocator *pages);
void wit_user_pressure_self_test(WitPageAllocator *pages);
int wit_user_capture_tls(WitUserProcess *process, const WitPeImage *image);
/* THREAD_CREATE: the one form, from a WitThreadCreateRequest in the process's memory. */
WitU64 wit_user_thread_create(WitUserProcess *process, WitU64 input, WitU64 size, WitU64 *result);
WitU64 wit_user_prepare_thread(WitUserProcess *process, WitU32 index, WitU64 entry, WitU64 argument, WitU64 flags);
WitU64 wit_virtual_kernel_root(void);

int wit_user_create(
    WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot, const WitU8 *code, WitU32 code_size);
WitPeStatus wit_user_create_pe_profile(
    WitUserProcess *, WitPageAllocator *, WitU32, const WitU8 *, WitU32, WitU64, const char *, WitU32);
WitPeStatus wit_user_create_named_pe(WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot,
    const WitU8 *file, WitU32 size, WitU64 base, const char *resource_name);
/* Creates a component from a file of the boot package, which the component's module queries then name. */
WitPeStatus wit_user_create_package_pe(
    WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot, const char *name, WitU64 base, WitU32 profile);
WitPeStatus wit_user_create_pe(
    WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot, const WitU8 *file, WitU32 size, WitU64 base);
int wit_user_image_map(WitUserSpace *space, const WitU8 *file, const WitPeImage *plan, WitU64 base);
/* Before the attach of a library with an entry point: every live thread that follows the notification protocol and
 * has not left gets the notification page and handles it lacks, as a thread created after the load would, so its exit
 * detaches the library. Returns the mask of threads that received them; fails with all of them released. */
WitU64 wit_user_thread_require_notifications(WitUserProcess *process, WitU32 *reserved);
void wit_user_thread_release_notifications(WitUserProcess *process, WitU32 reserved);
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
/* Completes every parked wait and sleep whose monotonic deadline has passed. */
void wit_user_wait_expire(WitUserProcess *process, WitU64 now);
WitU64 wit_user_sleep_until(WitUserProcess *process, WitU64 deadline, WitU64 now);
WitU64 wit_user_event_set(WitUserProcess *process, WitU64 handle);
WitU64 wit_user_event_reset(WitUserProcess *process, WitU64 handle);
WitU64 wit_user_event_close(WitUserProcess *process, WitU64 handle);
WitArchFrame *wit_user_syscall(
    WitArchFrame *frame, WitU64 number, WitU64 argument0, WitU64 argument1, WitU64 argument2);

/* One system call of the running component (user_calls.c). Status and Value point into the caller's frame; a
 * join replaces Context with the frame to continue. */
typedef struct WitUserCall {
    WitUserProcess *Process;
    WitArchFrame *Context;
    WitU64 Number;
    WitU64 Argument0, Argument1, Argument2;
    WitU64 *Status;
    WitU64 *Value;
} WitUserCall;

/* Runs one call; returns 0 to finish through the common completion of wit_user_syscall, or the frame to
 * resume as it is. */
WitArchFrame *wit_user_call(WitUserCall *call);

/* Scheduler services of user.c for the call table. */
WitUserProcess *wit_user_current(void);
WIT_NORETURN void wit_user_finish(WitUserState state, WitU64 code);
WitArchFrame *wit_user_yield(void);
WitArchFrame *wit_user_exit_thread(WitU64 code);
WitU64 wit_user_close_handle(WitU64 handle);
WitArchFrame *wit_user_timer_tick(WitArchFrame *frame);
WIT_NORETURN void wit_user_fault(
    const void *trap, WitU64 trap_size, WitU64 vector, WitU64 error, WitU64 address, const WitArchFaultState *state);
/* Contained user faults since boot, one per [USER-FAULT] line. */
WitU64 wit_user_contained_faults(void);

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
WitU64 wit_user_process_state(WitUserProcess *, WitU64, WitU64, WitU64, WitU64 *);
void wit_user_process_state_reset(WitUserProcess *);
/* Sets (value non-zero) or removes a variable of a component its creator prepares; the kernel's own strings. */
WitU64 wit_user_environment_set(
    WitUserProcess *, const WitU16 *name, WitU32 nameUnits, const WitU16 *value, WitU32 valueUnits);
WitU64 wit_user_file_call(WitUserProcess *, WitU64, WitU64, WitU64, WitU64 *);
void wit_user_file_self_test(WitPageAllocator *);
#endif
