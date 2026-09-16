#include "witos/handles.h"

void wit_handles_initialize(WitHandleTable *table, WitU32 owner)
{
    table->Owner = owner;
    table->Count = 0;
    for (WitU32 i = 0; i < WIT_HANDLE_CAPACITY; ++i) {
        table->Entries[i].Token = 0;
        table->Entries[i].Kind = 0;
        table->Entries[i].Rights = 0;
        table->Entries[i].Generation = 1;
        table->Entries[i].Live = 0;
    }
}

WitU64 wit_handle_grant(WitHandleTable *table, WitU32 kind, WitU32 rights)
{
    if (table->Owner == 0 || kind == 0) return 0;
    for (WitU32 i = 0; i < WIT_HANDLE_CAPACITY; ++i) {
        WitHandleEntry *entry = &table->Entries[i];
        if (!entry->Live && entry->Generation <= 0xFFFF) {
            entry->Token = ((WitU64)table->Owner << 32) |
                ((WitU64)entry->Generation << 16) | (i + 1);
            entry->Kind = kind;
            entry->Rights = rights;
            entry->Live = 1;
            ++table->Count;
            return entry->Token;
        }
    }
    return 0;
}

static WitHandleEntry *lookup(WitHandleTable *table, WitU64 token)
{
    const WitU32 slot = (WitU32)(token & 0xFFFF);
    WitHandleEntry *entry;
    if ((token >> 32) != table->Owner || slot == 0 || slot > WIT_HANDLE_CAPACITY) return 0;
    entry = &table->Entries[slot - 1];
    return entry->Live && entry->Token == token ? entry : 0;
}

WitU64 wit_handle_check(WitHandleTable *table, WitU64 token, WitU32 kind, WitU32 rights)
{
    WitHandleEntry *entry = lookup(table, token);
    if (entry == 0) return WIT_STATUS_BAD_HANDLE;
    if (entry->Kind != kind) return WIT_STATUS_WRONG_TYPE;
    if ((entry->Rights & rights) != rights) return WIT_STATUS_DENIED;
    return WIT_STATUS_OK;
}

WitU64 wit_handle_close(WitHandleTable *table, WitU64 token)
{
    WitHandleEntry *entry = lookup(table, token);
    if (entry == 0) return WIT_STATUS_BAD_HANDLE;
    entry->Live = 0;
    entry->Rights = 0;
    ++entry->Generation; /* Exhausted generations are never reissued. */
    --table->Count;
    return WIT_STATUS_OK;
}

void wit_handles_close_all(WitHandleTable *table)
{
    for (WitU32 i = 0; i < WIT_HANDLE_CAPACITY; ++i)
        if (table->Entries[i].Live) (void)wit_handle_close(table, table->Entries[i].Token);
}
