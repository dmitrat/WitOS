#include "directory.h"
#include "storage_manifest.h"
#pragma optimize("", off)
#define CHECK(value, code) \
    do { \
        if (!(value)) return code; \
    } while (0)

static WitU32 length(const char *value)
{
    WitU32 bytes = 0;
    while (value[bytes]) {
        ++bytes;
    }
    return bytes;
}

static int named(const WitStorageInfo *info, const char *value)
{
    WitU32 bytes = length(value);
    if (info->NameBytes != bytes) {
        return 0;
    }
    for (WitU32 i = 0; i < bytes; ++i) {
        if (info->Name[i] != (WitU8)value[i]) {
            return 0;
        }
    }
    return 1;
}

WitU64 wit_native_directory_test(void)
{
    WitNativeDirectory directory;
    WitStorageInfo info;
    WitU32 found = 0;
    CHECK(wit_native_directory_open("/", 1, &directory) == WIT_STATUS_OK, 2501);
    CHECK(wit_native_cwd_set("/app", 4) == WIT_STATUS_OK, 2502);
    const char *expected[] = {"app", "dotnet", "native", "test"};
    for (WitU32 i = 0; i < 4; ++i) {
        CHECK(wit_native_directory_next(&directory, "*", 1, WIT_DIRECTORY_ONLY, &info, &found) == WIT_STATUS_OK &&
                found &&
                info.Kind == WIT_STORAGE_DIRECTORY &&
                named(&info, expected[i]),
            2503);
    }
    CHECK(wit_native_directory_next(&directory, "*", 1, WIT_DIRECTORY_ONLY, &info, &found) == WIT_STATUS_OK && !found,
        2504);
    CHECK(wit_native_directory_open(".", 1, &directory) == WIT_STATUS_OK, 2505);
    WitU32 jsonCount = 0;
    for (;;) {
        CHECK(wit_native_directory_next(&directory, "*.json", 6, 0, &info, &found) == WIT_STATUS_OK, 2506);
        if (!found) {
            break;
        }
        CHECK(info.Kind == WIT_STORAGE_FILE &&
                (named(&info, "CoreClrProbe.deps.json") || named(&info, "CoreClrProbe.runtimeconfig.json")),
            2507);
        ++jsonCount;
    }
    CHECK(jsonCount == 2, 2508);
    const char fx[] = "/dotnet/shared/Microsoft.NETCore.App";
    CHECK(wit_native_directory_open(fx, sizeof(fx) - 1, &directory) == WIT_STATUS_OK, 2509);
    CHECK(wit_native_directory_next(&directory, "*", 1, WIT_DIRECTORY_ONLY, &info, &found) == WIT_STATUS_OK &&
            found &&
            named(&info, WIT_STORAGE_RUNTIME_VERSION),
        2510);
    CHECK(wit_native_directory_next(&directory, "*", 1, WIT_DIRECTORY_ONLY, &info, &found) == WIT_STATUS_OK && !found,
        2511);
    const char version[] = "/dotnet/shared/Microsoft.NETCore.App/" WIT_STORAGE_RUNTIME_VERSION;
    CHECK(wit_native_directory_open(version, sizeof(version) - 1, &directory) == WIT_STATUS_OK, 2512);
    jsonCount = 0;
    for (;;) {
        CHECK(wit_native_directory_next(&directory, "*.json", 6, 0, &info, &found) == WIT_STATUS_OK, 2513);
        if (!found) {
            break;
        }
        CHECK(
            named(&info, "Microsoft.NETCore.App.deps.json") || named(&info, "Microsoft.NETCore.App.runtimeconfig.json"),
            2514);
        ++jsonCount;
    }
    CHECK(jsonCount == 2, 2515);
    const WitU32 cursor = directory.Cursor;
    found = 99;
    info.NameBytes = 77;
    CHECK(wit_native_directory_next(&directory, "a/b", 3, 0, &info, &found) == WIT_STATUS_INVALID_ARGUMENT &&
            found == 99 &&
            info.NameBytes == 77 &&
            directory.Cursor == cursor,
        2516);
    CHECK(wit_native_directory_open("/p", 2, &directory) == WIT_STATUS_WRONG_TYPE, 2517);
    CHECK(wit_native_directory_open("/missing", 8, &directory) == WIT_STATUS_NOT_FOUND, 2518);
    CHECK(wit_native_cwd_set("/", 1) == WIT_STATUS_OK, 2519);
    return 42;
}
