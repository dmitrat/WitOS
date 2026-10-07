#ifndef WITOS_USER_ABI_H
#define WITOS_USER_ABI_H
#include "types.h"
#include "limits.h"
#include "image_info.h"
#include "memory_info.h"
#include "thread_info.h"
#include "thread_name.h"
#include "cpu_context_info.h"
#include "thread_context.h"
#include "stack_lease.h"
#include "exception.h"
#include "fatal_info.h"
#include "thread_reference.h"
#include "wait_objects.h"
#include "code_memory.h"
#include "file_io.h"
#include "storage_query.h"
#include "library.h"
#include "process_state.h"

/* ABI-1 of RFC 0011 v3 (sections 6 and 7): the system calls between the nano-kernel and the system layer.
 * The ABI is experimental until plan step K8 declares 1.0; since step K1 a call number, a status value and a rights
 * bit are never reused (RFC 0011 section 10.1). Transport (section 6.1): on x64 INT 0x80 with RAX=call, RCX/RDX/R8
 * arguments, RAX=status and RDX=result, other GPRs and x87/SSE state preserved, RFLAGS reset to 0x202; on ARM64
 * SVC #0 with x8=call, x0-x2 arguments, x0=status and x1=result. A structure passed by pointer starts with Version
 * and Size; an unknown version is UNSUPPORTED, a wrong size INVALID_ARGUMENT; unused arguments are zero. */
#define WIT_ABI_VERSION 53U
/* QUERY result: the low 32 bits are WIT_ABI_VERSION, the high 32 bits the mask of the families present. */
#define WIT_ABI_FEATURE_CHANNELS 1U
#define WIT_ABI_FEATURE_DEVICES 2U
#define WIT_ABI_FEATURE_PROCESSES 4U
#define WIT_ABI_FEATURE_UTC 8U
#define WIT_ABI_FEATURE_SMP 16U
#define WIT_ABI_FEATURES 0U
#define WIT_ABI_STARTUP_SIZE 24U
/* Existing single-module compiler TLS page layout of the frozen line; not a Windows TEB. */
#define WIT_COMPILER_TLS_DATA_OFFSET 256U

/* Kernel, handles and the own process (RFC 0011 section 7.1). */
/* Query(0, 0, 0) -> version and feature mask. */
#define WIT_CALL_QUERY 0U
/* Exit(code, 0, 0): every thread of the process ends; does not return. */
#define WIT_CALL_PROCESS_EXIT 1U
/* Close(handle, 0, 0). Closing never terminates an object's activity. */
#define WIT_CALL_HANDLE_CLOSE 2U
/* Duplicate(handle or WIT_THREAD_SELF, output pointer, rights; 0 = the same) -> 8 bytes written.
 * Rights may only be removed. Thread and event handles; channel endpoints join in step K2. */
#define WIT_CALL_HANDLE_DUPLICATE 3U
/* Write(kernel-log handle, buffer, length <= WIT_DEBUG_WRITE_MAX) -> bytes written. The kernel's last-resort
 * output through the board console; it is not the terminal of RFC 0020. */
#define WIT_CALL_DEBUG_WRITE 4U

/* Memory (RFC 0011 section 7.2). Calls operate on the current process's dynamic arena. Reserve(size, alignment)
 * returns a base; commit/protect(base, size, protection), decommit(base, size), release(exact reservation base)
 * return zero. Nonzero sizes and addresses are page-aligned; alignment is a power of two >= 4 KiB. Commit preserves
 * existing pages and rolls back all additions on failure; protect is all-or-nothing; decommit is idempotent within
 * one reservation. */
#define WIT_CALL_MEMORY_RESERVE 10U
#define WIT_CALL_MEMORY_COMMIT 11U
#define WIT_CALL_MEMORY_DECOMMIT 12U
#define WIT_CALL_MEMORY_PROTECT 13U
#define WIT_CALL_MEMORY_RELEASE 14U
/* Reset(base, size, 0) eagerly zeroes committed dynamic pages after validating the whole range; commitment,
 * ownership and protection are retained. */
