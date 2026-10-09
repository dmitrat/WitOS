#ifndef WITOS_EVENTS_H
#define WITOS_EVENTS_H
#include "handles.h"
#include "limits.h"

/* An event lives while a handle refers to it; every handle of the event names the slot through its Object. */
typedef struct WitEvent {
    WitU32 Live;
    WitU32 Handles;
    WitU32 ManualReset;
    WitU32 Signaled;
} WitEvent;

typedef struct WitEventTable {
    WitEvent Entries[WIT_PROCESS_EVENT_CAPACITY];
    WitU32 Count, Limit;
} WitEventTable;

/* Serialized, component-local state. The architecture scheduler owns waiters. */
void wit_events_initialize(WitEventTable *table);
WitU64 wit_event_create(WitEventTable *table, WitHandleTable *handles, WitU64 flags, WitU32 rights, WitU64 *result);
WitU64 wit_event_get(WitEventTable *table, WitHandleTable *handles, WitU64 handle, WitU32 rights, WitEvent **result);
/* A second handle of the same event with the requested rights (0: the source's), never more than the source has. */
WitU64 wit_event_duplicate(
    WitEventTable *table, WitHandleTable *handles, WitU64 source, WitU32 requested, WitU64 *result);
int wit_event_consume(WitEvent *event);
/* A reference a message carried and dropped: the event ends with its last. */
void wit_event_release(WitEventTable *table, WitU64 object);
/* Closes one handle; the event ends with its last handle. */
WitU64 wit_event_remove(WitEventTable *table, WitHandleTable *handles, WitU64 handle);
/* A reference by object number: the kernel's own (an interrupt binding, K3.2). */
WitEvent *wit_event_lookup(WitEventTable *table, WitU64 object);
void wit_event_retain(WitEventTable *table, WitU64 object);
#endif
