#include "pal.witos.h"
#include "../User/protocol.h"

WitU64 wit_pal_services(const WitUserStartup *startup);
WitU64 wit_pal_error(const WitUserStartup *startup);
WitU64 wit_pal_wait_any(const WitUserStartup *startup);
WitU64 wit_pal_pressure(const WitUserStartup *startup);
static WitUserThreadInfo observed[3];

// The caller's Version and Size in the buffer select the record; a destination the test cannot write keeps none.
static void header(void *p, WitU32 version = WIT_THREAD_INFO_VERSION, WitU32 size = sizeof(WitUserThreadInfo))
{
    ((volatile WitU32 *)p)[0] = version;
    ((volatile WitU32 *)p)[1] = size;
}

static WitU64 query(void *p, WitU64 size = sizeof(WitUserThreadInfo), WitU64 handle = WIT_THREAD_SELF)
{
    WitU64 copied = 99;
    const auto status = wit_native_call(WIT_CALL_THREAD_QUERY, handle, (uintptr_t)p, size, &copied);
    return copied == (status == WIT_STATUS_OK ? sizeof(WitUserThreadInfo) : 0) ? status : 999;
}

static bool equal(const WitUserThreadInfo &a, const WitUserThreadInfo &b)
{
    return a.Version == b.Version &&
        a.Size == b.Size &&
        a.ThreadId == b.ThreadId &&
        a.StackLow == b.StackLow &&
        a.StackHigh == b.StackHigh &&
        a.RawTls == b.RawTls &&
        a.CompilerTls == b.CompilerTls &&
        a.ProcessId == b.ProcessId &&
        a.ProcessorCount == b.ProcessorCount &&
        a.NativeId == b.NativeId &&
        a.Reserved == b.Reserved;
}

static void fill(void *p, size_t size)
{
    for (size_t i = 0; i < size; ++i) {
        ((volatile unsigned char *)p)[i] = 0xA5;
    }
}

static bool untouched(const void *p, size_t size)
{
    for (size_t i = 0; i < size; ++i) {
        if (((const volatile unsigned char *)p)[i] != 0xA5) {
            return false;
        }
    }
    return true;
}

static bool check(const WitUserThreadInfo &info)
{
    void *low = nullptr, *high = nullptr;
    WitUserThreadInfo local;
    header(&local);
    if (query(&local) != WIT_STATUS_OK ||
        !equal(info, local) ||
        !PalGetMaximumStackBounds(&low, &high) ||
        (uintptr_t)low != info.StackLow ||
        (uintptr_t)high != info.StackHigh ||
        (uintptr_t)&local < info.StackLow ||
        (uintptr_t)&local + sizeof(local) > info.StackHigh ||
        PalGetCurrentOSThreadId() != info.NativeId ||
        PalGetCurrentProcessId() != info.ProcessId ||
        PalGetProcessCpuCount() != (int)info.ProcessorCount) {
        return false;
    }
    low = high = (void *)1;
    return !PalGetMaximumStackBounds(nullptr, &high) &&
        high == (void *)1 &&
        !PalGetMaximumStackBounds(&low, nullptr) &&
        low == (void *)1 &&
        !PalGetMaximumStackBounds(&low, &low) &&
        low == (void *)1;
}

