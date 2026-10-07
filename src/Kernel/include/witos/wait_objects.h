#ifndef WITOS_WAIT_OBJECTS_H
#define WITOS_WAIT_OBJECTS_H
#include "types.h"
#include "limits.h"
#define WIT_WAIT_OBJECTS_VERSION 1U
/* Flag 1 (ALERTABLE) was retired at step K1.3 and is never reused: every wait ends with INTERRUPTED when an
 * activation is delivered to the thread. ALL stays UNSUPPORTED (RFC 0011 section 7.4). */
#define WIT_WAIT_OBJECTS_ALL 2U

typedef struct WitUserWaitRequest {
    WitU32 Version, Size;
    WitU64 Handles;
    WitU32 Count, Flags;
    WitU64 Deadline;
} WitUserWaitRequest;

WIT_STATIC_ASSERT(sizeof(WitUserWaitRequest) == 32, "Object wait request ABI");
#endif
