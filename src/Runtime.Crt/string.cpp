#include <ctype.h>
#include <errno.h>
#include <limits.h>
#include "crt.h"

/* Wide strings, C-locale characters, integer parsing and error messages (P6.4.h), and the string functions and
 * character classes the STL's locale sources call (P6.4.i3). The comparisons, case mappings and classes are the C
 * locale's: only A-Z and a-z change case, and a class comes from the C locale's table for -1 to 255 (EOF and the
 * values of unsigned char) and is 0 beyond it. Parsing follows UCRT: it skips the white space Windows classifies as
 * such, reads the decimal digits of the Unicode scripts UCRT knows besides ASCII, keeps reading after an overflow and
 * reports ERANGE. The messages are UCRT's. */
namespace WitCrt {

namespace {

/* The wide white space UCRT's parsing skips. */
constexpr wchar_t SPACES[] = {0x0009, 0x000A, 0x000B, 0x000C, 0x000D, 0x0020, 0x0085, 0x00A0, 0x1680, 0x180E, 0x2000,
    0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A, 0x2028, 0x2029, 0x202F, 0x205F,
    0x3000};

/* The zeros of the decimal digit ranges UCRT's parsing reads besides ASCII. */
constexpr wchar_t ZEROS[] = {0x0660, 0x06F0, 0x0966, 0x09E6, 0x0A66, 0x0AE6, 0x0B66, 0x0C66, 0x0CE6, 0x0D66, 0x0E50,
    0x0ED0, 0x0F20, 0x1040, 0x17E0, 0x1810, 0xFF10};

constexpr const wchar_t *MESSAGES[] = {L"No error", L"Operation not permitted", L"No such file or directory",
    L"No such process", L"Interrupted function call", L"Input/output error", L"No such device or address",
    L"Arg list too long", L"Exec format error", L"Bad file descriptor", L"No child processes",
    L"Resource temporarily unavailable", L"Not enough space", L"Permission denied", L"Bad address", L"Unknown error",
    L"Resource device", L"File exists", L"Improper link", L"No such device", L"Not a directory", L"Is a directory",
    L"Invalid argument", L"Too many open files in system", L"Too many open files",
    L"Inappropriate I/O control operation", L"Unknown error", L"File too large", L"No space left on device",
    L"Invalid seek", L"Read-only file system", L"Too many links", L"Broken pipe", L"Domain error", L"Result too large",
    L"Unknown error", L"Resource deadlock avoided", L"Unknown error", L"Filename too long", L"No locks available",
    L"Function not implemented", L"Directory not empty", L"Illegal byte sequence"};

/* The messages of the POSIX supplement, from 100. */
constexpr const wchar_t *SUPPLEMENT[] = {L"address in use", L"address not available", L"address family not supported",
    L"connection already in progress", L"bad message", L"operation canceled", L"connection aborted",
    L"connection refused", L"connection reset", L"destination address required", L"host unreachable",
    L"identifier removed", L"operation in progress", L"already connected", L"too many symbolic link levels",
    L"message size", L"network down", L"network reset", L"network unreachable", L"no buffer space",
    L"no message available", L"no link", L"no message", L"no protocol option", L"no stream resources", L"not a stream",
    L"not connected", L"state not recoverable", L"not a socket", L"not supported", L"operation not supported",
    L"Unknown error", L"value too large", L"owner dead", L"protocol error", L"protocol not supported",
    L"wrong protocol type", L"stream timeout", L"timed out", L"text file busy", L"operation would block"};

bool IsSpace(wchar_t value)
{
    for (const wchar_t space : SPACES) {
        if (value == space) {
            return true;
        }
    }
    return false;
}

/* The value of a digit in bases up to 36, or -1. */
int Digit(wchar_t value)
{
    if (value >= L'0' && value <= L'9') {
        return value - L'0';
    }
    if (value >= L'A' && value <= L'Z') {
        return value - L'A' + 10;
    }
    if (value >= L'a' && value <= L'z') {
        return value - L'a' + 10;
    }
    for (const wchar_t zero : ZEROS) {
        if (value >= zero && value <= zero + 9) {
            return value - zero;
        }
    }
    return -1;
}

wchar_t Lower(wchar_t value)
{
    return value >= L'A' && value <= L'Z' ? wchar_t(value - L'A' + L'a') : value;
}

/* Parses an integer as UCRT's wcstoul and _wtol do. The magnitude is clamped to `limit`; a negative result is the
 * caller's to form. Returns false when no digits were read. */
bool Parse(const wchar_t *text, wchar_t **end, int base, unsigned long long limit, unsigned long long &magnitude,
    bool &negative, bool &overflow)
{
    const wchar_t *p = text;
    while (IsSpace(*p)) {
        ++p;
    }
    negative = *p == L'-';
    if (*p == L'-' || *p == L'+') {
        ++p;
    }
    if ((base == 0 || base == 16) && Digit(p[0]) == 0 && (p[1] == L'x' || p[1] == L'X')) {
        p += 2;
        base = 16;
    } else if (base == 0) {
        base = Digit(p[0]) == 0 ? 8 : 10;
    }
    magnitude = 0;
    overflow = false;
    const wchar_t *first = p;
    for (int digit; (digit = Digit(*p)) >= 0 && digit < base; ++p) {
        if (magnitude > (limit - unsigned(digit)) / unsigned(base)) {
            overflow = true;
        } else {
            magnitude = magnitude * unsigned(base) + unsigned(digit);
        }
    }
    if (p == first) {
        // No digits, even after a 0x prefix: nothing is converted, as in UCRT.
        if (end) {
            *end = const_cast<wchar_t *>(text);
        }
        return false;
    }
    if (end) {
        *end = const_cast<wchar_t *>(p);
    }
    return true;
}

} // namespace

size_t Wcslen(const wchar_t *text)
{
    const wchar_t *end = text;
    while (*end) {
        ++end;
    }
    return size_t(end - text);
}

int Wcscmp(const wchar_t *first, const wchar_t *second)
{
    while (*first && *first == *second) {
        ++first;
        ++second;
    }
    return *first < *second ? -1 : *first > *second ? 1 : 0;
}

int Wcsncmp(const wchar_t *first, const wchar_t *second, size_t count)
{
    if (!count) {
        return 0;
    }
    while (--count && *first && *first == *second) {
        ++first;
        ++second;
    }
    return int(*first) - int(*second);
}

wchar_t *Wcschr(const wchar_t *text, wchar_t value)
{
    while (*text && *text != value) {
        ++text;
    }
    return *text == value ? const_cast<wchar_t *>(text) : nullptr;
}

int Wcsicmp(const wchar_t *first, const wchar_t *second)
{
    if (!first || !second) {
        InvalidParameter();
    }
    wchar_t left, right;
    do {
        left = Lower(*first++);
        right = Lower(*second++);
    } while (left && left == right);
    return int(left) - int(right);
}

int Wcsnicmp(const wchar_t *first, const wchar_t *second, size_t count)
{
    if (!count) {
        return 0;
    }
    if (!first || !second) {
        InvalidParameter();
    }
    wchar_t left, right;
    do {
        left = Lower(*first++);
        right = Lower(*second++);
    } while (--count && left && left == right);
    return int(left) - int(right);
}

int Tolower(int value)
{
    return value >= 'A' && value <= 'Z' ? value - 'A' + 'a' : value;
}

int Isctype(int value, unsigned short mask)
{
    return value >= -1 && value <= 255 ? Pctype()[value] & mask : 0;
}

size_t Strncnt(const char *text, size_t count)
{
    size_t length = 0;
    while (length < count && text[length]) {
        ++length;
    }
    return length;
}

size_t Wcsnlen(const wchar_t *text, size_t count)
{
    size_t length = 0;
    while (length < count && text[length]) {
        ++length;
    }
    return length;
}

size_t Strcspn(const char *text, const char *reject)
{
    size_t length = 0;
    for (; text[length]; ++length) {
        for (const char *r = reject; *r; ++r) {
            if (*r == text[length]) {
                return length;
            }
        }
    }
    return length;
}

/* The searches CoreCLR calls (P6.4.k3a), which vcruntime supplies: the value converts to the string's character, and
 * a search for 0 finds the terminator. */
char *Strchr(const char *text, int value)
{
    const char c = char(value);
    while (*text && *text != c) {
        ++text;
    }
    return *text == c ? const_cast<char *>(text) : nullptr;
}

char *Strrchr(const char *text, int value)
{
    const char c = char(value);
    const char *found = nullptr;
    do {
        if (*text == c) {
            found = text;
        }
    } while (*text++);
    return const_cast<char *>(found);
}

wchar_t *Wcsrchr(const wchar_t *text, wchar_t value)
{
    const wchar_t *found = nullptr;
    do {
        if (*text == value) {
            found = text;
        }
    } while (*text++);
    return const_cast<wchar_t *>(found);
}

/* The first occurrence of part, or text itself for an empty part. */
wchar_t *Wcsstr(const wchar_t *text, const wchar_t *part)
{
    if (!*part) {
        return const_cast<wchar_t *>(text);
    }
    for (; *text; ++text) {
        size_t i = 0;
        while (part[i] && text[i] == part[i]) {
            ++i;
        }
        if (!part[i]) {
            return const_cast<wchar_t *>(text);
        }
    }
    return nullptr;
}

wchar_t *Wcsdup(const wchar_t *text)
{
    if (!text) {
        return nullptr;
    }
    const size_t length = Wcslen(text);
    auto *copy = static_cast<wchar_t *>(Malloc((length + 1) * sizeof(wchar_t)));
    if (copy) {
        for (size_t i = 0; i <= length; ++i) {
            copy[i] = text[i];
        }
    }
    return copy;
}

int Toupper(int value)
{
    return value >= 'a' && value <= 'z' ? value - 'a' + 'A' : value;
}

unsigned long Wcstoul(const wchar_t *text, wchar_t **end, int base)
{
    if (end) {
        *end = const_cast<wchar_t *>(text);
    }
    if (!text || (base != 0 && (base < 2 || base > 36))) {
        InvalidParameter();
    }
    unsigned long long magnitude;
    bool negative, overflow;
    if (!Parse(text, end, base, ULONG_MAX, magnitude, negative, overflow)) {
        return 0;
    }
    if (overflow) {
        errno = ERANGE;
        return ULONG_MAX;
    }
    return negative ? 0UL - (unsigned long)magnitude : (unsigned long)magnitude;
}

int Wtoi(const wchar_t *text)
{
    if (!text) {
        InvalidParameter();
    }
    unsigned long long magnitude;
    bool negative, overflow;
    if (!Parse(text, nullptr, 10, unsigned(LONG_MAX) + 1ULL, magnitude, negative, overflow)) {
        return 0;
    }
    if (overflow || (!negative && magnitude > LONG_MAX)) {
        errno = ERANGE;
        return negative ? LONG_MIN : LONG_MAX;
    }
    return negative ? int(0 - magnitude) : int(magnitude);
}

errno_t Wcserror_s(wchar_t *buffer, size_t count, int error)
{
    if (!buffer || !count) {
        InvalidParameter();
    }
    const wchar_t *message = L"Unknown error";
    if (error >= 0 && size_t(error) < sizeof(MESSAGES) / sizeof(MESSAGES[0])) {
        message = MESSAGES[error];
    } else if (error >= 100 && size_t(error - 100) < sizeof(SUPPLEMENT) / sizeof(SUPPLEMENT[0])) {
        message = SUPPLEMENT[error - 100];
    }
    size_t length = 0;
    while (length + 1 < count && message[length]) {
        buffer[length] = message[length];
        ++length;
    }
    buffer[length] = 0;
    return 0;
}

} // namespace WitCrt

