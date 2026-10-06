#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <errno.h>
#include <limits.h>
#include <locale.h>
#include <malloc.h>
#include <math.h>
#include <share.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <wchar.h>
#include <initializer_list>
#include "crt.h"

/* The WitOS UCRT subset against UCRT in one process (P6.4.h, hosted). The subset is compiled with WITCRT_REFERENCE,
 * which leaves its exported names out, and every case calls both with the same input: printf over generated
 * specifications, options, locales and buffer contracts; streams into files; strings, parsing, messages, time,
 * locales, ceilf and the heap. A case UCRT reports to the invalid-parameter handler is not compared: there the subset
 * ends the process, as UCRT's default handler does. The first differences are printed; the last line counts the
 * compared cases. */
namespace {

unsigned long long compared, skipped, failures;
// Whether this UCRT fails a narrow string with a precision in the UTF-8 locale: the UCRT of Windows Server 2025 faults
// or reports EILSEQ there, even for "abc" or "(null)", newer ones convert it. String cases with a precision in the
// UTF-8 locale are then not compared and are counted.
bool precisionDefect;
unsigned long long precisionSkipped;
int invalids;
// --verbose names each print case on standard error first: the subset ends the process where it fails fast.
bool verbose;
// The side whose print call raised an exception, and its code: the case is reported instead of ending the run.
const char *crashed;
unsigned long crashCode;
// What the current print case passes besides the format, for the reports.
char note[64];

void Handler(const wchar_t *, const wchar_t *, const wchar_t *, unsigned, uintptr_t)
{
    ++invalids;
}

uint64_t seed = 0x2545F4914F6CDD1DULL;

uint64_t Next()
{
    seed ^= seed << 13;
    seed ^= seed >> 7;
    seed ^= seed << 17;
    return seed;
}

unsigned Pick(unsigned count)
{
    return unsigned(Next() % count);
}

void Escape(char *out, size_t size, const wchar_t *text, size_t count)
{
    size_t used = 0;
    for (size_t i = 0; i < count && used + 8 < size; ++i) {
        if (text[i] >= 0x20 && text[i] < 0x7F) {
            out[used++] = char(text[i]);
        } else {
            used += size_t(sprintf_s(out + used, size - used, "\\u%04X", unsigned(text[i])));
        }
    }
    out[used] = 0;
}

bool Report(const char *area, const wchar_t *input, const char *detail)
{
    ++failures;
    if (failures <= 40) {
        char escaped[512];
        Escape(escaped, sizeof(escaped), input ? input : L"", input ? wcslen(input) : 0);
        printf("FAIL %s [%s] %s\n", area, escaped, detail);
    }
    return false;
}

/* printf */

constexpr size_t BUFFER = 320;

int Crashed(const char *side, unsigned long code)
{
    crashed = side;
    crashCode = code;
    return EXCEPTION_EXECUTE_HANDLER;
}

int UcrtPrint(unsigned long long options, wchar_t *buffer, size_t count, const wchar_t *format, _locale_t locale, ...)
{
    va_list args;
    va_start(args, locale);
    int result = INT_MIN;
    __try {
        result = __stdio_common_vswprintf(options, buffer, count, format, locale, args);
    } __except (Crashed("ucrt", GetExceptionCode())) {
    }
    va_end(args);
    return result;
}

int WitPrint(unsigned long long options, wchar_t *buffer, size_t count, const wchar_t *format, _locale_t locale, ...)
{
    va_list args;
    va_start(args, locale);
    int result = INT_MIN;
    __try {
        result = WitCrt::Vswprintf(options, buffer, count, format, locale, args);
    } __except (Crashed("wit", GetExceptionCode())) {
    }
    va_end(args);
    return result;
}

int UcrtPrintS(unsigned long long options, wchar_t *buffer, size_t size, size_t limit, const wchar_t *format,
    _locale_t locale, ...)
{
    va_list args;
    va_start(args, locale);
    int result = INT_MIN;
    __try {
        result = __stdio_common_vsnwprintf_s(options, buffer, size, limit, format, locale, args);
    } __except (Crashed("ucrt", GetExceptionCode())) {
    }
    va_end(args);
    return result;
}

int WitPrintS(unsigned long long options, wchar_t *buffer, size_t size, size_t limit, const wchar_t *format,
    _locale_t locale, ...)
{
    va_list args;
    va_start(args, locale);
    int result = INT_MIN;
    __try {
        result = WitCrt::Vsnwprintf_s(options, buffer, size, limit, format, locale, args);
    } __except (Crashed("wit", GetExceptionCode())) {
    }
    va_end(args);
    return result;
}

int UcrtPrintN(unsigned long long options, char *buffer, size_t size, const char *format, _locale_t locale, ...)
{
    va_list args;
    va_start(args, locale);
    int result = INT_MIN;
    __try {
        result = __stdio_common_vsprintf_s(options, buffer, size, format, locale, args);
    } __except (Crashed("ucrt", GetExceptionCode())) {
    }
    va_end(args);
    return result;
}

int WitPrintN(unsigned long long options, char *buffer, size_t size, const char *format, _locale_t locale, ...)
{
    va_list args;
    va_start(args, locale);
    int result = INT_MIN;
    __try {
        result = WitCrt::Vsprintf_s(options, buffer, size, format, locale, args);
    } __except (Crashed("wit", GetExceptionCode())) {
    }
    va_end(args);
    return result;
}

struct Locales {
    _locale_t Ucrt, Wit;
};

void Fill(wchar_t *buffer)
{
    for (size_t i = 0; i < BUFFER; ++i) {
        buffer[i] = 0xA5A5;
    }
}

bool Same(const wchar_t *a, const wchar_t *b)
{
    return !memcmp(a, b, BUFFER * sizeof(wchar_t));
}

/* Compares the buffer functions. With an invalid narrow string, both must fail with EILSEQ (or succeed alike), but
 * the buffers are not compared: UCRT writes padding and characters before the failure, and with them may reach its
 * truncation result, while the subset fails before the conversion writes anything. */
bool Agree(
    int expected, int expectedErrno, int actual, int actualErrno, const wchar_t *ucrt, const wchar_t *wit, bool relaxed)
{
    if (relaxed && expected < 0) {
        return actual < 0 && actualErrno == expectedErrno;
    }
    return actual == expected && actualErrno == expectedErrno && Same(ucrt, wit);
}

bool CrashReported(const wchar_t *format, const char *call, unsigned long long options, size_t size, size_t limit)
{
    if (!crashed) {
        return false;
    }
    char detail[200];
    sprintf_s(detail, "%s options=%llx size=%zu limit=%zd %s: %s raised %08lX", call, options, size, (ptrdiff_t)limit,
        note, crashed, crashCode);
    crashed = nullptr;
    Report("crash", format, detail);
    return true;
}

void PrintWide(const wchar_t *format, const Locales &locales, const uint64_t (&a)[4], bool relaxed)
{
    static wchar_t ucrt[BUFFER], wit[BUFFER];
    if (verbose) {
        char escaped[256];
        Escape(escaped, sizeof(escaped), format, wcslen(format));
        fprintf(stderr, "case [%s] %llx %llx %llx %llx\n", escaped, a[0], a[1], a[2], a[3]);
    }
    for (const unsigned long long options : {0x24ULL, 0x25ULL, 0x26ULL, 0x04ULL, 0x00ULL}) {
        // A count of 0 comes once without a buffer (counting) and once with one (SIZE_MAX stands for it).
        for (size_t count :
            {size_t(0), SIZE_MAX, size_t(1), size_t(2), size_t(3), size_t(7), size_t(16), size_t(64), size_t(300)}) {
            const bool counting = !count;
            count = count == SIZE_MAX ? 0 : count;
            Fill(ucrt);
            Fill(wit);
            invalids = 0;
            errno = 71;
            const int expected =
                UcrtPrint(options, counting ? nullptr : ucrt, count, format, locales.Ucrt, a[0], a[1], a[2], a[3]);
            const int expectedErrno = errno;
            if (CrashReported(format, "vswprintf", options, count, 0)) {
                return;
            }
            if (invalids) {
                ++skipped;
                return;
            }
            errno = 71;
            const int actual =
                WitPrint(options, counting ? nullptr : wit, count, format, locales.Wit, a[0], a[1], a[2], a[3]);
            if (CrashReported(format, "vswprintf", options, count, 0)) {
                return;
            }
            ++compared;
            if (!Agree(expected, expectedErrno, actual, errno, ucrt, wit, relaxed)) {
                char detail[1024], u[256], w[256];
                Escape(u, sizeof(u), ucrt, count < 40 ? count : 40);
                Escape(w, sizeof(w), wit, count < 40 ? count : 40);
                sprintf_s(detail, "vswprintf options=%llx count=%zu %s ucrt=%d/%d [%s] wit=%d/%d [%s]", options, count,
                    note, expected, expectedErrno, u, actual, errno, w);
                Report("print", format, detail);
                return;
            }
        }
        if (options != 0x24 && options != 0) {
            continue;
        }
        for (const size_t size : {size_t(1), size_t(2), size_t(5), size_t(16), size_t(300)}) {
            for (const size_t limit : {size_t(0), size_t(1), size_t(3), size - 1, size, size_t(15), _TRUNCATE}) {
                Fill(ucrt);
                Fill(wit);
                invalids = 0;
                errno = 71;
                const int expected =
                    UcrtPrintS(options, ucrt, size, limit, format, locales.Ucrt, a[0], a[1], a[2], a[3]);
                const int expectedErrno = errno;
                if (CrashReported(format, "vsnwprintf_s", options, size, limit)) {
                    return;
                }
                if (invalids) {
                    ++skipped;
                    continue;
                }
                errno = 71;
                const int actual = WitPrintS(options, wit, size, limit, format, locales.Wit, a[0], a[1], a[2], a[3]);
                if (CrashReported(format, "vsnwprintf_s", options, size, limit)) {
                    return;
                }
                ++compared;
                if (!Agree(expected, expectedErrno, actual, errno, ucrt, wit, relaxed)) {
                    char detail[1024], u[256], w[256];
                    Escape(u, sizeof(u), ucrt, size < 40 ? size : 40);
                    Escape(w, sizeof(w), wit, size < 40 ? size : 40);
                    sprintf_s(detail, "vsnwprintf_s options=%llx size=%zu limit=%zd %s ucrt=%d/%d [%s] wit=%d/%d [%s]",
                        options, size, (ptrdiff_t)limit, note, expected, expectedErrno, u, actual, errno, w);
                    Report("print_s", format, detail);
                    return;
                }
            }
        }
    }
}

void EscapeNarrow(char *out, size_t size, const char *text, size_t count)
{
    size_t used = 0;
    for (size_t i = 0; i < count && used + 8 < size; ++i) {
        const unsigned char c = static_cast<unsigned char>(text[i]);
        if (c >= 0x20 && c < 0x7F) {
            out[used++] = char(c);
        } else {
            used += size_t(sprintf_s(out + used, size - used, "\\x%02X", c));
        }
    }
    out[used] = 0;
}

/* The same case through the narrow sprintf_s (P6.4.i3): the format narrowed, characters beyond U+00FF as ?. */
void PrintNarrow(const wchar_t *wideFormat, const Locales &locales, const uint64_t (&a)[4], bool relaxed)
{
    char format[256];
    size_t length = 0;
    for (; wideFormat[length] && length + 1 < sizeof(format); ++length) {
        format[length] = wideFormat[length] > 0xFF ? '?' : char(wideFormat[length]);
    }
    format[length] = 0;
    static char ucrt[BUFFER], wit[BUFFER];
    for (const unsigned long long options : {0x24ULL, 0x00ULL}) {
        for (const size_t size : {size_t(1), size_t(2), size_t(3), size_t(5), size_t(16), size_t(64), size_t(300)}) {
            memset(ucrt, 0xA5, BUFFER);
            memset(wit, 0xA5, BUFFER);
            invalids = 0;
            errno = 71;
            const int expected = UcrtPrintN(options, ucrt, size, format, locales.Ucrt, a[0], a[1], a[2], a[3]);
            const int expectedErrno = errno;
            if (CrashReported(wideFormat, "vsprintf_s", options, size, 0)) {
                return;
            }
            if (invalids) {
                ++skipped;
                continue;
            }
            errno = 71;
            const int actual = WitPrintN(options, wit, size, format, locales.Wit, a[0], a[1], a[2], a[3]);
            if (CrashReported(wideFormat, "vsprintf_s", options, size, 0)) {
                return;
            }
            ++compared;
            const bool same = relaxed && expected < 0
                ? actual < 0 && errno == expectedErrno
                : actual == expected && errno == expectedErrno && !memcmp(ucrt, wit, BUFFER);
            if (!same) {
                char detail[1024], u[256], w[256];
                EscapeNarrow(u, sizeof(u), ucrt, size < 40 ? size : 40);
                EscapeNarrow(w, sizeof(w), wit, size < 40 ? size : 40);
                sprintf_s(detail, "vsprintf_s options=%llx size=%zu %s ucrt=%d/%d [%s] wit=%d/%d [%s]", options, size,
                    note, expected, expectedErrno, u, actual, errno, w);
                Report("print_narrow", wideFormat, detail);
                return;
            }
        }
    }
}

void Print(const wchar_t *format, const Locales &locales, const uint64_t (&a)[4], bool relaxed = false)
{
    PrintNarrow(format, locales, a, relaxed);
    PrintWide(format, locales, a, relaxed);
}

const wchar_t *const FLAGS[] = {L"", L"-", L"+", L" ", L"#", L"0", L"-0", L"+0", L" 0", L"#0", L"-#", L"+ ", L"-+ #0"};
const wchar_t *const WIDTHS[] = {L"", L"1", L"2", L"5", L"12", L"25", L"*"};
const wchar_t *const PRECISIONS[] = {L"", L".", L".0", L".1", L".3", L".12", L".30", L".*"};
const wchar_t *const INTEGER_LENGTHS[] = {L"", L"hh", L"h", L"l", L"ll", L"I", L"I32", L"I64", L"z", L"j", L"t"};
const wchar_t *const CHARACTER_LENGTHS[] = {
    L"", L"h", L"l", L"w", L"", L"h", L"l", L"w", L"hh", L"ll", L"I", L"I32", L"I64", L"z", L"j", L"t", L"L", L"T"};
const wchar_t *const POINTER_LENGTHS[] = {L"", L"h", L"l", L"ll", L"I", L"w", L"L", L"T", L"z"};

const uint64_t INTEGERS[] = {0, 1, 7, 8, 9, 10, 15, 16, 42, 99, 100, 127, 128, 255, 256, 999, 1000, 0x7FFF, 0x8000,
    0xFFFF, 0x10000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0x100000000ULL, 0x7FFFFFFFFFFFFFFFULL, 0x8000000000000000ULL,
    0xFFFFFFFFFFFFFFFFULL, uint64_t(-1LL), uint64_t(-42LL), uint64_t(-128LL), uint64_t(-129LL), uint64_t(-32768LL),
    0x123456789ABCDEFULL, 0xFEDCBA9876543210ULL};

const int WIDTH_ARGUMENTS[] = {0, 1, 3, 12, -1, -5, -12};
const int PRECISION_ARGUMENTS[] = {0, 1, 4, 13, -1, -7};

const wchar_t *const WIDE_STRINGS[] = {L"", L"a", L"abc", L"hostfxr", L"h\x00E9llo \x20AC", L"\xD83D\xDE00x",
    L"x\xD83D", L"\xDE00y", L"tab\there", L"\xD83Dz", nullptr};

/* Narrow strings in zeroed slots: a conversion of the other width reads them as wide strings, and the slot's zero
 * tail ends them there instead of somewhere past a literal. */
struct NarrowSlot {
    const char Bytes[24];
};

const NarrowSlot NARROW_SLOTS[] = {{""}, {"a"}, {"abc"}, {"hostpolicy"}, {"h\xC3\xA9llo \xE2\x82\xAC"},
    {"\xF0\x9F\x98\x80x"}, {"\xE9t\xE9"}, {"\xC3"}, {"a\xE2\x82"}, {"\xED\xA0\x80"}, {"\xC0\xAF"}, {"\xF4\x90\x80\x80"},
    {"\xFF"}};
const char *const NARROW_STRINGS[] = {NARROW_SLOTS[0].Bytes, NARROW_SLOTS[1].Bytes, NARROW_SLOTS[2].Bytes,
    NARROW_SLOTS[3].Bytes, NARROW_SLOTS[4].Bytes, NARROW_SLOTS[5].Bytes, nullptr, NARROW_SLOTS[6].Bytes,
    NARROW_SLOTS[7].Bytes, NARROW_SLOTS[8].Bytes, NARROW_SLOTS[9].Bytes, NARROW_SLOTS[10].Bytes, NARROW_SLOTS[11].Bytes,
    NARROW_SLOTS[12].Bytes};
constexpr unsigned VALID_UTF8 = 7; // the strings before this index are valid UTF-8
const unsigned CHARACTERS[] = {
    'A', 'z', 0, 0x7F, 0x80, 0xC3, 0xE9, 0xFF, 0x100, 0x1E9, 0x20AC, 0xD83D, 0xDE00, 0xFFFF, 0x10041};

void IntegerCase(const Locales &locales)
{
    static const wchar_t conversions[] = L"diuoxX";
    sprintf_s(note, "%s", locales.Wit ? "utf8" : "C");
    wchar_t format[64];
    const wchar_t *width = WIDTHS[Pick(7)], *precision = PRECISIONS[Pick(8)];
    swprintf_s(
        format, L"<%%%s%s%s%s%c>", FLAGS[Pick(13)], width, precision, INTEGER_LENGTHS[Pick(11)], conversions[Pick(6)]);
    uint64_t a[4] = {};
    unsigned used = 0;
    if (width[0] == L'*') {
        a[used++] = uint64_t(int64_t(WIDTH_ARGUMENTS[Pick(7)]));
    }
    if (precision[0] && precision[1] == L'*') {
        a[used++] = uint64_t(int64_t(PRECISION_ARGUMENTS[Pick(6)]));
    }
    a[used] = Pick(4) ? INTEGERS[Pick(sizeof(INTEGERS) / sizeof(INTEGERS[0]))] : Next();
    Print(format, locales, a);
}

void TextCase(const Locales &locales)
{
    static const wchar_t conversions[] = L"cCsS";
    wchar_t format[64];
    const wchar_t *width = WIDTHS[Pick(7)], *precision = PRECISIONS[Pick(8)];
    const wchar_t conversion = conversions[Pick(4)];
    const wchar_t *length = CHARACTER_LENGTHS[Pick(sizeof(CHARACTER_LENGTHS) / sizeof(CHARACTER_LENGTHS[0]))];
    swprintf_s(format, L"<%%%s%s%s%s%c>", FLAGS[Pick(13)], width, precision, length, conversion);
    uint64_t a[4] = {};
    unsigned used = 0;
    if (width[0] == L'*') {
        a[used++] = uint64_t(int64_t(WIDTH_ARGUMENTS[Pick(7)]));
    }
    if (precision[0] && precision[1] == L'*') {
        a[used++] = uint64_t(int64_t(PRECISION_ARGUMENTS[Pick(6)]));
    }
    const char *locale = locales.Wit ? "utf8" : "C";
    if (conversion == L'c' || conversion == L'C') {
        a[used] = CHARACTERS[Pick(sizeof(CHARACTERS) / sizeof(CHARACTERS[0]))];
        sprintf_s(note, "%s char %llX", locale, a[used]);
    } else {
        // The width of the string depends on the options and length; give each case both kinds by trying both.
        if (precisionDefect && locales.Wit && precision[0]) {
            ++precisionSkipped;
            return;
        }
        const unsigned wide = Pick(sizeof(WIDE_STRINGS) / sizeof(WIDE_STRINGS[0]));
        a[used] = (uint64_t)(uintptr_t)WIDE_STRINGS[wide];
        sprintf_s(note, "%s wide#%u", locale, wide);
        Print(format, locales, a, locales.Wit != nullptr); // read as narrow, its bytes need not be valid UTF-8
        const unsigned narrow = Pick(sizeof(NARROW_STRINGS) / sizeof(NARROW_STRINGS[0]));
        a[used] = (uint64_t)(uintptr_t)NARROW_STRINGS[narrow];
        sprintf_s(note, "%s narrow#%u", locale, narrow);

        Print(format, locales, a, locales.Wit && narrow >= VALID_UTF8);
        return;
    }
    Print(format, locales, a);
}

void PrintCases(const Locales &c, const Locales &utf8)
{
    note[0] = 0;
    for (unsigned i = 0; i < 12000; ++i) {
        IntegerCase(Pick(4) ? c : utf8);
    }
    for (unsigned i = 0; i < 16000; ++i) {
        TextCase(i & 1 ? c : utf8);
    }
    for (unsigned i = 0; i < 600; ++i) {
        wchar_t format[64];
        swprintf_s(format, L"[%%%s%s%s%sp]", FLAGS[Pick(13)], WIDTHS[Pick(6)], PRECISIONS[Pick(7)],
            POINTER_LENGTHS[Pick(sizeof(POINTER_LENGTHS) / sizeof(POINTER_LENGTHS[0]))]);
        const uint64_t a[4] = {Pick(2) ? INTEGERS[Pick(sizeof(INTEGERS) / sizeof(INTEGERS[0]))] : Next()};
        Print(format, c, a);
    }
    const uint64_t none[4] = {};
    // Widths and precisions that do not parse fail alike; with counts beyond INT_MAX, UCRT's count wraps instead.
    for (const wchar_t *format :
        {L"", L"plain", L"100%%", L"%%%%", L"a%%b%%c", L"%5", L"%2147483648d", L"%.2147483648d", L"%-2147483648d"}) {
        Print(format, c, none);
    }
    // Malformed specifications: UCRT's secure functions reject them, the others write them leniently.
    const uint64_t numbers[4] = {7, 9, 11, 13};
    for (const wchar_t *format : {L"[%k]", L"[%5k]", L"[%-5k]", L"[%05k]", L"[%5%]", L"[%-5%]", L"[%#%]", L"[%.3%]",
             L"[%l%]", L"[%h%]", L"[%5", L"[%", L"[%-", L"[%l]", L"[%h]", L"[%hh]", L"[%I]", L"[%I6]", L"[%I3]",
             L"[%I64]", L"[%.k]", L"[%*k]%d", L"[%.*k]%d", L"[%$]", L"[%1$d]", L"[%lk]", L"[%wk]", L"[%Lk]", L"[%Tk]",
             L"[%zk]", L"[%llk]", L"[%I32k]", L"[%ld%k%d]", L"[%I32]", L"[%%%k]", L"%", L"%%%", L"%-5.3", L"x%",
             L"[%y%v%q]", L"[%\x00E9]", L"[%5\x20AC]"}) {
        Print(format, c, numbers);
    }
    const uint64_t big[4] = {uint64_t(int64_t(INT_MIN)), 1};
    Print(L"[%.*d]", c, big);
    const uint64_t many[4] = {42, (uint64_t)(uintptr_t)L"two", (uint64_t)(uintptr_t)"three", 0xFF};
    Print(L"%d %s %hs %x and more text after", c, many);
    Print(L"%d %s %hs %x and more text after", utf8, many);
}

/* Streams */

struct Api {
    FILE *(*Open)(const wchar_t *, const wchar_t *, int);
    int (*Vfwprintf)(unsigned long long, FILE *, const wchar_t *, _locale_t, va_list);
    wint_t (*Putwc)(wchar_t, FILE *);
    int (*Putc)(int, FILE *);
    int (*Puts)(const char *, FILE *);
    size_t (*Write)(const void *, size_t, size_t, FILE *);
    int (*Flush)(FILE *);
    int (*Setvbuf)(FILE *, char *, int, size_t);
    int (*Close)(FILE *);
    int (*Remove)(const wchar_t *);
    int (*Rename)(const wchar_t *, const wchar_t *);
    _locale_t Utf8;
};

FILE *UcrtOpen(const wchar_t *path, const wchar_t *mode, int share)
{
    return _wfsopen(path, mode, share);
}

int UcrtVfwprintf(unsigned long long options, FILE *stream, const wchar_t *format, _locale_t locale, va_list args)
{
    return __stdio_common_vfwprintf(options, stream, format, locale, args);
}

wint_t UcrtPutwc(wchar_t value, FILE *stream)
{
    return fputwc(value, stream);
}

int UcrtPutc(int value, FILE *stream)
{
    return fputc(value, stream);
}

int UcrtPuts(const char *text, FILE *stream)
{
    return fputs(text, stream);
}

size_t UcrtWrite(const void *data, size_t size, size_t count, FILE *stream)
{
    return fwrite(data, size, count, stream);
}

int UcrtFlush(FILE *stream)
{
    return fflush(stream);
}

int UcrtSetvbuf(FILE *stream, char *buffer, int mode, size_t size)
{
    return setvbuf(stream, buffer, mode, size);
}

int UcrtClose(FILE *stream)
{
    return fclose(stream);
}

int UcrtRemove(const wchar_t *path)
{
    return _wremove(path);
}

int UcrtRename(const wchar_t *from, const wchar_t *to)
{
    return _wrename(from, to);
}

/* A record of the results and errno values of one script. */
struct Log {
    char Text[4096];
    size_t Used;

