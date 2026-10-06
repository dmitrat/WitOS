#include <ctype.h>
#include <errno.h>
#include <intrin.h>
#include <limits.h>
#include <string.h>
#include "crt.h"
#include "../Runtime.NativeAot/native_ctype.witos.h"

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

/* The rest of the C runtime CoreCLR calls (P6.4.k3a2). Narrow parsing reads the C locale's white space and ASCII
 * digits, as UCRT's strtol family does; the wide classes of Latin-1 come from Windows' CT_CTYPE1 table, which UCRT's
 * _pwctype equals, and beyond it from the platform, where the guest ends the process: it has no Unicode character
 * database. Case changes only A-Z and a-z, as in the C locale. The secure functions follow UCRT's rules, and their
 * violations end the process as UCRT's default invalid-parameter handler does. */
namespace {

bool NarrowSpace(char value)
{
    return value == ' ' || (value >= '\t' && value <= '\r');
}

int NarrowDigit(char value)
{
    if (value >= '0' && value <= '9') {
        return value - '0';
    }
    if (value >= 'A' && value <= 'Z') {
        return value - 'A' + 10;
    }
    if (value >= 'a' && value <= 'z') {
        return value - 'a' + 10;
    }
    return -1;
}

/* Parses a narrow integer as the wide Parse does, over the narrow classes. */
bool ParseNarrow(const char *text, char **end, int base, unsigned long long limit, unsigned long long &magnitude,
    bool &negative, bool &overflow)
{
    const char *p = text;
    while (NarrowSpace(*p)) {
        ++p;
    }
    negative = *p == '-';
    if (*p == '-' || *p == '+') {
        ++p;
    }
    if ((base == 0 || base == 16) && p[0] == '0' && (p[1] == 'x' || p[1] == 'X')) {
        p += 2;
        base = 16;
    } else if (base == 0) {
        base = p[0] == '0' ? 8 : 10;
    }
    magnitude = 0;
    overflow = false;
    const char *first = p;
    for (int digit; (digit = NarrowDigit(*p)) >= 0 && digit < base; ++p) {
        if (magnitude > (limit - unsigned(digit)) / unsigned(base)) {
            overflow = true;
        } else {
            magnitude = magnitude * unsigned(base) + unsigned(digit);
        }
    }
    if (p == first) {
        if (end) {
            *end = const_cast<char *>(text);
        }
        return false;
    }
    if (end) {
        *end = const_cast<char *>(p);
    }
    return true;
}

/* A signed narrow integer of `bits` bits, as strtol and _strtoi64 form it. */
long long ParseSigned(const char *text, char **end, int base, long long maximum)
{
    if (end) {
        *end = const_cast<char *>(text);
    }
    if (!text || (base != 0 && (base < 2 || base > 36))) {
        InvalidParameter();
    }
    unsigned long long magnitude;
    bool negative, overflow;
    if (!ParseNarrow(text, end, base, (unsigned long long)maximum + 1, magnitude, negative, overflow)) {
        return 0;
    }
    if (overflow || (!negative && magnitude > (unsigned long long)maximum)) {
        errno = ERANGE;
        return negative ? -maximum - 1 : maximum;
    }
    return negative ? (long long)(0 - magnitude) : (long long)magnitude;
}

template <typename Char> errno_t Copy(Char *destination, size_t size, const Char *source)
{
    if (!destination || !size) {
        InvalidParameter();
    }
    if (!source) {
        destination[0] = 0;
        InvalidParameter();
    }
    Char *p = destination;
    size_t available = size;
    while ((*p++ = *source++) != 0 && --available > 0) {
    }
    if (!available) {
        destination[0] = 0;
        InvalidParameter();
    }
    return 0;
}

template <typename Char> errno_t Append(Char *destination, size_t size, const Char *source)
{
    if (!destination || !size) {
        InvalidParameter();
    }
    if (!source) {
        destination[0] = 0;
        InvalidParameter();
    }
    Char *p = destination;
    size_t available = size;
    while (available > 0 && *p != 0) {
        ++p;
        --available;
    }
    if (!available) {
        destination[0] = 0; // not terminated
        InvalidParameter();
    }
    while ((*p++ = *source++) != 0 && --available > 0) {
    }
    if (!available) {
        destination[0] = 0;
        InvalidParameter();
    }
    return 0;
}

template <typename Char> errno_t CopyCount(Char *destination, size_t size, const Char *source, size_t count)
{
    if (!count && !destination && !size) {
        return 0;
    }
    if (!destination || !size) {
        InvalidParameter();
    }
    if (!count) {
        destination[0] = 0;
        return 0;
    }
    if (!source) {
        destination[0] = 0;
        InvalidParameter();
    }
    Char *p = destination;
    size_t available = size;
    if (count == _TRUNCATE) {
        while ((*p++ = *source++) != 0 && --available > 0) {
        }
    } else {
        while ((*p++ = *source++) != 0 && --available > 0 && --count > 0) {
        }
        if (!count) {
            *p = 0;
        }
    }
    if (!available) {
        if (count == _TRUNCATE) {
            destination[size - 1] = 0;
            return STRUNCATE;
        }
        destination[0] = 0;
        InvalidParameter();
    }
    return 0;
}

template <typename Char> errno_t AppendCount(Char *destination, size_t size, const Char *source, size_t count)
{
    if (!count && !destination && !size) {
        return 0;
    }
    if (!destination || !size) {
        InvalidParameter();
    }
    if (count && !source) {
        destination[0] = 0;
        InvalidParameter();
    }
    Char *p = destination;
    size_t available = size;
    while (available > 0 && *p != 0) {
        ++p;
        --available;
    }
    if (!available) {
        destination[0] = 0; // not terminated
        InvalidParameter();
    }
    if (count == _TRUNCATE) {
        while ((*p++ = *source++) != 0 && --available > 0) {
        }
    } else {
        while (count > 0 && (*p++ = *source++) != 0 && --available > 0) {
            --count;
        }
        if (!count) {
            *p = 0;
        }
    }
    if (!available) {
        if (count == _TRUNCATE) {
            destination[size - 1] = 0;
            return STRUNCATE;
        }
        destination[0] = 0;
        InvalidParameter();
    }
    return 0;
}

} // namespace