#ifndef WITCRT_REFERENCE
#pragma function(wcslen, wcscmp, wcsncmp)

extern "C" size_t __cdecl wcslen(const wchar_t *text)
{
    return WitCrt::Wcslen(text);
}

extern "C" int __cdecl wcscmp(const wchar_t *first, const wchar_t *second)
{
    return WitCrt::Wcscmp(first, second);
}

extern "C" int __cdecl wcsncmp(const wchar_t *first, const wchar_t *second, size_t count)
{
    return WitCrt::Wcsncmp(first, second, count);
}

extern "C" _CONST_RETURN wchar_t *__cdecl wcschr(const wchar_t *text, wchar_t value)
{
    return WitCrt::Wcschr(text, value);
}

extern "C" int __cdecl _wcsicmp(const wchar_t *first, const wchar_t *second)
{
    return WitCrt::Wcsicmp(first, second);
}

extern "C" int __cdecl _wcsnicmp(const wchar_t *first, const wchar_t *second, size_t count)
{
    return WitCrt::Wcsnicmp(first, second, count);
}

extern "C" int __cdecl tolower(int value)
{
    return WitCrt::Tolower(value);
}

extern "C" int __cdecl toupper(int value)
{
    return WitCrt::Toupper(value);
}

extern "C" int __cdecl islower(int value)
{
    return WitCrt::Isctype(value, _LOWER);
}