    void Add(long long value)
    {
        if (Used + 32 < sizeof(Text)) {
            Used += size_t(sprintf_s(Text + Used, sizeof(Text) - Used, "%lld/%d ", value, errno));
        }
    }
};

int StreamPrint(
    const Api &api, Log &log, FILE *stream, unsigned long long options, _locale_t locale, const wchar_t *format, ...)
{
    va_list args;
    va_start(args, format);
    errno = 0;
    const int result = api.Vfwprintf(options, stream, format, locale, args);
    va_end(args);
    log.Add(result);
    return result;
}

void Script(const Api &api, Log &log, const wchar_t *path, const wchar_t *mode, unsigned buffering)
{
    errno = 0;
    FILE *stream = api.Open(path, mode, _SH_DENYNO);
    log.Add(stream != nullptr);
    if (!stream) {
        return;
    }
    static char user[64];
    if (buffering == 1) {
        errno = 0;
        log.Add(api.Setvbuf(stream, nullptr, _IONBF, 0));
    } else if (buffering == 2) {
        errno = 0;
        log.Add(api.Setvbuf(stream, nullptr, _IOFBF, 2));
    } else if (buffering == 3) {
        errno = 0;
        log.Add(api.Setvbuf(stream, user, _IOFBF, sizeof(user)));
    } else if (buffering == 4) {
        errno = 0;
        log.Add(api.Setvbuf(stream, nullptr, _IOLBF, 1000));
    }
    StreamPrint(api, log, stream, 0x24, nullptr, L"line %d|%s|%hs|%c\n", 1, L"wide", "narrow", L'x');
    errno = 0;
    log.Add(api.Putwc(L'\n', stream));
    errno = 0;
    log.Add(api.Putwc(0x00E9, stream));
    errno = 0;
    log.Add(api.Putwc(0x20AC, stream));
    errno = 0;
    log.Add((long long)api.Write("a\nb\r\nc", 1, 6, stream));
    errno = 0;
    log.Add((long long)api.Write("12345678", 4, 2, stream));
    errno = 0;
    log.Add(api.Puts("put\nstring\r\n", stream));
    errno = 0;
    log.Add(api.Puts("", stream));
    errno = 0;
    log.Add(api.Putc('\n', stream));
    errno = 0;
    log.Add(api.Putc(0x1E9, stream));
    errno = 0;
    log.Add(api.Putc(-1, stream));
    StreamPrint(api, log, stream, 0x24, nullptr, L"[%s]", L"\x00E9\x00FF");
    StreamPrint(api, log, stream, 0x24, nullptr, L"[%s]", L"ab\x20AC\x0100\xD83D\xDE00\xFFFF\xDC00");
    StreamPrint(api, log, stream, 0x24, api.Utf8, L"[%s]", L"\xFFFF\xFFFE\x0800\x07FF\x0080\x007F");
    StreamPrint(api, log, stream, 0x24, api.Utf8, L"[%s]\n", L"\x00E9\x20AC\xD83D\xDE00 end");
    StreamPrint(api, log, stream, 0x24, api.Utf8, L"[%s]", L"lone\xD83D");
    StreamPrint(api, log, stream, 0x24, api.Utf8, L"[%s]", L"next");
    StreamPrint(api, log, stream, 0x24, api.Utf8, L"[%s]", L"low\xDE00");
    StreamPrint(api, log, stream, 0x24, api.Utf8, L"[%hs|%hc]\n", "h\xC3\xA9llo", 'q');
    StreamPrint(api, log, stream, 0x24, api.Utf8, L"[%hs]", "\xC3");
    StreamPrint(api, log, stream, 0x00, nullptr, L"[%s|%S]\n", "iso", L"wide");
    StreamPrint(api, log, stream, 0x24, nullptr, L"%c%c", 0, L'z');
    wchar_t large[3000];
    for (int i = 0; i < 2999; ++i) {
        large[i] = i % 61 == 60 ? L'\n' : wchar_t(L'a' + i % 26);
    }
    large[2999] = 0;
    for (int i = 0; i < 3; ++i) {
        StreamPrint(api, log, stream, 0x24, nullptr, L"%s", large);
    }
    StreamPrint(api, log, stream, 0x24, nullptr, L"%-600d|\n", 5);
    errno = 0;
    log.Add(api.Flush(stream));
    StreamPrint(api, log, stream, 0x24, nullptr, L"after flush\n");
    errno = 0;
    log.Add(api.Close(stream));
}

bool ReadFileBytes(const wchar_t *path, char *buffer, size_t capacity, size_t &size)
{
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        size = 0;
        return false;
    }
    DWORD read = 0;
    const BOOL ok = ReadFile(file, buffer, DWORD(capacity), &read, nullptr);
    CloseHandle(file);
    size = read;
    return ok != FALSE;
}

