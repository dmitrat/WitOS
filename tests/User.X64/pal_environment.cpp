#include "pal.witos.h"
#include "pal_environment.witos.h"
#include "tls.h"
#include "../User/protocol.h"
#include <new>
extern "C" DWORD WINAPI wit_native_environment_get(LPCWSTR, LPWSTR, DWORD);
extern "C" decltype(&GetEnvironmentVariableW) const __imp_GetEnvironmentVariableW;
extern "C" decltype(&GetEnvironmentStringsW) const __imp_GetEnvironmentStringsW;
extern "C" decltype(&FreeEnvironmentStringsW) const __imp_FreeEnvironmentStringsW;

#define ENTRY(name, value) \
    {name, value, (uint32_t)(sizeof(name) / sizeof(wchar_t) - 1), (uint32_t)(sizeof(value) / sizeof(wchar_t) - 1)}
static const wchar_t long_value[] =
    L"0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
    L"0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
    L"0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789";
static const WitPalEnvironmentEntry entries[] = {ENTRY(L"DOTNET_GCHeapHardLimit", L"40000"),
    ENTRY(L"DOTNET_Empty", L""), ENTRY(L"DOTNET_Text", L"\x0416\x05D0\x20AC\xD83D\xDE80"),
    ENTRY(L"DOTNET_Long", long_value), ENTRY(L"Extra0", L""), ENTRY(L"Extra1", L""), ENTRY(L"Extra2", L""),
    ENTRY(L"Extra3", L""), ENTRY(L"Extra4", L""), ENTRY(L"Extra5", L""), ENTRY(L"Extra6", L""), ENTRY(L"Extra7", L""),
    ENTRY(L"Extra8", L""), ENTRY(L"Extra9", L""),
    ENTRY(L"KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK", L"1"),
    ENTRY(L"DOTNET_MaxValue",
        L"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
        L"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
        L"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
        L"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
        L"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
        L"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
        L"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
        L"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
        L"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
        L"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")};
static const WitPalEnvironmentEntry duplicate[] = {ENTRY(L"Key", L"1"), ENTRY(L"kEY", L"2")};
static const WitPalEnvironmentEntry bad_name[] = {ENTRY(L"a=b", L"1")};
static const WitPalEnvironmentEntry embedded_null[] = {ENTRY(L"Ok", L"a\0b")};
static const WitPalEnvironmentEntry truncated[] = {{L"Name", L"Value", 3, 5}};
static wchar_t writable[] = L"mutable";
static const WitPalEnvironmentEntry mutable_value[] = {ENTRY(L"Name", writable)};
static const WitPalEnvironmentEntry unmapped[] = {{L"Name", (const wchar_t *)~0ULL, 4, 1}};
static const WitPalEnvironmentEntry too_long[] = {
    ENTRY(L"KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK", L"")};
static const WitPalEnvironmentEntry huge[] = {{L"Name", L"Value", 4, ~0U}};
static wchar_t oversized[32769];
static WitU64 mode;
static volatile WitU64 constructed, destroyed;
static wchar_t *worker_block;

static bool equal(const wchar_t *a, const wchar_t *b, size_t size)
{
    for (size_t i = 0; i < size; ++i) {
        if (a[i] != b[i]) {
            return false;
        }
    }
    return true;
}

static bool same_memory(const WitUserMemoryInfo &a, const WitUserMemoryInfo &b)
{
    return a.OwnedBytes == b.OwnedBytes &&
        a.PhysicalAvailableBytes == b.PhysicalAvailableBytes &&
        a.ReservedBytes == b.ReservedBytes &&
        a.DynamicCommittedBytes == b.DynamicCommittedBytes &&
        a.PrivatePageTableBytes == b.PrivatePageTableBytes &&
        a.ReservationCount == b.ReservationCount;
}

static bool snapshot(WitUserMemoryInfo *info)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (uintptr_t)info, sizeof(*info), WIT_MEMORY_INFO_VERSION, nullptr) ==
        WIT_STATUS_OK;
}

static bool lookup()
{
    wchar_t buffer[8] = {0x1111, 0x2222, 0x3333, 0x4444, 0x5555, 0x6666, 0x7777, 0x1234};
    const DWORD saved = GetLastError();
    if (mode == 1) {
        return !PalGetEnvironmentVariable(L"DOTNET_GCHeapHardLimit", buffer, 8) &&
            GetLastError() == ERROR_ENVVAR_NOT_FOUND &&
            buffer[0] == 0x1111;
    }
    return wit_native_environment_get(L"DOTNET_GCHeapHardLimit", nullptr, 0) == 6 &&
        PalGetEnvironmentVariable(L"dotnet_gcheaphardlimit", nullptr, 0) == 6 &&
        GetEnvironmentVariableW(L"DOTNET_GCHeapHardLimit", buffer, 5) == 6 &&
        buffer[0] == 0x1111 &&
        PalGetEnvironmentVariable(L"DOTNET_GCHeapHardLimit", buffer, 6) == 5 &&
        equal(buffer, L"40000", 6) &&
        buffer[6] == 0x7777 &&
        GetLastError() == saved;
}

