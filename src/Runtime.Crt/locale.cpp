#include <errno.h>
#include <limits.h>
#include <locale.h>
#include "crt.h"

/* The C locale and the UTF-8 locale (P6.4.h). _create_locale accepts the C locale and the code-page-only UTF-8 names;
 * the UTF-8 locale keeps the C locale's collation and formats, as UCRT's ".utf8" locale does for every format this
 * subset implements. The ctype tables are UCRT's: the UTF-8 one adds the defined and alphabetic classes of its ASCII
 * range and marks the lead bytes of its multibyte sequences. The global locale is the C locale (P6.4.i3): setlocale
 * reports it and sets nothing else, and the queries of UCRT's headers and the STL answer for it. */
namespace WitCrt {

namespace {

constexpr unsigned short UPPER = 0x1, LOWER = 0x2, DIGIT = 0x4, SPACE = 0x8, PUNCT = 0x10, CONTROL = 0x20, BLANK = 0x40,
                         HEX = 0x80, ALPHA = 0x100, DEFINED = 0x200, LEADBYTE = 0x8000;

constexpr unsigned short Classify(int c, bool utf8)
{
    if (c < 0 || c > 127) {
        return utf8 && c >= 0xC2 && c <= 0xF4 ? LEADBYTE : 0; // the lead bytes of UTF-8 sequences
    }
    unsigned short type = 0;
    if (c < 32 || c == 127) {
        type = CONTROL;
        if (c >= 9 && c <= 13) {
            type |= SPACE;
        }
        if (c == 9 && utf8) {
            type |= BLANK;
        }
    } else if (c == ' ') {
        type = SPACE | BLANK;
    } else if (c >= '0' && c <= '9') {
        type = DIGIT | HEX;
    } else if (c >= 'A' && c <= 'Z') {
        type = UPPER | (c <= 'F' ? HEX : 0) | (utf8 ? ALPHA : 0);
    } else if (c >= 'a' && c <= 'z') {
        type = LOWER | (c <= 'f' ? HEX : 0) | (utf8 ? ALPHA : 0);
    } else {
        type = PUNCT;
    }
    return utf8 ? type | DEFINED : type;
}

struct Table {
    unsigned short Values[257];
};

constexpr Table Build(bool utf8)
{
    Table table{};
    for (int i = 0; i < 257; ++i) {
        table.Values[i] = Classify(i - 1, utf8);
    }
    return table;
}

constexpr Table C_TABLE = Build(false), UTF8_TABLE = Build(true);

/* The C locale of calls without one; never handed out as a _locale_t. */
const Locale C_LOCALE = {{nullptr, nullptr}, {nullptr, 1, 0}, {}, false};

/* The C locale's numeric and monetary conventions, as UCRT's localeconv reports them. */
char EMPTY[] = "";
char POINT[] = ".";
wchar_t WIDE_EMPTY[] = L"";
wchar_t WIDE_POINT[] = L".";
struct lconv C_CONVENTIONS = {POINT, EMPTY, EMPTY, EMPTY, EMPTY, EMPTY, EMPTY, EMPTY, EMPTY, EMPTY, CHAR_MAX, CHAR_MAX,
    CHAR_MAX, CHAR_MAX, CHAR_MAX, CHAR_MAX, CHAR_MAX, CHAR_MAX, WIDE_POINT, WIDE_EMPTY, WIDE_EMPTY, WIDE_EMPTY,
    WIDE_EMPTY, WIDE_EMPTY, WIDE_EMPTY, WIDE_EMPTY};

char C_NAME[] = "C";
Platform::Lock globalLock;

bool SameName(const char *name, const char *expected)
{
    for (;; ++name, ++expected) {
        char c = *name;
        if (c >= 'A' && c <= 'Z') {
            c = char(c - 'A' + 'a');
        }
        if (c != *expected) {
            return false;
        }
        if (!c) {
            return true;
        }
    }
}

} // namespace

const Locale &Resolve(_locale_t locale)
{
    if (!locale) {
        return C_LOCALE;
    }
    const auto *resolved = reinterpret_cast<const Locale *>(locale);
    if (resolved->Pointers.locinfo != reinterpret_cast<const __crt_locale_data *>(&resolved->Public)) {
        InvalidParameter(); // not a locale of this runtime
    }
    return *resolved;
}

int ToStream(const Locale &locale, wchar_t value, char bytes[3])
{
    if (!locale.Utf8) {
        bytes[0] = value > 0xFF ? '?' : char(value);
        return 1;
    }
    if (value >= 0xD800 && value <= 0xDFFF) {
        return 0; // UCRT converts each half of a pair alone and drops it
    }
    if (value < 0x80) {
        bytes[0] = char(value);
        return 1;
    }
    if (value < 0x800) {
        bytes[0] = char(0xC0 | (value >> 6));
        bytes[1] = char(0x80 | (value & 0x3F));
        return 2;
    }
    bytes[0] = char(0xE0 | (value >> 12));
    bytes[1] = char(0x80 | ((value >> 6) & 0x3F));
    bytes[2] = char(0x80 | (value & 0x3F));
    return 3;
}

int ToWide(const Locale &locale, const char *text, size_t available, wchar_t wide[2], int &count)
{
    const auto *bytes = reinterpret_cast<const unsigned char *>(text);
    if (!available) {
        return -1;
    }
    if (!locale.Utf8 || bytes[0] < 0x80) {
        wide[0] = bytes[0];
        count = 1;
        return 1;
    }
    unsigned code, length, minimum;
    if (bytes[0] >= 0xC2 && bytes[0] <= 0xDF) {
        code = bytes[0] & 0x1F;
        length = 2;
        minimum = 0x80;
    } else if (bytes[0] >= 0xE0 && bytes[0] <= 0xEF) {
        code = bytes[0] & 0x0F;
        length = 3;
        minimum = 0x800;
    } else if (bytes[0] >= 0xF0 && bytes[0] <= 0xF4) {
        code = bytes[0] & 0x07;
        length = 4;
        minimum = 0x10000;
    } else {
        return -1;
    }
    if (available < length) {
        return -1;
    }
    for (unsigned i = 1; i < length; ++i) {
        if ((bytes[i] & 0xC0) != 0x80) {
            return -1;
        }
        code = (code << 6) | (bytes[i] & 0x3F);
    }
    if (code < minimum || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) {
        return -1;
    }
    if (code < 0x10000) {
        wide[0] = wchar_t(code);
        count = 1;
    } else {
        wide[0] = wchar_t(0xD800 + ((code - 0x10000) >> 10));
        wide[1] = wchar_t(0xDC00 + ((code - 0x10000) & 0x3FF));
        count = 2;
    }
    return int(length);
}

int FromWide(const Locale &locale, wchar_t value, wchar_t &pending, char bytes[4])
{
    if (!locale.Utf8) {
        if (value > 0xFF) {
            return -1;
        }
        bytes[0] = char(value);
        return 1;
    }
    const bool high = value >= 0xD800 && value <= 0xDBFF, low = value >= 0xDC00 && value <= 0xDFFF;
    if (pending) {
        if (!low) {
            return -1; // a high surrogate without its low one
        }
        const unsigned code = 0x10000 + ((unsigned(pending) - 0xD800) << 10) + (unsigned(value) - 0xDC00);
        pending = 0;
        bytes[0] = char(0xF0 | (code >> 18));
        bytes[1] = char(0x80 | ((code >> 12) & 0x3F));
        bytes[2] = char(0x80 | ((code >> 6) & 0x3F));
        bytes[3] = char(0x80 | (code & 0x3F));
        return 4;
    }
    if (high) {
        pending = value;
        return 0;
    }
    if (low) {
        return -1; // a low surrogate without its high one
    }
    if (value < 0x80) {
        bytes[0] = char(value);
        return 1;
    }
    if (value < 0x800) {
        bytes[0] = char(0xC0 | (value >> 6));
        bytes[1] = char(0x80 | (value & 0x3F));
        return 2;
    }
    bytes[0] = char(0xE0 | (value >> 12));
    bytes[1] = char(0x80 | ((value >> 6) & 0x3F));
    bytes[2] = char(0x80 | (value & 0x3F));
    return 3;
}

char *Setlocale(int category, const char *name)
{
    if (category < LC_ALL || category > LC_MAX) {
        InvalidParameter();
    }
    if (!name || (name[0] == 'C' && !name[1])) {
        return C_NAME;
    }
    return nullptr; // the global locale stays the C locale
}

struct lconv *Localeconv()
{
    return &C_CONVENTIONS;
}

void LockLocales()
{
    Platform::Acquire(globalLock);
}

void UnlockLocales()
{
    Platform::Release(globalLock);
}

const unsigned short *Pctype()
{
    return C_TABLE.Values + 1;
}

_locale_t CreateLocale(int category, const char *name)
{
    if (category != LC_ALL) {
        InvalidParameter(); // single categories are not implemented
    }
    if (!name) {
        return nullptr;
    }
    bool utf8;
    if (name[0] == 'C' && !name[1]) {
        utf8 = false;
    } else if (SameName(name, ".utf8") || SameName(name, ".utf-8")) {
        utf8 = true;
    } else {
        return nullptr; // no other locale exists here
    }
    auto *locale = static_cast<Locale *>(Malloc(sizeof(Locale)));
    if (!locale) {
        return nullptr;
    }
    const Table &table = utf8 ? UTF8_TABLE : C_TABLE;
    for (int i = 0; i < 257; ++i) {
        locale->Ctype[i] = table.Values[i];
    }
    locale->Public._locale_pctype = locale->Ctype + 1;
    locale->Public._locale_mb_cur_max = utf8 ? 4 : 1;
    locale->Public._locale_lc_codepage = utf8 ? 65001 : 0;
    locale->Pointers.locinfo = reinterpret_cast<__crt_locale_data *>(&locale->Public);
    locale->Pointers.mbcinfo = nullptr;
    locale->Utf8 = utf8;
    return reinterpret_cast<_locale_t>(locale);
}

void FreeLocale(_locale_t locale)
{
    if (locale) {
        Free(const_cast<Locale *>(&Resolve(locale)));
    }
}

} // namespace WitCrt