void WriteFileBytes(const wchar_t *path, const char *bytes)
{
    HANDLE file = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
    DWORD written = 0;
    WriteFile(file, bytes, DWORD(strlen(bytes)), &written, nullptr);
    CloseHandle(file);
}

void StreamCases(const wchar_t *directory, _locale_t ucrtUtf8, _locale_t witUtf8)
{
    const Api ucrt = {UcrtOpen, UcrtVfwprintf, UcrtPutwc, UcrtPutc, UcrtPuts, UcrtWrite, UcrtFlush, UcrtSetvbuf,
        UcrtClose, UcrtRemove, UcrtRename, ucrtUtf8};
    const Api wit = {WitCrt::Wfsopen, WitCrt::Vfwprintf, WitCrt::Fputwc, WitCrt::Fputc, WitCrt::Fputs, WitCrt::Fwrite,
        WitCrt::Fflush, WitCrt::Setvbuf, WitCrt::Fclose, WitCrt::Wremove, WitCrt::Wrename, witUtf8};
    static char a[65536], b[65536];
    unsigned index = 0;
    for (const wchar_t *mode : {L"w", L"wb", L"wt", L"a", L"ab", L"at"}) {
        for (unsigned buffering = 0; buffering < 5; ++buffering) {
            wchar_t ucrtPath[MAX_PATH], witPath[MAX_PATH];
            swprintf_s(ucrtPath, L"%s\\ucrt-%u.txt", directory, index);
            swprintf_s(witPath, L"%s\\wit-%u.txt", directory, index);
            ++index;
            if (mode[0] == L'a') {
                WriteFileBytes(ucrtPath, "existing\r\n");
                WriteFileBytes(witPath, "existing\r\n");
            }
            static Log expected, actual;
            expected.Used = actual.Used = 0;
            Script(ucrt, expected, ucrtPath, mode, buffering);
            Script(wit, actual, witPath, mode, buffering);
            size_t aSize = 0, bSize = 0;
            ReadFileBytes(ucrtPath, a, sizeof(a), aSize);
            ReadFileBytes(witPath, b, sizeof(b), bSize);
            ++compared;
            expected.Text[expected.Used] = actual.Text[actual.Used] = 0;
            if (strcmp(expected.Text, actual.Text) || aSize != bSize || memcmp(a, b, aSize)) {
                size_t at = 0;
                while (at < aSize && at < bSize && a[at] == b[at]) {
                    ++at;
                }
                char detail[9000];
                sprintf_s(detail, "buffering=%u ucrt=[%s] wit=[%s] sizes %zu/%zu first difference at %zu", buffering,
                    expected.Text, actual.Text, aSize, bSize, at);
                Report("stream", mode, detail);
            }
        }
    }
    // Opening, removing and renaming that fail and succeed.
    const auto check = [&](const char *what, long long expectedResult, int expectedErrno, long long actualResult,
                           int actualErrno) {
        ++compared;
        if (expectedResult != actualResult || expectedErrno != actualErrno) {
            char detail[256];
            sprintf_s(
                detail, "%s ucrt=%lld/%d wit=%lld/%d", what, expectedResult, expectedErrno, actualResult, actualErrno);
            Report("files", L"", detail);
        }
    };
    wchar_t missing[MAX_PATH], existing[MAX_PATH], other[MAX_PATH], moved[MAX_PATH], invalid[MAX_PATH];
    swprintf_s(missing, L"%s\\missing\\file.txt", directory);
    swprintf_s(invalid, L"%s\\a<b.txt", directory); // a name Windows rejects: ERROR_INVALID_NAME is EINVAL
    swprintf_s(existing, L"%s\\existing.txt", directory);
    swprintf_s(other, L"%s\\other.txt", directory);
    swprintf_s(moved, L"%s\\moved.txt", directory);
    for (const wchar_t *path : {(const wchar_t *)missing, directory, (const wchar_t *)invalid}) {
        errno = 0;
        FILE *u = _wfsopen(path, L"w", _SH_DENYNO);
        const int ue = errno;
        errno = 0;
        FILE *w = WitCrt::Wfsopen(path, L"w", _SH_DENYNO);
        check("open", u != nullptr, ue, w != nullptr, errno);
    }
    WriteFileBytes(existing, "x");
    WriteFileBytes(other, "y");
    errno = 0;
    int u = _wremove(missing);
    int ue = errno;
    errno = 0;
    int w = WitCrt::Wremove(missing);
    check("remove missing", u, ue, w, errno);
    errno = 0;
    u = _wremove(invalid);
    ue = errno;
    errno = 0;
    w = WitCrt::Wremove(invalid);
    check("remove invalid name", u, ue, w, errno);
    errno = 0;
    u = _wrename(existing, other);
    ue = errno;
    errno = 0;
    w = WitCrt::Wrename(existing, other);
    check("rename onto existing", u, ue, w, errno);
    errno = 0;
    u = _wrename(missing, moved);
    ue = errno;
    errno = 0;
    w = WitCrt::Wrename(missing, moved);
    check("rename missing", u, ue, w, errno);
    errno = 0;
    u = _wremove(directory);
    ue = errno;
    errno = 0;
    w = WitCrt::Wremove(directory);
    check("remove directory", u, ue, w, errno);
    errno = 0;
    w = WitCrt::Wrename(existing, moved);
    const int we = errno;
    check("rename", 0, 0, w, we);
    errno = 0;
    w = WitCrt::Wremove(moved);
    check("remove", 0, 0, w, errno);
}

