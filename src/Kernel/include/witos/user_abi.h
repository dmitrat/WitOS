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
#include "console_info.h"
#include "code_memory.h"
#include "file_io.h"
#include "storage_query.h"
#include "library.h"

/* Experimental x64 interrupt ABI, not a stable public SDK.
 * INT 0x80: RAX=call, RCX/RDX/R8=arguments; RAX=status, RDX=result.
 * Other GPRs and baseline x87/SSE state survive; flags are reset to 0x202. */
#define WIT_ABI_VERSION 48U
#define WIT_ABI_STARTUP_SIZE 24U
/* Existing single-module compiler TLS page layout; not a Windows TEB. */
#define WIT_COMPILER_TLS_DATA_OFFSET 256U
/* Set current diagnostic UTF-16 name(pointer, units, flags=0); query whole snapshot. */
#define WIT_CALL_THREAD_NAME_SET 43U
#define WIT_CALL_THREAD_NAME_QUERY 44U
/* Atomic current CPU preservation/selector snapshot (buffer, size, version). */
#define WIT_CALL_CPU_CONTEXT_QUERY 45U
/* Snapshot(reference or current pseudo, destination, exact size); no suspension. */
#define WIT_CALL_THREAD_CONTEXT_GET 46U
#define WIT_CALL_THREAD_SUSPEND 47U
#define WIT_CALL_THREAD_RESUME 48U
#define WIT_CALL_THREAD_CONTEXT_SET 49U
#define WIT_CALL_THREAD_CONTEXT_RESTORE 50U
#define WIT_CALL_THREAD_CONTEXT_METADATA 51U
/* Acquire(reference, output, size), query(token, output, size), release(token,0,0). */
#define WIT_CALL_STACK_LEASE_ACQUIRE 52U
#define WIT_CALL_STACK_LEASE_QUERY 53U
#define WIT_CALL_STACK_LEASE_RELEASE 54U
/* Register(callback or zero, version, flags=0); query(token, out, size);
 * continue(token, context, size) transfers execution; reject(token,0,0) is fatal. */
#define WIT_CALL_EXCEPTION_REGISTER 55U
#define WIT_CALL_EXCEPTION_QUERY 56U
#define WIT_CALL_EXCEPTION_CONTINUE 57U
#define WIT_CALL_EXCEPTION_REJECT 58U
/* Begin a software scope (validated caller context, size, opaque 32-bit code) -> token. */
#define WIT_CALL_EXCEPTION_BEGIN 59U
/* Arm(default opaque code,0,0), then publish diagnostic snapshot(pointer,size,version). */
#define WIT_CALL_FATAL_ARM 60U
#define WIT_CALL_FATAL_REPORT 61U
/* Unwind(current token, complete transfer request, exact size). */
#define WIT_CALL_EXCEPTION_UNWIND 62U
/* Lifecycle-aware current-thread completion(code,0,0). User space runs its
 * TLS/runtime notifications first; the kernel never performs managed cleanup.
 * This is a completion assertion, not authority over another thread. */
#define WIT_CALL_THREAD_COMPLETE 63U
/* Create(request, exact size, reserved=0) -> independently closable reference. */
#define WIT_CALL_THREAD_CREATE_REFERENCE 64U
#define WIT_CALL_CODE_MEMORY 65U
#define WIT_CALL_FILE 66U
#define WIT_CALL_STORAGE_QUERY 67U
#define WIT_CALL_LIBRARY 68U
#define WIT_PROCESS_ABRUPT_THREAD_EXIT 0xFFFF0002ULL
#define WIT_CALL_QUERY 0U
#define WIT_CALL_WRITE 1U
#define WIT_CALL_EXIT 2U
#define WIT_CALL_CLOSE 3U
/* Memory calls operate only on the current component's dynamic arena.
 * Reserve(size, alignment) returns a base; commit/protect(base, size, protection),
 * decommit(base, size), release(exact reservation base) return zero result.
 * Nonzero sizes and addresses are page-aligned; alignment is a power of two >= 4 KiB.
 * Commit preserves existing pages and rolls back all additions on failure.
 * Protect is all-or-nothing; decommit is idempotent within one reservation. */
#define WIT_CALL_MEMORY_RESERVE 4U
#define WIT_CALL_MEMORY_COMMIT 5U
#define WIT_CALL_MEMORY_DECOMMIT 6U
#define WIT_CALL_MEMORY_PROTECT 7U
#define WIT_CALL_MEMORY_RELEASE 8U
/* Create(entry, argument, flags=0) -> join handle. DETACHED returns zero,
 * retains a private identity while live, and automatically reaps on exit.
 * Join consumes a joinable handle. Raw exit is local in the ordinary native
 * profile. Coordinated full-runtime admission makes raw exit component-fatal;
 * orderly user-space lifecycle uses THREAD_COMPLETE. */
#define WIT_THREAD_DETACHED 1U
#define WIT_THREAD_LIBRARY_NOTIFICATIONS 2U
#define WIT_CALL_THREAD_CREATE 9U
/* Yield result is 1 if this call selected another thread, otherwise 0. */
#define WIT_CALL_THREAD_YIELD 10U
#define WIT_CALL_THREAD_EXIT 11U
#define WIT_CALL_THREAD_JOIN 12U
#define WIT_CALL_CLOCK_READ 13U
#define WIT_CALL_CLOCK_FREQUENCY 14U
#define WIT_CALL_THREAD_SLEEP 15U
#define WIT_CALL_EVENT_CREATE 16U
#define WIT_CALL_EVENT_SET 17U
#define WIT_CALL_EVENT_RESET 18U
#define WIT_CALL_EVENT_WAIT 19U
/* Query(buffer, exact size, version) atomically copies WitUserMemoryInfo.
 * The whole buffer must be writable. Result is bytes copied, or zero on failure. */
