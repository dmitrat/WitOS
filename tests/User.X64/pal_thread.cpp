#include "pal.witos.h"
#include "protocol.h"

static WitUserThreadInfo observed[3];
static WitU64 query(void* p, WitU64 size = sizeof(WitUserThreadInfo), WitU64 version = WIT_THREAD_INFO_VERSION)
{
    WitU64 copied = 99;
    const auto status = wit_native_call(WIT_CALL_THREAD_QUERY, (uintptr_t)p, size, version, &copied);
    return copied == (status == WIT_STATUS_OK ? sizeof(WitUserThreadInfo) : 0) ? status : 999;
}
static bool equal(const WitUserThreadInfo& a, const WitUserThreadInfo& b)
{
    return a.Version == b.Version && a.Size == b.Size && a.ThreadId == b.ThreadId && a.StackLow == b.StackLow &&
        a.StackHigh == b.StackHigh && a.RawTls == b.RawTls && a.CompilerTls == b.CompilerTls &&
        a.ProcessId == b.ProcessId && a.ProcessorCount == b.ProcessorCount;
}
static void fill(void* p, size_t size)
{
    for (size_t i = 0; i < size; ++i) ((volatile unsigned char*)p)[i] = 0xA5;
}
static bool untouched(const void* p, size_t size)
{
    for (size_t i = 0; i < size; ++i) if (((const volatile unsigned char*)p)[i] != 0xA5) return false;
    return true;
}
static bool check(const WitUserThreadInfo& info)
{
    void *low = nullptr, *high = nullptr;
    WitUserThreadInfo local;
    if (query(&local) != WIT_STATUS_OK || !equal(info, local) || !PalGetMaximumStackBounds(&low, &high) ||
        (uintptr_t)low != info.StackLow || (uintptr_t)high != info.StackHigh ||
        (uintptr_t)&local < info.StackLow || (uintptr_t)&local + sizeof(local) > info.StackHigh ||
        PalGetCurrentOSThreadId() != info.ThreadId || PalGetCurrentProcessId() != info.ProcessId ||
        PalGetProcessCpuCount() != (int)info.ProcessorCount) return false;
    low = high = (void*)1;
    return !PalGetMaximumStackBounds(nullptr, &high) && high == (void*)1 &&
        !PalGetMaximumStackBounds(&low, nullptr) && low == (void*)1 &&
        !PalGetMaximumStackBounds(&low, &low) && low == (void*)1;
}
static void done(WitU64 code)
{
    (void)wit_native_call(WIT_CALL_THREAD_EXIT, code, 0, 0, nullptr);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}