static bool block_matches(const wchar_t *block)
{
    if (!block) {
        return false;
    }
    if (mode == 1) {
        return block[0] == 0 && block[1] == 0;
    }
    size_t at = 0;
    for (const auto &entry : entries) {
        if (!equal(block + at, entry.Name, entry.NameLength)) {
            return false;
        }
        at += entry.NameLength;
        if (block[at++] != L'=') {
            return false;
        }
        if (!equal(block + at, entry.Value, entry.ValueLength)) {
            return false;
        }
        at += entry.ValueLength;
        if (block[at++]) {
            return false;
        }
    }
    return block[at] == 0;
}

static bool environment_blocks()
{
    WitUserMemoryInfo before, after;
    if (!snapshot(&before)) {
        return false;
    }
    SetLastError(0x7890);
    auto first = GetEnvironmentStringsW();
    auto second = __imp_GetEnvironmentStringsW();
    if (!first ||
        !second ||
        first == second ||
        !block_matches(first) ||
        !block_matches(second) ||
        GetLastError() != 0x7890) {
        return false;
    }
    first[0] = L'X';
    if (!block_matches(second) || !lookup()) {
        return false;
    }
    if (FreeEnvironmentStringsW(first + 1) || GetLastError() != ERROR_INVALID_PARAMETER) {
        return false;
    }
    SetLastError(0x6543);
    if (!__imp_FreeEnvironmentStringsW(first) || !FreeEnvironmentStringsW(second) || GetLastError() != 0x6543) {
        return false;
    }
    if (FreeEnvironmentStringsW(first) ||
        FreeEnvironmentStringsW(nullptr) ||
        GetLastError() != ERROR_INVALID_PARAMETER) {
        return false;
    }
    wchar_t *held[WIT_PAL_ENV_BLOCK_CAPACITY];
    for (uint32_t i = 0; i < WIT_PAL_ENV_BLOCK_CAPACITY; ++i) {
        if (!(held[i] = GetEnvironmentStringsW())) {
            return false;
        }
    }
    if (GetEnvironmentStringsW() || GetLastError() != ERROR_NOT_ENOUGH_MEMORY) {
        return false;
    }
    for (auto block : held) {
        if (!FreeEnvironmentStringsW(block)) {
            return false;
        }
    }
    // A foreign allocation must never be mistaken for an environment block.
    auto foreign = new (std::nothrow) wchar_t[4];
    if (!foreign) {
        return false;
    }
    if (FreeEnvironmentStringsW(foreign) || GetLastError() != ERROR_INVALID_PARAMETER) {
        return false;
    }
    delete[] foreign;
    void *slots[128];
    for (size_t i = 0; i < 128; ++i) {
        if (!(slots[i] = ::operator new(1, std::nothrow))) {
            return false;
        }
    }
    if (GetEnvironmentStringsW() || GetLastError() != ERROR_NOT_ENOUGH_MEMORY) {
        return false;
    }
    for (auto slot : slots) {
        ::operator delete(slot);
    }
    auto retry = GetEnvironmentStringsW();
    if (!block_matches(retry) || !FreeEnvironmentStringsW(retry)) {
        return false;
    }
    return snapshot(&after) && same_memory(before, after);
}

class EnvironmentLocal {
public:
    EnvironmentLocal() noexcept
    {
        if (!lookup()) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        ++constructed;
    }

    ~EnvironmentLocal() noexcept
    {
        if (!lookup()) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        ++destroyed;
    }
};

static thread_local EnvironmentLocal local;

