#ifndef WITOS_USER_ABI_FROZEN_H
#define WITOS_USER_ABI_FROZEN_H
#include "user_abi.h"

/* Names the frozen Windows-form line (ADR 0024) uses for calls and constants RFC 0011 v3 renamed in plan step K1.1.
 * Only renames live here; a call whose arguments or meaning changed has no alias and its callers were rewritten.
 * The kernel never includes this header; the frozen line's own headers do. It is removed with that line at K8. */
#define WIT_CALL_EXIT WIT_CALL_PROCESS_EXIT
#define WIT_CALL_CLOSE WIT_CALL_HANDLE_CLOSE
#define WIT_CALL_WRITE WIT_CALL_DEBUG_WRITE
#define WIT_CALL_THREAD_CREATE_REFERENCE WIT_CALL_THREAD_CREATE
#define WIT_CALL_THREAD_REFERENCE_DUPLICATE WIT_CALL_HANDLE_DUPLICATE
#define WIT_CALL_CPU_CONTEXT_QUERY WIT_CALL_CONTEXT_PROFILE
#define WIT_CALL_APC_QUEUE WIT_CALL_THREAD_ACTIVATE
#define WIT_CALL_EVENT_CREATE_RIGHTS WIT_CALL_EVENT_CREATE
#define WIT_CALL_MONOTONIC_READ WIT_CALL_CLOCK_READ
#define WIT_CALL_MONOTONIC_FREQUENCY WIT_CALL_CLOCK_FREQUENCY
#define WIT_STATUS_APC_PENDING WIT_STATUS_INTERRUPTED
#define WIT_THREAD_REFERENCE_CURRENT WIT_THREAD_SELF
#endif
