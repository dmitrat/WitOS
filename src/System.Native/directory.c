#include "directory.h"

static WitU32 advance(const char *value, WitU32 at)
{
    const WitU8 c = (WitU8)value[at];
    return at + (c < 128 ? 1 : c < 0xE0 ? 2 : c < 0xF0 ? 3 : 4);
}

static int pattern_valid(const char *pattern, WitU32 bytes)
{
    if (!pattern || !bytes || bytes > WIT_STORAGE_NAME_BYTES || !wit_path_utf8_valid(pattern, bytes)) {
        return 0;
    }
    for (WitU32 i = 0; i < bytes; ++i) {
        if (pattern[i] == '/' || pattern[i] == 92) {
            return 0;
        }
    }
    return 1;
}

int wit_directory_match(const char *name, WitU32 bytes, const char *pattern, WitU32 patternBytes)
{
    if (!name ||
        !bytes ||
        bytes > WIT_STORAGE_NAME_BYTES ||
        !wit_path_utf8_valid(name, bytes) ||
        !pattern_valid(pattern, patternBytes)) {
        return -1;
    }
    WitU32 input = 0, match = 0, star = ~0U, retry = 0;
    while (input < bytes) {
        if (match < patternBytes && pattern[match] == '*') {
            star = ++match;
            retry = input;
            continue;
        }
        if (match < patternBytes && pattern[match] == '?') {
            input = advance(name, input);
            ++match;
            continue;
        }
        if (match < patternBytes) {
            const WitU32 nextInput = advance(name, input), nextMatch = advance(pattern, match);
            int equal = nextInput - input == nextMatch - match;
            if (equal) {
                for (WitU32 i = 0; i < nextInput - input; ++i) {
                    if (name[input + i] != pattern[match + i]) {
                        equal = 0;
                    }
                }
            }
            if (equal) {
                input = nextInput;
                match = nextMatch;
                continue;
            }
        }
        if (star == ~0U) {
            return 0;
        }
        retry = advance(name, retry);
        input = retry;
        match = star;
    }
    while (match < patternBytes && pattern[match] == '*') {
        ++match;
    }
    return match == patternBytes;
}

WitU64 wit_native_directory_open(const char *input, WitU32 bytes, WitNativeDirectory *output)
{
    if (!output) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitNativeDirectory value;
    WitU64 status = wit_native_path_resolve(input, bytes, &value.Path);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    WitStorageInfo info;
    status = wit_native_storage_stat(value.Path.Text + 1, value.Path.Bytes - 1, &info);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (info.Kind != WIT_STORAGE_DIRECTORY) {
        return WIT_STATUS_WRONG_TYPE;
    }
    value.Cursor = 0;
    value.Done = 0;
    *output = value;
    return WIT_STATUS_OK;
}

WitU64 wit_native_directory_next(WitNativeDirectory *directory, const char *pattern, WitU32 bytes, WitU32 flags,
    WitStorageInfo *output, WitU32 *found)
{
    if (!directory || !output || !found || flags > WIT_DIRECTORY_ONLY || !pattern_valid(pattern, bytes)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (directory->Path.Bytes < 1 || directory->Path.Bytes >= WIT_PATH_BUFFER || directory->Path.Text[0] != '/') {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (directory->Done) {
        *found = 0;
        return WIT_STATUS_OK;
    }
    WitU32 cursor = directory->Cursor;
    for (;;) {
        WitStorageInfo info;
        WitU64 copied = 0;
        const WitU64 status =
            wit_native_storage_list(directory->Path.Text + 1, directory->Path.Bytes - 1, cursor, &info, &copied);
        if (status != WIT_STATUS_OK) {
            return status;
        }
        if (!copied) {
            directory->Cursor = cursor;
            directory->Done = 1;
            *found = 0;
            return WIT_STATUS_OK;
        }
        if (copied != sizeof(info) ||
            info.Version != WIT_STORAGE_QUERY_VERSION ||
            info.Size != sizeof(info) ||
            !info.NameBytes ||
            info.NameBytes > WIT_STORAGE_NAME_BYTES ||
            info.NextCursor <= cursor ||
            info.NextCursor > 0xFFFFFFFFULL ||
            (info.Kind != WIT_STORAGE_FILE && info.Kind != WIT_STORAGE_DIRECTORY)) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        cursor = (WitU32)info.NextCursor;
        const int match = wit_directory_match((const char *)info.Name, info.NameBytes, pattern, bytes);
        if (match < 0) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        if (match && (!flags || info.Kind == WIT_STORAGE_DIRECTORY)) {
            *output = info;
            directory->Cursor = cursor;
            *found = 1;
            return WIT_STATUS_OK;
        }
    }
}