/* Strings, parsing and messages */

void Check(
    const char *area, const wchar_t *input, long long expected, long long actual, int expectedErrno, int actualErrno)
{
    ++compared;
    if (expected != actual || expectedErrno != actualErrno) {
        char detail[256];
        sprintf_s(detail, "ucrt=%lld/%d wit=%lld/%d", expected, expectedErrno, actual, actualErrno);
        Report(area, input, detail);
    }
}

void RandomText(wchar_t *text, size_t maximum, const wchar_t *alphabet, size_t letters)
{
    const size_t length = Pick(unsigned(maximum));
    for (size_t i = 0; i < length; ++i) {
        text[i] = alphabet[Pick(unsigned(letters))];
    }
    text[length] = 0;
}

void StringCases()
{
    static const wchar_t letters[] = {L'a', L'b', L'A', L'B', L'z', L'Z', L'[', L'_', L'@', L'`', 0x00E9, 0x00C9,
        0x0130, 0x0131, 0x00FF, 0x0178, 0x7FFF, 0xFFFF};
    for (unsigned i = 0; i < 200000; ++i) {
        wchar_t first[8], second[8];
        RandomText(first, 7, letters, sizeof(letters) / sizeof(letters[0]));
        if (Pick(3)) {
            wcscpy_s(second, first);
            if (second[0] && Pick(2)) {
                second[Pick(unsigned(wcslen(second)))] = letters[Pick(sizeof(letters) / sizeof(letters[0]))];
            }
        } else {
            RandomText(second, 7, letters, sizeof(letters) / sizeof(letters[0]));
        }
        const size_t count = Pick(9);
        Check("wcslen", first, (long long)wcslen(first), (long long)WitCrt::Wcslen(first), 0, 0);
        Check("wcscmp", first, wcscmp(first, second), WitCrt::Wcscmp(first, second), 0, 0);
        Check("wcsncmp", first, wcsncmp(first, second, count), WitCrt::Wcsncmp(first, second, count), 0, 0);
        Check("_wcsicmp", first, _wcsicmp(first, second), WitCrt::Wcsicmp(first, second), 0, 0);
        Check("_wcsnicmp", first, _wcsnicmp(first, second, count), WitCrt::Wcsnicmp(first, second, count), 0, 0);
        const wchar_t value = Pick(4) ? letters[Pick(sizeof(letters) / sizeof(letters[0]))] : 0;
        Check("wcschr", first, wcschr(first, value) ? wcschr(first, value) - first : -1,
            WitCrt::Wcschr(first, value) ? WitCrt::Wcschr(first, value) - first : -1, 0, 0);
    }
    for (int value = -1000; value <= 1000; ++value) {
        Check("tolower", L"", tolower(value), WitCrt::Tolower(value), 0, 0);
        Check("toupper", L"", toupper(value), WitCrt::Toupper(value), 0, 0);
    }
    // Every code unit as white space before a digit, as a digit and as a 0x prefix digit.
    for (unsigned c = 1; c < 0x10000; ++c) {
        for (const int base : {0, 10, 16, 36}) {
            const wchar_t texts[3][4] = {{wchar_t(c), L'7', 0}, {wchar_t(c), 0}, {wchar_t(c), L'x', L'1', 0}};
            for (const auto &text : texts) {
                wchar_t *end1 = nullptr, *end2 = nullptr;
                errno = 0;
                const unsigned long expected = wcstoul(text, &end1, base);
                const int expectedErrno = errno;
                errno = 0;
                const unsigned long actual = WitCrt::Wcstoul(text, &end2, base);
                Check("wcstoul", text, (long long)expected * 16 + (end1 - text), (long long)actual * 16 + (end2 - text),
                    expectedErrno, errno);
            }
        }
        const wchar_t text[] = {wchar_t(c), L'4', L'2', 0};
        errno = 0;
        const int expected = _wtoi(text);
        const int expectedErrno = errno;
        errno = 0;
        const int actual = WitCrt::Wtoi(text);
        Check("_wtoi", text, expected, actual, expectedErrno, errno);
    }
    static const wchar_t numbers[] = {L' ', L'\t', L'+', L'-', L'0', L'0', L'1', L'7', L'8', L'9', L'x', L'X', L'a',
        L'f', L'F', L'g', L'z', L'Z', 0x0660, 0x0669, 0xFF10, 0xFF19, 0x3000, 0x00A0, L'.'};
    for (unsigned i = 0; i < 300000; ++i) {
        wchar_t text[24];
        RandomText(text, Pick(3) ? 8 : 23, numbers, sizeof(numbers) / sizeof(numbers[0]));
        static const int bases[] = {0, 2, 8, 10, 16, 36};
        const int base = bases[Pick(6)];
        wchar_t *end1 = nullptr, *end2 = nullptr;
        errno = 0;
        const unsigned long expected = wcstoul(text, &end1, base);
        const int expectedErrno = errno;
        errno = 0;
        const unsigned long actual = WitCrt::Wcstoul(text, &end2, base);
        Check("wcstoul", text, (long long)expected * 64 + (end1 - text), (long long)actual * 64 + (end2 - text),
            expectedErrno, errno);
        errno = 0;
        const int expectedInt = _wtoi(text);
        const int expectedIntErrno = errno;
        errno = 0;
        const int actualInt = WitCrt::Wtoi(text);
        Check("_wtoi", text, expectedInt, actualInt, expectedIntErrno, errno);
    }
    for (const wchar_t *text : {L"4294967295", L"4294967296", L"-4294967295", L"-4294967296", L"99999999999999999999",
             L"2147483647", L"2147483648", L"-2147483648", L"-2147483649", L"0x", L"0xg", L"-0x", L"0x0", L"00x1",
             L"ffffffff", L"fffffffff", L"zzzzzzz", L"+", L"-", L"", L"   ", L"0"}) {
        for (const int base : {0, 10, 16, 36}) {
            wchar_t *end1 = nullptr, *end2 = nullptr;
            errno = 0;
            const unsigned long expected = wcstoul(text, &end1, base);
            const int expectedErrno = errno;
            errno = 0;
            const unsigned long actual = WitCrt::Wcstoul(text, &end2, base);
            Check("wcstoul", text, (long long)expected * 64 + (end1 - text), (long long)actual * 64 + (end2 - text),
                expectedErrno, errno);
        }
        errno = 0;
        const int expected = _wtoi(text);
        const int expectedErrno = errno;
        errno = 0;
        const int actual = WitCrt::Wtoi(text);
        Check("_wtoi", text, expected, actual, expectedErrno, errno);
    }
    for (int error = -5; error < 200; ++error) {
        for (const size_t count : {size_t(1), size_t(2), size_t(8), size_t(20), size_t(128)}) {
            wchar_t expected[128], actual[128];
            wmemset(expected, 0xA5A5, 128);
            wmemset(actual, 0xA5A5, 128);
            errno = 71;
            const errno_t r1 = _wcserror_s(expected, count, error);
            const int e1 = errno;
            errno = 71;
            const errno_t r2 = WitCrt::Wcserror_s(actual, count, error);
            Check("_wcserror_s", expected, r1, r2, e1, errno);
            if (wmemcmp(expected, actual, 128)) {
                Report("_wcserror_s", expected, "text");
            }
        }
    }
}