#define WIT_CALL_MEMORY_RESET 15U
/* Query(buffer, exact size, version) atomically copies WitUserMemoryInfo; the result is the bytes copied. */
#define WIT_CALL_MEMORY_QUERY 16U
/* Create(0, 0, 0) -> a kernel-controlled manual event, set under physical or quota pressure, waitable only. */
#define WIT_CALL_MEMORY_PRESSURE_EVENT 17U
/* 18 MEMORY_OBJECT_CREATE, 19 MEMORY_OBJECT_MAP and 20 CODE_PUBLISH arrive with plan step K5. */

/* Threads and contexts (RFC 0011 section 7.3). */
/* Create(WitThreadCreateRequest, exact size, 0) -> thread handle with every thread right: the one form. The handle
 * observes the thread's lifetime (OBJECT_WAIT, THREAD_QUERY); closing it detaches, and the thread's stack and TLS are
 * reclaimed when it exits. */
#define WIT_CALL_THREAD_CREATE 30U
/* Exit(code, 0, 0): the current thread ends; does not return. The one exit: lifecycle notifications and the orderly
 * completion of a runtime's thread are user space's business. */
#define WIT_CALL_THREAD_EXIT 31U
/* Yield(0, 0, 0) -> 1 when this call selected another thread, otherwise 0. */
#define WIT_CALL_THREAD_YIELD 32U
/* 33 THREAD_SET_TLS arrives with plan step K5. */
/* Query(thread handle or WIT_THREAD_SELF, buffer, exact size) copies one atomic snapshot of the thread
 * (WitUserThreadInfo: identity, stack, state, exit code, suspend count, the handle's rights); the caller's Version
 * and Size in the buffer select the record, and the result is the bytes copied. The QUERY right, or a context right:
 * a context carries the same prefix. */
#define WIT_CALL_THREAD_QUERY 34U
/* Suspend/resume(thread handle, 0, 0) -> the previous suspend count. */
#define WIT_CALL_THREAD_SUSPEND 35U
#define WIT_CALL_THREAD_RESUME 36U
/* Get/set(thread handle or WIT_THREAD_SELF for get, WitThreadContext, exact size). Set requires a suspended target. */
#define WIT_CALL_THREAD_CONTEXT_GET 37U
#define WIT_CALL_THREAD_CONTEXT_SET 38U
/* Query(buffer, exact size, version) copies the processor's context profile (WitCpuContextInfo): the register block
 * and floating-point state a thread context carries. */
#define WIT_CALL_CONTEXT_PROFILE 39U
/* Activate(thread handle, callback, argument): make the target run the callback. Step K1.1 queues it for the target's
 * next alertable wait (dequeued through the transitional APC_DEQUEUE); step K1.3 delivers it through the fault
 * callback with the interrupted context. */
#define WIT_CALL_THREAD_ACTIVATE 40U
/* 41 THREAD_AFFINITY arrives with plan step K7. */

/* Events, waits, time and entropy (RFC 0011 section 7.4). */
/* Create(flags, rights, 0) -> event handle. Rights 0 means WAIT and SIGNAL. */
#define WIT_CALL_EVENT_CREATE 50U
#define WIT_CALL_EVENT_SET 51U
#define WIT_CALL_EVENT_RESET 52U
/* Wait(WitUserWaitRequest, exact size, 0) -> index of the object that completed. The one wait: every handle is
 * validated before any signal is consumed, and one winner is published atomically. */
#define WIT_CALL_OBJECT_WAIT 53U
/* Sleep(absolute monotonic deadline, 0, 0). */
#define WIT_CALL_SLEEP_UNTIL 54U
/* Read/frequency(clock, 0, 0) -> the counter value or its frequency in counts per second. Deadlines use the
 * monotonic clock; all ones means infinite. UTC arrives with plan step K6. */
#define WIT_CALL_CLOCK_READ 55U
#define WIT_CALL_CLOCK_FREQUENCY 56U
/* Random(buffer, size <= WIT_ABI_MAX_RANDOM, 0) -> bytes written; the whole destination is validated first. */
#define WIT_CALL_RANDOM 57U