#ifndef WITCRT_REFERENCE
extern "C" _locale_t __cdecl _create_locale(int category, const char *name)
{
    return WitCrt::CreateLocale(category, name);
}

extern "C" void __cdecl _free_locale(_locale_t locale)
{
    WitCrt::FreeLocale(locale);
}

extern "C" char *__cdecl setlocale(int category, const char *name)
{
    return WitCrt::Setlocale(category, name);
}

extern "C" struct lconv *__cdecl localeconv()
{
    return WitCrt::Localeconv();
}

extern "C" void __cdecl _lock_locales()
{
    WitCrt::LockLocales();
}

extern "C" void __cdecl _unlock_locales()
{
    WitCrt::UnlockLocales();
}

extern "C" const unsigned short *__cdecl __pctype_func()
{
    return WitCrt::Pctype();
}

extern "C" int __cdecl ___mb_cur_max_func()
{
    return 1;
}

extern "C" unsigned int __cdecl ___lc_codepage_func()
{
    return 0;
}

extern "C" unsigned int __cdecl ___lc_collate_cp_func()
{
    return 0;
}

namespace {
wchar_t *localeNames[LC_MAX + 1]; // the C locale has no names
} // namespace

extern "C" wchar_t **__cdecl ___lc_locale_name_func()
{
    return localeNames;
}
#endif