int Isalpha(int value)
{
    return Isctype(value, _ALPHA);
}

int Isdigit(int value)
{
    return Isctype(value, _DIGIT);
}

int Iswctype(wint_t value, unsigned short mask)
{
    if (value == WEOF) {
        return 0;
    }
    return (value < 256 ? wit_native_ctype1(value) : Platform::CharacterType(wchar_t(value))) & mask;
}

int Iswascii(wint_t value)
{
    return value < 0x80;
}

wint_t Towlower(wint_t value)
{
    return value >= L'A' && value <= L'Z' ? wint_t(value - L'A' + L'a') : value;
}

wint_t Towupper(wint_t value)
{
    return value >= L'a' && value <= L'z' ? wint_t(value - L'a' + L'A') : value;
}

long Strtol(const char *text, char **end, int base)
{
    return long(ParseSigned(text, end, base, LONG_MAX));
}

long Atol(const char *text)
{
    return long(ParseSigned(text, nullptr, 10, LONG_MAX));
}

long long Atoi64(const char *text)
{
    return ParseSigned(text, nullptr, 10, LLONG_MAX);
}

unsigned long long Wcstoui64(const wchar_t *text, wchar_t **end, int base)
{
    if (end) {
        *end = const_cast<wchar_t *>(text);
    }
    if (!text || (base != 0 && (base < 2 || base > 36))) {
        InvalidParameter();
    }
    unsigned long long magnitude;
    bool negative, overflow;
    if (!Parse(text, end, base, ULLONG_MAX, magnitude, negative, overflow)) {
        return 0;
    }
    if (overflow) {
        errno = ERANGE;
        return ULLONG_MAX;
    }
    return negative ? 0ULL - magnitude : magnitude;
}

errno_t Ltow_s(long value, wchar_t *buffer, size_t size, int radix)
{
    if (!buffer || !size) {
        InvalidParameter();
    }
    buffer[0] = 0;
    if (radix < 2 || radix > 36) {
        InvalidParameter();
    }
    const bool negative = radix == 10 && value < 0;
    if (size <= size_t(negative ? 2 : 1)) {
        InvalidParameter();
    }
    unsigned long magnitude = negative ? 0UL - (unsigned long)value : (unsigned long)value;
    wchar_t digits[40];
    size_t count = 0;
    do {
        const unsigned digit = unsigned(magnitude % unsigned(radix));
        magnitude /= unsigned(radix);
        digits[count++] = wchar_t(digit < 10 ? L'0' + digit : L'a' + digit - 10);
    } while (magnitude);
    if (count + (negative ? 1 : 0) >= size) {
        buffer[0] = 0;
        InvalidParameter();
    }
    size_t at = 0;
    if (negative) {
        buffer[at++] = L'-';
    }
    while (count) {
        buffer[at++] = digits[--count];
    }
    buffer[at] = 0;
    return 0;
}