static void done(WitU64 code)
{
    (void)wit_native_call(WIT_CALL_THREAD_EXIT, code, 0, 0, nullptr);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

static void worker(WitU64 index)
{
    header(&observed[index]);
    if (query(&observed[index]) != WIT_STATUS_OK || !check(observed[index])) {
        done(1101);
    }
    WitU64 start = 0, now = 0;
    if (index < 2) {
        if (wit_native_call(WIT_CALL_CLOCK_READ, 0, 0, 0, &start) != WIT_STATUS_OK) {
            done(1102);
        }
        do {
            if (!check(observed[index]) || wit_native_call(WIT_CALL_CLOCK_READ, 0, 0, 0, &now) != WIT_STATUS_OK) {
                done(1103);
            }
        } while (now - start < wit_native_tick_counts(2));
    }
    done(WIT_TEST_EXIT_CODE);
}

extern "C" WitU64 wit_native_main(const WitUserStartup *startup)
{
    const auto config = (const WitUserTestConfig *)startup;
    if (config->Mode >= 60 && config->Mode <= 63) {
        return wit_pal_pressure(startup);
    }
    if (config->Mode >= 50 && config->Mode <= 57) {
        return wit_pal_wait_any(startup);
    }
    if (config->Mode >= 30) {
        return wit_pal_error(startup);
    }
    if (config->Mode >= 10) {
        return wit_pal_services(startup);
    }
    auto original = (WitUserThreadInfo *)WIT_GC_INFO_REPORT;
    auto cross = (WitUserThreadInfo *)(WIT_GC_INFO_REPORT + 4096 - 16);
    WitUserThreadInfo current;
    header(original);
    if (query(original) != WIT_STATUS_OK ||
        !check(*original) ||
        original->Version != WIT_THREAD_INFO_VERSION ||
        original->Size != sizeof(*original) ||
        original->StackHigh - original->StackLow != 65536 ||
        !original->ProcessId ||
        original->ProcessorCount != 1) {
        return 1110;
    }
    if (config->Mode == 2) {
        *(volatile char *)(original->StackLow - 1) = 1;
        return 1111;
    }
    if (config->Mode == 3) {
        *(volatile char *)original->StackHigh = 1;
        return 1112;
    }
    // Writable raw TLS hints and compiler control data must not define identity/bounds.
    auto raw = (WitU64 *)(uintptr_t)original->RawTls;
    const auto saved = raw[1];
    raw[1] = 0;
    if (!check(*original)) {
        return 1113;
    }
    raw[1] = saved;
    if (original->CompilerTls) {
        auto control = (WitU64 *)(uintptr_t)original->CompilerTls;
        control[1] = 1;
        control[2] = 2;
        control[6] = 3;
        if (!check(*original)) {
            return 1114;
        }
        control[1] = control[2] = control[6] = 0;
    }
    header(cross);
    if (query(cross) != WIT_STATUS_OK || !equal(*original, *cross)) {
        return 1115;
    }
    fill(&current, sizeof(current));
    if (query(&current, 0) != WIT_STATUS_INVALID_ARGUMENT ||
        query(&current, sizeof(current) - 1) != WIT_STATUS_INVALID_ARGUMENT ||
        query(&current, sizeof(current) + 1) != WIT_STATUS_INVALID_ARGUMENT ||
        query(&current, ~0ULL) != WIT_STATUS_INVALID_ARGUMENT ||
        !untouched(&current, sizeof(current))) {
        return 1116;
    }
    // A foreign version or size in the header fails before anything beyond the header changes; so does a thread
    // handle that names no thread.
    header(&current, 0);
    if (query(&current) != WIT_STATUS_UNSUPPORTED || !untouched((char *)&current + 8, sizeof(current) - 8)) {
        return 1130;
    }
    header(&current, WIT_THREAD_INFO_VERSION, sizeof(current) - 8);
    if (query(&current) != WIT_STATUS_INVALID_ARGUMENT || !untouched((char *)&current + 8, sizeof(current) - 8)) {
        return 1131;
    }
    header(&current);
    if (query(&current, sizeof(current), config->ForeignHandle) != WIT_STATUS_BAD_HANDLE ||
        query(&current, sizeof(current), startup->ConsoleHandle) != WIT_STATUS_WRONG_TYPE ||
        !untouched((char *)&current + 8, sizeof(current) - 8)) {
        return 1132;
    }
    if (query(nullptr) != WIT_STATUS_BAD_ADDRESS ||
        query((void *)startup) != WIT_STATUS_BAD_ADDRESS ||
        query((void *)(uintptr_t)&wit_native_main) != WIT_STATUS_BAD_ADDRESS ||
        query((void *)(uintptr_t)config->KernelProbe) != WIT_STATUS_BAD_ADDRESS ||
        query((void *)(~0ULL - 15)) != WIT_STATUS_BAD_ADDRESS) {
        return 1117;
    }
    auto partial = (void *)(WIT_GC_INFO_REPORT + 8192 - 32);
    fill(partial, 32);
    if (query(partial) != WIT_STATUS_BAD_ADDRESS || !untouched(partial, 32)) {
        return 1118;
    }
    // Repeated discovery must not allocate handles or change memory accounting.
    WitUserMemoryInfo before, after;
    if (wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)&before, sizeof(before), WIT_MEMORY_INFO_VERSION, nullptr) !=
        WIT_STATUS_OK) {
        return 1119;
    }
    for (size_t i = 0; i < 32; ++i) {
        if (!check(*original)) {
            return 1120;
        }
    }
    if (wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)&after, sizeof(after), WIT_MEMORY_INFO_VERSION, nullptr) !=
            WIT_STATUS_OK ||
        before.OwnedBytes != after.OwnedBytes ||
        before.ReservedBytes != after.ReservedBytes) {
        return 1121;
    }
    WitU64 handles[2], identities[2], code;
    WitUserThreadInfo record;
    for (WitU64 i = 0; i < 2; ++i) {
        if (wit_native_thread_start((uintptr_t)worker, i, 0, &handles[i]) != WIT_STATUS_OK ||
            wit_native_thread_query(handles[i], &record) != WIT_STATUS_OK) {
            return 1122;
        }
        identities[i] = record.ThreadId; // The handle is a capability; the record names the identity the worker sees.
    }
    for (size_t i = 0; i < 2; ++i) {
        if (wit_native_thread_join(handles[i], &code) != WIT_STATUS_OK ||
            code != WIT_TEST_EXIT_CODE ||
            observed[i].ThreadId != identities[i] ||
            identities[i] == handles[i] ||
            observed[i].ProcessId != original->ProcessId ||
            observed[i].StackLow == original->StackLow ||
            observed[i].RawTls == original->RawTls) {
            return 1123;
        }
    }
    if (observed[0].StackLow == observed[1].StackLow ||
        !observed[0].NativeId ||
        observed[0].NativeId == observed[1].NativeId ||
        !check(*original)) {
        return 1124;
    }
    if (wit_native_thread_start((uintptr_t)worker, 2, 0, &handles[0]) != WIT_STATUS_OK ||
        wit_native_thread_query(handles[0], &record) != WIT_STATUS_OK ||
        wit_native_thread_join(handles[0], &code) != WIT_STATUS_OK ||
        code != WIT_TEST_EXIT_CODE ||
        observed[2].NativeId <= observed[1].NativeId ||
        observed[2].ThreadId == observed[0].ThreadId ||
        observed[2].ThreadId != record.ThreadId ||
        observed[2].StackLow != observed[0].StackLow) {
        return 1125;
    }
    if (wit_native_call(WIT_CALL_CLOCK_READ, 0, 0, 0, &code) != WIT_STATUS_OK) {
        return 1126;
    }
    const WitU64 sleep_deadline = code + wit_native_tick_counts(1);
    if (config->Mode == 4) {
        // Force the valid race from CI: the absolute deadline expires before
        // the sleep syscall is entered. This must not require kernel idle.
        do {
            if (wit_native_call(WIT_CALL_CLOCK_READ, 0, 0, 0, &code) != WIT_STATUS_OK) {
                return 1129;
            }
        } while (code < sleep_deadline);
    }
    const WitU64 before_sleep = code;
    WitU64 after_sleep = 0;
    if (wit_native_call(WIT_CALL_SLEEP_UNTIL, sleep_deadline, 0, 0, nullptr) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_CLOCK_READ, 0, 0, 0, &after_sleep) != WIT_STATUS_OK ||
        after_sleep < sleep_deadline ||
        !check(*original)) {
        return 1126;
    }
    auto sleep_report = (WitU64 *)(original + 1);
    sleep_report[0] = sleep_deadline;
    sleep_report[1] = before_sleep;
    sleep_report[2] = after_sleep;
    WitU64 barrier_result = 99;
    SetLastError(0x12341111);
    PalFlushProcessWriteBuffers();
    if (wit_native_call(WIT_CALL_PROCESS_WRITE_BARRIER, 0, 0, 0, &barrier_result) != WIT_STATUS_OK ||
        barrier_result ||
        GetLastError() != 0x12341111) {
        return 1127;
    }
    for (WitU32 i = 0; i < 3; ++i) {
        barrier_result = 99;
        if (wit_native_call(WIT_CALL_PROCESS_WRITE_BARRIER, i == 0, i == 1, i == 2, &barrier_result) !=
                WIT_STATUS_INVALID_ARGUMENT ||
            barrier_result ||
            GetLastError() != 0x12341111) {
            return 1128;
        }
    }
    return WIT_TEST_EXIT_CODE;
}
