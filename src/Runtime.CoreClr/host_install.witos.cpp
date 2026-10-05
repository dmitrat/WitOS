// Actual corehost PAL signatures (P6.4.j2): where .NET is installed and serviced, the platform part of the runtime
// identifier and the small queries. The Windows PAL reads these from Program Files, ProgramData and the registry,
// which the guest has none of; the guest follows the Unix PAL instead, over the immutable package: a default
// installation directory, a registration file in /etc/dotnet, servicing from CORE_SERVICING or /opt/coreservicing, no
// global directories, and case-sensitive paths. The package cannot be written, so there is no breadcrumb store, no
// bundle extraction directory and no breadcrumb file. Unlike the PAL objects of upstream, these do not trace.
#pragma warning(push)
#pragma warning(disable : 4100) // Upstream inline mkdir intentionally ignores mode on Windows.
#include "pal.h"
#pragma warning(pop)
extern "C" {
#include "file.h"
}
#include "host_path_codec.witos.h"

#if !defined(_M_X64)
#error The WitOS host PAL knows the x64 architecture only.
#endif

namespace {
constexpr pal::architecture CURRENT = pal::architecture::x64;

// The package's own .NET: the boot package places shared/Microsoft.NETCore.App at its root, as an installation does.
constexpr const pal::char_t *INSTALLATION = _X("/");
constexpr const pal::char_t *REGISTRATION = _X("/etc/dotnet/install_location");
constexpr const pal::char_t *SERVICING = _X("/opt/coreservicing");

// Whether the path resolves to an entry of the package, and the entry if so.
bool stat(const pal::string_t &path, WitStorageInfo &info)
{
    WitNativePath resolved;
    if (!WitHostPath::resolve(path, resolved, true)) {
        return false;
    }
    const auto status = wit_native_storage_stat(resolved.Text + 1, resolved.Bytes - 1, &info);
    if (status != WIT_STATUS_OK) {
        SetLastError(WitHostPath::error(status));
        return false;
    }
    return true;
}

// The first line of an install_location file, as the Unix PAL reads it; found is false only when there is no file.
bool first_line(const pal::string_t &path, bool &found, pal::string_t &line)
{
    found = false;
    line.clear();
    WitNativePath resolved;
    if (!WitHostPath::resolve(path, resolved, true)) {
        return false;
    }
    found = true;
    WitU64 handle = 0;
    if (wit_native_file_open(resolved.Text + 1, resolved.Bytes - 1, &handle) != WIT_STATUS_OK) {
        return false;
    }
    char bytes[WIT_PATH_INPUT_MAX];
    WitU64 count = 0;
    const auto status = wit_native_file_read(handle, bytes, sizeof(bytes), &count);
    if (wit_native_file_close(handle) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    int length = 0;
    while (status == WIT_STATUS_OK && (WitU64)length < count && bytes[length] != '\n') {
        ++length;
    }
    if (!length) {
        return false;
    }
    const int units = ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes, length, nullptr, 0);
    if (units <= 0) {
        return false;
    }
    line.resize((size_t)units);
    return ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes, length, &line[0], units) == units;
}
} // namespace

pal::string_t pal::get_current_os_rid_platform()
{
    return _X("witos"); // the generated configuration's fallback OS; no version
}

bool pal::is_running_in_wow64()
{
    return false;
}

bool pal::are_paths_equal_with_normalized_casing(const string_t &path1, const string_t &path2)
{
    return path1 == path2; // package names are case-sensitive
}

bool pal::is_directory(const string_t &path)
{
    const DWORD previous = GetLastError();
    WitStorageInfo info;
    if (!stat(path, info)) {
        return false;
    }
    SetLastError(previous);
    return info.Kind == WIT_STORAGE_DIRECTORY;
}

// A breadcrumb file cannot be created in the immutable package; CreateFileW with CREATE_NEW reports an existing name
// as ERROR_FILE_EXISTS and any other as write-protected media.
bool pal::touch_file(const string_t &path)
{
    WitStorageInfo info;
    const bool exists = stat(path, info);
    SetLastError(exists ? ERROR_FILE_EXISTS : ERROR_WRITE_PROTECT);
    return false;
}

bool pal::get_default_installation_dir(string_t *recv)
{
    return get_default_installation_dir_for_arch(CURRENT, recv);
}

bool pal::get_default_installation_dir_for_arch(architecture arch, string_t *recv)
{
    if (arch != CURRENT) {
        return false; // no installation for another architecture
    }
    recv->assign(INSTALLATION);
    return true;
}

pal::string_t pal::get_dotnet_self_registered_config_location(architecture arch)
{
    return string_t(REGISTRATION) + _X("_") + (arch == CURRENT ? _X("x64") : _X("unknown"));
}

bool pal::get_dotnet_self_registered_dir(string_t *recv)
{
    return get_dotnet_self_registered_dir_for_arch(CURRENT, recv);
}

// The architecture's file, then for the current architecture the file without one, as the Unix PAL looks.
bool pal::get_dotnet_self_registered_dir_for_arch(architecture arch, string_t *recv)
{
    recv->clear();
    string_t location;
    bool found = false;
    if (first_line(get_dotnet_self_registered_config_location(arch), found, location)) {
        recv->assign(location);
        return true;
    }
    if (found || arch != CURRENT || !first_line(REGISTRATION, found, location)) {
        return false;
    }
    recv->assign(location);
    return found;
}

bool pal::get_global_dotnet_dirs(std::vector<string_t> *)
{
    return false; // as on Unix, no global locations
}

bool pal::get_default_servicing_directory(string_t *recv)
{
    recv->clear();
    string_t directory;
    if (!pal::getenv(_X("CORE_SERVICING"), &directory) || !pal::fullpath(&directory) || !pal::is_directory(directory)) {
        directory = SERVICING;
        if (!pal::is_directory(directory)) {
            return false;
        }
    }
    recv->assign(directory);
    return true;
}

bool pal::get_default_breadcrumb_store(string_t *recv)
{
    recv->clear();
    return false; // breadcrumbs need writable storage
}

bool pal::get_default_bundle_extraction_base_dir(string_t &extraction_dir)
{
    extraction_dir.clear();
    return false; // extraction needs writable storage
}