#define WIT_CALL_MEMORY_QUERY 20U
/* A separate, interrupt-independent monotonic domain. Counts are nonnegative
 * signed-64 compatible; frequency is counts/second. Absolute deadlines use
 * this counter, never the delivered-PIT clock. All-ones means infinite. */
#define WIT_CALL_MONOTONIC_READ 21U
#define WIT_CALL_MONOTONIC_FREQUENCY 22U
#define WIT_CALL_SLEEP_UNTIL 23U
#define WIT_CALL_EVENT_WAIT_UNTIL 24U
/* Borrow the current thread's existing generation-bearing handle. No new
 * handle is granted; identity comes from kernel state, not writable raw TLS. */
#define WIT_CALL_THREAD_CURRENT 25U
/* Reset(base, size, flags=0) eagerly zeroes committed dynamic pages.
 * Validate the whole range first; retain commitment, ownership and protection. */
#define WIT_CALL_MEMORY_RESET 26U
/* ThreadQuery(buffer, exact size, version) copies one atomic current-thread
 * snapshot. Result is bytes copied, or zero; no handle is allocated. */
#define WIT_CALL_THREAD_QUERY 27U
/* Process data-memory barrier: all arguments zero; no allocation or parking.
 * Current UP backend only. Unsupported CPU topology must never report success. */
#define WIT_CALL_PROCESS_WRITE_BARRIER 28U
/* Largest architecturally reported CPU cache bytes; zero arguments.
 * Unsupported/unknown discovery returns UNSUPPORTED and zero result. */
#define WIT_CALL_CPU_CACHE_SIZE 29U
/* WaitAnyUntil(user handle array, count, absolute monotonic deadline).
 * All handles are validated before consuming one signal. Result is winner index. */
#define WIT_CALL_EVENT_WAIT_ANY_UNTIL 30U
/* Create a kernel-controlled manual memory-pressure event; all arguments zero.
 * Returned handle permits waiting and closing, never user signaling/reset. */
#define WIT_CALL_MEMORY_PRESSURE_EVENT 31U
/* Copy a uint64 monotonic value to a completely writable user buffer.
 * Query(buffer, exact size=8, selector); IF-disabled validation/copy-out.
 * Result is 8 bytes copied on success, zero on failure. No TLS/heap required. */
#define WIT_CALL_MONOTONIC_QUERY 32U
/* Random(buffer, size, reserved=0): validate the entire destination with IF
 * clear before consuming generator state or writing. Zero is a no-op.
 * A finite work quota bounds nonpreemptible generation per syscall. */
#define WIT_CALL_RANDOM 33U
/* Duplicate(current pseudo or reference, output token pointer, rights; 0=same).
 * Snapshot(reference, output, exact sizeof(WitThreadReferenceInfo)). */
#define WIT_CALL_THREAD_REFERENCE_DUPLICATE 34U
#define WIT_CALL_THREAD_REFERENCE_QUERY 35U
/* Scalar current native DWORD ID, zero arguments, no writable TLS authority. */
#define WIT_CALL_THREAD_NATIVE_ID 36U
#define WIT_CALL_OBJECT_WAIT 37U
#define WIT_CALL_APC_QUEUE 38U
#define WIT_CALL_APC_DEQUEUE 39U
#define WIT_CALL_EVENT_CREATE_RIGHTS 40U
#define WIT_CALL_CONSOLE_WRITE 41U
/* Current processor: exact 4-byte {group:u16, number:u8, reserved:u8}. */
#define WIT_CALL_PROCESSOR_QUERY 42U
#define WIT_EVENT_ACCESS_WAIT 4U
#define WIT_EVENT_ACCESS_SIGNAL 8U
#define WIT_MONOTONIC_COUNTER 0U
#define WIT_MONOTONIC_HZ 1U
#define WIT_MONOTONIC_MAX 0x7FFFFFFFFFFFFFFFULL
/* Legacy calls 13-19 use absolute delivered PIT ticks. Zero polls; all-ones waits forever.
 * The frequency is nominal; this bootstrap clock pauses while IRQ0 is disabled. */
#define WIT_CLOCK_FREQUENCY 100ULL
#define WIT_WAIT_INFINITE 0xFFFFFFFFFFFFFFFFULL
#define WIT_EVENT_MANUAL_RESET 1ULL
#define WIT_EVENT_INITIAL_SIGNALED 2ULL
/* Kernel-selected FS base: self pointer, thread handle, initial argument,
 * 32-bit native last-error, 32-bit reserved zero, then application storage.
 * The error word is caller-writable state, never authority or kernel status. */
#define WIT_TLS_SELF_OFFSET 0U
#define WIT_TLS_HANDLE_OFFSET 8U
#define WIT_TLS_ARGUMENT_OFFSET 16U
#define WIT_TLS_LAST_ERROR_OFFSET 24U
#define WIT_TLS_DATA_OFFSET 32U
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
#define WIT_STATUS_DEADLOCK 11U
#define WIT_STATUS_BUSY 12U
#define WIT_STATUS_TIMED_OUT 13U
#define WIT_STATUS_CLOSED 14U
#define WIT_STATUS_APC_PENDING 15U
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
