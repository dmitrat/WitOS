#include "witos/events.h"

void wit_events_initialize(WitEventTable *table)
{
    table->Count = 0;
    for (WitU32 i = 0; i < WIT_EVENT_CAPACITY; ++i) {
        table->Entries[i].Handle = 0;
        table->Entries[i].ManualReset = 0;
        table->Entries[i].Signaled = 0;
    }
}

WitU64 wit_event_create(WitEventTable *table, WitHandleTable *handles,
    WitU64 flags, WitU32 rights, WitU64 *result)
{
    *result = 0;
    if ((flags & ~(WIT_EVENT_MANUAL_RESET | WIT_EVENT_INITIAL_SIGNALED)) ||
        (rights & ~(WIT_RIGHT_WAIT | WIT_RIGHT_SIGNAL))) return WIT_STATUS_INVALID_ARGUMENT;
    for (WitU32 i = 0; i < WIT_EVENT_CAPACITY; ++i) {
        WitEvent *event = &table->Entries[i];
        WitU64 handle;
        if (event->Handle) continue;
        handle = wit_handle_grant(handles, WIT_HANDLE_EVENT, rights);
        if (!handle) return WIT_STATUS_NO_MEMORY;
        event->ManualReset = (flags & WIT_EVENT_MANUAL_RESET) != 0;
        event->Signaled = (flags & WIT_EVENT_INITIAL_SIGNALED) != 0;
        event->Handle = handle;
        ++table->Count;
        *result = handle;
        return WIT_STATUS_OK;
    }
    return WIT_STATUS_NO_MEMORY;
}

WitU64 wit_event_get(WitEventTable *table, WitHandleTable *handles,
    WitU64 handle, WitU32 rights, WitEvent **result)
{
    const WitU64 status = wit_handle_check(handles, handle, WIT_HANDLE_EVENT, rights);
    *result = 0;
    if (status != WIT_STATUS_OK) return status;
    for (WitU32 i = 0; i < WIT_EVENT_CAPACITY; ++i) {
        if (table->Entries[i].Handle != handle) continue;
        *result = &table->Entries[i];
        return WIT_STATUS_OK;
    }
    return WIT_STATUS_BAD_HANDLE;
}

int wit_event_consume(WitEvent *event)
{
    if (!event->Signaled) return 0;
    if (!event->ManualReset) event->Signaled = 0;
    return 1;
}

WitU64 wit_event_remove(WitEventTable *table, WitHandleTable *handles, WitU64 handle)
{
    WitEvent *event;
    WitU64 status = wit_event_get(table, handles, handle, 0, &event);
    if (status != WIT_STATUS_OK) return status;
    status = wit_handle_close(handles, handle);
    if (status != WIT_STATUS_OK) return status;
    event->Handle = 0;
    event->Signaled = 0;
    --table->Count;
    return WIT_STATUS_OK;
}