extern "C" int __cdecl isupper(int value)
{
    return WitCrt::Isctype(value, _UPPER);
}

extern "C" int __cdecl isspace(int value)
{
    return WitCrt::Isctype(value, _SPACE);
}

extern "C" size_t __cdecl __strncnt(const char *text, size_t count)
{
    return WitCrt::Strncnt(text, count);
}

extern "C" size_t __cdecl wcsnlen(const wchar_t *text, size_t count)
{
    return WitCrt::Wcsnlen(text, count);
}

extern "C" size_t __cdecl strcspn(const char *text, const char *reject)
{
    return WitCrt::Strcspn(text, reject);
}

extern "C" _CONST_RETURN char *__cdecl strchr(const char *text, int value)
{
    return WitCrt::Strchr(text, value);
}

extern "C" _CONST_RETURN char *__cdecl strrchr(const char *text, int value)
{
    return WitCrt::Strrchr(text, value);
}

extern "C" _CONST_RETURN wchar_t *__cdecl wcsrchr(const wchar_t *text, wchar_t value)
{
    return WitCrt::Wcsrchr(text, value);
}

extern "C" _CONST_RETURN wchar_t *__cdecl wcsstr(const wchar_t *text, const wchar_t *part)
{
    return WitCrt::Wcsstr(text, part);
}

extern "C" wchar_t *__cdecl _wcsdup(const wchar_t *text)
{
    return WitCrt::Wcsdup(text);
}

extern "C" unsigned long __cdecl wcstoul(const wchar_t *text, wchar_t **end, int base)
{
    return WitCrt::Wcstoul(text, end, base);
}

extern "C" int __cdecl _wtoi(const wchar_t *text)
{
    return WitCrt::Wtoi(text);
}

extern "C" errno_t __cdecl _wcserror_s(wchar_t *buffer, size_t count, int error)
{
    return WitCrt::Wcserror_s(buffer, count, error);
}
#endif
