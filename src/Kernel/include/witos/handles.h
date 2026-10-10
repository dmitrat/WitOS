#ifndef WITOS_HANDLES_H
#define WITOS_HANDLES_H
#include "user_abi.h"
#include "limits.h"

/* Kinds are kernel-internal. THREAD_IDENTITY is a thread's private generation-bearing identity: never granted to user
 * space as a capability, closing it is BUSY, and it ends with the thread. THREAD_REFERENCE is the thread handle. */
#define WIT_HANDLE_CONSOLE 1U
#define WIT_HANDLE_SELF 2U
#define WIT_HANDLE_THREAD_IDENTITY 3U
#define WIT_HANDLE_EVENT 4U
#define WIT_HANDLE_THREAD_REFERENCE 5U
/* 6-9 (a file, a library, a module reader, a library lifecycle) left with the kernel policy at K8.4a. */
#define WIT_HANDLE_CHANNEL_ENDPOINT 10U
#define WIT_HANDLE_MEMORY_OBJECT 11U
#define WIT_HANDLE_DEVICE 12U
#define WIT_HANDLE_INTERRUPT 13U
#define WIT_HANDLE_PIN 14U
#define WIT_HANDLE_PROCESS \
    15U /* a process (K5.2c); Object is its Id in the high word and registry slot plus one in the low */
#define WIT_HANDLE_CLOCK 16U /* the authority to set UTC (CLOCK_SET, K6): the handle is the capability, no record */

/* The rights are the ABI's (user_abi.h). */

/* Object names the object an entry refers to within its kind (an event slot, a thread identity); zero when the kind
 * has one object per handle. */
typedef struct WitHandleEntry {
    WitU64 Token;
    WitU32 Kind;
    WitU32 Rights;
    WitU32 Generation;
    WitU32 Live;
    WitU64 Object;
} WitHandleEntry;

typedef struct WitHandleTable {
    WitU32 Owner;
    WitU32 Count;
    WitU32 Limit;
    WitHandleEntry Entries[WIT_PROCESS_HANDLE_CAPACITY];
} WitHandleTable;

void wit_handles_initialize(WitHandleTable *table, WitU32 owner);
WitU64 wit_handle_grant(WitHandleTable *table, WitU32 kind, WitU32 rights);
WitU64 wit_handle_grant_object(WitHandleTable *table, WitU32 kind, WitU32 rights, WitU64 object);
WitU64 wit_handle_check(WitHandleTable *table, WitU64 token, WitU32 kind, WitU32 rights);
/* The object and rights of a live handle of the kind; zero object and rights when there is none. */
int wit_handle_describe(const WitHandleTable *table, WitU64 token, WitU32 kind, WitU64 *object, WitU32 *rights);
WitU64 wit_handle_close(WitHandleTable *table, WitU64 token);
/* Entries a grant can still take. */
WitU32 wit_handles_free_count(const WitHandleTable *table);
void wit_handles_close_all(WitHandleTable *table);
#endif
