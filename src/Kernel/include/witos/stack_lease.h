#ifndef WITOS_STACK_LEASE_H
#define WITOS_STACK_LEASE_H
#include "types.h"
#define WIT_STACK_LEASE_VERSION 1U
#define WIT_STACK_LEASE_CAPACITY 4U
/* Private bring-up contract. The token is owned by the requesting thread;
 * copied user fields are descriptive, never authority. No automatic resume. */
typedef struct WitStackLeaseInfo {
    WitU32 Version,Size;
    WitU64 Token,OwnerId,ThreadId,StackLow,StackHigh;
} WitStackLeaseInfo;
WIT_STATIC_ASSERT(sizeof(WitStackLeaseInfo)==48,"Stack lease snapshot ABI");
#endif
