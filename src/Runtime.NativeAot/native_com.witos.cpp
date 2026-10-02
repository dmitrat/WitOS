#include "pal.witos.h"
#include "tls.h"
#include "com_counter.witos.h"
#include <objbase.h>

namespace {
// Component-private MTA participation. This is apartment lifecycle, not COM
// activation, RPC, STA dispatch, WinRT or Windows FLS. All owners come from the
// kernel; raw FS/GS words never select a registry entry.
struct Participant {
    WitU64 Owner;
    uint32_t References;
};

Participant participants[8];
volatile WitU32 gate;

void lock()
{
    while (!wit_native_try_lock(&gate)) {
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
    }
}

WitU64 current()
{
    WitU64 id = 0;
    if (wit_native_call(WIT_CALL_THREAD_CURRENT, 0, 0, 0, &id) != WIT_STATUS_OK || !id) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return id;
}

Participant *find(WitU64 id)
{
    for (auto &entry : participants) {
        if (entry.Owner == id) {
            return &entry;
        }
    }
    return nullptr;
}

void cleanup(void *context)
{
    const WitU64 id = current();
    lock();
    auto entry = find(id);
    if (!entry || entry != context) {
        wit_native_unlock(&gate);
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    entry->References = 0;
    entry->Owner = 0;
    wit_native_unlock(&gate);
}
}

extern "C" HRESULT WINAPI wit_native_com_initialize(LPVOID reserved, DWORD flags)
{
    if (reserved || (flags & ~(COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE | COINIT_SPEED_OVER_MEMORY))) {
        return E_INVALIDARG;
    }
    const WitU64 id = current();
    lock();
    auto entry = find(id);
    if (flags & COINIT_APARTMENTTHREADED) {
        const HRESULT error = entry && entry->References ? RPC_E_CHANGED_MODE : E_NOTIMPL;
        wit_native_unlock(&gate);
        return error;
    }
    if (entry) {
        const bool existing = entry->References != 0;
        const bool added = wit_com_add_reference(entry->References);
        wit_native_unlock(&gate);
        return added ? (existing ? S_FALSE : S_OK) : E_OUTOFMEMORY;
    }
    for (auto &candidate : participants) {
        if (!candidate.Owner) {
            entry = &candidate;
            break;
        }
    }
    if (!entry) {
        wit_native_unlock(&gate);
        return E_OUTOFMEMORY;
    }
    // Registration checks published image/native TLS and is non-parking. Do not
    // publish MTA participation if orderly cleanup cannot be guaranteed.
    if (wit_native_thread_on_cleanup(cleanup, entry) != WIT_STATUS_OK) {
        wit_native_unlock(&gate);
        return E_UNEXPECTED;
    }
    entry->Owner = id;
    entry->References = 1;
    wit_native_unlock(&gate);
    return S_OK;
}

extern "C" void WINAPI wit_native_com_uninitialize()
{
    const WitU64 id = current();
    lock();
    auto entry = find(id);
    if (entry && entry->References) {
        --entry->References;
    }
    // Keep the cleanup registration until exit; reinitialization reuses it.
    wit_native_unlock(&gate);
}

extern "C" HRESULT WINAPI wit_native_com_apartment(APTTYPE *type, APTTYPEQUALIFIER *qualifier)
{
    if (!type || !qualifier) {
        return E_INVALIDARG;
    }
    const WitU64 id = current();
    lock();
    auto entry = find(id);
    const bool explicitMta = entry && entry->References;
    bool implicitMta = false;
    for (const auto &candidate : participants) {
        if (candidate.References) {
            implicitMta = true;
            break;
        }
    }
    wit_native_unlock(&gate);
    // Snapshot is scalar and immutable after releasing the gate; outputs are
    // caller-owned native buffers. No callbacks or waits run under the gate.
    *type = explicitMta || implicitMta ? APTTYPE_MTA : APTTYPE_CURRENT;
    *qualifier = explicitMta ? APTTYPEQUALIFIER_NONE
        : implicitMta        ? APTTYPEQUALIFIER_IMPLICIT_MTA
                             : APTTYPEQUALIFIER_NONE;
    return explicitMta || implicitMta ? S_OK : CO_E_NOTINITIALIZED;
}
