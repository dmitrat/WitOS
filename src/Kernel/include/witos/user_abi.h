#ifndef WITOS_USER_ABI_H
#define WITOS_USER_ABI_H
#include "types.h"

/* Experimental x64 interrupt ABI, not a stable public SDK.
 * INT 0x80: RAX=call, RCX/RDX/R8=arguments; RAX=status, RDX=result.
 * Other GPRs and baseline x87/SSE state survive; flags are reset to 0x202. */
#define WIT_ABI_VERSION 3U
#define WIT_ABI_STARTUP_SIZE 16U
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
/* Create(entry, argument, flags=0) -> join handle. Join(handle) blocks and consumes
 * the handle on success. Thread exit affects only the caller; call 2 exits the component. */
#define WIT_CALL_THREAD_CREATE 9U
#define WIT_CALL_THREAD_YIELD 10U
#define WIT_CALL_THREAD_EXIT 11U
#define WIT_CALL_THREAD_JOIN 12U
/* Kernel-selected FS base: self pointer, thread handle, initial argument, then zeroed bytes.
 * This raw TLS block is not yet a compiler/CoreLib TLS layout. */
#define WIT_TLS_SELF_OFFSET 0U
#define WIT_TLS_HANDLE_OFFSET 8U
#define WIT_TLS_ARGUMENT_OFFSET 16U
#define WIT_TLS_DATA_OFFSET 24U
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
#define WIT_MEMORY_NONE 0U
#define WIT_MEMORY_READ 1U
#define WIT_MEMORY_WRITE 2U
#define WIT_ABI_MAX_WRITE 256U

typedef struct WitUserStartup {
    WitU32 Version;
    WitU32 Size;
    WitU64 ConsoleHandle;
} WitUserStartup;
_Static_assert(sizeof(WitUserStartup) == WIT_ABI_STARTUP_SIZE, "User startup ABI");
#endif
