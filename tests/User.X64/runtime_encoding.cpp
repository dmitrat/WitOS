#include "pal.witos.h"
#include "native_encoding.witos.h"
extern "C" int WINAPI wit_encoding_direct_mb(UINT, DWORD, LPCCH, int, LPWSTR, int);
extern "C" int WINAPI wit_encoding_direct_wc(UINT, DWORD, LPCWCH, int, LPSTR, int, LPCCH, LPBOOL);
extern "C" const void *const __imp_MultiByteToWideChar;
extern "C" const void *const __imp_WideCharToMultiByte;

extern "C" bool wit_test_encoding()
{
    const DWORD saved = GetLastError();
    const wchar_t wide[] = {L'A', 0, 0x20AC, 0xD83D, 0xDE00, 0};
    const char bytes[] = "A\0\xE2\x82\xAC\xF0\x9F\x98\x80";
    wchar_t w[8];
    char b[16];
    for (auto &c : w) {
        c = 0x5A5A;
    }
    for (auto &c : b) {
        c = 0x5A;
    }
    if (MultiByteToWideChar(CP_UTF8, 0, bytes, 10, nullptr, 0) != 6 ||
        WideCharToMultiByte(CP_UTF8, 0, wide, 6, nullptr, 0, nullptr, nullptr) != 10 ||
        wit_encoding_direct_mb(CP_UTF8, MB_ERR_INVALID_CHARS, bytes, 10, w, 8) != 6 ||
        wit_encoding_direct_wc(CP_UTF8, WC_ERR_INVALID_CHARS, wide, 6, b, 16, nullptr, nullptr) != 10) {
        return false;
    }
    for (int i = 0; i < 6; ++i) {
        if (w[i] != wide[i]) {
            return false;
        }
    }
    for (int i = 0; i < 10; ++i) {
        if (b[i] != bytes[i]) {
            return false;
        }
    }
    if (w[6] != 0x5A5A || b[10] != 0x5A || GetLastError() != saved) {
        return false;
    }
    if (MultiByteToWideChar(CP_ACP, MB_PRECOMPOSED, bytes, 10, w, 8) != 6 ||
        WideCharToMultiByte(CP_THREAD_ACP, 0, wide, 6, b, 16, nullptr, nullptr) != 10 ||
        MultiByteToWideChar(CP_UTF8, 0, bytes, -1, w, 8) != 2 ||
        WideCharToMultiByte(CP_UTF8, 0, wide, -1, b, 16, nullptr, nullptr) != 2) {
        return false;
    }
    const wchar_t bad[] = {0xD800, L'x', 0xDC00};
    const char malformed[] = "\xE0\x80x\xF0\x90\x80";
    if (MultiByteToWideChar(CP_UTF8, 0, malformed, 6, w, 8) != 3 ||
        w[0] != 0xFFFD ||
        w[1] != L'x' ||
        w[2] != 0xFFFD ||
        WideCharToMultiByte(CP_UTF8, 0, bad, 3, b, 16, nullptr, nullptr) != 7) {
        return false;
    }
    for (auto &c : w) {
        c = 0x5A5A;
    }
    for (auto &c : b) {
        c = 0x5A;
    }
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, malformed, 6, w, 8) ||
        GetLastError() != ERROR_NO_UNICODE_TRANSLATION ||
        WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, bad, 3, b, 16, nullptr, nullptr) ||
        GetLastError() != ERROR_NO_UNICODE_TRANSLATION ||
        MultiByteToWideChar(CP_UTF8, 0, bytes, 10, w, 5) ||
        GetLastError() != ERROR_INSUFFICIENT_BUFFER ||
        WideCharToMultiByte(CP_UTF8, 0, wide, 6, b, 9, nullptr, nullptr) ||
        GetLastError() != ERROR_INSUFFICIENT_BUFFER) {
        return false;
    }
    for (auto c : w) {
        if (c != 0x5A5A) {
            return false;
        }
    }
    for (auto c : b) {
        if (c != 0x5A) {
            return false;
        }
    }
    BOOL used = TRUE;
    if (MultiByteToWideChar(1252, 0, bytes, 10, w, 8) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        MultiByteToWideChar(CP_UTF8, 0x10, bytes, 10, w, 8) ||
        GetLastError() != ERROR_INVALID_FLAGS ||
        WideCharToMultiByte(CP_UTF8, 0, wide, 6, b, 16, nullptr, &used) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        !used ||
        WideCharToMultiByte(CP_UTF8, 0, wide, 0, b, 16, nullptr, nullptr) ||
        GetLastError() != ERROR_INVALID_PARAMETER) {
        return false;
    }
    WitU64 arena = 0;
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, 8192, 4096, 0, &arena) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_COMMIT, arena, 4096, 3, nullptr) != WIT_STATUS_OK) {
        return false;
    }
    auto edge = (char *)(arena + 4096);
    edge[-3] = '\xE2';
    edge[-2] = '\x82';
    edge[-1] = '\xAC';
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, edge - 3, 3, w, 8) != 1 || w[0] != 0x20AC) {
        return false;
    }
    edge[-1] = 0;
    if (MultiByteToWideChar(CP_UTF8, 0, edge - 3, -1, w, 8) != 2 || w[0] != 0xFFFD || w[1]) {
        return false;
    }
    auto we = (wchar_t *)edge;
    we[-2] = 0xD83D;
    we[-1] = 0xDE00;
    if (WideCharToMultiByte(CP_UTF8, 0, we - 2, 2, b, 16, nullptr, nullptr) != 4) {
        return false;
    }
    we[-1] = 0;
    if (WideCharToMultiByte(CP_UTF8, 0, we - 2, -1, b, 16, nullptr, nullptr) != 4 || b[3]) {
        return false;
    }
    if (MultiByteToWideChar(CP_UTF8, 0, bytes, 10, we - 6, 6) != 6 ||
        WideCharToMultiByte(CP_UTF8, 0, wide, 6, edge - 10, 10, nullptr, nullptr) != 10) {
        return false;
    }
    const auto image = wit_native_process_image();
    if (!wit_native_image_range(image, (WitU64)&__imp_MultiByteToWideChar, 8, WIT_IMAGE_INFO_READ,
            WIT_IMAGE_INFO_WRITE | WIT_IMAGE_INFO_EXECUTE, 1) ||
        !wit_native_image_range(image, (WitU64)&__imp_WideCharToMultiByte, 8, WIT_IMAGE_INFO_READ,
            WIT_IMAGE_INFO_WRITE | WIT_IMAGE_INFO_EXECUTE, 1) ||
        wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) {
        return false;
    }
    SetLastError(saved);
    return true;
}
