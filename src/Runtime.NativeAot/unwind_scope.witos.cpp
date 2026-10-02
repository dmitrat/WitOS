#include "unwind_scope.witos.h"
extern "C" {
#include "bootstrap.h"
}

namespace {
static __declspec(thread) WitNativeUnwindScope *active;

WitU64 current(WitUserThreadInfo &info)
{
    const WitU64 result =
        wit_native_call(WIT_CALL_THREAD_QUERY, (WitU64)&info, sizeof(info), WIT_THREAD_INFO_VERSION, nullptr);
    if (result != WIT_STATUS_OK) {
        return result;
    }
    if (info.Version != WIT_THREAD_INFO_VERSION || info.Size != sizeof(info) || !info.ThreadId || !info.CompilerTls) {
        return WIT_STATUS_DENIED;
    }
    return WIT_STATUS_OK;
}

bool valid(const WitStackLeaseInfo &lease, const WitUserThreadInfo &owner)
{
    return lease.Version == WIT_STACK_LEASE_VERSION &&
        lease.Size == sizeof(lease) &&
        lease.Token &&
        lease.OwnerId == owner.ThreadId &&
        lease.ThreadId &&
        lease.StackLow < lease.StackHigh;
}

[[noreturn]] void corrupt()
{
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}
}

WitNativeUnwindScope::WitNativeUnwindScope(WitU64 reference)
    : status(WIT_STATUS_DENIED), token(0), ownerId(0), previous(nullptr)
{
    WitUserThreadInfo owner;
    status = current(owner);
    if (status != WIT_STATUS_OK) {
        return;
    }
    WitStackLeaseInfo lease;
    status = wit_native_call(WIT_CALL_STACK_LEASE_ACQUIRE, reference, (WitU64)&lease, sizeof(lease), nullptr);
    if (status != WIT_STATUS_OK) {
        return;
    }
    if (!valid(lease, owner)) {
        corrupt();
    }
    ownerId = owner.ThreadId;
    token = lease.Token;
    previous = active;
    active = this;
}

WitNativeUnwindScope::~WitNativeUnwindScope()
{
    if (status == WIT_STATUS_OK && Close() != WIT_STATUS_OK) {
        corrupt();
    }
}

WitU64 WitNativeUnwindScope::Close()
{
    if (status != WIT_STATUS_OK) {
        return status;
    }
    WitUserThreadInfo owner;
    const WitU64 check = current(owner);
    if (check != WIT_STATUS_OK) {
        return check;
    }
    WitStackLeaseInfo lease;
    const WitU64 found = wit_native_call(WIT_CALL_STACK_LEASE_QUERY, token, (WitU64)&lease, sizeof(lease), nullptr);
    if (found != WIT_STATUS_OK) {
        return found;
    }
    if (!valid(lease, owner) || lease.Token != token) {
        corrupt();
    }
    if (active != this) {
        return WIT_STATUS_BUSY;
    }
    const WitU64 released = wit_native_call(WIT_CALL_STACK_LEASE_RELEASE, token, 0, 0, nullptr);
    if (released != WIT_STATUS_OK) {
        return released;
    }
    active = previous;
    previous = nullptr;
    token = 0;
    status = WIT_STATUS_CLOSED;
    return WIT_STATUS_OK;
}

WitU64 WitNativeUnwindScope::ReopenCurrent()
{
    if (status != WIT_STATUS_CLOSED || token || previous) {
        return WIT_STATUS_BUSY;
    }
    WitUserThreadInfo owner;
    const WitU64 check = current(owner);
    if (check != WIT_STATUS_OK) {
        return check;
    }
    if (!ownerId || owner.ThreadId != ownerId) {
        return WIT_STATUS_DENIED;
    }
    WitStackLeaseInfo lease;
    const WitU64 acquired = wit_native_call(
        WIT_CALL_STACK_LEASE_ACQUIRE, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&lease, sizeof(lease), nullptr);
    if (acquired != WIT_STATUS_OK) {
        return acquired;
    }
    if (!valid(lease, owner) || lease.ThreadId != owner.ThreadId) {
        corrupt();
    }
    token = lease.Token;
    previous = active;
    active = this;
    status = WIT_STATUS_OK;
    return WIT_STATUS_OK;
}

WitU64 WitNativeUnwindScope::Current(WitU64 stackPointer, WitStackLeaseInfo *output)
{
    if (!output) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitUserThreadInfo owner;
    const WitU64 check = current(owner);
    if (check != WIT_STATUS_OK) {
        return check;
    }
    WitNativeUnwindScope *scope = active;
    if (!scope) {
        return WIT_STATUS_CLOSED;
    }
    if (scope->status != WIT_STATUS_OK || !scope->token) {
        corrupt();
    }
    WitStackLeaseInfo lease;
    const WitU64 found =
        wit_native_call(WIT_CALL_STACK_LEASE_QUERY, scope->token, (WitU64)&lease, sizeof(lease), nullptr);
    if (found != WIT_STATUS_OK) {
        return found;
    }
    if (!valid(lease, owner) || lease.Token != scope->token) {
        corrupt();
    }
    if (stackPointer < lease.StackLow || stackPointer >= lease.StackHigh) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    *output = lease;
    return WIT_STATUS_OK;
}
