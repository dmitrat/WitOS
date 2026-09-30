#include "user.h"
#include "witos/platform.h"

_Static_assert(WIT_PRESSURE_LOW_PAGES < WIT_PRESSURE_HIGH_PAGES && WIT_PRESSURE_HIGH_PAGES < WIT_USER_PAGE_CAPACITY,
    "Memory-pressure hysteresis must fit the component quota");
void wit_user_pressure_update(WitUserProcess *process)
{
    const WitU64 physical = wit_pages_free_count(process->Space.Allocator);
    const WitU64 quota = process->Space.OwnedCount < process->Space.PageLimit ? process->Space.PageLimit - process->Space.OwnedCount : 0;
    const WitU64 available = physical < quota ? physical : quota;
    if (available <= WIT_PRESSURE_LOW_PAGES) process->MemoryPressureLow = 1;
    else if (available >= WIT_PRESSURE_HIGH_PAGES) process->MemoryPressureLow = 0;
    for (WitU32 i = 0; i < process->Events.Limit; ++i) {
        const WitU64 handle = process->MemoryPressureEvents[i];
        if (!handle) continue;
        const WitU64 status = wit_user_event_notify(process, handle, process->MemoryPressureLow);
        if (status == WIT_STATUS_BAD_HANDLE) process->MemoryPressureEvents[i] = 0;
        else if (status != WIT_STATUS_OK) wit_panic("Memory-pressure event ownership lost");
    }
}
WitU64 wit_user_pressure_create(WitUserProcess *process, WitU64 *handle)
{
    *handle = 0;
    WitU32 slot;
    for (slot = 0; slot < process->Events.Limit; ++slot) if (!process->MemoryPressureEvents[slot]) break;
    if (slot == process->Events.Limit) return WIT_STATUS_NO_MEMORY;
    const WitU64 flags = WIT_EVENT_MANUAL_RESET | (process->MemoryPressureLow ? WIT_EVENT_INITIAL_SIGNALED : 0);
    const WitU64 status = wit_event_create(&process->Events, &process->Handles, flags, WIT_RIGHT_WAIT, handle);
    if (status == WIT_STATUS_OK) process->MemoryPressureEvents[slot] = *handle;
    return status;
}
