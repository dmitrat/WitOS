#include "witos/handles.h"

void wit_handles_initialize(WitHandleTable *table, WitU32 owner)
{
    table->Owner = owner;
    table->Count = 0;
    table->Limit = WIT_HANDLE_CAPACITY;
    for (WitU32 i = 0; i < WIT_RUNTIME_HANDLE_CAPACITY; ++i) {
        table->Entries[i].Token = 0;
        table->Entries[i].Kind = 0;
        table->Entries[i].Rights = 0;
        table->Entries[i].Generation = 1;
        table->Entries[i].Live = 0;
        table->Entries[i].Object = 0;
    }
}

WitU64 wit_handle_grant_object(WitHandleTable *table, WitU32 kind, WitU32 rights, WitU64 object)
{
    if (table->Owner == 0 || kind == 0) {
        return 0;
    }
    for (WitU32 i = 0; i < table->Limit; ++i) {
        WitHandleEntry *entry = &table->Entries[i];
        if (!entry->Live && entry->Generation <= 0xFFFF) {
            entry->Token = ((WitU64)table->Owner << 32) | ((WitU64)entry->Generation << 16) | (i + 1);
            entry->Kind = kind;
            entry->Rights = rights;
            entry->Object = object;
            entry->Live = 1;
            ++table->Count;
            return entry->Token;
        }
    }
    return 0;
}

WitU64 wit_handle_grant(WitHandleTable *table, WitU32 kind, WitU32 rights)
{
    return wit_handle_grant_object(table, kind, rights, 0);
}

static WitHandleEntry *lookup(const WitHandleTable *table, WitU64 token)
{
    const WitU32 slot = (WitU32)(token & 0xFFFF);
    const WitHandleEntry *entry;
    if ((token >> 32) != table->Owner || slot == 0 || slot > table->Limit) {
        return 0;
    }
    entry = &table->Entries[slot - 1];
    return entry->Live && entry->Token == token ? (WitHandleEntry *)entry : 0;
}

WitU64 wit_handle_check(WitHandleTable *table, WitU64 token, WitU32 kind, WitU32 rights)
{
    WitHandleEntry *entry = lookup(table, token);
    if (entry == 0) {
        return WIT_STATUS_BAD_HANDLE;
    }
    if (entry->Kind != kind) {
        return WIT_STATUS_WRONG_TYPE;
    }
    if ((entry->Rights & rights) != rights) {
        return WIT_STATUS_DENIED;
    }
    return WIT_STATUS_OK;
}

int wit_handle_describe(const WitHandleTable *table, WitU64 token, WitU32 kind, WitU64 *object, WitU32 *rights)
{
    const WitHandleEntry *entry = lookup(table, token);
    *object = 0;
    *rights = 0;
    if (entry == 0 || entry->Kind != kind) {
        return 0;
    }
    *object = entry->Object;
    *rights = entry->Rights;
    return 1;
}

WitU64 wit_handle_close(WitHandleTable *table, WitU64 token)
{
    WitHandleEntry *entry = lookup(table, token);
    if (entry == 0) {
        return WIT_STATUS_BAD_HANDLE;
    }
    entry->Live = 0;
    entry->Rights = 0;
    entry->Object = 0;
    ++entry->Generation; /* Exhausted generations are never reissued. */
    --table->Count;
    return WIT_STATUS_OK;
}

void wit_handles_close_all(WitHandleTable *table)
{
    for (WitU32 i = 0; i < table->Limit; ++i) {
        if (table->Entries[i].Live) {
            (void)wit_handle_close(table, table->Entries[i].Token);
        }
    }
}

WitU32 wit_handles_free_count(const WitHandleTable *table)
{
    WitU32 free = 0;
    for (WitU32 i = 0; i < table->Limit; ++i) {
        if (!table->Entries[i].Live && table->Entries[i].Generation <= 0xFFFF) {
            ++free;
        }
    }
    return free;
}
