#include "native_encoding.witos.h"
#include <stdint.h>
#include <limits.h>

namespace {
int failure(DWORD error) { SetLastError(error); return 0; }
bool codepage(UINT page) { return page == CP_UTF8 || page == CP_ACP || page == CP_THREAD_ACP; }
// Caller-owned buffers must remain stable during conversion, like the CRT byte
// routines. No allocation, compiler TLS, locale state or initialization needed.
template<class T> bool length(const T* input, int count, int& result)
{
    if (count > 0) { result = count; return true; }
    for (int i = 0; i < INT_MAX; ++i)
        if (!input[i]) { result = i + 1; return true; }
    return false;
}
bool overlaps(const void* input, size_t inputBytes, const void* output, size_t outputBytes)
{
    auto a = (uintptr_t)input, b = (uintptr_t)output;
    return a <= b ? b - a < inputBytes : a - b < outputBytes;
}
// Match the Windows UTF-8 converter: consume a continuation byte before
// rejecting a second-byte scalar-range restriction. Non-continuations stay
// available for the next scalar. Exhaustive hosted comparisons cover this.
uint32_t decode(const unsigned char* input, int size, int& offset, bool& valid)
{
    const auto lead = input[offset++];
    if (lead < 0x80) return lead;
    int trailing;
    uint32_t point;
    if (lead >= 0xC2 && lead <= 0xDF) { trailing = 1; point = lead & 31; }
    else if (lead >= 0xE0 && lead <= 0xEF) { trailing = 2; point = lead & 15; }
    else if (lead >= 0xF0 && lead <= 0xF4) { trailing = 3; point = lead & 7; }
    else { valid = false; return 0xFFFD; }
    for (int n = 0; n < trailing; ++n) {
        if (offset == size) { valid = false; return 0xFFFD; }
        const auto next = input[offset];
        if (next < 0x80 || next > 0xBF) { valid = false; return 0xFFFD; }
        ++offset;
        if (!n &&
            ((lead == 0xE0 && next < 0xA0) || (lead == 0xED && next > 0x9F) ||
             (lead == 0xF0 && next < 0x90) || (lead == 0xF4 && next > 0x8F))) {
            valid = false; return 0xFFFD;
        }
        point = (point << 6) | (next & 63);
    }
    return point;
}
uint32_t decode(const wchar_t* input, int size, int& offset, bool& valid)
{
    uint32_t point = input[offset++];
    if (point < 0xD800 || point > 0xDFFF) return point;
    if (point <= 0xDBFF && offset < size && input[offset] >= 0xDC00 && input[offset] <= 0xDFFF)
        return 0x10000 + ((point - 0xD800) << 10) + (input[offset++] - 0xDC00);
    valid = false;
    return 0xFFFD;
}
}
extern "C" int WINAPI wit_native_multibyte_to_wide(UINT page, DWORD flags, LPCCH input, int count, LPWSTR output, int capacity)
{
    if (!input || !count || count < -1 || capacity < 0 || (capacity && !output) || (const void*)input == output)
        return failure(ERROR_INVALID_PARAMETER);
    if (!codepage(page)) return failure(ERROR_INVALID_PARAMETER);
    // Windows accepts the historical MB flags even for UTF-8; only the strict
    // flag affects Unicode conversion. CoreLib uses MB_PRECOMPOSED. Verify the
    // accepted mask against the manifested Windows reference, not normalization.
    if (flags & ~(MB_PRECOMPOSED | MB_COMPOSITE | MB_USEGLYPHCHARS | MB_ERR_INVALID_CHARS)) return failure(ERROR_INVALID_FLAGS);
    int size;
    if (!length(input, count, size)) return failure(ERROR_INSUFFICIENT_BUFFER);
    if (capacity && overlaps(input, (size_t)size, output, (size_t)capacity * 2)) return failure(ERROR_INVALID_PARAMETER);
    int required = 0;
    for (int pass = 0; pass < 2; ++pass) {
        int written = 0, offset = 0;
        while (offset < size) {
            bool valid = true;
            auto point = decode((const unsigned char*)input, size, offset, valid);
            if (!valid && (flags & MB_ERR_INVALID_CHARS)) return failure(ERROR_NO_UNICODE_TRANSLATION);
            const int units = point >= 0x10000 ? 2 : 1;
            if (written > INT_MAX - units || (pass && units > capacity - written)) return failure(ERROR_INSUFFICIENT_BUFFER);
            if (pass) {
                if (units == 1) output[written] = (wchar_t)point;
                else { point -= 0x10000; output[written] = (wchar_t)(0xD800 + (point >> 10)); output[written + 1] = (wchar_t)(0xDC00 + (point & 1023)); }
            }
            written += units;
        }
        if (pass) return written;
        required = written;
        if (!capacity) return required;
        if (capacity < required) return failure(ERROR_INSUFFICIENT_BUFFER);
    }
    return required;
}
extern "C" int WINAPI wit_native_wide_to_multibyte(UINT page, DWORD flags, LPCWCH input, int count, LPSTR output, int capacity, LPCCH defaultChar, LPBOOL usedDefault)
{
    if (!input || !count || count < -1 || capacity < 0 || (capacity && !output) || (const void*)input == output || defaultChar || usedDefault)
        return failure(ERROR_INVALID_PARAMETER);
    if (!codepage(page)) return failure(ERROR_INVALID_PARAMETER);
    if (flags & ~(WC_DISCARDNS | WC_SEPCHARS | WC_DEFAULTCHAR | WC_ERR_INVALID_CHARS | WC_COMPOSITECHECK | WC_NO_BEST_FIT_CHARS)) return failure(ERROR_INVALID_FLAGS);
    int size;
    if (!length(input, count, size)) return failure(ERROR_INSUFFICIENT_BUFFER);
    if (capacity && overlaps(input, (size_t)size * 2, output, (size_t)capacity)) return failure(ERROR_INVALID_PARAMETER);
    int required = 0;
    for (int pass = 0; pass < 2; ++pass) {
        int written = 0, offset = 0;
        while (offset < size) {
            bool valid = true;
            auto point = decode(input, size, offset, valid);
            if (!valid && (flags & WC_ERR_INVALID_CHARS)) return failure(ERROR_NO_UNICODE_TRANSLATION);
            const int bytes = point < 0x80 ? 1 : point < 0x800 ? 2 : point < 0x10000 ? 3 : 4;
            if (written > INT_MAX - bytes || (pass && bytes > capacity - written)) return failure(ERROR_INSUFFICIENT_BUFFER);
            if (pass) {
                if (bytes == 1) output[written] = (char)point;
                else {
                    const unsigned prefix = bytes == 2 ? 0xC0 : bytes == 3 ? 0xE0 : 0xF0;
                    output[written] = (char)(prefix | (point >> (6 * (bytes - 1))));
                    for (int n = 1; n < bytes; ++n) output[written + n] = (char)(0x80 | ((point >> (6 * (bytes - 1 - n))) & 63));
                }
            }
            written += bytes;
        }
        if (pass) return written;
        required = written;
        if (!capacity) return required;
        if (capacity < required) return failure(ERROR_INSUFFICIENT_BUFFER);
    }
    return required;
}
