#ifndef WITOS_HANDLES_H
#define WITOS_HANDLES_H
#include "user_abi.h"

#define WIT_HANDLE_CAPACITY 16U
#define WIT_RUNTIME_HANDLE_CAPACITY 32U
#define WIT_HANDLE_CONSOLE 1U
#define WIT_HANDLE_SELF 2U
#define WIT_HANDLE_THREAD 3U
#define WIT_HANDLE_EVENT 4U
#define WIT_HANDLE_THREAD_REFERENCE 5U
#define WIT_HANDLE_FILE 6U
#define WIT_HANDLE_LIBRARY 7U
#define WIT_HANDLE_LIBRARY_READER 8U
#define WIT_HANDLE_LIBRARY_LIFECYCLE 9U
#define WIT_RIGHT_READ 16U
#define WIT_RIGHT_WAIT 4U
#define WIT_RIGHT_SIGNAL 8U
WIT_STATIC_ASSERT(WIT_RIGHT_WAIT==WIT_EVENT_ACCESS_WAIT && WIT_RIGHT_SIGNAL==WIT_EVENT_ACCESS_SIGNAL,"Event access ABI");
#define WIT_RIGHT_JOIN 2U
#define WIT_RIGHT_WRITE 1U

typedef struct WitHandleEntry {
    WitU64 Token;
    WitU32 Kind;
    WitU32 Rights;
    WitU32 Generation;
    WitU32 Live;
} WitHandleEntry;

typedef struct WitHandleTable {
    WitU32 Owner;
    WitU32 Count;
    WitU32 Limit;
    WitHandleEntry Entries[WIT_RUNTIME_HANDLE_CAPACITY];
} WitHandleTable;

void wit_handles_initialize(WitHandleTable *table, WitU32 owner);
WitU64 wit_handle_grant(WitHandleTable *table, WitU32 kind, WitU32 rights);
WitU64 wit_handle_check(WitHandleTable *table, WitU64 token, WitU32 kind, WitU32 rights);
WitU64 wit_handle_close(WitHandleTable *table, WitU64 token);
void wit_handles_close_all(WitHandleTable *table);
#endif
