#ifndef WITOS_USER_ABI_H
#define WITOS_USER_ABI_H
#include "types.h"

/* Experimental x64 interrupt ABI, not a stable public SDK.
 * INT 0x80: RAX=call, RCX/RDX/R8=arguments; RAX=status, RDX=result.
 * Other GPRs and baseline x87/SSE state survive; flags are reset to 0x202. */
#define WIT_ABI_VERSION 1U
#define WIT_ABI_STARTUP_SIZE 16U
#define WIT_CALL_QUERY 0U
#define WIT_CALL_WRITE 1U
#define WIT_CALL_EXIT 2U
#define WIT_CALL_CLOSE 3U
#define WIT_STATUS_OK 0U
#define WIT_STATUS_UNSUPPORTED 1U
#define WIT_STATUS_BAD_HANDLE 2U
#define WIT_STATUS_DENIED 3U
#define WIT_STATUS_BAD_ADDRESS 4U
#define WIT_STATUS_TOO_LARGE 5U
#define WIT_STATUS_INVALID_ARGUMENT 6U
#define WIT_STATUS_WRONG_TYPE 7U
#define WIT_ABI_MAX_WRITE 256U

typedef struct WitUserStartup {
    WitU32 Version;
    WitU32 Size;
    WitU64 ConsoleHandle;
} WitUserStartup;
_Static_assert(sizeof(WitUserStartup) == WIT_ABI_STARTUP_SIZE, "User startup ABI");
#endif
