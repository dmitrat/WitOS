// Actual corehost PAL signatures (P6.4.j2): the host's trace and error output and the trace's timestamp. The Windows
// PAL writes UTF-16 to a console with WriteConsoleW, and otherwise formats into the C runtime's stream with a UTF-8
// locale. The guest's console is no Windows console: its standard streams take UTF-8 bytes, so every line takes the
// stream path, which writes the same bytes the Windows PAL writes when its output is redirected.
#pragma warning(push)
#pragma warning(disable : 4100) // Upstream inline mkdir intentionally ignores mode on Windows.
#include "pal.h"
#pragma warning(pop)
#include <ctime>
#include <locale.h>

void pal::file_vprintf(FILE *f, const char_t *format, va_list vl)
{
    _locale_t locale = ::_create_locale(LC_ALL, ".utf8");
    ::_vfwprintf_l(f, format, locale, vl);
    ::fputwc(_X('\n'), f);
    ::_free_locale(locale);
}

namespace {
void print_line(FILE *stream, const pal::char_t *format, ...)
{
    va_list args;
    va_start(args, format);
    pal::file_vprintf(stream, format, args);
    va_end(args);
}
} // namespace

void pal::err_print_line(const char_t *message)
{
    print_line(stderr, _X("%s"), message);
}

void pal::out_vprint_line(const char_t *format, va_list vl)
{
    va_list copy;
    va_copy(copy, vl);
    const int length = 1 + pal::strlen_vprintf(format, copy); // with the terminator
    va_end(copy);
    if (length < 1) {
        return;
    }
    std::vector<char_t> buffer((size_t)length);
    if (pal::str_vprintf(&buffer[0], (size_t)length, format, vl) != length - 1) {
        return;
    }
    print_line(stdout, _X("%s"), &buffer[0]);
}

// UTC time, as the Windows PAL formats it; the guest has no UTC clock and says so rather than invent a time.
pal::string_t pal::get_timestamp()
{
    const std::time_t now = std::time(nullptr);
    tm utc{};
    if (now == (std::time_t)-1 || ::gmtime_s(&utc, &now) != 0) {
        return _X("(no UTC clock)");
    }
    char_t text[100];
    std::wcsftime(text, sizeof(text) / sizeof(text[0]), _X("%c GMT"), &utc);
    return string_t(text);
}
