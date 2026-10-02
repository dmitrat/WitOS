#ifndef WITOS_EVENTS_H
#define WITOS_EVENTS_H
#include "handles.h"

#define WIT_EVENT_CAPACITY 4U
#define WIT_RUNTIME_EVENT_CAPACITY 16U

typedef struct WitEvent {
    WitU64 Handle;
    WitU32 ManualReset;
    WitU32 Signaled;
} WitEvent;

typedef struct WitEventTable {
    WitEvent Entries[WIT_RUNTIME_EVENT_CAPACITY];
    WitU32 Count, Limit;
} WitEventTable;

/* Serialized, component-local state. The architecture scheduler owns waiters. */
void wit_events_initialize(WitEventTable *table);
WitU64 wit_event_create(WitEventTable *table, WitHandleTable *handles, WitU64 flags, WitU32 rights, WitU64 *result);
WitU64 wit_event_get(WitEventTable *table, WitHandleTable *handles, WitU64 handle, WitU32 rights, WitEvent **result);
int wit_event_consume(WitEvent *event);
WitU64 wit_event_remove(WitEventTable *table, WitHandleTable *handles, WitU64 handle);
#endif
