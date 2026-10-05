#pragma warning(push)
#pragma warning(disable : 4100) // Upstream inline mkdir intentionally ignores mode on Windows.
#include "pal.h"
#pragma warning(pop)
#include <cstring>
#include <cwchar>
extern "C" {
#include "bootstrap.h"
#include "image.h"
#include "path.h"
#include "../User/protocol.h"
}

/* The rest of the corehost PAL (P6.4.j2) in the guest, mode 26 of the host runtime fixture: WitOS's PAL objects over
 * the guest's adapters, the native encoding, the C runtime's streams on the process console, the package and the
 * environment the fixture's startup publishes (CORE_SERVICING=/). The same contracts as the hosted checks, with the
 * guest's own answers where the guest differs: no UTC clock, no writable storage. The output lines are markers the
 * kernel test requires. */
extern "C" void wit_crt_initialize_stdio_options(void);

namespace {
void out_line(const pal::char_t *format, ...)
{
    va_list args;
    va_start(args, format);
    pal::out_vprint_line(format, args);
    va_end(args);
}

WitU64 strings()
{
    pal::string_t text = L"a\x03BB";
    text += (wchar_t)0xD83D;
    text += (wchar_t)0xDE42;
    text += (wchar_t)0xD800;
    text += L"z";
    const char expected[] = "a\xCE\xBB\xF0\x9F\x99\x82\xEF\xBF\xBDz";
    std::vector<char> utf8;
    if (!pal::pal_utf8string(text, &utf8) ||
        utf8.size() != sizeof(expected) ||
        memcmp(utf8.data(), expected, sizeof(expected))) {
        return 2701;
    }
    char shortBuffer[4] = {1, 1, 1, 1};
    char exact[sizeof(expected)];
    if (pal::pal_utf8string(text, shortBuffer, sizeof(shortBuffer)) != sizeof(expected) ||
        shortBuffer[0] != 1 ||
        pal::pal_utf8string(text, exact, sizeof(exact)) != sizeof(expected) ||
        memcmp(exact, expected, sizeof(expected))) {
        return 2702;
    }
    std::vector<char> clr;
    pal::string_t back;
    if (!pal::pal_clrstring(text, &clr) ||
        clr != utf8 ||
        !pal::clr_palstring("a\xCE\xBB\xF0\x9F\x99\x82", &back) ||
        back != L"a\x03BB\xD83D\xDE42") {
        return 2703;
    }
    back = L"keep";
    if (pal::clr_palstring("", &back) ||
        !back.empty() ||
        pal::xtoi(L" 42") != 42 ||
        pal::xtoi(L"-17x") != -17 ||
        pal::xtoi(L"x") != 0) {
        return 2704;
    }
    return 0;
}

WitU64 policy()
{
    if (pal::get_timestamp() != L"(no UTC clock)" ||
        pal::get_current_os_rid_platform() != L"witos" ||
        pal::is_running_in_wow64() ||
        pal::are_paths_equal_with_normalized_casing(L"/A", L"/a")) {
        return 2711;
    }
    pal::string_t location = L"keep";
    std::vector<pal::string_t> global{L"keep"};
    if (!pal::get_default_installation_dir(&location) ||
        location != L"/" ||
        pal::get_global_dotnet_dirs(&global) ||
        global.size() != 1 ||
        pal::get_dotnet_self_registered_dir(&location) ||
        !location.empty()) {
        return 2712; // the package carries no install_location file
    }
    if (pal::get_default_breadcrumb_store(&location) ||
        pal::get_default_bundle_extraction_base_dir(location) ||
        !pal::get_default_servicing_directory(&location) ||
        location != L"/") {
        return 2713;
    }
    SetLastError(0x2468);
    if (!pal::is_directory(L"/") ||
        GetLastError() != 0x2468 ||
        pal::is_directory(L"/missing") ||
        GetLastError() != ERROR_FILE_NOT_FOUND) {
        return 2714;
    }
    if (pal::touch_file(L"/") ||
        GetLastError() != ERROR_FILE_EXISTS ||
        pal::touch_file(L"/missing") ||
        GetLastError() != ERROR_WRITE_PROTECT) {
        return 2715;
    }
    return 0;
}

// The process's environment and current directory (P6.4.j3a), which the kernel keeps for every module: the creator's
// variables, the image's defaults where the creator set none, and changes through SetEnvironmentVariableW.
bool value(const wchar_t *name, const wchar_t *expected)
{
    wchar_t buffer[32];
    const DWORD length = GetEnvironmentVariableW(name, buffer, 32);
    return length == wcslen(expected) && !wcscmp(buffer, expected);
}

WitU64 process_state()
{
    if (!value(L"WITOS_CREATOR", L"kernel") ||
        !value(L"witos_creator", L"kernel") ||
        !value(L"WITOS_SEEDED", L"creator") ||
        !value(L"CORE_SERVICING", L"/")) {
        return 2731;
    }
    SetLastError(0x1357);
    if (!SetEnvironmentVariableW(L"WITOS_SET", L"one") ||
        !SetEnvironmentVariableW(L"WITOS_SET", L"two") ||
        GetLastError() != 0x1357 ||
        !value(L"WITOS_SET", L"two")) {
        return 2732;
    }
    // Setting order, with a variable set again moved to the end.
    const wchar_t expected[] = L"WITOS_CREATOR=kernel\0WITOS_SEEDED=creator\0CORE_SERVICING=/\0WITOS_SET=two\0";
    auto block = GetEnvironmentStringsW();
    if (!block || memcmp(block, expected, sizeof(expected)) || !FreeEnvironmentStringsW(block)) {
        return 2733;
    }
    if (!SetEnvironmentVariableW(L"WITOS_SET", nullptr) ||
        !SetEnvironmentVariableW(L"WITOS_SET", nullptr) ||
        GetLastError() != 0x1357 ||
        GetEnvironmentVariableW(L"WITOS_SET", nullptr, 0) ||
        GetLastError() != ERROR_ENVVAR_NOT_FOUND) {
        return 2734; // removing an absent variable succeeds, as on Windows
    }
    if (SetEnvironmentVariableW(L"", L"v") ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        SetEnvironmentVariableW(L"A=B", L"v") ||
        GetLastError() != ERROR_INVALID_PARAMETER) {
        return 2735;
    }
    pal::string_t directory;
    if (!pal::getcwd(&directory) ||
        directory != L"/" ||
        wit_native_cwd_set("/", 1) != WIT_STATUS_OK ||
        wit_native_cwd_set("/missing", 8) != WIT_STATUS_NOT_FOUND ||
        !pal::getcwd(&directory) ||
        directory != L"/") {
        return 2736;
    }
    return 0;
}

// The module at an address and the process's executable (P6.4.j3b): the kernel created this component from the boot
// package's host/HostRuntimeFixture.pe, and the PAL's code, its data and its headers all lie in that image; a library
// loaded from the package names its own path, and an address in no module fails as GetModuleHandleExW does.
int image_data = 1;

WitU64 module_paths()
{
    const pal::string_t own = L"/host/HostRuntimeFixture.pe";
    pal::string_t path;
    SetLastError(0x2468);
    if (!pal::get_own_executable_path(&path) ||
        path != own ||
        !pal::get_own_module_path(&path) ||
        path != own ||
        GetLastError() != 0x2468) {
        return 2741;
    }
    const auto image = wit_native_process_image();
    if (!image ||
        !pal::get_method_module_path(&path, reinterpret_cast<void *>(&module_paths)) ||
        path != own ||
        !pal::get_method_module_path(&path, reinterpret_cast<void *>(static_cast<uintptr_t>(image->Base))) ||
        path != own ||
        !pal::get_method_module_path(&path, &image_data) ||
        path != own) {
        return 2742;
    }
    const pal::string_t libraryPath = L"/native/lib.dll";
    pal::dll_t library = nullptr;
    if (!pal::load_library(&libraryPath, &library)) {
        return 2743;
    }
    const auto symbol = pal::get_symbol(library, "LibraryAdd");
    const auto data = pal::get_symbol(library, "LibraryData");
    const bool named = symbol &&
        data &&
        pal::get_method_module_path(&path, reinterpret_cast<void *>(symbol)) &&
        path == libraryPath &&
        pal::get_method_module_path(&path, reinterpret_cast<void *>(data)) &&
        path == libraryPath;
    pal::unload_library(library);
    if (!named) {
        return 2744;
    }
    // After the unload, its addresses belong to no module; neither does the stack.
    path = L"keep";
    int local = 0;
    if (pal::get_method_module_path(&path, reinterpret_cast<void *>(symbol)) ||
        GetLastError() != ERROR_MOD_NOT_FOUND ||
        pal::get_method_module_path(&path, &local) ||
        GetLastError() != ERROR_MOD_NOT_FOUND ||
        path != L"keep") {
        return 2745;
    }
    return 0;
}

// The Win32 functions only the host calls.
WitU64 adapters()
{
    WitUserThreadInfo info;
    if (!wit_native_thread_info(&info) || GetCurrentProcessId() != info.ProcessId) {
        return 2721;
    }
    OutputDebugStringW(L"discarded without a debugger");
    if (CreateDirectoryW(L"/", nullptr) ||
        GetLastError() != ERROR_ALREADY_EXISTS ||
        CreateDirectoryW(L"/missing", nullptr) ||
        GetLastError() != ERROR_WRITE_PROTECT ||
        RemoveDirectoryW(L"/missing") ||
        GetLastError() != ERROR_FILE_NOT_FOUND ||
        RemoveDirectoryW(L"/") ||
        GetLastError() != ERROR_WRITE_PROTECT ||
        CreateDirectoryW(nullptr, nullptr) ||
        GetLastError() != ERROR_INVALID_PARAMETER) {
        return 2722;
    }
    return 0;
}
} // namespace

extern "C" WitU64 wit_host_pal_probe()
{
    wit_crt_initialize_stdio_options();
    WitU64 code = strings();
    if (!code) {
        code = policy();
    }
    if (!code) {
        code = adapters();
    }
    if (!code) {
        code = process_state();
    }
    if (!code) {
        code = module_paths();
    }
    if (!code) {
        // UTF-8 on the console through the C runtime's streams; the file compiles as UTF-8, so the markers the kernel
        // test requires stand here as written.
        pal::err_print_line(L"[HOST-PAL-ERR] λ");
        out_line(L"[HOST-PAL-OUT] λ %d", 7);
    }
    return code ? code : WIT_TEST_EXIT_CODE;
}