/* Faults (RFC 0011 section 7.5). Register(callback or zero, version, flags=0); query(token, buffer, exact size)
 * copies the WitUserExceptionInfo of the current delivery; continue(token, WitUserExceptionTransfer, exact size)
 * resumes the validated context and retires the record and the abandoned ancestors through RetireThroughToken;
 * reject(token, 0, 0) ends the process with the original fault. Continue does not return on success. */
#define WIT_CALL_EXCEPTION_REGISTER 60U
#define WIT_CALL_EXCEPTION_QUERY 61U
#define WIT_CALL_EXCEPTION_CONTINUE 62U
#define WIT_CALL_EXCEPTION_REJECT 63U
/* 70-72 channels (K2), 80-85 devices (K3), 90-92 processes (K5). */

/* Processors (RFC 0011 section 7.9). Query(buffer, exact 4 bytes, 0): the current processor as
 * {group:u16, number:u8, reserved:u8}; topology arrives with plan step K7. */
#define WIT_CALL_PROCESSOR_QUERY 93U
/* Barrier(0, 0, 0): a process data-memory barrier on the sole online processor; unsupported topology fails. */
#define WIT_CALL_PROCESS_WRITE_BARRIER 94U

/* Transitional calls of the current implementation. RFC 0011 section 8 merges, removes or moves each of them in the
 * plan step named; a retired number is never reused. */
/* 200-205 were retired at step K1.2: THREAD_CREATE_SIMPLE, THREAD_JOIN and THREAD_COMPLETE folded into
 * THREAD_CREATE, OBJECT_WAIT and THREAD_EXIT; THREAD_REFERENCE_QUERY, THREAD_NATIVE_ID and THREAD_CONTEXT_METADATA
 * into THREAD_QUERY. */
/* Dequeue(buffer, exact size, 0) -> one queued activation; delivery becomes push (K1.3). */
#define WIT_CALL_APC_DEQUEUE 206U
/* Query(buffer, exact 8 bytes, selector) copies the monotonic counter or frequency; merges into CLOCK_READ (K6). */
#define WIT_CALL_MONOTONIC_QUERY 207U
/* CacheSize(0, 0, 0) -> the largest architecturally reported cache; merges into PROCESSOR_QUERY (K7). */
#define WIT_CALL_CPU_CACHE_SIZE 208U
/* Restore(WitThreadContext, exact size, version) of the current thread; user-space code after K8. */
#define WIT_CALL_THREAD_CONTEXT_RESTORE 209U
/* Stack leases, software exception scopes and fatal reports leave with the Windows-form line (K8). */
#define WIT_CALL_STACK_LEASE_ACQUIRE 210U
#define WIT_CALL_STACK_LEASE_QUERY 211U
#define WIT_CALL_STACK_LEASE_RELEASE 212U
#define WIT_CALL_EXCEPTION_BEGIN 213U
#define WIT_CALL_FATAL_ARM 214U
#define WIT_CALL_FATAL_REPORT 215U
/* Thread names leave for the libc (K8). */
#define WIT_CALL_THREAD_NAME_SET 216U
#define WIT_CALL_THREAD_NAME_QUERY 217U
/* Code memory dissolves into memory objects (K5); its unwind validation leaves (K8). */
#define WIT_CALL_CODE_MEMORY 218U
/* The file namespace, the boot package, native libraries and the process state leave for the system layer (K8). A
 * thread that exits while it owes the DLL lifecycle its notifications ends the component with this code. */
#define WIT_PROCESS_ABRUPT_THREAD_EXIT 0xFFFF0002ULL
#define WIT_CALL_FILE 219U
#define WIT_CALL_STORAGE_QUERY 220U
#define WIT_CALL_LIBRARY 221U
#define WIT_CALL_PROCESS_STATE 222U

/* Flags of WitThreadCreateRequest beside START_SUSPENDED (1): LIBRARY_NOTIFICATIONS follows the frozen line's DLL
 * thread lifecycle and leaves with it (K8). */
