#include "user.h"
#include "witos/platform.h"
/* All entry points run with IF clear on the sole supported online CPU.
 * Tokens never repeat, including across component/thread slot reuse. */
static WitU64 next_token = 1;

void wit_user_stack_leases_initialize(WitUserProcess *p)
{
    for (WitU32 i = 0; i < WIT_STACK_LEASE_CAPACITY; ++i) {
        p->StackLeases[i] = (WitUserStackLease){0};
    }
}

int wit_user_stack_leased(const WitUserProcess *p, WitU64 target, int foreign_only)
{
    for (WitU32 i = 0; i < WIT_STACK_LEASE_CAPACITY; ++i) {
        const WitUserStackLease *l = &p->StackLeases[i];
        if (l->Token && l->ThreadId == target && (!foreign_only || l->OwnerId != target)) {
            return 1;
        }
    }
    return 0;
}

int wit_user_stack_leases_owned(const WitUserProcess *p, WitU64 owner)
{
    for (WitU32 i = 0; i < WIT_STACK_LEASE_CAPACITY; ++i) {
        if (p->StackLeases[i].Token && p->StackLeases[i].OwnerId == owner) {
            return 1;
        }
    }
    return 0;
}

void wit_user_stack_leases_exit(WitUserProcess *p, WitU64 owner)
{
    for (WitU32 i = 0; i < WIT_STACK_LEASE_CAPACITY; ++i) {
        if (p->StackLeases[i].Token && p->StackLeases[i].OwnerId == owner) {
            p->StackLeases[i] = (WitUserStackLease){0};
        }
    }
}

static WitU64 lookup(WitUserProcess *p, WitU64 token, WitUserStackLease **result)
{
    *result = 0;
    if (!token) {
        return WIT_STATUS_BAD_HANDLE;
    }
    for (WitU32 i = 0; i < WIT_STACK_LEASE_CAPACITY; ++i) {
        WitUserStackLease *l = &p->StackLeases[i];
        if (l->Token == token) {
            if (l->OwnerId != p->Threads[p->CurrentThread].Handle) {
                return WIT_STATUS_DENIED;
            }
            *result = l;
            return WIT_STATUS_OK;
        }
    }
    return WIT_STATUS_BAD_HANDLE;
}

static WitUserThread *live_target(WitUserProcess *p, const WitUserStackLease *l)
{
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        WitUserThread *t = &p->Threads[i];
        if (t->State != WitThreadEmpty && t->State != WitThreadExited && t->Handle == l->ThreadId) {
            if (l->OwnerId != l->ThreadId && !t->SuspendCount) {
                wit_panic("Leased foreign stack resumed");
            }
            return t;
        }
    }
    wit_panic("Stack lease lost its live target");
}

static WitStackLeaseInfo describe(const WitUserStackLease *lease, const WitUserThread *target)
{
    return (WitStackLeaseInfo){WIT_STACK_LEASE_VERSION, sizeof(WitStackLeaseInfo), lease->Token, lease->OwnerId,
        lease->ThreadId, target->StackBottom, target->StackTop};
}

WitU64 wit_user_stack_lease_acquire(WitUserProcess *p, WitU64 reference, WitU64 output, WitU64 size)
{
    if (size != sizeof(WitStackLeaseInfo)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitUserThread *target = 0;
    WitU64 status = wit_user_reference_target(p, reference, WIT_THREAD_REFERENCE_GET_CONTEXT, &target);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    const WitUserThread *caller = &p->Threads[p->CurrentThread];
    if (target != caller) {
        status = wit_user_reference_target(p, reference, WIT_THREAD_REFERENCE_SUSPEND_RESUME, &target);
        if (status != WIT_STATUS_OK) {
            return status;
        }
        if (!target->SuspendCount || (target->State != WitThreadReady && target->State != WitThreadWaiting)) {
            return WIT_STATUS_BUSY;
        }
    }
    if (!wit_user_buffer_writable(&p->Space, output, sizeof(WitStackLeaseInfo))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    WitUserStackLease *destination = 0;
    for (WitU32 i = 0; i < WIT_STACK_LEASE_CAPACITY; ++i) {
        if (!p->StackLeases[i].Token) {
            destination = &p->StackLeases[i];
            break;
        }
    }
    if (!destination || !next_token) {
        return WIT_STATUS_NO_MEMORY;
    }
    if (target->StackBottom >= target->StackTop ||
        target->StackTop - target->StackBottom > WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM ||
        !wit_user_buffer_readable(&p->Space, target->StackBottom, (WitU32)(target->StackTop - target->StackBottom))) {
        wit_panic("Stack lease target lost fixed backing");
    }
    const WitUserStackLease lease = {next_token, caller->Handle, target->Handle};
    const WitStackLeaseInfo snapshot = describe(&lease, target);
    if (!wit_user_copy_to(&p->Space, output, (const WitU8 *)&snapshot, sizeof(snapshot))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    *destination = lease;
    ++next_token; // Overflow becomes permanent exhaustion, never token reuse.
    return WIT_STATUS_OK;
}

WitU64 wit_user_stack_lease_query(WitUserProcess *p, WitU64 token, WitU64 output, WitU64 size)
{
    if (size != sizeof(WitStackLeaseInfo)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitUserStackLease *lease = 0;
    const WitU64 status = lookup(p, token, &lease);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    const WitStackLeaseInfo snapshot = describe(lease, live_target(p, lease));
    return wit_user_copy_to(&p->Space, output, (const WitU8 *)&snapshot, sizeof(snapshot)) ? WIT_STATUS_OK
                                                                                           : WIT_STATUS_BAD_ADDRESS;
}

WitU64 wit_user_stack_lease_release(WitUserProcess *p, WitU64 token)
{
    WitUserStackLease *lease = 0;
    const WitU64 status = lookup(p, token, &lease);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    (void)live_target(p, lease);
    *lease = (WitUserStackLease){0};
    return WIT_STATUS_OK;
}
