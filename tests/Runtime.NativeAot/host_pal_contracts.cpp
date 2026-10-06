#pragma warning(push)
#pragma warning(disable : 4100) // Upstream inline mkdir intentionally ignores mode on Windows.
#include "pal.h"
#pragma warning(pop)
#include <cstdio>
#include <cstring>
#include <fcntl.h>
#include <io.h>

/* The rest of the corehost PAL (P6.4.j2) on Windows, the package through the syscall model: string conversions as
 * the Windows PAL makes them, the integer parse, trace output, the timestamp and the WitOS installation policy. The
 * output lines also go to the harness, which checks their UTF-8 bytes. */
namespace {
void write_line(FILE *stream, const pal::char_t *format, ...)
{
    va_list args;
    va_start(args, format);
    pal::file_vprintf(stream, format, args);
    va_end(args);
}

void out_line(const pal::char_t *format, ...)
{
    va_list args;
    va_start(args, format);
    pal::out_vprint_line(format, args);
    va_end(args);
}
} // namespace

extern "C" int host_pal_contracts()
{
    // UTF-16 to UTF-8 with the terminator; a lone surrogate becomes U+FFFD, as Windows converts it.
    pal::string_t text = L"a\u03BB";
    text += (wchar_t)0xD83D;
    text += (wchar_t)0xDE42;
    text += (wchar_t)0xD800;
    text += L"z";
    const char expected[] = "a\xCE\xBB\xF0\x9F\x99\x82\xEF\xBF\xBDz";
    std::vector<char> utf8;
    if (!pal::pal_utf8string(text, &utf8) ||
        utf8.size() != sizeof(expected) ||
        memcmp(utf8.data(), expected, sizeof(expected))) {
        return 101;
    }
    char shortBuffer[4] = {1, 1, 1, 1};
    if (pal::pal_utf8string(text, shortBuffer, sizeof(shortBuffer)) != sizeof(expected) || shortBuffer[0] != 1) {
        return 102;
    }
    char exact[sizeof(expected)];
    if (pal::pal_utf8string(text, exact, sizeof(exact)) != sizeof(expected) ||
        memcmp(exact, expected, sizeof(expected))) {
        return 103;
    }
    std::vector<char> clr;
    if (!pal::pal_clrstring(text, &clr) || clr != utf8) {
        return 104;
    }
    pal::string_t back;
    if (!pal::clr_palstring("a\xCE\xBB\xF0\x9F\x99\x82", &back) || back != L"a\u03BB\xD83D\xDE42") {
        return 105;
    }
    back = L"keep";
    if (pal::clr_palstring("", &back) || !back.empty()) {
        return 106; // nothing converts, which the Windows PAL reports as failure
    }
    if (pal::xtoi(L" 42") != 42 || pal::xtoi(L"-17x") != -17 || pal::xtoi(L"x") != 0) {
        return 107;
    }

    // A formatted line in UTF-8 with its newline, in a text-mode stream as the standard streams and the trace file are:
    // UCRT converts wide output to the locale's multibyte characters only in text mode.
    FILE *stream = nullptr;
    if (tmpfile_s(&stream) || !stream || _setmode(_fileno(stream), _O_TEXT) == -1) {
        return 108;
    }
    write_line(stream, L"%s|%d", L"\u03BB", 7);
    rewind(stream);
    char line[16] = {};
    const size_t length = fread(line, 1, sizeof(line), stream);
    fclose(stream);
    if (length != 5 || memcmp(line, "\xCE\xBB|7\n", 5)) { // read back in text mode too
        return 109;
    }
    pal::err_print_line(L"[HOST-PAL] err \u03BB");
    out_line(L"[HOST-PAL] out %s %d", L"\u03BB", 7);
    const auto timestamp = pal::get_timestamp();
    if (timestamp.size() < 5 || timestamp.compare(timestamp.size() - 4, 4, L" GMT")) {
        return 110; // Windows has a UTC clock
    }

    // The installation policy and the small queries.
    if (pal::get_current_os_rid_platform() != L"witos" ||
        pal::is_running_in_wow64() ||
        pal::are_paths_equal_with_normalized_casing(L"/A", L"/a") ||
        !pal::are_paths_equal_with_normalized_casing(L"/a", L"/a")) {
        return 111;
    }
    pal::string_t location = L"keep";
    if (!pal::get_default_installation_dir(&location) ||
        location != L"\\dotnet" ||
        pal::get_default_installation_dir_for_arch(pal::architecture::arm64, &location) ||
        pal::get_dotnet_self_registered_config_location(pal::architecture::x64) !=
            L"\\etc\\dotnet\\install_location_x64") {
        return 112;
    }
    std::vector<pal::string_t> global{L"keep"};
    if (pal::get_global_dotnet_dirs(&global) || global.size() != 1) {
        return 113;
    }
    location = L"keep";
    if (pal::get_default_breadcrumb_store(&location) ||
        !location.empty() ||
        pal::get_default_bundle_extraction_base_dir(location) ||
        !location.empty()) {
        return 114;
    }
    // The model's files read only through views, so a registration file is found but has no line.
    location = L"keep";
    if (pal::get_dotnet_self_registered_dir(&location) || !location.empty()) {
        return 115;
    }
    SetLastError(0x2468);
    if (!pal::is_directory(L"/dir") ||
        GetLastError() != 0x2468 ||
        pal::is_directory(L"/file") ||
        pal::is_directory(L"/missing") ||
        GetLastError() != ERROR_FILE_NOT_FOUND) {
        return 116;
    }
    if (pal::touch_file(L"/file") ||
        GetLastError() != ERROR_FILE_EXISTS ||
        pal::touch_file(L"/missing") ||
        GetLastError() != ERROR_WRITE_PROTECT) {
        return 117;
    }
    // Servicing: CORE_SERVICING when it names a directory, otherwise /opt/coreservicing, a file in the model.
    location = L"keep";
    if (!SetEnvironmentVariableW(L"CORE_SERVICING", nullptr) ||
        pal::get_default_servicing_directory(&location) ||
        !location.empty()) {
        return 118;
    }
    if (!SetEnvironmentVariableW(L"CORE_SERVICING", L"/dir") ||
        !pal::get_default_servicing_directory(&location) ||
        location != L"\\dir" ||
        !SetEnvironmentVariableW(L"CORE_SERVICING", nullptr)) {
        return 119;
    }
    return 0;
}
