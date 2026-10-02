#include "pal.witos.h"
#include "tls.h"
#include "protocol.h"

static const WitUserImageInfo *expected;
static const WitUserStartup *handoff;
static WitU64 mode;
static volatile WitU64 constructions, destructions;
static __declspec(thread) WitU64 local_value;
static volatile unsigned char bss[5120];

static WitU64 *report()
{
    return (WitU64 *)WIT_GC_INFO_REPORT;
}

static bool found(WitU64 address)
{
    const DWORD before = GetLastError();
    return PalGetModuleHandleFromPointer((void *)(uintptr_t)address) == (HANDLE)(uintptr_t)expected->Base &&
        GetLastError() == before;
}

static bool absent(WitU64 address)
{
    SetLastError(0x12345678);
    return !PalGetModuleHandleFromPointer((void *)(uintptr_t)address) && GetLastError() == ERROR_INVALID_ADDRESS;
}

static bool bounds()
{
    uint8_t *low = nullptr, *high = nullptr;
    const DWORD before = GetLastError();
    PalGetModuleBounds((HANDLE)(uintptr_t)expected->Base, &low, &high);
    return (uintptr_t)low == expected->Base &&
        (uintptr_t)high == expected->Base + expected->ImageSize - 1 &&
        GetLastError() == before;
}

class ModuleLocal {
public:
    ModuleLocal() noexcept
    {
        if (GetLastError() || !bounds() || !found((uintptr_t)&bounds)) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        ++constructions;
        local_value = 73;
    }

    ~ModuleLocal() noexcept
    {
        if (local_value != 73 || !bounds() || !found((uintptr_t)&bounds)) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        ++destructions;
    }
};

static thread_local ModuleLocal local;

static WitU64 worker(WitU64 argument)
{
    if (argument != 73 || local_value != 73 || !bounds() || !found((uintptr_t)&worker)) {
        return 1401;
    }
    WitUserThreadInfo info;
    if (wit_native_call(WIT_CALL_THREAD_QUERY, (uintptr_t)&info, sizeof(info), WIT_THREAD_INFO_VERSION, nullptr) !=
            WIT_STATUS_OK ||
        !absent((uintptr_t)&info) ||
        !absent(info.RawTls) ||
        !absent(info.CompilerTls)) {
        return 1402;
    }
    SetLastError(0x87654321);
    return 73;
}

static void invalid_bounds()
{
    report()[1] = 1;
    report()[2] = 0x1111;
    report()[3] = 0x2222;
    auto low = (uint8_t **)&report()[2];
    auto high = (uint8_t **)&report()[3];
    HANDLE module = (HANDLE)(uintptr_t)expected->Base;
    if (mode == 1) {
        module = nullptr;
    }
    if (mode == 2) {
        module = (HANDLE)(uintptr_t)(expected->Base + 1);
    }
    if (mode == 3) {
        low = nullptr;
    }
    if (mode == 4) {
        high = low;
    }
    if (mode == 6) {
        high = nullptr;
    }
    PalGetModuleBounds(module, low, high);
    report()[1] = 2; // Supervisor must reject a returned invalid void call.
}

extern "C" void wit_module_configure(const WitUserStartup *startup)
{
    handoff = startup;
    expected = (const WitUserImageInfo *)startup->ImageInfo;
    mode = ((const WitUserTestConfig *)startup)->Mode;
    report()[0] = mode;
    if (wit_native_process_image() ||
        PalGetModuleHandleFromPointer((void *)(uintptr_t)expected->Entry) ||
        GetLastError() != ERROR_NOT_READY) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    if (mode == 5) {
        invalid_bounds(); // Lookup/bounds before publication.
    }
    SetLastError(0);
}

extern "C" WitU64 wit_module_program(void)
{
    if (constructions != 1 || destructions || local_value != 73 || wit_native_process_image() != expected) {
        return 1410;
    }
    if (mode) {
        invalid_bounds();
        return 1411;
    }
    SetLastError(0xABCDEF01);
    bss[0] = 17;
    bss[sizeof(bss) - 1] = 31;
    if (!bounds() ||
        !found(expected->Base) ||
        !found(expected->Base + expected->HeadersSize - 1) ||
        !found(expected->Entry) ||
        !found((uintptr_t)&expected) ||
        !found((uintptr_t)bss) ||
        !found((uintptr_t)&bss[sizeof(bss) - 1])) {
        return 1412;
    }
    bool gap = false, zero_fill = false;
    for (WitU32 i = 0; i < expected->RangeCount; ++i) {
        const auto &part = expected->Ranges[i];
        if (!found(expected->Base + part.Rva) || !found(expected->Base + part.Rva + part.Size - 1)) {
            return 1413;
        }
        if (part.Size > part.InitializedSize) {
            zero_fill = true;
            if (!found(expected->Base + part.Rva + part.InitializedSize)) {
                return 1414;
            }
        }
        const WitU64 end = part.Rva + part.Size;
        bool next = false;
        for (WitU32 j = 0; j < expected->RangeCount; ++j) {
            if (expected->Ranges[j].Rva == end) {
                next = true;
            }
        }
        if (!next && end < expected->ImageSize) {
            gap = true;
            if (!absent(expected->Base + end)) {
                return 1415;
            }
        }
    }
    if (!gap ||
        !zero_fill ||
        !absent(0) ||
        !absent(~0ULL) ||
        !absent(expected->Base - 1) ||
        !absent(expected->Base + expected->ImageSize) ||
        !absent(expected->Base + expected->HeadersSize) ||
        !absent((uintptr_t)handoff) ||
        !absent((uintptr_t)expected) ||
        !absent((uintptr_t)&mode + expected->ImageSize)) {
        return 1416;
    }
    WitU64 memory = 0;
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, 4096, 4096, 0, &memory) != WIT_STATUS_OK ||
        !absent(memory) ||
        wit_native_call(WIT_CALL_MEMORY_COMMIT, memory, 4096, 3, nullptr) != WIT_STATUS_OK ||
        !absent(memory) ||
        wit_native_call(WIT_CALL_MEMORY_RELEASE, memory, 0, 0, nullptr) != WIT_STATUS_OK ||
        !absent(memory)) {
        return 1417;
    }
    for (WitU64 i = 0; i < 3; ++i) {
        WitU64 handle = 0, result = 0;
        SetLastError(0x10203040);
        if (wit_native_thread_create(worker, 73, &handle) != WIT_STATUS_OK ||
            wit_native_call(WIT_CALL_THREAD_JOIN, handle, 0, 0, &result) != WIT_STATUS_OK ||
            result != 73 ||
            wit_native_call(WIT_CALL_CLOSE, handle, 0, 0, nullptr) != WIT_STATUS_BAD_HANDLE ||
            constructions != i + 2 ||
            destructions != i + 1 ||
            GetLastError() != 0x10203040) {
            return 1418;
        }
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" WitU64 wit_module_finish(WitU64 result)
{
    if (result != WIT_TEST_EXIT_CODE) {
        return result;
    }
    return constructions == 4 && destructions == 4 && bounds() ? result : 1419;
}