static void worker(WitU64 index)
{
    if (query(&observed[index]) != WIT_STATUS_OK || !check(observed[index])) done(1101);
    WitU64 start = 0, now = 0;
    if (index < 2) {
        if (wit_native_call(WIT_CALL_CLOCK_READ, 0, 0, 0, &start) != WIT_STATUS_OK) done(1102);
        do {
            if (!check(observed[index]) || wit_native_call(WIT_CALL_CLOCK_READ, 0, 0, 0, &now) != WIT_STATUS_OK) done(1103);
        } while (now - start < 2);
    }
    done(WIT_TEST_EXIT_CODE);
}
extern "C" WitU64 wit_native_main(const WitUserStartup* startup)
{
    const auto config = (const WitUserTestConfig*)startup;
    auto original = (WitUserThreadInfo*)WIT_GC_INFO_REPORT;
    auto cross = (WitUserThreadInfo*)(WIT_GC_INFO_REPORT + 4096 - 16);
    WitUserThreadInfo current;
    if (query(original) != WIT_STATUS_OK || !check(*original) ||
        original->Version != WIT_THREAD_INFO_VERSION || original->Size != sizeof(*original) ||
        original->StackHigh - original->StackLow != 16384 || !original->ProcessId || original->ProcessorCount != 1)
        return 1110;
    if (config->Mode == 2) { *(volatile char*)(original->StackLow - 1) = 1; return 1111; }
    if (config->Mode == 3) { *(volatile char*)original->StackHigh = 1; return 1112; }
    // Writable raw TLS hints and compiler control data must not define identity/bounds.
    auto raw = (WitU64*)(uintptr_t)original->RawTls;
    const auto saved = raw[1]; raw[1] = 0;
    if (!check(*original)) return 1113;
    raw[1] = saved;
    if (original->CompilerTls) {
        auto control = (WitU64*)(uintptr_t)original->CompilerTls;
        control[1] = 1; control[2] = 2; control[6] = 3;
        if (!check(*original)) return 1114;
        control[1] = control[2] = control[6] = 0;
    }
    if (query(cross) != WIT_STATUS_OK || !equal(*original, *cross)) return 1115;
    fill(&current, sizeof(current));
    if (query(&current, 0) != WIT_STATUS_INVALID_ARGUMENT || query(&current, sizeof(current) - 1) != WIT_STATUS_INVALID_ARGUMENT ||
        query(&current, sizeof(current) + 1) != WIT_STATUS_INVALID_ARGUMENT || query(&current, ~0ULL) != WIT_STATUS_INVALID_ARGUMENT ||
        query(&current, sizeof(current), 0) != WIT_STATUS_UNSUPPORTED || !untouched(&current, sizeof(current))) return 1116;
    if (query(nullptr) != WIT_STATUS_BAD_ADDRESS || query((void*)startup) != WIT_STATUS_BAD_ADDRESS ||
        query((void*)(uintptr_t)&wit_native_main) != WIT_STATUS_BAD_ADDRESS ||
        query((void*)(uintptr_t)config->KernelProbe) != WIT_STATUS_BAD_ADDRESS ||
        query((void*)(~0ULL - 15)) != WIT_STATUS_BAD_ADDRESS) return 1117;
    auto partial = (void*)(WIT_GC_INFO_REPORT + 8192 - 32);
    fill(partial, 32);
    if (query(partial) != WIT_STATUS_BAD_ADDRESS || !untouched(partial, 32)) return 1118;
    // Repeated discovery must not allocate handles or change memory accounting.
    WitUserMemoryInfo before, after;
    if (wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)&before, sizeof(before), WIT_MEMORY_INFO_VERSION, nullptr) != WIT_STATUS_OK)
        return 1119;
    for (size_t i = 0; i < 32; ++i) if (!check(*original)) return 1120;
    if (wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)&after, sizeof(after), WIT_MEMORY_INFO_VERSION, nullptr) != WIT_STATUS_OK ||
        before.OwnedBytes != after.OwnedBytes || before.ReservedBytes != after.ReservedBytes) return 1121;
    WitU64 handles[2], code;
    for (WitU64 i = 0; i < 2; ++i)
        if (wit_native_call(WIT_CALL_THREAD_CREATE, (uintptr_t)worker, i, 0, &handles[i]) != WIT_STATUS_OK) return 1122;
    for (size_t i = 0; i < 2; ++i)
        if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &code) != WIT_STATUS_OK || code != WIT_TEST_EXIT_CODE ||
            observed[i].ThreadId != handles[i] || observed[i].ProcessId != original->ProcessId ||
            observed[i].StackLow == original->StackLow || observed[i].RawTls == original->RawTls) return 1123;
    if (observed[0].StackLow == observed[1].StackLow || !check(*original)) return 1124;
    if (wit_native_call(WIT_CALL_THREAD_CREATE, (uintptr_t)worker, 2, 0, &handles[0]) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_THREAD_JOIN, handles[0], 0, 0, &code) != WIT_STATUS_OK || code != WIT_TEST_EXIT_CODE ||
        observed[2].ThreadId == observed[0].ThreadId || observed[2].ThreadId != handles[0] ||
        observed[2].StackLow != observed[0].StackLow) return 1125;
    if (wit_native_call(WIT_CALL_CLOCK_READ, 0, 0, 0, &code) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_THREAD_SLEEP, code + 1, 0, 0, nullptr) != WIT_STATUS_OK || !check(*original)) return 1126;
    return WIT_TEST_EXIT_CODE;
}
