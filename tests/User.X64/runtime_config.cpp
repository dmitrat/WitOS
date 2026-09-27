#include "common.h"
#include "gcenv.h"
#include "gcenv.ee.h"
#include "gcconfig.h"
#include "RhConfig.h"
#include "pal.witos.h"
#include "pal_environment.witos.h"
#include "tls.h"
#include "protocol.h"
#include <errno.h>

extern "C" bool wit_test_runtime_allocator();
extern "C" bool wit_test_interface_dispatch();
extern "C" bool wit_test_runtime_instance();
extern "C" bool wit_test_runtime_thread_record();
extern "C" void wit_test_runtime_missing_tls();
extern "C" bool wit_test_barrier_early();
extern "C" bool wit_test_barrier_threads();
static RhConfig config;
RhConfig* g_pRhConfig = &config;
/* Fixture-owned configuration inputs, using the actual upstream declaration.
 * No GC heap, collector callback or ThreadStore implementation is supplied. */
GCHeapHardLimitInfo g_gcHeapHardLimitInfo;
bool g_gcHeapHardLimitInfoSpecified = false;
struct SettingsBlob { uint32_t count; const char* pointers[14]; };
struct KnobsBlob { uint32_t count; const char* pointers[10]; };
static_assert(offsetof(SettingsBlob, pointers) == offsetof(RhConfig::Config, m_first));
static_assert(offsetof(KnobsBlob, pointers) == offsetof(RhConfig::Config, m_first));
extern "C" const SettingsBlob g_compilerEmbeddedSettingsBlob = {7, {
    "FromSettings", "CaseKey", "GCHeapHardLimit", "gcServer", "Thread_DefaultStackSize", "Invalid", "Overflow",
    "2a", "2b", "40000", "0", "10000", "junk", "18446744073709551616"
}};
extern "C" const KnobsBlob g_compilerEmbeddedKnobsBlob = {5, {
    "System.GC.HeapHardLimit", "System.GC.HeapHardLimitPercent", "System.GC.Concurrent", "Knob.Bool", "Knob.Bad",
    "1048576", "60", "true", "True", "invalid"
}};
#define ENTRY(name, value) { name, value, (uint32_t)(sizeof(name)/2-1), (uint32_t)(sizeof(value)/2-1) }
static const wchar_t long_text[] =
    L"0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
    L"0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
    L"0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789";
