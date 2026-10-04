#define _CRT_SECURE_NO_WARNINGS // the scenarios call the legacy functions the host calls
#include <ctype.h>
#include <errno.h>
#include <limits.h>
#include <locale.h>
#include <math.h>
#include <share.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <wchar.h>
#include <initializer_list>

/* Scenarios of the UCRT subset (P6.4.h) through the public UCRT functions and the inline functions of its headers,
 * as hostfxr and hostpolicy compile against them. The same source runs on UCRT (hosted reference), on the WitOS
 * subset without UCRT (hosted) and in the guest; every run must trace the same tokens. Wide text is traced as ASCII
 * with _ for a space and \uXXXX for anything else. The standard output and standard error lines are checked by the
 * runners. */
extern "C" void crt_trace(const char *text);

namespace {

char token[512];
unsigned length;

void Begin(const char *name)
{
    length = 0;
    while (*name && length < sizeof(token) - 2) {
        token[length++] = *name++;
    }
    token[length++] = ':';
    token[length] = 0;
}

void Add(char value)
{
    if (length < sizeof(token) - 1) {
        token[length++] = value;
        token[length] = 0;
    }
}

void AddNumber(long long value)
{
    if (value < 0) {
        Add('-');
    }
    unsigned long long rest = value < 0 ? 0 - (unsigned long long)value : (unsigned long long)value;
    char digits[24];
    int count = 0;
    do {
        digits[count++] = char('0' + rest % 10);
        rest /= 10;
    } while (rest);
    while (count) {
        Add(digits[--count]);
    }
}

void AddWide(const wchar_t *text, size_t count)
{
    for (size_t i = 0; i < count; ++i) {
        const unsigned value = text[i];
        if (value == ' ') {
            Add('_');
        } else if (value > ' ' && value < 0x7F) {
            Add(char(value));
        } else {
            static const char hex[] = "0123456789ABCDEF";
            Add('\\');
            Add('u');
            for (int shift = 12; shift >= 0; shift -= 4) {
                Add(hex[(value >> shift) & 0xF]);
            }
        }
    }
}

void Number(const char *name, long long value)
{
    Begin(name);
    AddNumber(value);
    crt_trace(token);
}

/* A result, the errno it left and the wide text, up to its terminator or `count` characters. */
void Result(const char *name, long long result, const wchar_t *text, size_t count = 100)
{
    Begin(name);
    AddNumber(result);
    Add(':');
    AddNumber(errno);
    Add(':');
    size_t used = 0;
    while (used < count && text[used]) {
        ++used;
    }
    AddWide(text, used);
    crt_trace(token);
}

void Formatting()
{
    wchar_t buffer[96];
    errno = 0;
    int result = _snwprintf_s(buffer, 96, _TRUNCATE, L"%s|%hs|%S|%c|%C|%5d|%-5x|%#o|%+i|%p|%%|%05d|%.3d|%lld|%I64x",
        L"wide", "narrow", "N", L'w', 'n', 42, 255, 8, 7, (void *)0x1234, -42, 5, -1234567890123LL, 0xABCDEF012345ULL);
    Result("f1", result, buffer);
    result = _snwprintf_s(buffer, 96, _TRUNCATE, L"[%*d][%-*d][%.*d][%-8.3x][%hhd][%hu][%zu][%#X][%ls][%.2s][%6hs]", 6,
        1, 4, 2, 4, 3, 0xab, 300, 70000, (size_t)-1, 0xBEEFu, L"long", L"precise", "pad");
    Result("f2", result, buffer);
    result = _snwprintf_s(buffer, 6, _TRUNCATE, L"%d", 1234567);
    Result("f3", result, buffer);
    result = _snwprintf_s(buffer, 8, 3, L"%ls", L"abcdef");
    Result("f4", result, buffer);
    for (int i = 0; i < 8; ++i) {
        buffer[i] = L'#';
    }
    result = swprintf(buffer, 4, L"%d", 12345);
    Result("f5", result, buffer, 8);
    for (int i = 0; i < 8; ++i) {
        buffer[i] = L'#';
    }
    result = _snwprintf(buffer, 5, L"%d", 12345);
    Result("f6", result, buffer, 7);
    result = _snwprintf(buffer, 8, L"%d", 123);
    Result("f7", result, buffer);
    Number("f8", _scwprintf(L"%ls-%d-%hs", L"ab", 7, "xyz"));
    result = swprintf(buffer, 96, L"[%s][%hs][%c]", (const wchar_t *)nullptr, (const char *)nullptr, 0x00E9);
    Result("f9", result, buffer);
    result = _snwprintf_s(buffer, 96, _TRUNCATE, L"[%hs][%hc][%C]", "\xE9\xFF", 0xC4, 0x80);
    Result("f10", result, buffer);

    _locale_t utf8 = _create_locale(LC_ALL, ".utf8");
    result = _snwprintf_s_l(buffer, 96, _TRUNCATE, L"[%hs][%.2hs][%5hs]", utf8, "\xC3\xA9\xE2\x82\xAC\xF0\x9F\x98\x80",
        "\xC3\xA9\xE2\x82\xAC", "\xC3\xA9");
    Result("u1", result, buffer);
    result = _snwprintf_s_l(buffer, 96, _TRUNCATE, L"[%hs]", utf8, "\xC3");
    Result("u2", result, buffer);
    errno = 0;
    result = _snwprintf_s_l(buffer, 96, _TRUNCATE, L"[%hc]", utf8, 0xC3);
    Result("u3", result, buffer);
    _free_locale(utf8);
}

void Strings()
{
    errno = 0;
    Number("s1", (long long)wcslen(L"hostfxr"));
    Number("s2", wcscmp(L"abc", L"abd"));
    Number("s3", wcscmp(L"abd", L"abc") > 0);
    Number("s4", wcsncmp(L"abcx", L"abcy", 3));
    Number("s5", wcsncmp(L"abcx", L"abcy", 4) < 0);
    const wchar_t *path = L"C:\\dotnet\\host";
    Number("s6", (long long)(wcschr(path, L'\\') - path));
    Number("s7", wcschr(path, L'/') == nullptr);
    Number("s8", (long long)(wcschr(path, 0) - path));
    Number("s9", _wcsicmp(L"HostFXR", L"hostfxr"));
    Number("s10", _wcsicmp(L"ABC", L"abd") < 0);
    Number("s11", _wcsnicmp(L"Microsoft.NETCore.App", L"MICROSOFT.netcore.app", 21));
    Number("s12", _wcsicmp(L"\x00C9", L"\x00E9") != 0);
    Number("s13", tolower('Q'));
    Number("s14", toupper('q'));
    Number("s15", tolower(0xC9));
    Number("s16", _wtoi(L"  -123x"));
    errno = 0;
    Number("s17", _wtoi(L"99999999999"));
    Number("s18", errno);
    wchar_t *end = nullptr;
    const wchar_t *hex = L" 0x1fZ";
    errno = 0;
    Number("s19", (long long)wcstoul(hex, &end, 0));
    Number("s20", (long long)(end - hex));
    Number("s21", (long long)wcstoul(L"\x0663\x0664", nullptr, 10));
    Number("s22", (long long)wcstoul(L"-1", nullptr, 10));
    Number("s23", (long long)wcstoul(L"4294967296", &end, 10));
    Number("s24", errno);
    Number("s25", (long long)wcstoul(L"\x3000+17", nullptr, 10));
    wchar_t message[64];
    Number("e1", _wcserror_s(message, 64, ENOENT));
    Result("e2", 0, message);
    _wcserror_s(message, 8, EACCES);
    Result("e3", 0, message);
    _wcserror_s(message, 64, 999);
    Result("e4", 0, message);
    _wcserror_s(message, 64, ETIMEDOUT);
    Result("e5", 0, message);
}

void Time()
{
    struct tm time;
    __time64_t value = 1759587381; // 2025-10-04 14:16:21 UTC
    Number("t1", _gmtime64_s(&time, &value));
    Begin("t2");
    for (const int field : {time.tm_year, time.tm_mon, time.tm_mday, time.tm_hour, time.tm_min, time.tm_sec,
             time.tm_wday, time.tm_yday, time.tm_isdst}) {
        AddNumber(field);
        Add(',');
    }
    crt_trace(token);
    wchar_t buffer[100];
    errno = 0;
    size_t count = wcsftime(buffer, 100, L"%c GMT", &time); // pal::get_timestamp
    Result("t3", (long long)count, buffer);
    count = wcsftime(buffer, 100, L"%Y-%m-%dT%H:%M:%S|%j|%U|%W|%V|%G|%u|%I%p|%#x|%#d", &time);
    Result("t4", (long long)count, buffer);
    count = wcsftime(buffer, 8, L"%A %B", &time);
    Result("t5", (long long)count, buffer);
    value = -43201;
    errno = 0;
    Number("t6", _gmtime64_s(&time, &value));
    Number("t7", time.tm_year);
    value = -1;
    Number("t8", _gmtime64_s(&time, &value));
    Number("t9", time.tm_yday);
}

void Memory()
{
    Number("m1", malloc(0) != nullptr); // freed with the process
    auto *block = static_cast<unsigned char *>(malloc(16));
    for (int i = 0; i < 16; ++i) {
        block[i] = static_cast<unsigned char>(i * 7);
    }
    block = static_cast<unsigned char *>(realloc(block, 4096));
    int same = 1;
    for (int i = 0; i < 16; ++i) {
        same &= block[i] == static_cast<unsigned char>(i * 7);
    }
    Number("m2", same);
    Number("m3", realloc(block, 0) == nullptr);
    free(nullptr);
    _locale_t c = _create_locale(LC_ALL, "C"), utf8 = _create_locale(LC_ALL, ".UTF-8");
    const auto *cData = reinterpret_cast<const __crt_locale_data_public *>(c->locinfo);
    const auto *utf8Data = reinterpret_cast<const __crt_locale_data_public *>(utf8->locinfo);
    Begin("l1");
    for (const long long field : {(long long)cData->_locale_mb_cur_max, (long long)cData->_locale_lc_codepage,
             (long long)utf8Data->_locale_mb_cur_max, (long long)utf8Data->_locale_lc_codepage,
             (long long)cData->_locale_pctype['A'], (long long)utf8Data->_locale_pctype['\t'],
             (long long)utf8Data->_locale_pctype[0xE9]}) {
        AddNumber(field);
        Add(',');
    }
    crt_trace(token);
    Number("l2", _create_locale(LC_ALL, "C.UTF-8") == nullptr);
    _free_locale(utf8);
    _free_locale(c);
    volatile float value = 1.25f;
    Number("c1", (long long)ceilf(value));
    value = -1.5f;
    Number("c2", (long long)ceilf(value));
    value = -0.25f;
    const float zero = ceilf(value);
    unsigned bits;
    memcpy(&bits, &zero, sizeof(bits));
    Number("c3", (long long)bits);
}

/* Prints the lines "[CRT-STDOUT] wide narrow 42", "[CRT-STDERR] err" and "[CRT-FWRITE]", which the runners check. */
void Streams()
{
    errno = 0;
    Number("o1", (long long)*__local_stdio_printf_options());
    Number("o2", (long long)*__local_stdio_scanf_options());
    Number("o3", setvbuf(stdout, nullptr, _IONBF, 0));
    Number("o4", fwprintf(stdout, L"[CRT-STDOUT] %s %hs %d\n", L"wide", "narrow", 42));
    Number("o5", fputwc(L'.', stdout));
    Number("o6", fputwc(L'\n', stdout));
    Number("o7", fflush(stdout));
    Number("o8", fflush(nullptr));
    Number("o9", fwprintf(stderr, L"[CRT-STDERR] %ls\n", L"err"));
    errno = 0;
    Number("o10", fputwc(0x20AC, stdout) == WEOF);
    Number("o11", errno);
    errno = 0;
    Number("o12", fwprintf(stdout, L"%s\n", L"\x20AC"));
    Number("o13", errno);
    Number("o14", (long long)fwrite("[CRT-FWRITE]\n", 1, 13, stdout));
}

} // namespace

extern "C" void crt_scenarios_run()
{
    Streams();
    Formatting();
    Strings();
    Time();
    Memory();
}
