#include "pal.witos.h"
#include "native_security.h"
#include "tls.h"
#include "../User/protocol.h"
#include <errno.h>
extern "C" BOOL WINAPI wit_console_direct_write(HANDLE, LPCVOID, DWORD, LPDWORD, LPOVERLAPPED);
extern "C" HANDLE WINAPI wit_console_direct_handle(DWORD);
extern "C" UINT WINAPI wit_console_direct_codepage();
extern "C" void WINAPI wit_processor_direct_query(PPROCESSOR_NUMBER);
extern "C" const void *const __imp_WriteFile;
extern "C" bool wit_test_encoding();
static volatile WitU64 writes[4];

static bool output(unsigned slot, HANDLE handle, const void *text, DWORD length, bool direct = false)
{
    DWORD done = 99;
    const BOOL ok = direct ? wit_console_direct_write(handle, text, length, &done, nullptr)
                           : WriteFile(handle, text, length, &done, nullptr);
    if (!ok || done != length) {
        return false;
    }
    if (length) {
        ++writes[slot];
    }
    return true;
}

static bool processor()
{
    PROCESSOR_NUMBER a = {1, 2, 3}, b = {4, 5, 6};
    GetCurrentProcessorNumberEx(&a);
    wit_processor_direct_query(&b);
    return !a.Group && !a.Number && !a.Reserved && !b.Group && !b.Number && !b.Reserved;
}