size_t Strnlen(const char *text, size_t count)
{
    return Strncnt(text, count);
}

char *Strdup(const char *text)
{
    if (!text) {
        return nullptr;
    }
    size_t length = 0;
    while (text[length]) {
        ++length;
    }
    auto *copy = static_cast<char *>(Malloc(length + 1));
    if (copy) {
        for (size_t i = 0; i <= length; ++i) {
            copy[i] = text[i];
        }
    }
    return copy;
}

int Strnicmp(const char *first, const char *second, size_t count)
{
    if (!count) {
        return 0;
    }
    if (!first || !second) {
        InvalidParameter();
    }
    int left, right;
    do {
        left = Tolower((unsigned char)*first++);
        right = Tolower((unsigned char)*second++);
    } while (--count && left && left == right);
    return left - right;
}

/* strncmp and strncpy (P6.4.k3a4), which CoreCLR's configuration and event pipe call. */
int Strncmp(const char *first, const char *second, size_t count)
{
    if (!count) {
        return 0;
    }
    if (!first || !second) {
        InvalidParameter();
    }
    int left, right;
    do {
        left = (unsigned char)*first++;
        right = (unsigned char)*second++;
    } while (--count && left && left == right);
    return left < right ? -1 : left > right; // UCRT's strncmp returns the sign, unlike its _strnicmp
}

char *Strncpy(char *destination, const char *source, size_t count)
{
    size_t i = 0;
    for (; i < count && source[i]; ++i) {
        destination[i] = source[i];
    }
    if (i < count) {
        __stosb(reinterpret_cast<unsigned char *>(destination + i), 0, count - i); // a loop would become memset
    }
    return destination;
}

errno_t Strupr_s(char *text, size_t size)
{
    if (!text || Strncnt(text, size) >= size) {
        InvalidParameter();
    }
    for (; *text; ++text) {
        *text = char(Toupper((unsigned char)*text));
    }
    return 0;
}

errno_t Wcslwr_s(wchar_t *text, size_t size)
{
    if (!text || Wcsnlen(text, size) >= size) {
        InvalidParameter();
    }
    for (; *text; ++text) {
        *text = wchar_t(Towlower(*text));
    }
    return 0;
}

char *Strtok_s(char *text, const char *delimiters, char **context)
{
    if (!context || !delimiters || (!text && !*context)) {
        InvalidParameter();
    }
    char *p = text ? text : *context;
    while (*p && Strchr(delimiters, *p)) {
        ++p;
    }
    if (!*p) {
        *context = p;
        return nullptr;
    }
    char *token = p;
    while (*p && !Strchr(delimiters, *p)) {
        ++p;
    }
    if (*p) {
        *p++ = 0;
    }
    *context = p;
    return token;
}

errno_t Strcpy_s(char *destination, size_t size, const char *source)
{
    return Copy(destination, size, source);
}

errno_t Strcat_s(char *destination, size_t size, const char *source)
{
    return Append(destination, size, source);
}

errno_t Strncpy_s(char *destination, size_t size, const char *source, size_t count)
{
    return CopyCount(destination, size, source, count);
}

errno_t Strncat_s(char *destination, size_t size, const char *source, size_t count)
{
    return AppendCount(destination, size, source, count);
}

errno_t Wcscpy_s(wchar_t *destination, size_t size, const wchar_t *source)
{
    return Copy(destination, size, source);
}

errno_t Wcscat_s(wchar_t *destination, size_t size, const wchar_t *source)
{
    return Append(destination, size, source);
}

errno_t Wcsncpy_s(wchar_t *destination, size_t size, const wchar_t *source, size_t count)
{
    return CopyCount(destination, size, source, count);
}

