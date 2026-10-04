#ifndef WITOS_WAIT_OBJECTS_H
#define WITOS_WAIT_OBJECTS_H
#include "types.h"
#include "limits.h"
#define WIT_WAIT_OBJECTS_VERSION 1U
#define WIT_WAIT_OBJECTS_ALERTABLE 1U
#define WIT_WAIT_OBJECTS_ALL 2U

typedef struct WitUserWaitRequest {
    WitU32 Version, Size;
    WitU64 Handles;
    WitU32 Count, Flags;
    WitU64 Deadline;
} WitUserWaitRequest;

typedef struct WitUserApc {
    WitU64 Callback, Argument;
} WitUserApc;

WIT_STATIC_ASSERT(sizeof(WitUserWaitRequest) == 32, "Object wait request ABI");
WIT_STATIC_ASSERT(sizeof(WitUserApc) == 16, "APC record ABI");
#endif