static WitU64 worker(WitU64 index)
{
    SetLastError(DWORD(3200 + index));
    errno = int(3300 + index);
    if (!wit_test_encoding() ||
        !processor() ||
        !output(unsigned(index), GetStdHandle(STD_ERROR_HANDLE), "[NATIVE-CONSOLE] worker\n",
            sizeof("[NATIVE-CONSOLE] worker\n") - 1) ||
        GetLastError() != 3200 + index ||
        errno != 3300 + index) {
        return 2901;
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" WitU64 wit_test_console(const WitUserStartup *startup, WitU64 mode)
{
    auto report = (WitU64 *)WIT_GC_INFO_REPORT;
    report[1] = 8589934592ULL;
    wit_native_security_initialize_system();
    if (GetStdHandle(STD_ERROR_HANDLE) != INVALID_HANDLE_VALUE || GetLastError() != ERROR_NOT_READY) {
        return 2902;
    }
    wit_native_process_image_initialize(startup);
    const bool tls = mode == 68;
    if (tls) {
        wit_native_tls_initialize(startup);
    }
    SetLastError(0x73124685);
    if (tls) {
        errno = 149;
    }
    const HANDLE handle = GetStdHandle(STD_OUTPUT_HANDLE);
    if ((WitU64)handle != startup->ConsoleHandle ||
        wit_console_direct_handle(STD_ERROR_HANDLE) != handle ||
        GetConsoleOutputCP() != CP_UTF8 ||
        wit_console_direct_codepage() != CP_UTF8 ||
        !processor() ||
        GetLastError() != 0x73124685) {
        return 2903;
    }
    WitU64 arena = 0;
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, 8192, 4096, 0, &arena) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_COMMIT, arena, 4096, 3, nullptr) != WIT_STATUS_OK) {
        return 2904;
    }
    if (mode == 70) {
        auto edge = (unsigned char *)(arena + 4094);
        edge[0] = edge[1] = 0xa5;
        report[2] = (WitU64)edge;
        GetCurrentProcessorNumberEx((PROCESSOR_NUMBER *)edge);
        return 2905;
    }
    if (!wit_test_encoding()) {
        return 2927;
    }
    report[3] = 0x55544638;
    const char text[] = "[NATIVE-CONSOLE] UTF8 \xE2\x82\xAC\n";
    if (!output(3, handle, text, sizeof(text) - 1) || !output(3, handle, nullptr, 0, true)) {
        return 2906;
    }
    char large[512];
    for (unsigned i = 0; i < sizeof(large); ++i) {
        large[i] = 'x';
    }
    large[511] = '\n';
    if (!output(3, handle, large, sizeof(large), true)) {
        return 2907;
    }
    DWORD done = 99;
    if (WriteFile(handle, (void *)(arena + 4092), 16, &done, nullptr) ||
        done ||
        GetLastError() != ERROR_INVALID_ADDRESS) {
        return 2908;
    }
    done = 99;
    if (WriteFile(INVALID_HANDLE_VALUE, text, sizeof(text) - 1, &done, nullptr) ||
        done ||
        GetLastError() != ERROR_INVALID_HANDLE) {
        return 2909;
    }
    const auto binding = __imp_WriteFile;
    if (WriteFile(handle, text, sizeof(text) - 1, (DWORD *)&__imp_WriteFile, nullptr) ||
        GetLastError() != ERROR_INVALID_ADDRESS ||
        binding != __imp_WriteFile) {
        return 2910;
    }
    auto edge = (unsigned char *)(arena + 4094);
    edge[0] = edge[1] = 0xa5;
    if (WriteFile(handle, text, sizeof(text) - 1, (DWORD *)edge, nullptr) ||
        edge[0] != 0xa5 ||
        edge[1] != 0xa5 ||
        GetLastError() != ERROR_INVALID_ADDRESS) {
        return 2911;
    }
    WitU64 copied = 99;
    if (wit_native_call(WIT_CALL_PROCESSOR_QUERY, (WitU64)edge, 4, 0, &copied) != WIT_STATUS_BAD_ADDRESS ||
        copied ||
        edge[0] != 0xa5 ||
        edge[1] != 0xa5) {
        return 2912;
    }
    if (wit_native_call(WIT_CALL_MEMORY_COMMIT, arena + 4096, 4096, 3, nullptr) != WIT_STATUS_OK) {
        return 2913;
    }
    if (!WriteFile(handle, text, sizeof(text) - 1, (DWORD *)edge, nullptr) || *(DWORD *)edge != sizeof(text) - 1) {
        return 2914;
    }
    ++writes[3];
    for (unsigned i = 0; i < 4; ++i) {
        edge[i] = 0xa5;
    }
    if (wit_native_call(WIT_CALL_MEMORY_PROTECT, arena + 4096, 4096, WIT_MEMORY_READ, nullptr) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_PROCESSOR_QUERY, (WitU64)edge, 4, 0, &copied) != WIT_STATUS_BAD_ADDRESS ||
        copied) {
        return 2915;
    }
    for (unsigned i = 0; i < 4; ++i) {
        if (edge[i] != 0xa5) {
            return 2916;
        }
    }
    if (wit_native_call(WIT_CALL_MEMORY_PROTECT, arena + 4096, 4096, 3, nullptr) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_PROCESSOR_QUERY, (WitU64)edge, 4, 0, &copied) != WIT_STATUS_OK ||
        copied != 4 ||
        *(DWORD *)edge ||
        wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) {
        return 2917;
    }
    DWORD untouched = 0xa5a5a5a5;
    copied = 99;
    if (wit_native_call(WIT_CALL_PROCESSOR_QUERY, (WitU64)&untouched, 3, 0, &copied) != WIT_STATUS_INVALID_ARGUMENT ||
        copied ||
        untouched != 0xa5a5a5a5 ||
        wit_native_call(WIT_CALL_PROCESSOR_QUERY, (WitU64)&untouched, 4, 1, &copied) != WIT_STATUS_INVALID_ARGUMENT ||
        copied ||
        untouched != 0xa5a5a5a5) {
        return 2924;
    }
    if (tls) {
        if (wit_native_call(WIT_CALL_MEMORY_RESERVE, WIT_DEBUG_WRITE_MAX, 4096, 0, &arena) != WIT_STATUS_OK ||
            wit_native_call(WIT_CALL_MEMORY_COMMIT, arena, WIT_DEBUG_WRITE_MAX, 3, nullptr) != WIT_STATUS_OK) {
            return 2925;
        }
        auto maximum = (char *)arena;
        for (unsigned i = 0; i < WIT_DEBUG_WRITE_MAX; ++i) {
            maximum[i] = 'x';
        }
        maximum[WIT_DEBUG_WRITE_MAX - 1] = '\n';
        if (!output(3, handle, maximum, WIT_DEBUG_WRITE_MAX) ||
            wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) {
            return 2926;
        }
    }
    done = 99;
    if (WriteFile(handle, text, WIT_DEBUG_WRITE_MAX + 1, &done, nullptr) ||
        done ||
        GetLastError() != ERROR_NOT_ENOUGH_QUOTA ||
        WriteFile(handle, text, sizeof(text) - 1, nullptr, nullptr) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        WriteFile(handle, text, sizeof(text) - 1, &done, (OVERLAPPED *)startup) ||
        GetLastError() != ERROR_NOT_SUPPORTED) {
        return 2918;
    }
    if (GetStdHandle(STD_INPUT_HANDLE) != INVALID_HANDLE_VALUE ||
        GetLastError() != ERROR_NOT_SUPPORTED ||
        GetStdHandle(0) != INVALID_HANDLE_VALUE ||
        GetLastError() != ERROR_INVALID_PARAMETER) {
        return 2919;
    }
    SetLastError(0x73124685);
    if (tls) {
        WitU64 handles[3], result;
        for (WitU64 i = 0; i < 3; ++i) {
            if (wit_native_thread_create(worker, i, &handles[i]) != WIT_STATUS_OK) {
                return 2920;
            }
        }
        for (unsigned i = 0; i < 3; ++i) {
            if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &result) != WIT_STATUS_OK ||
                result != WIT_TEST_EXIT_CODE) {
                return 2921;
            }
        }
        wit_native_tls_leave();
    }
    if (GetLastError() != 0x73124685 || (tls && errno != 149) || !CloseHandle(handle)) {
        return 2922;
    }
    done = 99;
    if (WriteFile(handle, text, sizeof(text) - 1, &done, nullptr) || done || GetLastError() != ERROR_INVALID_HANDLE) {
        return 2923;
    }
    report[2] = 0;
    for (unsigned i = 0; i < 4; ++i) {
        report[2] += writes[i];
    }
    return WIT_TEST_EXIT_CODE;
}
