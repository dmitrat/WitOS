#ifndef WITOS_HANDLES_H
#define WITOS_HANDLES_H
#include "user_abi.h"

#define WIT_HANDLE_CAPACITY 8U
#define WIT_HANDLE_CONSOLE 1U
#define WIT_HANDLE_SELF 2U
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
    WitHandleEntry Entries[WIT_HANDLE_CAPACITY];
} WitHandleTable;

void wit_handles_initialize(WitHandleTable *table, WitU32 owner);
WitU64 wit_handle_grant(WitHandleTable *table, WitU32 kind, WitU32 rights);
WitU64 wit_handle_check(WitHandleTable *table, WitU64 token, WitU32 kind, WitU32 rights);
WitU64 wit_handle_close(WitHandleTable *table, WitU64 token);
void wit_handles_close_all(WitHandleTable *table);
#endif