/* Time */

void TimeCases()
{
    void *const names = _Gettnames();
    {
        char *days = _Getdays(), *months = _Getmonths(), *myDays = WitCrt::Getdays(), *myMonths = WitCrt::Getmonths();
        wchar_t *wideDays = _W_Getdays(), *wideMonths = _W_Getmonths(), *myWideDays = WitCrt::WGetdays(),
                *myWideMonths = WitCrt::WGetmonths();
        compared += 4;
        if (strcmp(days, myDays) || strcmp(months, myMonths)) {
            char detail[400];
            sprintf_s(detail, "ucrt [%s] [%s] wit [%s] [%s]", days, months, myDays, myMonths);
            Report("_Getdays", L"", detail);
        }
        if (wcscmp(wideDays, myWideDays) || wcscmp(wideMonths, myWideMonths)) {
            Report("_W_Getdays", wideDays, "differs");
        }
        void *myNames = WitCrt::Gettnames();
        if (!myNames || !WitCrt::OwnTimeNames(myNames) || WitCrt::OwnTimeNames(names)) {
            Report("_Gettnames", L"", "record");
        }
        free(days);
        free(months);
        free(wideDays);
        free(wideMonths);
        WitCrt::Free(myDays);
        WitCrt::Free(myMonths);
        WitCrt::Free(myWideDays);
        WitCrt::Free(myWideMonths);
        WitCrt::Free(myNames);
    }
    const auto compareTm = [](const struct tm &a, const struct tm &b) {
        return a.tm_sec == b.tm_sec &&
            a.tm_min == b.tm_min &&
            a.tm_hour == b.tm_hour &&
            a.tm_mday == b.tm_mday &&
            a.tm_mon == b.tm_mon &&
            a.tm_year == b.tm_year &&
            a.tm_wday == b.tm_wday &&
            a.tm_yday == b.tm_yday &&
            a.tm_isdst == b.tm_isdst;
    };
    static const wchar_t *const formats[] = {L"%a", L"%A", L"%b", L"%B", L"%c", L"%C", L"%d", L"%D", L"%e", L"%F",
        L"%g", L"%G", L"%h", L"%H", L"%I", L"%j", L"%m", L"%M", L"%n", L"%p", L"%r", L"%R", L"%S", L"%t", L"%T", L"%u",
        L"%U", L"%V", L"%w", L"%W", L"%x", L"%X", L"%y", L"%Y", L"%%", L"%#a", L"%#A", L"%#b", L"%#B", L"%#c", L"%#C",
        L"%#d", L"%#D", L"%#e", L"%#F", L"%#g", L"%#G", L"%#h", L"%#H", L"%#I", L"%#j", L"%#m", L"%#M", L"%#n", L"%#p",
        L"%#r", L"%#R", L"%#S", L"%#t", L"%#T", L"%#u", L"%#U", L"%#V", L"%#w", L"%#W", L"%#x", L"%#X", L"%#y", L"%#Y",
        L"%#%", L"%Ec", L"%EC", L"%Ex", L"%EX", L"%Ey", L"%EY", L"%Od", L"%Oe", L"%OH", L"%OI", L"%Om", L"%OM", L"%OS",
        L"%Ou", L"%OU", L"%OV", L"%Ow", L"%OW", L"%Oy", L"%c GMT", L"plain text", L""};
    const long long edges[] = {-43201, -43200, -43199, -86400, -1, 0, 1, 951782400, 951868800, 1759587381, 4102444799,
        4102444800, 32535215999, 32535216000, 32536850399, 32536850400, 32536850401, 99999999999};
    for (unsigned i = 0; i < 40000; ++i) {
        long long value;
        if (i < sizeof(edges) / sizeof(edges[0])) {
            value = edges[i];
        } else if (i % 3 == 0) {
            // The last and first days of years, where the week numbers turn.
            const long long year = 1970 + Pick(1031);
            const long long start = 365 * (year - 1970) + ((year - 1) / 4 - (year - 1) / 100 + (year - 1) / 400) - 477;
            value = (start + (long long)Pick(14) - 7) * 86400 + Pick(86400);
        } else {
            value = (long long)(Next() % 32536893600ULL) - 43200;
        }
        struct tm expected, actual;
        memset(&expected, 0x5A, sizeof(expected));
        memset(&actual, 0x5A, sizeof(actual));
        const __time64_t time = value;
        invalids = 0;
        errno = 0;
        const errno_t r1 = _gmtime64_s(&expected, &time);
        const int e1 = errno;
        if (invalids) {
            ++skipped;
            continue;
        }
        errno = 0;
        const errno_t r2 = WitCrt::Gmtime64_s(&actual, &time);
        Check("_gmtime64_s", L"", r1 * 1000000000000LL + value, r2 * 1000000000000LL + value, e1, errno);
        if (!compareTm(expected, actual)) {
            char detail[128];
            sprintf_s(detail, "fields differ for %lld", value);
            Report("_gmtime64_s", L"", detail);
        }
        if (r1) {
            continue;
        }
        const wchar_t *format = formats[Pick(sizeof(formats) / sizeof(formats[0]))];
        for (const size_t count : {size_t(1), size_t(3), size_t(9), size_t(100)}) {
            wchar_t a[100], b[100];
            wmemset(a, 0xA5A5, 100);
            wmemset(b, 0xA5A5, 100);
            invalids = 0;
            errno = 71;
            const size_t n1 = wcsftime(a, count, format, &expected);
            const int fe = errno;
            if (invalids) {
                ++skipped;
                continue;
            }
            errno = 71;
            const size_t n2 = WitCrt::Wcsftime(b, count, format, &expected);
            Check("wcsftime", format, (long long)n1, (long long)n2, fe, errno);
            if (wmemcmp(a, b, 100)) {
                char detail[400], u[180], w[180];
                Escape(u, sizeof(u), a, n1 ? n1 : 1);
                Escape(w, sizeof(w), b, n2 ? n2 : 1);
                sprintf_s(detail, "time %lld count %zu ucrt [%s] wit [%s]", value, count, u, w);
                Report("wcsftime", format, detail);
            }
            // The STL's time facets: UCRT's names record, and the narrow form with a byte beyond ASCII.
            wmemset(a, 0xA5A5, 100);
            errno = 71;
            const size_t n3 = _Wcsftime(a, count, format, &expected, names);
            const int we = errno;
            wmemset(b, 0xA5A5, 100);
            errno = 71;
            const size_t n6 = WitCrt::Wcsftime(b, count, format, &expected, true);
            Check("_Wcsftime", format, (long long)n3, (long long)n6, we, errno);
            if (wmemcmp(a, b, 100)) {
                Report("_Wcsftime", format, "differs");
            }
            char narrowFormat[64], c[100], d[100];
            size_t length = 0;
            for (; format[length]; ++length) {
                narrowFormat[length] = char(format[length]);
            }
            narrowFormat[length++] = '\xE9';
            narrowFormat[length] = 0;
            memset(c, 0xA5, 100);
            memset(d, 0xA5, 100);
            errno = 71;
            const size_t n4 = _Strftime(c, count, narrowFormat, &expected, names);
            const int se = errno;
            errno = 71;
            const size_t n5 = WitCrt::Strftime(d, count, narrowFormat, &expected, true);
            Check("_Strftime", format, (long long)n4, (long long)n5, se, errno);
            if (memcmp(c, d, 100)) {
                char detail[400], u[180], w[180];
                EscapeNarrow(u, sizeof(u), c, n4 ? n4 : 1);
                EscapeNarrow(w, sizeof(w), d, n5 ? n5 : 1);
                sprintf_s(detail, "time %lld count %zu ucrt [%s] wit [%s]", value, count, u, w);
                Report("_Strftime", format, detail);
            }
        }
    }
    free(names);
    const __time64_t now = _time64(nullptr), mine = WitCrt::Time64(nullptr);
    ++compared;
    if (mine < now - 2 || mine > now + 2) {
        Report("_time64", L"", "clock");
    }
}

