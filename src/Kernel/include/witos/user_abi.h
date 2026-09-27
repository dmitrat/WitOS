#ifndef WITOS_USER_ABI_H
#define WITOS_USER_ABI_H
#include "types.h"
#include "image_info.h"
#include "memory_info.h"
#include "thread_info.h"

/* Experimental x64 interrupt ABI, not a stable public SDK.
 * INT 0x80: RAX=call, RCX/RDX/R8=arguments; RAX=status, RDX=result.
 * Other GPRs and baseline x87/SSE state survive; flags are reset to 0x202. */
#define WIT_ABI_VERSION 13U
#define WIT_ABI_STARTUP_SIZE 24U
/* Existing single-module compiler TLS page layout; not a Windows TEB. */
#define WIT_COMPILER_TLS_DATA_OFFSET 256U
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
 * Join consumes a joinable handle. Thread exit affects only the caller. */
#define WIT_THREAD_DETACHED 1U
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
#define WIT_MEMORY_NONE 0U
#define WIT_MEMORY_READ 1U
#define WIT_MEMORY_WRITE 2U
#define WIT_ABI_MAX_WRITE 256U

typedef struct WitUserStartup {
    WitU32 Version;
    WitU32 Size;
    WitU64 ConsoleHandle;
    WitU64 ImageInfo; /* Immutable WitUserImageInfo for PE images; zero for raw fixtures. */
} WitUserStartup;
WIT_STATIC_ASSERT(sizeof(WitUserStartup) == WIT_ABI_STARTUP_SIZE, "User startup ABI");
#endif
