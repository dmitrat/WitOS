#include "path.h"

static int separator(WitU8 c)
{
    return c == '/' || c == 92;
}

int wit_path_utf8_valid(const char *value, WitU32 bytes)
{
    if (!value && bytes) {
        return 0;
    }
    for (WitU32 i = 0; i < bytes;) {
        WitU32 c = (WitU8)value[i++];
        if (c < 32 || c == 127 || c == ':') {
            return 0;
        }
        if (c < 128) {
            continue;
        }
        WitU32 extra, minimum;
        if (c >= 0xC2 && c <= 0xDF) {
            extra = 1;
            minimum = 0x80;
            c &= 31;
        } else if (c >= 0xE0 && c <= 0xEF) {
            extra = 2;
            minimum = 0x800;
            c &= 15;
        } else if (c >= 0xF0 && c <= 0xF4) {
            extra = 3;
            minimum = 0x10000;
            c &= 7;
        } else {
            return 0;
        }
        if (extra > bytes - i) {
            return 0;
        }
        while (extra--) {
            WitU32 next = (WitU8)value[i++];
            if ((next & 0xC0) != 0x80) {
                return 0;
            }
            c = (c << 6) | (next & 63);
        }
        if (c < minimum || c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF)) {
            return 0;
        }
    }
    return 1;
}

static WitU64 append(WitNativePath *path, const char *input, WitU32 bytes)
{
    for (WitU32 i = 0; i < bytes;) {
        while (i < bytes && separator((WitU8)input[i])) {
            ++i;
        }
        WitU32 start = i;
        while (i < bytes && !separator((WitU8)input[i])) {
            ++i;
        }
        const WitU32 count = i - start;
        if (!count) {
            continue;
        }
        if (count == 1 && input[start] == '.') {
            continue;
        }
        if (count == 2 && input[start] == '.' && input[start + 1] == '.') {
            while (path->Bytes > 1 && path->Text[path->Bytes - 1] != '/') {
                --path->Bytes;
            }
            if (path->Bytes > 1) {
                --path->Bytes;
            }
            continue;
        }
        const WitU32 slash = path->Bytes > 1 ? 1U : 0U;
        const WitU32 available = WIT_PATH_BUFFER - 1 - path->Bytes;
        if (slash > available || count > available - slash) {
            return WIT_STATUS_TOO_LARGE;
        }
        if (slash) {
            path->Text[path->Bytes++] = '/';
        }
        for (WitU32 j = 0; j < count; ++j) {
            path->Text[path->Bytes++] = input[start + j];
        }
    }
    return WIT_STATUS_OK;
}

WitU64 wit_path_resolve(const char *cwd, WitU32 cwdBytes, const char *input, WitU32 inputBytes, WitNativePath *output)
{
    if (!output || !input || !inputBytes || !cwd || !cwdBytes) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (inputBytes > WIT_PATH_INPUT_MAX || cwdBytes > WIT_STORAGE_NAME_BYTES + 1) {
        return WIT_STATUS_TOO_LARGE;
    }
    if (!separator((WitU8)cwd[0]) || !wit_path_utf8_valid(cwd, cwdBytes) || !wit_path_utf8_valid(input, inputBytes)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitNativePath path;
    for (WitU32 i = 0; i < sizeof(path); ++i) {
        ((WitU8 *)&path)[i] = 0;
    }
    path.Text[0] = '/';
    path.Bytes = 1;
    WitU64 status = WIT_STATUS_OK;
    if (!separator((WitU8)input[0])) {
        status = append(&path, cwd, cwdBytes);
    }
    if (status == WIT_STATUS_OK) {
        status = append(&path, input, inputBytes);
    }
    if (status != WIT_STATUS_OK) {
        return status;
    }
    path.Text[path.Bytes] = 0;
    WitU32 last = inputBytes;
    while (last && !separator((WitU8)input[last - 1])) {
        --last;
    }
    const WitU32 tail = inputBytes - last;
    path.RequireDirectory = separator((WitU8)input[inputBytes - 1]) ||
        (tail == 1 && input[last] == '.') ||
        (tail == 2 && input[last] == '.' && input[last + 1] == '.');
    *output = path;
    return WIT_STATUS_OK;
}