/* The global locale's queries, the C locale's classes, the STL's string helpers and the floating-point helpers. */
void GlobalLocaleCases()
{
    const auto check = [](const char *what, bool same) {
        ++compared;
        if (!same) {
            Report("global locale", L"", what);
        }
    };
    for (int category = LC_ALL; category <= LC_MAX; ++category) {
        const char *ucrt = setlocale(category, nullptr), *wit = WitCrt::Setlocale(category, nullptr);
        check("setlocale query", ucrt && wit && !strcmp(ucrt, wit));
        ucrt = setlocale(category, "C");
        wit = WitCrt::Setlocale(category, "C");
        check("setlocale C", ucrt && wit && !strcmp(ucrt, wit));
    }
    const lconv *a = localeconv(), *b = WitCrt::Localeconv();
    const char *const narrowA[] = {a->decimal_point, a->thousands_sep, a->grouping, a->int_curr_symbol,
        a->currency_symbol, a->mon_decimal_point, a->mon_thousands_sep, a->mon_grouping, a->positive_sign,
        a->negative_sign};
    const char *const narrowB[] = {b->decimal_point, b->thousands_sep, b->grouping, b->int_curr_symbol,
        b->currency_symbol, b->mon_decimal_point, b->mon_thousands_sep, b->mon_grouping, b->positive_sign,
        b->negative_sign};
    for (size_t i = 0; i < 10; ++i) {
        check("localeconv text", !strcmp(narrowA[i], narrowB[i]));
    }
    const char charsA[] = {a->int_frac_digits, a->frac_digits, a->p_cs_precedes, a->p_sep_by_space, a->n_cs_precedes,
        a->n_sep_by_space, a->p_sign_posn, a->n_sign_posn};
    const char charsB[] = {b->int_frac_digits, b->frac_digits, b->p_cs_precedes, b->p_sep_by_space, b->n_cs_precedes,
        b->n_sep_by_space, b->p_sign_posn, b->n_sign_posn};
    check("localeconv values", !memcmp(charsA, charsB, sizeof(charsA)));
    const wchar_t *const wideA[] = {a->_W_decimal_point, a->_W_thousands_sep, a->_W_int_curr_symbol,
        a->_W_currency_symbol, a->_W_mon_decimal_point, a->_W_mon_thousands_sep, a->_W_positive_sign,
        a->_W_negative_sign};
    const wchar_t *const wideB[] = {b->_W_decimal_point, b->_W_thousands_sep, b->_W_int_curr_symbol,
        b->_W_currency_symbol, b->_W_mon_decimal_point, b->_W_mon_thousands_sep, b->_W_positive_sign,
        b->_W_negative_sign};
    for (size_t i = 0; i < 8; ++i) {
        check("localeconv wide text", !wcscmp(wideA[i], wideB[i]));
    }
    const auto &c = WitCrt::Resolve(nullptr);
    check("___lc_codepage_func", ___lc_codepage_func() == c.Public._locale_lc_codepage);
    check("___lc_collate_cp_func", ___lc_collate_cp_func() == c.Public._locale_lc_codepage);
    check("___mb_cur_max_func", ___mb_cur_max_func() == c.Public._locale_mb_cur_max);
    wchar_t **names = ___lc_locale_name_func();
    for (int category = LC_ALL; category <= LC_MAX; ++category) {
        check("___lc_locale_name_func", !names[category]);
    }
    // Both locks are recursive: the STL's _Lockit takes the locale lock again while it holds it.
    _lock_locales();
    _lock_locales();
    _unlock_locales();
    _unlock_locales();
    WitCrt::LockLocales();
    WitCrt::LockLocales();
    WitCrt::UnlockLocales();
    WitCrt::UnlockLocales();
    ++compared;
    const unsigned short *table = __pctype_func(), *mine = WitCrt::Pctype();
    for (int value = -1; value < 256; ++value) {
        check("__pctype_func", table[value] == mine[value]);
        check("islower", islower(value) == WitCrt::Isctype(value, _LOWER));
        check("isupper", isupper(value) == WitCrt::Isctype(value, _UPPER));
        check("isspace", isspace(value) == WitCrt::Isctype(value, _SPACE));
    }
}

void HelperCases()
{
    const auto check = [](const char *what, bool same) {
        ++compared;
        if (!same) {
            Report("helpers", L"", what);
        }
    };
    char text[40];
    wchar_t wide[40];
    for (unsigned i = 0; i < 200000; ++i) {
        const size_t length = Pick(32);
        for (size_t j = 0; j < length; ++j) {
            text[j] = char('a' + Pick(6));
            wide[j] = wchar_t(Pick(3) ? L'a' + Pick(6) : Pick(0x10000));
            wide[j] = wide[j] ? wide[j] : L'z';
        }
        text[length] = 0;
        wide[length] = 0;
        const size_t count = Pick(40);
        check("__strncnt", __strncnt(text, count) == WitCrt::Strncnt(text, count));
        check("wcsnlen", wcsnlen(wide, count) == WitCrt::Wcsnlen(wide, count));
        char reject[8];
        const size_t rejected = Pick(4);
        for (size_t j = 0; j < rejected; ++j) {
            reject[j] = char('a' + Pick(8));
        }
        reject[rejected] = 0;
        check("strcspn", strcspn(text, reject) == WitCrt::Strcspn(text, reject));
        // The searches of P6.4.k3a, the terminator included.
        const int narrow = Pick(5) ? 'a' + int(Pick(8)) : int(Pick(3)) * 256;
        check("strchr", strchr(text, narrow) == WitCrt::Strchr(text, narrow));
        check("strrchr", strrchr(text, narrow) == WitCrt::Strrchr(text, narrow));
        const wchar_t value = Pick(5) ? wide[Pick(unsigned(length + 1))] : wchar_t(Pick(0x10000));
        check("wcsrchr", wcsrchr(wide, value) == WitCrt::Wcsrchr(wide, value));
        wchar_t part[6];
        const size_t start = length ? Pick(unsigned(length)) : 0, parts = Pick(5);
        for (size_t j = 0; j < parts; ++j) {
            part[j] = Pick(4) && start + j < length ? wide[start + j] : wchar_t(L'a' + Pick(6));
        }
        part[parts] = 0;
        check("wcsstr", wcsstr(wide, part) == WitCrt::Wcsstr(wide, part));
        if (i % 100 == 0) {
            wchar_t *copy = WitCrt::Wcsdup(wide), *expected = _wcsdup(wide);
            check("_wcsdup", copy && expected && !wcscmp(copy, expected) && copy != wide);
            WitCrt::Free(copy);
            free(expected);
        }
    }
    check("_wcsdup null", !WitCrt::Wcsdup(nullptr) && !_wcsdup(nullptr));
    const auto number = [&](uint64_t bits) {
        double value;
        memcpy(&value, &bits, sizeof(value));
        int e1 = 12345, e2 = 12345;
        const double a = frexp(value, &e1), b = WitCrt::Frexp(value, &e2);
        uint64_t x, y;
        memcpy(&x, &a, sizeof(x));
        memcpy(&y, &b, sizeof(y));
        // A NaN keeps its payload in both; compare the bits, and the exponent wherever UCRT sets one.
        check("frexp", x == y && e1 == e2);
        check("_dclass", _dclass(value) == WitCrt::Dclass(value));
        check("_ldclass", _ldclass(value) == WitCrt::Dclass(value));
        double copy = value;
        check("_dtest", _dtest(&copy) == WitCrt::Dclass(value));
        volatile double input = value;
        const double absolute = fabs(input), mine = WitCrt::Fabs(value);
        memcpy(&x, &absolute, sizeof(x));
        memcpy(&y, &mine, sizeof(y));
        check("fabs", x == y);
    };
    for (const long long value :
        {0LL, 1LL, -1LL, 2147483647LL, -2147483647LL - 1, 9223372036854775807LL, -9223372036854775807LL - 1}) {
        volatile long long input = value;
        volatile int narrow = int(value);
        check("llabs", llabs(input) == WitCrt::Llabs(value));
        check("abs", abs(narrow) == int(WitCrt::Llabs(int(value)) & 0xFFFFFFFF));
    }
    for (const uint64_t bits : {0ULL, 1ULL << 63, 1ULL, 0x000FFFFFFFFFFFFFULL, 0x0010000000000000ULL,
             0x3FF0000000000000ULL, 0x7FEFFFFFFFFFFFFFULL, 0x7FF0000000000000ULL, 0xFFF0000000000000ULL,
             0x7FF8000000000000ULL, 0xFFF8000000000000ULL, 0x7FF0000000000001ULL, 0x8000000000000001ULL}) {
        number(bits);
    }
    for (unsigned i = 0; i < 1000000; ++i) {
        uint64_t bits = Next();
        if (i % 4 == 0) {
            bits &= 0x800FFFFFFFFFFFFFULL; // subnormal or zero
        }
        number(bits);
    }
}

/* Locales, ceilf and the heap */