static const WitPalEnvironmentEntry environment[] = {
    ENTRY(L"DOTNET_GCHeapHardLimit", L"80000"), ENTRY(L"DOTNET_gcConcurrent", L"0"),
    ENTRY(L"DOTNET_gcConservative", L"0"), ENTRY(L"DOTNET_FromSettings", L"2f"),
    ENTRY(L"DOTNET_Number", L"42"), ENTRY(L"DOTNET_Bad", L"0x10"), ENTRY(L"DOTNET_Empty", L""),
    ENTRY(L"DOTNET_Text", L"\x0416\xD83D\xDE80"), ENTRY(L"DOTNET_Long", long_text),
    ENTRY(L"DOTNET_Max", L"ffffffffffffffff"), ENTRY(L"DOTNET_TooLong", L"10000000000000000")
};
static const WitPalEnvironmentEntry cpu_profiles[][1] = {
    { ENTRY(L"DOTNET_PROCESSOR_COUNT", L"1") },
    { ENTRY(L"DOTNET_PROCESSOR_COUNT", L"2") },
    { ENTRY(L"DOTNET_PROCESSOR_COUNT", L"0") },
    { ENTRY(L"DOTNET_PROCESSOR_COUNT", L"65536") },
    { ENTRY(L"DOTNET_PROCESSOR_COUNT", L"invalid") },
    { ENTRY(L"DOTNET_PROCESSOR_COUNT", L"65535") }
};
static WitU64 mode;
static int64_t expected_limit;
static unsigned enumeration_count, enumerated_limit, enumerated_conservative, enumerated_string;
static WitU64* report() { return (WitU64*)WIT_GC_INFO_REPORT; }
static bool same_memory(const WitUserMemoryInfo& a, const WitUserMemoryInfo& b)
{
    return a.OwnedBytes == b.OwnedBytes && a.PhysicalAvailableBytes == b.PhysicalAvailableBytes &&
        a.ReservedBytes == b.ReservedBytes && a.DynamicCommittedBytes == b.DynamicCommittedBytes &&
        a.PrivatePageTableBytes == b.PrivatePageTableBytes && a.ReservationCount == b.ReservationCount;
}
static bool snapshot(WitUserMemoryInfo* p)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)p, sizeof(*p), WIT_MEMORY_INFO_VERSION, nullptr) == WIT_STATUS_OK;
}
static bool crt()
{
    struct Case { const char* text; int base; uint64_t value; size_t end; int error; };
    const Case cases[] = {
        {" \t-0x2a!", 0, 0ULL-42, 7, 71}, {"18446744073709551615!", 10, UINT64_MAX, 20, 71},
        {"18446744073709551616tail", 10, UINT64_MAX, 20, ERANGE},
        {"-18446744073709551615", 10, 1, 21, 71}, {"-18446744073709551616", 10, UINT64_MAX, 21, ERANGE},
        {"09", 0, 0, 1, 71}, {"0x", 0, 0, 1, 71}, {"-", 10, 0, 0, 71}, {"", 10, 0, 0, 71},
        {"xyz", 36, 44027, 3, 71}, {"0Xff", 16, 255, 4, 71}, {"1012", 2, 5, 3, 71},
        {"12", 1, 0, 0, EINVAL}, {"12", 37, 0, 0, EINVAL}, {"12", -1, 0, 0, EINVAL}
    };
    auto (*volatile parse)(const char*, char**, int) = &strtoull;
    for (const auto& test : cases) {
        errno = 71; SetLastError(0x12345678);
        char* end = nullptr;
        if (parse(test.text, &end, test.base) != test.value || end != test.text + test.end ||
            errno != test.error || GetLastError() != 0x12345678) return false;
    }
    char data[8] = {'a','b','c',0,'d','e','f',0};
    void* (*volatile copy)(void*, const void*, size_t) = &memcpy;
    size_t (*volatile length)(const char*) = &strlen;
    int (*volatile compare)(const char*, const char*) = &strcmp;
    int (*volatile insensitive)(const char*, const char*) = &_stricmp;
    return copy(data + 4, data, 4) == data + 4 && length(data + 4) == 3 &&
        !compare(data, data + 4) && compare("\xFF", "\x80") > 0 && insensitive("AbC", "aBc") == 0 &&
        insensitive("\xFF", "\x80") > 0 && copy(nullptr, nullptr, 0) == nullptr;
}
static bool precedence()
{
    uint64_t value = 99;
    bool flag = true;
    if (!config.ReadConfigValue("FromSettings", &value) || value != (mode ? 42 : 47) ||
        !config.ReadConfigValue("CaseKey", &value) || value != 43 ||
        config.ReadConfigValue("casekey", &value) || value != 43 ||
        !config.ReadKnobUInt64Value("system.gc.heaphardlimit", &value) || value != 1048576 ||
        !config.ReadKnobBooleanValue("knob.bool", &flag) || flag ||
        !config.ReadKnobUInt64Value("Knob.Bad", &value) || value) return false;
    errno = 71;
    if (!config.ReadConfigValue("Overflow", &value, true) || value != UINT64_MAX || errno != ERANGE ||
        !config.ReadConfigValue("Invalid", &value) || value ||
        GetDefaultStackSizeSetting() != 65536 || config.GetKnobCount() != 5 ||
        strcmp(config.GetKnobNames()[0], "System.GC.HeapHardLimit") || strcmp(config.GetKnobValues()[0], "1048576")) return false;
    SetLastError(0);
    if (config.GetgcServer() || GetLastError() != ERROR_ENVVAR_NOT_FOUND) return false;
    SetLastError(0x4321);
    if (config.GetgcServer() || GetLastError() != 0x4321) return false; // Actual accessor cache.
    if (!mode) {
        if (!RhConfig::Environment::TryGetIntegerValue("Number", &value) || value != 0x42 ||
            !RhConfig::Environment::TryGetIntegerValue("Number", &value, true) || value != 42 ||
            !RhConfig::Environment::TryGetIntegerValue("Max", &value) || value != UINT64_MAX) return false;
        value = 123;
        if (RhConfig::Environment::TryGetIntegerValue("Bad", &value) || value != 123 ||
            RhConfig::Environment::TryGetIntegerValue("TooLong", &value) || value != 123 ||
            RhConfig::Environment::TryGetIntegerValue("Empty", &value) || value != 123) return false;
    }
    return true;
}
static bool strings()
{
    WitUserMemoryInfo before, after;
    if (!snapshot(&before)) return false;
    char* value = (char*)0x1234;
    if (RhConfig::Environment::TryGetStringValue("Missing", &value) || value != (char*)0x1234) return false;
    if (!mode) {
        if (!RhConfig::Environment::TryGetStringValue("Text", &value) || !value || strcmp(value, "\xD0\x96\xF0\x9F\x9A\x80")) return false;
        delete[] value;
        if (!RhConfig::Environment::TryGetStringValue("Long", &value) || !value || strlen(value) != 300 ||
            value[0] != '0' || value[299] != '9') return false;
        delete[] value;
        // Both a failed UTF-8 allocation and a failed wide resize must return
        // false, preserve the output and free every temporary allocation.
        void* blocks[128];
        for (size_t i = 0; i < 128; ++i) { blocks[i] = ::operator new(1, std::nothrow); if (!blocks[i]) return false; }
        value = (char*)0x1234;
        if (RhConfig::Environment::TryGetStringValue("Text", &value) || value != (char*)0x1234 ||
            RhConfig::Environment::TryGetStringValue("Long", &value) || value != (char*)0x1234) return false;
        ::operator delete(blocks[127]); // Wide buffer succeeds, UTF-8 conversion still exhausts capacity.
        if (RhConfig::Environment::TryGetStringValue("Long", &value) || value != (char*)0x1234) return false;
        for (size_t i = 0; i < 127; ++i) ::operator delete(blocks[i]);
        if (!RhConfig::Environment::TryGetStringValue("Long", &value) || !value) return false;
        delete[] value;
    }
    return snapshot(&after) && same_memory(before, after);
}
static void enumerate(void*, void* name, void*, GCConfigurationType type, int64_t value)
{
    ++enumeration_count;
    if (!strcmp((const char*)name, "GCHeapHardLimit") && type == GCConfigurationType::Int64 && value == expected_limit) ++enumerated_limit;
    if (!strcmp((const char*)name, "ConservativeGC") && type == GCConfigurationType::Boolean && value == 1) ++enumerated_conservative;
    if (type == GCConfigurationType::StringUtf8 && value == 0) ++enumerated_string;
}
static bool cached_init()
{
    const DWORD saved = GetLastError();
    for (size_t attempt = 0; attempt < 16; ++attempt) {
        if (PalInit()) return true;
        if (GetLastError() != ERROR_BUSY ||
            wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) return false;
        SetLastError(saved);
    }
    return false;
}
static WitU64 worker(WitU64 argument)
{
    if (errno) return 1670;
    for (size_t i = 0; i < 8; ++i) {
        errno = (int)(100 + argument); SetLastError((DWORD)(200 + argument));
        uint64_t value;
        if (!config.ReadKnobUInt64Value("System.GC.HeapHardLimitPercent", &value) || value != 60 ||
            !cached_init() || GCToOSInterface::GetTotalProcessorCount() != 1 || GCConfig::GetGCHeapHardLimit() != expected_limit) return 1671;
        (void)wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr);
        if (errno != 100 + argument || GetLastError() != 200 + argument) return 1672;
    }
    return WIT_TEST_EXIT_CODE;
}
extern "C" WitU64 wit_native_main(const WitUserStartup* startup)
{
    mode = ((const WitUserTestConfig*)startup)->Mode;
    report()[0] = mode; report()[1] = 0;
    if (mode == 12 || mode == 13) {
        if (!wit_test_barrier_early()) return 1695;
        if (mode == 12) {
            wit_native_process_image_initialize(startup);
            wit_native_tls_initialize(startup);
            if (!wit_test_barrier_threads()) return 1696;
            wit_native_tls_leave();
        }
        report()[1] = 65536;
        return WIT_TEST_EXIT_CODE;
    }
    if (mode == 11) {
        report()[1] = 32768;
        wit_test_runtime_missing_tls();
        return 1699;
    }
    if (mode == 9 || mode == 10 || mode == 14) {
        wit_native_process_image_initialize(startup);
        if (mode != 9 && !wit_pal_environment_initialize(nullptr, 0)) return 1693;
        wit_native_tls_initialize(startup);
        if (mode != 9 && !PalInit()) return 1694;
        if (!wit_test_interface_dispatch()) return 1691;
        report()[1] = 8192;
        if (mode != 9) {
            if (!wit_test_runtime_instance()) return 1692;
            report()[1] |= 16384;
            if (mode == 14) {
                if (!wit_test_runtime_thread_record()) return 1697;
                report()[1] |= 131072;
            }
        }
        wit_native_tls_leave();
        return WIT_TEST_EXIT_CODE;
    }
    if (mode == 8) {
        // Identical native code, but a PE with no TLS directory. PalInit must
        // reject this before touching errno/compiler GS storage.
        wit_native_process_image_initialize(startup);
        if (!wit_pal_environment_initialize(nullptr, 0) || PalInit() || GetLastError() != ERROR_NOT_READY ||
            GCToOSInterface::GetTotalProcessorCount() || GCConfig::GetGCHeapHardLimit()) return 1680;
        report()[1] = 2048;
        return WIT_TEST_EXIT_CODE;
    }
    // Numeric CRT and compiler TLS work before dynamic constructors.
    if (!crt()) return 1601;
    report()[1] |= 1;
    if (PalInit() || GetLastError() != ERROR_NOT_READY || GCToOSInterface::GetTotalProcessorCount() ||
        GCConfig::GetGCHeapHardLimit()) return 1681;
    wit_native_process_image_initialize(startup);
    if (PalInit() || GetLastError() != ERROR_NOT_READY || GCToOSInterface::GetTotalProcessorCount() ||
        GCConfig::GetGCHeapHardLimit()) return 1682;
    const WitPalEnvironmentEntry* selected = mode == 0 ? environment : (mode == 1 ? nullptr : cpu_profiles[mode - 2]);
    const uint32_t selected_count = mode == 0 ? (uint32_t)(sizeof(environment)/sizeof(environment[0])) : (mode == 1 ? 0 : 1);
    if (!wit_pal_environment_initialize(selected, selected_count)) return 1602;
    g_pRhConfig = nullptr;
    if (PalInit() || GetLastError() != ERROR_NOT_READY || GCToOSInterface::GetTotalProcessorCount() ||
        GCConfig::GetGCHeapHardLimit()) return 1683;
    g_pRhConfig = &config;
    report()[1] |= 64;
    if (mode == 3 || mode == 7) {
        errno = 91;
        for (size_t i = 0; i < 2; ++i)
            if (PalInit() || GetLastError() != ERROR_NOT_SUPPORTED || errno != 91 ||
                GCToOSInterface::GetTotalProcessorCount() || GCConfig::GetGCHeapHardLimit()) return 1684;
        report()[1] |= 1024;
        return WIT_TEST_EXIT_CODE;
    }
    wit_native_tls_initialize(startup);
    if (!precedence()) return 1610;
    report()[1] |= 2;
    if (!strings()) return 1620;
    report()[1] |= 4;
    WitUserMemoryInfo before_init, after_init;
    WitUserThreadInfo info;
    if (!snapshot(&before_init) || wit_native_call(WIT_CALL_THREAD_QUERY, (uintptr_t)&info, sizeof(info),
        WIT_THREAD_INFO_VERSION, nullptr) != WIT_STATUS_OK) return 1685;
    auto raw = (WitU64*)(uintptr_t)info.RawTls;
    const WitU64 saved_self = raw[0], saved_id = raw[1];
    raw[0] = raw[1] = 0; // Writable hints cannot supply processor/thread identity.
    errno = 77; SetLastError(0x87654321);
    const bool initialized = PalInit();
    raw[0] = saved_self; raw[1] = saved_id;
    if (!initialized || errno != 77 || GetLastError() != 0x87654321 ||
        GCToOSInterface::GetTotalProcessorCount() != 1 || PalGetProcessCpuCount() != 1 ||
        !snapshot(&after_init) || !same_memory(before_init, after_init)) return 1686;
    report()[1] |= 128;
    expected_limit = mode ? 262144 : 524288;
    if (GCConfig::GetGCHeapHardLimit() != expected_limit || GCConfig::GetGCHeapHardLimit(17) != expected_limit ||
        GCConfig::GetGCHeapHardLimitPercent() != 60 || GCConfig::GetConcurrentGC() != (mode != 0) ||
        !GCConfig::GetConservativeGC() || GCConfig::GetRetainVM() || !GCConfig::GetRetainVM(true) ||
        GCConfig::GetGCHeapHardLimitSOH(23) != 23) return 1630;
    const char* unused = (const char*)0x1234;
    if (GCToEEInterface::GetStringConfigValue("Text", "Text", &unused) || unused != (const char*)0x1234) return 1631;
    report()[1] |= 8;
    g_gcHeapHardLimitInfo = {786432, UINT64_MAX, UINT64_MAX, UINT64_MAX, UINT64_MAX, UINT64_MAX, UINT64_MAX, UINT64_MAX};
    g_gcHeapHardLimitInfoSpecified = true;
    GCConfig::RefreshHeapHardLimitSettings();
    expected_limit = 786432;
    if (GCConfig::GetGCHeapHardLimit() != expected_limit || GCConfig::GetGCHeapHardLimitPercent() != 60) return 1640;
    GCConfig::EnumerateConfigurationValues(nullptr, enumerate);
    if (enumeration_count < 20 || enumerated_limit != 1 || enumerated_conservative != 1 || !enumerated_string) return 1641;
    report()[1] |= 16;
    g_gcHeapHardLimitInfoSpecified = false; // Re-initialization would now replace the refreshed value.
    errno = 83; SetLastError(0x12344321);
    if (!PalInit() || errno != 83 || GetLastError() != 0x12344321 ||
        GCConfig::GetGCHeapHardLimit() != expected_limit) return 1687;
    report()[1] |= 256;
    WitU64 handles[2], result;
    errno = 42;
    for (WitU64 i = 0; i < 2; ++i) if (wit_native_thread_create(worker, i, &handles[i]) != WIT_STATUS_OK) return 1650;
    for (size_t i = 0; i < 2; ++i)
        if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &result) != WIT_STATUS_OK || result != WIT_TEST_EXIT_CODE) return 1651;
    if (wit_native_thread_create(worker, 2, &handles[0]) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_THREAD_JOIN, handles[0], 0, 0, &result) != WIT_STATUS_OK ||
        result != WIT_TEST_EXIT_CODE || errno != 42) return 1652;
    report()[1] |= 32;
    if (!wit_test_runtime_allocator()) return 1690;
    report()[1] |= 4096;
    GCToOSInterface::Shutdown();
    errno = 89;
    if (PalInit() || GetLastError() != ERROR_INVALID_STATE || errno != 89 ||
        GCToOSInterface::GetTotalProcessorCount() || GCConfig::GetGCHeapHardLimit() != expected_limit) return 1688;
    report()[1] |= 512;
    wit_native_tls_leave();
    return WIT_TEST_EXIT_CODE;
}
