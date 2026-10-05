#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cwchar>
#include "hostfxr.h"
#include "host_libraries.h"
extern "C" {
#include "library.h"
#include "../User/protocol.h"
}

/* The .NET host's libraries in the guest (P6.4.j3c3), mode 28 of the host runtime fixture: upstream's hostfxr.dll and
 * hostpolicy.dll, built for the guest, from the boot package's .NET root layout. hostfxr's startup runs at the load;
 * hostfxr_get_dotnet_environment_info then reads the root through WitOS's PAL and reports the one framework the package
 * holds, and its errors reach the writer hostfxr_set_error_writer installs. hostpolicy loads and unloads. */
namespace {
struct Environment {
    int Calls;
    size_t Sdks, Frameworks;
    bool Framework, Version;
};

wchar_t last_error[256];
int errors;

void HOSTFXR_CALLTYPE on_error(const char_t *message)
{
    ++errors;
    size_t i = 0;
    for (; message[i] && i + 1 < sizeof(last_error) / sizeof(last_error[0]); ++i) {
        last_error[i] = message[i];
    }
    last_error[i] = 0;
}

void HOSTFXR_CALLTYPE on_environment(const hostfxr_dotnet_environment_info *info, void *context)
{
    auto &result = *static_cast<Environment *>(context);
    ++result.Calls;
    result.Sdks = info->sdk_count;
    result.Frameworks = info->framework_count;
    result.Version = info->size == sizeof(*info) && !std::wcscmp(info->hostfxr_version, WIT_HOST_RUNTIME_VERSION);
    if (info->framework_count == 1) {
        const auto &framework = info->frameworks[0];
        result.Framework = framework.size == sizeof(framework) &&
            !std::wcscmp(framework.name, L"Microsoft.NETCore.App") &&
            !std::wcscmp(framework.version, WIT_HOST_RUNTIME_VERSION) &&
            // The host is built for Windows and joins paths with '\', which WitOS's paths accept as a separator.
            !std::wcscmp(framework.path, L"/\\shared\\Microsoft.NETCore.App");
    }
}

template <typename Function> Function symbol(WitU64 library, const char *name)
{
    WitU64 address = 0;
    WitU32 bytes = 0;
    while (name[bytes]) {
        ++bytes;
    }
    return wit_native_library_symbol(library, name, bytes, 0, &address) == WIT_STATUS_OK
        ? reinterpret_cast<Function>(address)
        : nullptr;
}

WitU64 load(const char *path, WitU64 *library)
{
    WitU32 bytes = 0;
    while (path[bytes]) {
        ++bytes;
    }
    return wit_native_library_load(path, bytes, library);
}
} // namespace

extern "C" WitU64 wit_host_libraries_probe()
{
    WitU64 hostfxr = 0;
    if (load("/host/fxr/" WIT_HOST_RUNTIME_VERSION_UTF8 "/hostfxr.dll", &hostfxr) != WIT_STATUS_OK) {
        return 2850;
    }
    const auto set_error_writer = symbol<hostfxr_set_error_writer_fn>(hostfxr, "hostfxr_set_error_writer");
    const auto environment_info =
        symbol<hostfxr_get_dotnet_environment_info_fn>(hostfxr, "hostfxr_get_dotnet_environment_info");
    if (!set_error_writer || !environment_info) {
        return 2851;
    }
    // The .NET root of the boot package: one framework and no SDK.
    Environment result = {};
    if (environment_info(L"/", nullptr, &on_environment, &result) != 0 ||
        result.Calls != 1 ||
        result.Sdks ||
        result.Frameworks != 1 ||
        !result.Framework ||
        !result.Version) {
        return 2852;
    }
    // An invalid argument fails with upstream's status and message, which reach the installed writer.
    if (set_error_writer(&on_error)) {
        return 2853;
    }
    result = {};
    if (environment_info(L"/", &result, &on_environment, &result) != (int32_t)0x80008081 ||
        result.Calls ||
        errors != 1 ||
        std::wcscmp(last_error,
            L"hostfxr_get_dotnet_environment_info received an invalid argument: reserved should be null.")) {
        return 2854;
    }
    if (set_error_writer(nullptr) != &on_error) {
        return 2855;
    }
    // hostpolicy's startup and teardown, from the framework's directory.
    WitU64 hostpolicy = 0;
    if (load("/shared/Microsoft.NETCore.App/" WIT_HOST_RUNTIME_VERSION_UTF8 "/hostpolicy.dll", &hostpolicy) !=
            WIT_STATUS_OK ||
        !symbol<void *>(hostpolicy, "corehost_main") ||
        wit_native_library_unload(hostpolicy) != WIT_STATUS_OK) {
        return 2856;
    }
    if (wit_native_library_unload(hostfxr) != WIT_STATUS_OK) {
        return 2857;
    }
    return WIT_TEST_EXIT_CODE;
}