void LocaleCases()
{
    for (const char *name : {"C", ".utf8", ".UTF8", ".utf-8", ".UTF-8", "C.UTF-8"}) {
        _locale_t expected = _create_locale(LC_ALL, name), actual = WitCrt::CreateLocale(LC_ALL, name);
        ++compared;
        if (!expected != !actual) {
            Report("_create_locale", L"", name);
            continue;
        }
        if (!expected) {
            continue;
        }
        const auto *a = reinterpret_cast<const __crt_locale_data_public *>(expected->locinfo);
        const auto *b = reinterpret_cast<const __crt_locale_data_public *>(actual->locinfo);
        char detail[160];
        detail[0] = 0;
        if (a->_locale_mb_cur_max != b->_locale_mb_cur_max || a->_locale_lc_codepage != b->_locale_lc_codepage) {
            sprintf_s(detail, "%s data %d/%u wit %d/%u", name, a->_locale_mb_cur_max, a->_locale_lc_codepage,
                b->_locale_mb_cur_max, b->_locale_lc_codepage);
        }
        for (int c = -1; c < 256 && !detail[0]; ++c) {
            if (a->_locale_pctype[c] != b->_locale_pctype[c]) {
                sprintf_s(detail, "%s ctype[%d] %04X wit %04X", name, c, a->_locale_pctype[c], b->_locale_pctype[c]);
            }
        }
        if (detail[0]) {
            Report("_create_locale", L"", detail);
        }
        _free_locale(expected);
        WitCrt::FreeLocale(actual);
    }
    ++compared;
    if (_create_locale(LC_ALL, nullptr) || WitCrt::CreateLocale(LC_ALL, nullptr)) {
        Report("_create_locale", L"", "null name");
    }
}

void CeilCases()
{
    const auto check = [](uint32_t bits) {
        float value;
        memcpy(&value, &bits, sizeof(value));
        const float a = ceilf(value), b = WitCrt::Ceilf(value);
        uint32_t x, y;
        memcpy(&x, &a, sizeof(x));
        memcpy(&y, &b, sizeof(y));
        ++compared;
        if (x != y) {
            char detail[64];
            sprintf_s(detail, "%08X -> %08X / %08X", bits, x, y);
            Report("ceilf", L"", detail);
        }
    };
    for (const uint32_t bits :
        {0x00000000u, 0x80000000u, 0x3F800000u, 0xBF800000u, 0x3F000000u, 0xBF000000u, 0x3F7FFFFFu, 0xBF7FFFFFu,
            0x00000001u, 0x80000001u, 0x007FFFFFu, 0x4B000000u, 0x4AFFFFFFu, 0xCAFFFFFFu, 0x4B7FFFFFu, 0x7F7FFFFFu,
            0xFF7FFFFFu, 0x7F800000u, 0xFF800000u, 0x7FC00000u, 0xFFC00000u, 0x7F800001u, 0xFF800001u, 0x7FBFFFFFu}) {
        check(bits);
    }
    for (unsigned i = 0; i < 2000000; ++i) {
        check(uint32_t(Next()));
    }
}

void HeapCases()
{
    const auto check = [](const char *what, bool same) {
        ++compared;
        if (!same) {
            Report("heap", L"", what);
        }
    };
    errno = 0;
    void *a = malloc(SIZE_MAX);
    const int ae = errno;
    errno = 0;
    void *b = WitCrt::Malloc(SIZE_MAX);
    check("malloc huge", !a && !b && ae == errno);
    a = malloc(0);
    b = WitCrt::Malloc(0);
    check("malloc zero", a && b);
    free(a);
    WitCrt::Free(b);
    auto *block = static_cast<unsigned char *>(WitCrt::Malloc(100));
    for (int i = 0; i < 100; ++i) {
        block[i] = static_cast<unsigned char>(i);
    }
    errno = 0;
    check("realloc huge", !WitCrt::Realloc(block, SIZE_MAX) && errno == ENOMEM && block[99] == 99);
    block = static_cast<unsigned char *>(WitCrt::Realloc(block, 100000));
    bool same = block != nullptr;
    for (int i = 0; same && i < 100; ++i) {
        same = block[i] == i;
    }
    check("realloc grow", same);
    check("realloc zero", !WitCrt::Realloc(block, 0));
    block = static_cast<unsigned char *>(WitCrt::Realloc(nullptr, 10));
    check("realloc null", block != nullptr);
    WitCrt::Free(block);
    // calloc: a product beyond the largest request fails with ENOMEM; zero still allocates; blocks come zeroed even
    // where the heap reuses memory a dirty block used.
    for (const size_t count : {size_t(SIZE_MAX / 2), size_t(_HEAP_MAXREQ / 4 + 1), size_t(2)}) {
        const size_t size = count == 2 ? SIZE_MAX / 2 : 4;
        errno = 0;
        a = calloc(count, size);
        const int ce = errno;
        errno = 0;
        b = WitCrt::Calloc(count, size);
        check("calloc overflow", !a && !b && ce == errno);
    }
    a = calloc(0, 0);
    b = WitCrt::Calloc(0, 0);
    check("calloc zero", a && b);
    free(a);
    WitCrt::Free(b);
    for (size_t round = 0; round < 64; ++round) {
        const size_t count = 1 + round * 37 % 300, size = 1 + round % 7;
        auto *dirty = static_cast<unsigned char *>(WitCrt::Malloc(count * size));
        for (size_t i = 0; i < count * size; ++i) {
            dirty[i] = 0xA5;
        }
        WitCrt::Free(dirty);
        auto *clean = static_cast<unsigned char *>(WitCrt::Calloc(count, size));
        bool zero = clean != nullptr;
        for (size_t i = 0; zero && i < count * size; ++i) {
            zero = !clean[i];
        }
        check("calloc zeroed", zero);
        WitCrt::Free(clean);
    }
    WitCrt::Free(nullptr);
}

} // namespace

/* The rest of the C runtime CoreCLR calls (P6.4.k3a2) */

int __cdecl CompareInts(const void *first, const void *second)
{
    const int a = *static_cast<const int *>(first), b = *static_cast<const int *>(second);
    return a < b ? -1 : a > b ? 1 : 0;
}