errno_t Wcsncat_s(wchar_t *destination, size_t size, const wchar_t *source, size_t count)
{
    return AppendCount(destination, size, source, count);
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

extern "C" int __cdecl isalpha(int value)
{
    return WitCrt::Isalpha(value);
}

extern "C" int __cdecl isdigit(int value)
{
    return WitCrt::Isdigit(value);
}

extern "C" int __cdecl iswalpha(wint_t value)
{
    return WitCrt::Iswctype(value, _ALPHA);
}

extern "C" int __cdecl iswspace(wint_t value)
{
    return WitCrt::Iswctype(value, _SPACE);
}

extern "C" int __cdecl iswupper(wint_t value)
{
    return WitCrt::Iswctype(value, _UPPER);
}

extern "C" int __cdecl iswascii(wint_t value)
{
    return WitCrt::Iswascii(value);
}

extern "C" wint_t __cdecl towlower(wint_t value)
{
    return WitCrt::Towlower(value);
}

extern "C" wint_t __cdecl towupper(wint_t value)
{
    return WitCrt::Towupper(value);
}

extern "C" long __cdecl strtol(const char *text, char **end, int base)
{
    return WitCrt::Strtol(text, end, base);
}

extern "C" long __cdecl atol(const char *text)
{
    return WitCrt::Atol(text);
}

extern "C" long long __cdecl _atoi64(const char *text)
{
    return WitCrt::Atoi64(text);
}

extern "C" unsigned long long __cdecl _wcstoui64(const wchar_t *text, wchar_t **end, int base)
{
    return WitCrt::Wcstoui64(text, end, base);
}

extern "C" errno_t __cdecl _ltow_s(long value, wchar_t *buffer, size_t size, int radix)
{
    return WitCrt::Ltow_s(value, buffer, size, radix);
}

extern "C" size_t __cdecl strnlen(const char *text, size_t count)
{
    return WitCrt::Strnlen(text, count);
}

extern "C" char *__cdecl _strdup(const char *text)
{
    return WitCrt::Strdup(text);
}

extern "C" int __cdecl _strnicmp(const char *first, const char *second, size_t count)
{
    return WitCrt::Strnicmp(first, second, count);
}

#pragma function(strncmp, strncpy)

extern "C" int __cdecl strncmp(const char *first, const char *second, size_t count)
{
    return WitCrt::Strncmp(first, second, count);
}

extern "C" char *__cdecl strncpy(char *destination, const char *source, size_t count)
{
    return WitCrt::Strncpy(destination, source, count);
}

extern "C" errno_t __cdecl _strupr_s(char *text, size_t size)
{
    return WitCrt::Strupr_s(text, size);
}

extern "C" errno_t __cdecl _wcslwr_s(wchar_t *text, size_t size)
{
    return WitCrt::Wcslwr_s(text, size);
}

extern "C" char *__cdecl strtok_s(char *text, const char *delimiters, char **context)
{
    return WitCrt::Strtok_s(text, delimiters, context);
}

extern "C" errno_t __cdecl strcpy_s(char *destination, rsize_t size, const char *source)
{
    return WitCrt::Strcpy_s(destination, size, source);
}

extern "C" errno_t __cdecl strcat_s(char *destination, rsize_t size, const char *source)
{
    return WitCrt::Strcat_s(destination, size, source);
}

extern "C" errno_t __cdecl strncpy_s(char *destination, rsize_t size, const char *source, rsize_t count)
{
    return WitCrt::Strncpy_s(destination, size, source, count);
}

extern "C" errno_t __cdecl strncat_s(char *destination, rsize_t size, const char *source, rsize_t count)
{
    return WitCrt::Strncat_s(destination, size, source, count);
}

extern "C" errno_t __cdecl wcscpy_s(wchar_t *destination, rsize_t size, const wchar_t *source)
{
    return WitCrt::Wcscpy_s(destination, size, source);
}

extern "C" errno_t __cdecl wcscat_s(wchar_t *destination, rsize_t size, const wchar_t *source)
{
    return WitCrt::Wcscat_s(destination, size, source);
}

extern "C" errno_t __cdecl wcsncpy_s(wchar_t *destination, rsize_t size, const wchar_t *source, rsize_t count)
{
    return WitCrt::Wcsncpy_s(destination, size, source, count);
}

extern "C" errno_t __cdecl wcsncat_s(wchar_t *destination, rsize_t size, const wchar_t *source, rsize_t count)
{
    return WitCrt::Wcsncat_s(destination, size, source, count);
}
#endif
