#include <windows.h>
#include <atomic>
#include <thread>
#include <stdio.h>
#include "function_tables.witos.h"
static SRWLOCK gate = SRWLOCK_INIT;
static thread_local bool held;

static void enter()
{
    AcquireSRWLockExclusive(&gate);
    held = true;
}

static void leave()
{
    held = false;
    ReleaseSRWLockExclusive(&gate);
}

static bool mapped(uint64_t address, size_t bytes, bool execute)
{
    if (!address || !bytes || address > UINT64_MAX - bytes) {
        return false;
    }
    const auto end = address + bytes;
    while (address < end) {
        MEMORY_BASIC_INFORMATION info = {};
        if (!VirtualQuery((void *)address, &info, sizeof(info)) ||
            info.State != MEM_COMMIT ||
            (info.Protect & (PAGE_GUARD | PAGE_NOACCESS))) {
            return false;
        }
        const DWORD p = info.Protect & 0xff;
        if (execute &&
            p != PAGE_EXECUTE &&
            p != PAGE_EXECUTE_READ &&
            p != PAGE_EXECUTE_READWRITE &&
            p != PAGE_EXECUTE_WRITECOPY) {
            return false;
        }
        const auto next = (uint64_t)info.BaseAddress + info.RegionSize;
        if (next <= address) {
            return false;
        }
        address = next < end ? next : end;
    }
    return true;
}

static bool readable(uint64_t a, size_t n)
{
    return mapped(a, n, false);
}

static bool executable(uint64_t a, size_t n)
{
    return mapped(a, n, true);
}

static WitRuntimeFunction entry = {0, 6, 256};
static std::atomic<bool> entered, releaseCallback;

static WitRuntimeFunction *callback(uint64_t, void *value)
{
    if (held || value != &entry) {
        abort();
    }
    entered = true;
    while (!releaseCallback) {
        SwitchToThread();
    }
    return &entry;
}

static PRUNTIME_FUNCTION CALLBACK windows_callback(DWORD64, void *value)
{
    return (PRUNTIME_FUNCTION)value;
}

#define REQUIRE(x) \
    do { \
        if (!(x)) { \
            printf("FAIL line %u: %s\n", __LINE__, #x); \
            return 1; \
        } \
    } while (0)

int main()
{
    auto code = (unsigned char *)VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    REQUIRE(code);
    unsigned char instructions[] = {0xb8, 42, 0, 0, 0, 0xc3};
    for (unsigned i = 0; i < sizeof(instructions); ++i) {
        code[i] = instructions[i];
    }
    code[256] = 1;
    DWORD previous;
    REQUIRE(VirtualProtect(code, 4096, PAGE_EXECUTE_READ, &previous));
    const auto base = (uint64_t)code;
    WitFunctionTables tables({enter, leave, readable, executable});
    REQUIRE(tables.AddTable(&entry, 1, base, 4096));
    REQUIRE(!tables.AddTable(&entry, 1, base, 4096));
    WitFunctionLease lease = {};
    REQUIRE(tables.Acquire(base + 2, &lease) && lease.Entry == &entry && lease.Base == base);
    REQUIRE(RtlAddFunctionTable((PRUNTIME_FUNCTION)&entry, 1, base));
    DWORD64 windowsBase = 0;
    REQUIRE(RtlLookupFunctionEntry(base + 2, &windowsBase, nullptr) == (PRUNTIME_FUNCTION)lease.Entry &&
        windowsBase == lease.Base);
    REQUIRE(RtlDeleteFunctionTable((PRUNTIME_FUNCTION)&entry));
    auto duplicate = lease;
    REQUIRE(tables.BeginRemove((uint64_t)&entry) && !tables.FinishRemove((uint64_t)&entry));
    WitRuntimeFunction *leased = nullptr;
    REQUIRE(tables.Lookup(lease, base + 3, &leased) && leased == &entry);
    WitFunctionLease sentinel = {77, 88, 99, 100, (WitRuntimeFunction *)111};
    REQUIRE(
        !tables.Acquire(base + 2, &sentinel) && sentinel.Token == 77 && sentinel.Entry == (WitRuntimeFunction *)111);
    REQUIRE(tables.Release(&lease) && !tables.Release(&duplicate) && tables.FinishRemove((uint64_t)&entry));
    const auto key = base | 3;
    REQUIRE(!tables.AddCallback(base, base, 4096, callback, &entry));
    REQUIRE(tables.AddCallback(key, base, 4096, callback, &entry));
    REQUIRE(RtlInstallFunctionTableCallback(key, base, 4096, windows_callback, &entry, nullptr));
    windowsBase = 0;
    REQUIRE(
        RtlLookupFunctionEntry(base + 2, &windowsBase, nullptr) == (PRUNTIME_FUNCTION)&entry && windowsBase == base);
    REQUIRE(RtlDeleteFunctionTable((PRUNTIME_FUNCTION)key));
    bool acquired = false;
    std::thread reader([&] { acquired = tables.Acquire(base + 2, &lease); });
    while (!entered) {
        SwitchToThread();
    }
    REQUIRE(tables.BeginRemove(key) && !tables.FinishRemove(key));
    releaseCallback = true;
    reader.join();
    REQUIRE(acquired && lease.Entry == &entry);
    REQUIRE(!tables.FinishRemove(key) && tables.Release(&lease) && tables.FinishRemove(key));
    REQUIRE(tables.AddTable(&entry, 1, base, 4096));
    WitFunctionLease heldReaders[128] = {};
    for (auto &value : heldReaders) {
        REQUIRE(tables.Acquire(base + 2, &value));
    }
    REQUIRE(!tables.Acquire(base + 2, &sentinel) && sentinel.Token == 77);
    for (auto &value : heldReaders) {
        REQUIRE(tables.Release(&value));
    }
    REQUIRE(tables.BeginRemove((uint64_t)&entry) && tables.FinishRemove((uint64_t)&entry));
    REQUIRE(VirtualFree(code, 0, MEM_RELEASE));
    printf("PASS: dynamic registry static/callback Windows comparisons, callback rundown, stale leases and reader "
           "quota\n");
    return 0;
}
