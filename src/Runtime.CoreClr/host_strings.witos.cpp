// Actual corehost PAL signatures (P6.4.j2): the conversions between the host's UTF-16 strings and the UTF-8 strings
// the runtime takes, which the Windows PAL makes with the code page conversions, and the integer parse of the trace
// settings. The guest's conversions are the native encoding adapter's, which convert as Windows does: lengths include
// the terminator, and ill-formed UTF-16 becomes U+FFFD.
#pragma warning(push)
#pragma warning(disable : 4100) // Upstream inline mkdir intentionally ignores mode on Windows.
#include "pal.h"
#pragma warning(pop)

size_t pal::pal_utf8string(const string_t &str, char *out_buffer, size_t len)
{
    const size_t size = (size_t)::WideCharToMultiByte(CP_UTF8, 0, str.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (size == 0 || size > len) {
        return size; // what the caller needs, or 0 on failure
    }
    return (size_t)::WideCharToMultiByte(CP_UTF8, 0, str.c_str(), -1, out_buffer, (int)len, nullptr, nullptr);
}

bool pal::pal_utf8string(const string_t &str, std::vector<char> *out)
{
    out->clear();
    const size_t size = (size_t)::WideCharToMultiByte(CP_UTF8, 0, str.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (size == 0) {
        return false;
    }
    out->resize(size, '\0');
    return ::WideCharToMultiByte(CP_UTF8, 0, str.c_str(), -1, out->data(), (int)out->size(), nullptr, nullptr) != 0;
}

bool pal::pal_clrstring(const string_t &str, std::vector<char> *out)
{
    return pal_utf8string(str, out);
}

// Without the terminator, so the result holds exactly the converted characters.
bool pal::clr_palstring(const char *cstr, string_t *out)
{
    out->clear();
    const int length = (int)::strlen(cstr);
    const int size = ::MultiByteToWideChar(CP_UTF8, 0, cstr, length, nullptr, 0);
    if (size == 0) {
        return false;
    }
    out->resize((size_t)size, '\0');
    return ::MultiByteToWideChar(CP_UTF8, 0, cstr, length, &(*out)[0], size) != 0;
}

int pal::xtoi(const char_t *input)
{
    return ::_wtoi(input);
}