static WitU64 worker(WitU64 value)
{
    for (size_t i = 0; i < 6; ++i) {
        SetLastError((DWORD)(0x1000 + value));
        if (!lookup()) {
            return 1510;
        }
        auto block = GetEnvironmentStringsW();
        if (!block_matches(block) || !FreeEnvironmentStringsW(block)) {
            return 1512;
        }
        (void)wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr);
        if (GetLastError() != (mode == 1 ? ERROR_ENVVAR_NOT_FOUND : 0x1000 + value)) {
            return 1511;
        }
    }
    if (value == 2) {
        worker_block = GetEnvironmentStringsW();
        if (!worker_block) {
            return 1513;
        }
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" WitU64 wit_environment_configure(const WitUserStartup *startup)
{
    mode = ((const WitUserTestConfig *)startup)->Mode;
    *(WitU64 *)WIT_GC_INFO_REPORT = mode;
    // The process's environment exists before any image seeds it (P6.4.j3a); this component's creator set none.
    wchar_t sentinel = 0x1234;
    if (wit_pal_environment_initialize(nullptr, 0) ||
        GetLastError() != ERROR_NOT_READY ||
        PalGetEnvironmentVariable(L"Name", &sentinel, 1) ||
        GetLastError() != ERROR_ENVVAR_NOT_FOUND ||
        sentinel != 0x1234) {
        return 1500;
    }
    auto empty = GetEnvironmentStringsW();
    if (!empty || empty[0] || empty[1] || !FreeEnvironmentStringsW(empty)) {
        return 1507; // the empty block holds two terminators, as on Windows
    }
    wit_native_process_image_initialize(startup);
    WitUserMemoryInfo before, after;
    if (!snapshot(&before)) {
        return 1501;
    }
    WitPalEnvironmentEntry stack_entry = ENTRY(L"Stack", L"bad");
    const WitPalEnvironmentEntry *bad[] = {
        &stack_entry, duplicate, bad_name, embedded_null, truncated, mutable_value, unmapped, huge, too_long};
    for (size_t i = 0; i < sizeof(bad) / sizeof(bad[0]); ++i) {
        if (wit_pal_environment_initialize(bad[i], i == 1 ? 2 : 1) ||
            GetLastError() != ERROR_INVALID_PARAMETER ||
            PalGetEnvironmentVariable(L"Key", nullptr, 0) ||
            GetLastError() != ERROR_ENVVAR_NOT_FOUND) {
            return 1502; // a rejected table seeds nothing
        }
    }
    if (wit_pal_environment_initialize(entries, WIT_PAL_ENV_CAPACITY + 1) ||
        wit_pal_environment_initialize(nullptr, 1) ||
        wit_pal_environment_initialize(entries, 0) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        !snapshot(&after) ||
        !same_memory(before, after)) {
        return 1503;
    }
    SetLastError(0x5555);
    if (!wit_pal_environment_initialize(mode == 1 ? nullptr : entries, mode == 1 ? 0 : 16) ||
        GetLastError() != 0x5555) {
        return 1504;
    }
    if (wit_pal_environment_initialize(nullptr, 0) || GetLastError() != ERROR_ALREADY_INITIALIZED) {
        return 1505;
    }
    SetLastError(0);
    return 0;
}

static bool copy(const wchar_t *input, const unsigned char *expected, size_t size)
{
    SetLastError(0xCAFEBABE);
    char *result = PalCopyTCharAsChar(input);
    if (!result || GetLastError() != 0xCAFEBABE) {
        return false;
    }
    bool matches = true;
    for (size_t i = 0; i < size; ++i) {
        if ((unsigned char)result[i] != expected[i]) {
            matches = false;
        }
    }
    delete[] result;
    return matches;
}

extern "C" WitU64 wit_environment_program()
{
    if (constructed != 1 ||
        destroyed ||
        !lookup() ||
        !wit_native_image_range(wit_native_process_image(), (uintptr_t)&__imp_GetEnvironmentVariableW, sizeof(void *),
            WIT_IMAGE_INFO_READ, WIT_IMAGE_INFO_WRITE | WIT_IMAGE_INFO_EXECUTE, 1) ||
        __imp_GetEnvironmentVariableW != &wit_native_environment_get) {
        return 1520;
    }
    wchar_t sentinel = 0x1234;
    if (PalGetEnvironmentVariable(nullptr, &sentinel, 1) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        PalGetEnvironmentVariable(L"", &sentinel, 1) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        PalGetEnvironmentVariable(L"Bad=Name", &sentinel, 1) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        PalGetEnvironmentVariable(L"\x0416", &sentinel, 1) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        PalGetEnvironmentVariable(L"DOTNET_Text", nullptr, 1) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        sentinel != 0x1234) {
        return 1521;
    }
    if (GetEnvironmentVariableW(L"PATH", &sentinel, 1) ||
        GetLastError() != ERROR_ENVVAR_NOT_FOUND ||
        sentinel != 0x1234) {
        return 1522;
    }
    if (!mode) {
        SetLastError(0x1111);
        if (PalGetEnvironmentVariable(L"DOTNET_Empty", nullptr, 0) != 1 ||
            PalGetEnvironmentVariable(L"DOTNET_Empty", &sentinel, 1) ||
            sentinel ||
            GetLastError() != 0x1111) {
            return 1523;
        }
        if (PalGetEnvironmentVariable(entries[14].Name, nullptr, 0) != 2 ||
            PalGetEnvironmentVariable(L"DOTNET_MaxValue", nullptr, 0) != 1024 ||
            PalGetEnvironmentVariable(too_long[0].Name, &sentinel, 1) ||
            GetLastError() != ERROR_INVALID_PARAMETER) {
            return 1526;
        }
        wchar_t buffer[301];
        if (PalGetEnvironmentVariable(L"DOTNET_Long", buffer, 260) != 301 ||
            PalGetEnvironmentVariable(L"DOTNET_Long", buffer, 301) != 300 ||
            !equal(buffer, long_value, 301)) {
            return 1524;
        }
        wchar_t unicode[6];
        if (PalGetEnvironmentVariable(L"DOTNET_Text", unicode, 6) != 5 || !equal(unicode, entries[2].Value, 6)) {
            return 1525;
        }
    }
    if (!environment_blocks()) {
        return 1527;
    }
    WitUserMemoryInfo before, after;
    if (!snapshot(&before)) {
        return 1530;
    }
    const unsigned char text[] = {0xD0, 0x96, 0xD7, 0x90, 0xE2, 0x82, 0xAC, 0xF0, 0x9F, 0x9A, 0x80, 0};
    const unsigned char replacement[] = {0xEF, 0xBF, 0xBD, 'A', 0xEF, 0xBF, 0xBD, 0};
    const unsigned char edges[] = {0x7F, 0xC2, 0x80, 0xDF, 0xBF, 0xE0, 0xA0, 0x80, 0xED, 0x9F, 0xBF, 0xEE, 0x80, 0x80,
        0xEF, 0xBF, 0xBF, 0xF0, 0x90, 0x80, 0x80, 0xF4, 0x8F, 0xBF, 0xBF, 0};
    if (!copy(L"", (const unsigned char *)"", 1) ||
        !copy(L"ABC", (const unsigned char *)"ABC", 4) ||
        !copy(entries[2].Value, text, sizeof(text)) ||
        !copy(L"\xD800"
              L"A"
              L"\xDC00",
            replacement, sizeof(replacement)) ||
        !copy(L"\x7F\x80\x7FF\x800\xD7FF\xE000\xFFFF\xD800\xDC00\xDBFF\xDFFF", edges, sizeof(edges))) {
        return 1531;
    }
    if (PalCopyTCharAsChar(nullptr) || GetLastError() != ERROR_INVALID_PARAMETER) {
        return 1532;
    }
    for (size_t i = 0; i < 32768; ++i) {
        oversized[i] = L'A';
    }
    if (PalCopyTCharAsChar(oversized) || GetLastError() != ERROR_BUFFER_OVERFLOW) {
        return 1533;
    }
    oversized[32767] = 0;
    auto longest = PalCopyTCharAsChar(oversized);
    if (!longest || longest[0] != 'A' || longest[32766] != 'A' || longest[32767]) {
        return 1534;
    }
    delete[] longest;
    // Exhaust actual heap descriptors, then prove conversion fails without leaks.
    void *blocks[128];
    for (size_t i = 0; i < 128; ++i) {
        blocks[i] = ::operator new(1, std::nothrow);
        if (!blocks[i]) {
            return 1535;
        }
    }
    if (PalCopyTCharAsChar(L"oom") || GetLastError() != ERROR_NOT_ENOUGH_MEMORY) {
        return 1536;
    }
    for (size_t i = 0; i < 128; ++i) {
        ::operator delete(blocks[i]);
    }
    if (!copy(L"retry", (const unsigned char *)"retry", 6) || !snapshot(&after) || !same_memory(before, after)) {
        return 1537;
    }
    WitU64 handles[2], result;
    for (WitU64 i = 0; i < 2; ++i) {
        if (wit_native_thread_create(worker, i, &handles[i]) != WIT_STATUS_OK) {
            return 1540;
        }
    }
    for (size_t i = 0; i < 2; ++i) {
        if (wit_native_thread_join(handles[i], &result) != WIT_STATUS_OK || result != WIT_TEST_EXIT_CODE) {
            return 1541;
        }
    }
    if (wit_native_thread_create(worker, 2, &handles[0]) != WIT_STATUS_OK ||
        wit_native_thread_join(handles[0], &result) != WIT_STATUS_OK ||
        result != WIT_TEST_EXIT_CODE ||
        constructed != 4 ||
        destroyed != 3) {
        return 1542;
    }
    if (!block_matches(worker_block) || !FreeEnvironmentStringsW(worker_block)) {
        return 1543;
    }
    worker_block = nullptr;
    return WIT_TEST_EXIT_CODE;
}

extern "C" WitU64 wit_environment_finish(WitU64 result)
{
    return result != WIT_TEST_EXIT_CODE ? result : (destroyed == 4 && lookup() ? WIT_TEST_EXIT_CODE : 1550);
}