void CoreClrCases()
{
    const auto check = [](const char *what, bool same) {
        ++compared;
        if (!same) {
            Report("coreclr", L"", what);
        }
    };
    // Every class and case UCRT's tables answer, and every wide character, which the hosted platform classifies as
    // Windows does.
    for (int value = -1; value <= 255; ++value) {
        check("isalpha", isalpha(value) == WitCrt::Isalpha(value));
        check("isdigit", isdigit(value) == WitCrt::Isdigit(value));
    }
    for (unsigned value = 0; value <= 0xFFFF; ++value) {
        const wint_t c = wint_t(value);
        check("iswalpha", iswalpha(c) == WitCrt::Iswctype(c, _ALPHA));
        check("iswspace", iswspace(c) == WitCrt::Iswctype(c, _SPACE));
        check("iswupper", iswupper(c) == WitCrt::Iswctype(c, _UPPER));
        check("iswascii", iswascii(c) == WitCrt::Iswascii(c));
        check("towlower", towlower(c) == WitCrt::Towlower(c));
        check("towupper", towupper(c) == WitCrt::Towupper(c));
    }
    // Integers: white space, signs, prefixes, digits of every base, overflow and the end of what was read.
    static const char narrowLetters[] = " \t\v\r-+0123456789abcdefxXzZ7";
    static const wchar_t wideLetters[] = {
        L' ', L'\t', L'-', L'+', L'0', L'1', L'7', L'9', L'a', L'f', L'x', L'X', L'z', 0x0660, 0x0669, 0x3000, 0x00A0};
    static const int bases[] = {0, 2, 8, 10, 16, 36};
    for (unsigned i = 0; i < 300000; ++i) {
        char text[32];
        const size_t length = Pick(Pick(4) ? 12 : 30);
        for (size_t j = 0; j < length; ++j) {
            text[j] = narrowLetters[Pick(sizeof(narrowLetters) - 1)];
        }
        text[length] = 0;
        const int base = bases[Pick(6)];
        char *end1 = nullptr, *end2 = nullptr;
        errno = 0;
        const long a = strtol(text, &end1, base);
        const int e1 = errno;
        errno = 0;
        const long b = WitCrt::Strtol(text, &end2, base);
        check("strtol", a == b && end1 == end2 && e1 == errno);
        errno = 0;
        const long c = atol(text);
        const int e3 = errno;
        errno = 0;
        check("atol", c == WitCrt::Atol(text) && e3 == errno);
        errno = 0;
        const long long d = _atoi64(text);
        const int e4 = errno;
        errno = 0;
        check("_atoi64", d == WitCrt::Atoi64(text) && e4 == errno);
        wchar_t wide[32];
        for (size_t j = 0; j < length; ++j) {
            wide[j] = wideLetters[Pick(sizeof(wideLetters) / sizeof(wideLetters[0]))];
        }
        wide[length] = 0;
        wchar_t *wend1 = nullptr, *wend2 = nullptr;
        errno = 0;
        const unsigned long long x = _wcstoui64(wide, &wend1, base);
        const int e5 = errno;
        errno = 0;
        const unsigned long long y = WitCrt::Wcstoui64(wide, &wend2, base);
        check("_wcstoui64", x == y && wend1 == wend2 && e5 == errno);
    }
    const long specials[] = {0, 1, -1, 7, -7, LONG_MAX, LONG_MIN, 123456789, -987654321};
    for (unsigned i = 0; i < 20000; ++i) {
        const long value = i < 9 * 35 ? specials[i % 9] : long(Next());
        const int radix = 2 + int(i % 35);
        wchar_t expected[70], actual[70];
        const errno_t r1 = _ltow_s(value, expected, 70, radix), r2 = WitCrt::Ltow_s(value, actual, 70, radix);
        check("_ltow_s", r1 == r2 && !wcscmp(expected, actual));
    }
    // The secure copies and appends where the result fits, and the truncating forms.
    for (unsigned i = 0; i < 100000; ++i) {
        char source[20], prefix[8];
        const size_t length = Pick(16), prefixLength = Pick(6);
        for (size_t j = 0; j < length; ++j) {
            source[j] = char('a' + Pick(26));
        }
        source[length] = 0;
        for (size_t j = 0; j < prefixLength; ++j) {
            prefix[j] = char('A' + Pick(26));
        }
        prefix[prefixLength] = 0;
        char a[48], b[48];
        const size_t fits = length + 1 + Pick(8), joined = prefixLength + length + 1 + Pick(8);
        memset(a, '#', sizeof(a));
        memset(b, '#', sizeof(b));
        check("strcpy_s", strcpy_s(a, fits, source) == WitCrt::Strcpy_s(b, fits, source) && !memcmp(a, b, sizeof(a)));
        memset(a, '#', sizeof(a));
        memset(b, '#', sizeof(b));
        memcpy(a, prefix, prefixLength + 1);
        memcpy(b, prefix, prefixLength + 1);
        check(
            "strcat_s", strcat_s(a, joined, source) == WitCrt::Strcat_s(b, joined, source) && !memcmp(a, b, sizeof(a)));
        const size_t count = Pick(4) ? Pick(unsigned(length + 3)) : _TRUNCATE;
        const size_t copied = count == _TRUNCATE ? length : (count < length ? count : length);
        const size_t size = count == _TRUNCATE ? 1 + Pick(unsigned(length + 4)) : copied + 1 + Pick(6);
        memset(a, '#', sizeof(a));
        memset(b, '#', sizeof(b));
        check("strncpy_s",
            strncpy_s(a, size, source, count) == WitCrt::Strncpy_s(b, size, source, count) && !memcmp(a, b, sizeof(a)));
        memset(a, '#', sizeof(a));
        memset(b, '#', sizeof(b));
        memcpy(a, prefix, prefixLength + 1);
        memcpy(b, prefix, prefixLength + 1);
        const size_t room =
            count == _TRUNCATE ? prefixLength + 1 + Pick(unsigned(length + 4)) : prefixLength + copied + 1 + Pick(6);
        check("strncat_s",
            strncat_s(a, room, source, count) == WitCrt::Strncat_s(b, room, source, count) && !memcmp(a, b, sizeof(a)));
        wchar_t wsource[20], wprefix[8], wa[48], wb[48];
        for (size_t j = 0; j <= length; ++j) {
            wsource[j] = wchar_t((unsigned char)source[j]);
        }
        for (size_t j = 0; j <= prefixLength; ++j) {
            wprefix[j] = wchar_t((unsigned char)prefix[j]);
        }
        wmemset(wa, L'#', 48);
        wmemset(wb, L'#', 48);
        check("wcscpy_s", wcscpy_s(wa, fits, wsource) == WitCrt::Wcscpy_s(wb, fits, wsource) && !wmemcmp(wa, wb, 48));
        wmemset(wa, L'#', 48);
        wmemset(wb, L'#', 48);
        wmemcpy(wa, wprefix, prefixLength + 1);
        wmemcpy(wb, wprefix, prefixLength + 1);
        check(
            "wcscat_s", wcscat_s(wa, joined, wsource) == WitCrt::Wcscat_s(wb, joined, wsource) && !wmemcmp(wa, wb, 48));
        wmemset(wa, L'#', 48);
        wmemset(wb, L'#', 48);
        check("wcsncpy_s",
            wcsncpy_s(wa, size, wsource, count) == WitCrt::Wcsncpy_s(wb, size, wsource, count) && !wmemcmp(wa, wb, 48));
        wmemset(wa, L'#', 48);
        wmemset(wb, L'#', 48);
        wmemcpy(wa, wprefix, prefixLength + 1);
        wmemcpy(wb, wprefix, prefixLength + 1);
        check("wcsncat_s",
            wcsncat_s(wa, room, wsource, count) == WitCrt::Wcsncat_s(wb, room, wsource, count) && !wmemcmp(wa, wb, 48));
        // Lengths, copies, case and comparison.
        const size_t limit = Pick(20);
        check("strnlen", strnlen(source, limit) == WitCrt::Strnlen(source, limit));
        char *copy1 = _strdup(source), *copy2 = WitCrt::Strdup(source);
        check("_strdup", copy1 && copy2 && !strcmp(copy1, copy2));
        free(copy1);
        WitCrt::Free(copy2);
        char mixed1[24], mixed2[24];
        for (size_t j = 0; j < length; ++j) {
            mixed1[j] = Pick(2) ? char(toupper(source[j])) : source[j];
        }
        mixed1[length] = 0;
        memcpy(mixed2, mixed1, length + 1);
        check("_strupr_s",
            _strupr_s(mixed1, length + 1 + Pick(4)) == WitCrt::Strupr_s(mixed2, length + 1) && !strcmp(mixed1, mixed2));
        check("_strnicmp",
            _strnicmp(source, prefix, limit) == WitCrt::Strnicmp(source, prefix, limit) &&
                _strnicmp(source, mixed1, limit) == WitCrt::Strnicmp(source, mixed1, limit));
        wchar_t wmixed1[24], wmixed2[24];
        for (size_t j = 0; j <= length; ++j) {
            wmixed1[j] = wmixed2[j] = wchar_t((unsigned char)mixed1[j]);
        }
        check("_wcslwr_s",
            _wcslwr_s(wmixed1, length + 1) == WitCrt::Wcslwr_s(wmixed2, length + 1) && !wcscmp(wmixed1, wmixed2));
    }
    // Tokens: the offsets of every token and of the context, over the same buffers.
    for (unsigned i = 0; i < 50000; ++i) {
        char text1[32], text2[32], delimiters[5];
        const size_t length = Pick(30), count = Pick(4);
        for (size_t j = 0; j < length; ++j) {
            text1[j] = "ab,; c"[Pick(6)];
        }
        text1[length] = 0;
        memcpy(text2, text1, length + 1);
        for (size_t j = 0; j < count; ++j) {
            delimiters[j] = ",; "[Pick(3)];
        }
        delimiters[count] = 0;
        char *context1 = nullptr, *context2 = nullptr;
        char *token1 = strtok_s(text1, delimiters, &context1), *token2 = WitCrt::Strtok_s(text2, delimiters, &context2);
        for (int round = 0; round < 40; ++round) {
            const bool same = (!token1 && !token2) || (token1 && token2 && token1 - text1 == token2 - text2);
            check("strtok_s", same && context1 - text1 == context2 - text2 && !memcmp(text1, text2, length + 1));
            if (!token1 || !token2) {
                break;
            }
            token1 = strtok_s(nullptr, delimiters, &context1);
            token2 = WitCrt::Strtok_s(nullptr, delimiters, &context2);
        }
    }
    // The environment as the process started with it.
    for (const char *name : {"PATH", "path", "Path", "WINDIR", "SystemRoot", "TEMP", "NO_SUCH_WITOS_VARIABLE", "PAT",
             "", "=C:", "=Z:", "ComSpec"}) {
#pragma warning(suppress : 4996) // getenv itself is what is compared
        const char *expected = getenv(name);
        const char *actual = WitCrt::Getenv(name);
        check("getenv", (!expected && !actual) || (expected && actual && !strcmp(expected, actual)));
    }
    // Sorting, duplicates included.
    for (unsigned i = 0; i < 2000; ++i) {
        int first[64], second[64];
        const size_t count = Pick(64);
        for (size_t j = 0; j < count; ++j) {
            first[j] = second[j] = int(Pick(Pick(2) ? 8 : 1000)) - 4;
        }
        qsort(first, count, sizeof(int), CompareInts);
        WitCrt::Qsort(second, count, sizeof(int), CompareInts);
        check("qsort", !memcmp(first, second, count * sizeof(int)));
    }
}

int wmain(int count, wchar_t **arguments)
{
    if (count < 2) {
        puts("usage: crt-differential <scratch directory> [--verbose]");
        return 2;
    }
    verbose = count > 2 && !wcscmp(arguments[2], L"--verbose");
    // A drive's current directory, as a command shell records it, before either narrow environment is made: getenv
    // leaves such entries out.
    SetEnvironmentVariableW(L"=Z:", L"Z:\\witos");
    _set_invalid_parameter_handler(Handler);
    setvbuf(stdout, nullptr, _IONBF, 0); // the phase lines survive a crash
    const Locales c = {nullptr, nullptr};
    const Locales utf8 = {_create_locale(LC_ALL, ".utf8"), WitCrt::CreateLocale(LC_ALL, ".utf8")};
    if (!utf8.Ucrt || !utf8.Wit) {
        puts("FAIL: no UTF-8 locale");
        return 1;
    }
    {
        wchar_t probe[8];
        const uint64_t text = (uint64_t)(uintptr_t)"abc";
        const int counted = UcrtPrint(0x24, nullptr, 0, L"<%.1hs>", utf8.Ucrt, text, 0, 0, 0);
        const bool countCrashed = crashed != nullptr;
        crashed = nullptr;
        const int written = UcrtPrint(0x24, probe, 8, L"<%.1hs>", utf8.Ucrt, text, 0, 0, 0);
        precisionDefect = countCrashed || crashed || counted != 3 || written != 3 || wcscmp(probe, L"<a>");
        crashed = nullptr;
        printf(
            "UCRT %s a narrow string with a precision in the UTF-8 locale\n", precisionDefect ? "fails" : "converts");
    }
    PrintCases(c, utf8);
    printf("print done: %llu compared, %llu skipped\n", compared, skipped);
    StreamCases(arguments[1], utf8.Ucrt, utf8.Wit);
    printf("streams done: %llu compared\n", compared);
    StringCases();
    printf("strings done: %llu compared\n", compared);
    TimeCases();
    printf("time done: %llu compared\n", compared);
    LocaleCases();
    GlobalLocaleCases();
    HelperCases();
    printf("locales and helpers done: %llu compared\n", compared);
    CeilCases();
    HeapCases();
    CoreClrCases();
    printf("coreclr functions done: %llu compared\n", compared);
    printf("%s: %llu compared, %llu skipped as invalid, %llu skipped for UCRT's precision defect, %llu failed\n",
        failures ? "FAIL" : "PASS", compared, skipped, precisionSkipped, failures);
    return failures ? 1 : 0;
}