#define WIT_THREAD_LIBRARY_NOTIFICATIONS 2U
/* Rights (RFC 0011 section 6.1): a bit is never reused, and 2 (the join right of the retired join capability) is
 * retired. WAIT and SIGNAL belong to events and the kernel log; QUERY, GET_CONTEXT, SET_CONTEXT and SUSPEND_RESUME to
 * threads; ACTIVATE arrives with K1.3, DUPLICATE and TRANSFER with K2. */
#define WIT_RIGHT_WRITE 1U
#define WIT_RIGHT_WAIT 4U
#define WIT_RIGHT_SIGNAL 8U
#define WIT_RIGHT_QUERY 16U
#define WIT_RIGHT_GET_CONTEXT 32U
#define WIT_RIGHT_SET_CONTEXT 64U
#define WIT_RIGHT_SUSPEND_RESUME 128U
#define WIT_RIGHT_THREAD_ALL 244U
#define WIT_EVENT_ACCESS_WAIT WIT_RIGHT_WAIT
#define WIT_EVENT_ACCESS_SIGNAL WIT_RIGHT_SIGNAL
/* Clocks of CLOCK_READ and CLOCK_FREQUENCY. UTC is UNSUPPORTED until plan step K6. */
#define WIT_CLOCK_MONOTONIC 0U
#define WIT_CLOCK_UTC 1U
/* Selectors of the transitional MONOTONIC_QUERY. */
#define WIT_MONOTONIC_COUNTER 0U
#define WIT_MONOTONIC_HZ 1U
/* Monotonic counts are nonnegative signed-64 compatible; a deadline of all ones waits forever. */
#define WIT_MONOTONIC_MAX 0x7FFFFFFFFFFFFFFFULL
#define WIT_WAIT_INFINITE 0xFFFFFFFFFFFFFFFFULL
#define WIT_EVENT_MANUAL_RESET 1ULL
#define WIT_EVENT_INITIAL_SIGNALED 2ULL
/* Kernel-selected raw TLS block of the frozen line (leaves with it at K8): self pointer, thread handle, initial
 * argument, 32-bit native last-error, 32-bit reserved zero, then application storage. */
#define WIT_TLS_SELF_OFFSET 0U
#define WIT_TLS_HANDLE_OFFSET 8U
#define WIT_TLS_ARGUMENT_OFFSET 16U
#define WIT_TLS_LAST_ERROR_OFFSET 24U
#define WIT_TLS_DATA_OFFSET 32U
/* Statuses (RFC 0011 section 6.2). 17 is retired with the library family at K8; 18 PEER_CLOSED arrives with K2. */
#define WIT_STATUS_OK 0U
#define WIT_STATUS_UNSUPPORTED 1U
#define WIT_STATUS_BAD_HANDLE 2U
#define WIT_STATUS_DENIED 3U
#define WIT_STATUS_BAD_ADDRESS 4U
#define WIT_STATUS_TOO_LARGE 5U
#define WIT_STATUS_INVALID_ARGUMENT 6U
#define WIT_STATUS_WRONG_TYPE 7U
#define WIT_STATUS_NO_MEMORY 8U
#define WIT_STATUS_NOT_RESERVED 9U
#define WIT_STATUS_NOT_COMMITTED 10U
#define WIT_STATUS_DEADLOCK 11U /* The join cycle check left at K1.2; the DLL lifecycle still returns it (K8). */
#define WIT_STATUS_BUSY 12U
#define WIT_STATUS_TIMED_OUT 13U
#define WIT_STATUS_CLOSED 14U
/* A wait ended because an activation was delivered to the thread. */
#define WIT_STATUS_INTERRUPTED 15U
#define WIT_STATUS_NOT_FOUND 16U
#define WIT_STATUS_INITIALIZATION_FAILED 17U
#define WIT_MEMORY_NONE 0U
#define WIT_MEMORY_READ 1U
#define WIT_MEMORY_WRITE 2U

typedef struct WitUserStartup {
    WitU32 Version;
    WitU32 Size;
    WitU64 ConsoleHandle;
    WitU64 ImageInfo; /* Immutable WitUserImageInfo for PE images; zero for raw fixtures. */
} WitUserStartup;

WIT_STATIC_ASSERT(sizeof(WitUserStartup) == WIT_ABI_STARTUP_SIZE, "User startup ABI");
#endif
