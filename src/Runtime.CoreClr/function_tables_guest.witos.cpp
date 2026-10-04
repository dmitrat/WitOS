#include "function_tables_guest.witos.h"
extern "C" {
#include "bootstrap.h"
}
static_assert(sizeof(WitRuntimeFunction) == sizeof(RUNTIME_FUNCTION), "Actual Windows function entry ABI");

namespace {
volatile WitU32 gate;

struct CodeGuard {
    CodeGuard()
    {
        wit_coreclr_code_gate_enter();
    }

    ~CodeGuard()
    {
        wit_coreclr_code_gate_leave();
    }
};

void enter()
{
    wit_native_lock(&gate);
}

void leave()
{
    wit_native_unlock(&gate);
}

bool check(uint64_t address, size_t bytes, WitU32 protection)
{
    WitCodeMemoryRequest request = {
        WIT_CODE_MEMORY_VERSION, sizeof(request), WIT_CODE_VALIDATE, protection, address, 0, bytes, 0, 0, 0};
    return wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&request, sizeof(request), 0, nullptr) == WIT_STATUS_OK;
}

bool readable(uint64_t a, size_t n)
{
    return check(a, n, 1);
}

bool executable(uint64_t a, size_t n)
{
    return check(a, n, 5);
}

WitFunctionTables tables({enter, leave, readable, executable});
static __declspec(thread) unsigned lookupDepth;

bool tls_ready()
{
    WitUserThreadInfo info;
    return wit_native_thread_info(&info) && info.CompilerTls;
}

bool name_valid(PCWSTR name)
{
    if (!name) {
        return true;
    }
    for (unsigned i = 0; i < 1024; ++i) {
        if (!readable((uint64_t)(name + i), sizeof(*name))) {
            return false;
        }
        if (!name[i]) {
            return true;
        }
    }
    return false;
}
}

extern "C" BOOLEAN WINAPI wit_coreclr_add_function_table(PRUNTIME_FUNCTION table, DWORD count, DWORD64 base)
{
    CodeGuard codeLifetime;
    if (!count || count > 4096 || !readable((uint64_t)table, (size_t)count * sizeof(*table))) {
        return FALSE;
    }
    for (DWORD i = 0; i < count; ++i) {
        const auto &entry = table[i];
        if (entry.BeginAddress >= entry.EndAddress ||
            base > UINT64_MAX - entry.EndAddress ||
            !executable(base + entry.BeginAddress, entry.EndAddress - entry.BeginAddress)) {
            return FALSE;
        }
    }
    const auto length = table[count - 1].EndAddress;
    return tables.AddTable((WitRuntimeFunction *)table, count, base, length) ? TRUE : FALSE;
}

extern "C" BOOLEAN WINAPI wit_coreclr_install_function_table(DWORD64 key, DWORD64 base, DWORD length,
    PGET_RUNTIME_FUNCTION_CALLBACK callback, PVOID context, PCWSTR outOfProcessName)
{
    // CoreCLR passes its DAC path. This is validated diagnostic metadata only:
    // no Windows debugger DLL is loaded or advertised by this in-process layer.
    if (!name_valid(outOfProcessName)) {
        return FALSE;
    }
    CodeGuard codeLifetime;
    if (!check(base, length, 0)) {
        return FALSE;
    }
    return tables.AddCallback(key, base, length, (WitFunctionCallback)callback, context) ? TRUE : FALSE;
}

extern "C" BOOLEAN WINAPI wit_coreclr_delete_function_table(PRUNTIME_FUNCTION identity)
{
    // A callback cannot synchronously wait for itself. Normal CoreCLR unload
    // already quiesces code users; never park while holding the registry gate.
    if (!tls_ready() || lookupDepth || !tables.BeginRemove((uint64_t)identity)) {
        return FALSE;
    }
    while (!tables.FinishRemove((uint64_t)identity)) {
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
    }
    return TRUE;
}

bool wit_coreclr_acquire_function(DWORD64 pc, WitFunctionLease *lease)
{
    if (!tls_ready()) {
        return false;
    }
    ++lookupDepth;
    const bool result = tables.Acquire(pc, lease) || wit_coreclr_acquire_module(pc, lease);
    --lookupDepth;
    return result;
}

void wit_coreclr_release_function(WitFunctionLease *lease)
{
    if (lease && lease->ModuleReader) {
        wit_coreclr_release_module(lease);
        return;
    }
    if (!tables.Release(lease)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
}

PRUNTIME_FUNCTION wit_coreclr_leased_function(const WitFunctionLease &lease, DWORD64 pc)
{
    if (!tls_ready()) {
        return nullptr;
    }
    if (lease.ModuleReader) {
        return wit_coreclr_leased_module(lease, pc);
    }
    WitRuntimeFunction *function = nullptr;
    ++lookupDepth;
    const bool found = tables.Lookup(lease, pc, &function);
    --lookupDepth;
    return found ? (PRUNTIME_FUNCTION)function : nullptr;
}

const void *wit_coreclr_unwind_read(DWORD64 address, DWORD bytes, bool code)
{
    return check(address, bytes, code ? 5 : 1) ? (const void *)address : nullptr;
}

extern "C" PRUNTIME_FUNCTION WINAPI wit_coreclr_lookup_function_entry(
    DWORD64 pc, PDWORD64 imageBase, PUNWIND_HISTORY_TABLE)
{
    // No history cache for dynamic/mutable metadata. Internal unwinds take a new
    // explicit lease; callers of this Windows-shaped API retain their own code lifetime.
    if (!imageBase) {
        return nullptr;
    }
    WitFunctionLease lease = {};
    if (!wit_coreclr_acquire_function(pc, &lease)) {
        return nullptr;
    }
    const auto entry = (PRUNTIME_FUNCTION)lease.Entry;
    *imageBase = lease.Base;
    wit_coreclr_release_function(&lease);
    return entry;
}

bool wit_coreclr_code_registered(uint64_t base, uint64_t size)
{
    return tables.HasRange(base, size);
}
