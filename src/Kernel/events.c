#include "witos/events.h"

void wit_events_initialize(WitEventTable *table)
{
    table->Count = 0;
    table->Limit = WIT_EVENT_CAPACITY;
    for (WitU32 i = 0; i < WIT_RUNTIME_EVENT_CAPACITY; ++i) {
        table->Entries[i].Live = 0;
        table->Entries[i].Handles = 0;
        table->Entries[i].ManualReset = 0;
        table->Entries[i].Signaled = 0;
    }
}

WitU64 wit_event_create(WitEventTable *table, WitHandleTable *handles, WitU64 flags, WitU32 rights, WitU64 *result)
{
    *result = 0;
    if ((flags & ~(WIT_EVENT_MANUAL_RESET | WIT_EVENT_INITIAL_SIGNALED)) ||
        (rights & ~(WIT_RIGHT_WAIT | WIT_RIGHT_SIGNAL))) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    for (WitU32 i = 0; i < table->Limit; ++i) {
        WitEvent *event = &table->Entries[i];
        WitU64 handle;
        if (event->Live) {
            continue;
        }
        handle = wit_handle_grant_object(handles, WIT_HANDLE_EVENT, rights, i + 1);
        if (!handle) {
            return WIT_STATUS_NO_MEMORY;
        }
        event->ManualReset = (flags & WIT_EVENT_MANUAL_RESET) != 0;
        event->Signaled = (flags & WIT_EVENT_INITIAL_SIGNALED) != 0;
        event->Handles = 1;
        event->Live = 1;
        ++table->Count;
        *result = handle;
        return WIT_STATUS_OK;
    }
    return WIT_STATUS_NO_MEMORY;
}

static WitEvent *slot(WitEventTable *table, WitU64 object)
{
    if (!object || object > table->Limit || !table->Entries[object - 1].Live) {
        return 0;
    }
    return &table->Entries[object - 1];
}

WitU64 wit_event_get(WitEventTable *table, WitHandleTable *handles, WitU64 handle, WitU32 rights, WitEvent **result)
{
    const WitU64 status = wit_handle_check(handles, handle, WIT_HANDLE_EVENT, rights);
    WitU64 object = 0;
    WitU32 granted = 0;
    *result = 0;
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!wit_handle_describe(handles, handle, WIT_HANDLE_EVENT, &object, &granted) ||
        !(*result = slot(table, object))) {
        return WIT_STATUS_BAD_HANDLE;
    }
    return WIT_STATUS_OK;
}

WitU64 wit_event_duplicate(
    WitEventTable *table, WitHandleTable *handles, WitU64 source, WitU32 requested, WitU64 *result)
{
    WitEvent *event;
    WitU64 object = 0, handle;
    WitU32 granted = 0;
    *result = 0;
    const WitU64 status = wit_event_get(table, handles, source, 0, &event);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!wit_handle_describe(handles, source, WIT_HANDLE_EVENT, &object, &granted)) {
        return WIT_STATUS_BAD_HANDLE;
    }
    const WitU32 rights = requested ? requested : granted;
    if ((rights & granted) != rights) {
        return WIT_STATUS_DENIED;
    }
    handle = wit_handle_grant_object(handles, WIT_HANDLE_EVENT, rights, object);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    ++event->Handles;
    *result = handle;
    return WIT_STATUS_OK;
}

int wit_event_consume(WitEvent *event)
{
    if (!event->Signaled) {
        return 0;
    }
    if (!event->ManualReset) {
        event->Signaled = 0;
    }
    return 1;
}

WitU64 wit_event_remove(WitEventTable *table, WitHandleTable *handles, WitU64 handle)
{
    WitEvent *event;
    WitU64 status = wit_event_get(table, handles, handle, 0, &event);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    status = wit_handle_close(handles, handle);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (--event->Handles == 0) {
        event->Live = 0;
        event->Signaled = 0;
        event->ManualReset = 0;
        --table->Count;
    }
    return WIT_STATUS_OK;
}
