#if !defined(UNICODE)
#error WitOS environment adapter requires the pinned Windows Unicode PAL profile.
#endif
#include "pal.witos.h"
#include "pal_environment.witos.h"
#include <new>
static_assert(sizeof(TCHAR) == 2, "The selected PAL uses UTF-16 TCHAR.");

static const WitPalEnvironmentEntry *environment;
static uint32_t environment_count;
static bool environment_ready;

bool wit_pal_environment_is_ready()
{
    return environment_ready;
}

static wchar_t fold(wchar_t c)
{
    return c >= L'a' && c <= L'z' ? c - (L'a' - L'A') : c;
}

static bool name_character(wchar_t c)
{
    return c >= L'!' && c <= L'~' && c != L'=';
}

static bool equal(const wchar_t *a, const wchar_t *b, uint32_t count)
{
    for (uint32_t i = 0; i < count; ++i) {
        if (fold(a[i]) != fold(b[i])) {
            return false;
        }
    }
    return true;
}

static bool readonly(const void *p, size_t bytes)
{
    return p &&
        !((uintptr_t)p & 1) &&
        wit_native_image_range(wit_native_process_image(), (uintptr_t)p, bytes, WIT_IMAGE_INFO_READ,
            WIT_IMAGE_INFO_WRITE | WIT_IMAGE_INFO_EXECUTE, 1);
}

bool wit_pal_environment_initialize(const WitPalEnvironmentEntry *entries, uint32_t count)
{
    // Startup is externally serialized. Failed validation publishes nothing and
    // can be retried; after success, no replacement or mutation is supported.
    if (environment_ready) {
        SetLastError(ERROR_ALREADY_INITIALIZED);
        return false;
    }
    if (!wit_native_process_image()) {
        SetLastError(ERROR_NOT_READY);
        return false;
    }
    if (count > WIT_PAL_ENV_CAPACITY ||
        (!count && entries) ||
        (count &&
            (((uintptr_t)entries & (alignof(WitPalEnvironmentEntry) - 1)) ||
                !readonly(entries, count * sizeof(*entries))))) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }
    for (uint32_t i = 0; i < count; ++i) {
        const auto &entry = entries[i];
        if (!entry.NameLength ||
            entry.NameLength > WIT_PAL_ENV_NAME_MAX ||
            entry.ValueLength > WIT_PAL_ENV_VALUE_MAX ||
            !readonly(entry.Name, (entry.NameLength + 1) * sizeof(wchar_t)) ||
            !readonly(entry.Value, (entry.ValueLength + 1) * sizeof(wchar_t))) {
            SetLastError(ERROR_INVALID_PARAMETER);
            return false;
        }
        for (uint32_t c = 0; c < entry.NameLength; ++c) {
            if (!name_character(entry.Name[c])) {
                SetLastError(ERROR_INVALID_PARAMETER);
                return false;
            }
        }
        for (uint32_t c = 0; c < entry.ValueLength; ++c) {
            if (!entry.Value[c]) {
                SetLastError(ERROR_INVALID_PARAMETER);
                return false;
            }
        }
        if (entry.Name[entry.NameLength] || entry.Value[entry.ValueLength]) {
            SetLastError(ERROR_INVALID_PARAMETER);
            return false;
        }
        for (uint32_t j = 0; j < i; ++j) {
            if (entries[j].NameLength == entry.NameLength && equal(entries[j].Name, entry.Name, entry.NameLength)) {
                SetLastError(ERROR_INVALID_PARAMETER);
                return false;
            }
        }
    }
    environment = entries;
    environment_count = count;
    environment_ready = true;
    return true;
}

uint32_t PalGetEnvironmentVariable(LPCWSTR name, LPWSTR buffer, uint32_t size)
{
    if (!name || (size && !buffer)) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return 0;
    }
    uint32_t length = 0;
    while (length <= WIT_PAL_ENV_NAME_MAX && name[length]) {
        if (!name_character(name[length])) {
            SetLastError(ERROR_INVALID_PARAMETER);
            return 0;
        }
        ++length;
    }
    if (!length || length > WIT_PAL_ENV_NAME_MAX) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return 0;
    }
    if (!environment_ready) {
        SetLastError(ERROR_NOT_READY);
        return 0;
    }
    for (uint32_t i = 0; i < environment_count; ++i) {
        const auto &entry = environment[i];
        if (entry.NameLength != length || !equal(entry.Name, name, length)) {
            continue;
        }
        if (size <= entry.ValueLength) {
            return entry.ValueLength + 1;
        }
        for (uint32_t c = 0; c <= entry.ValueLength; ++c) {
            buffer[c] = entry.Value[c];
        }
        // Empty values are present, return zero and preserve prior last-error.
        return entry.ValueLength;
    }
    SetLastError(ERROR_ENVVAR_NOT_FOUND);
    return 0;
}

extern "C" uint32_t wit_pal_environment_get(LPCWSTR name, LPWSTR buffer, uint32_t size)
{
    return PalGetEnvironmentVariable(name, buffer, size);
}

namespace {
struct EnvironmentBlock {
    wchar_t *Address;
    bool Busy;
};

EnvironmentBlock blocks[WIT_PAL_ENV_BLOCK_CAPACITY];
volatile WitU32 blocks_gate;

void block_lock()
{
    while (!wit_native_try_lock(&blocks_gate)) {
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
    }
}

void block_unlock()
{
    wit_native_unlock(&blocks_gate);
}
}

extern "C" wchar_t *wit_pal_environment_strings()
{
    if (!environment_ready) {
        SetLastError(ERROR_NOT_READY);
        return nullptr;
    }
    block_lock();
    uint32_t slot = WIT_PAL_ENV_BLOCK_CAPACITY;
    for (uint32_t i = 0; i < WIT_PAL_ENV_BLOCK_CAPACITY; ++i) {
        if (!blocks[i].Busy) {
            slot = i;
            blocks[i].Busy = true;
            break;
        }
    }
    block_unlock();
    if (slot == WIT_PAL_ENV_BLOCK_CAPACITY) {
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return nullptr;
    }
    size_t units = environment_count ? 1 : 2;
    for (uint32_t i = 0; i < environment_count; ++i) {
        units += environment[i].NameLength + environment[i].ValueLength + 2;
    }
    auto block = new (std::nothrow) wchar_t[units];
    if (!block) {
        block_lock();
        blocks[slot] = {nullptr, false};
        block_unlock();
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return nullptr;
    }
    size_t at = 0;
    for (uint32_t i = 0; i < environment_count; ++i) {
        const auto &entry = environment[i];
        for (uint32_t j = 0; j < entry.NameLength; ++j) {
            block[at++] = entry.Name[j];
        }
        block[at++] = L'=';
        for (uint32_t j = 0; j < entry.ValueLength; ++j) {
            block[at++] = entry.Value[j];
        }
        block[at++] = 0;
    }
    block[at++] = 0;
    if (!environment_count) {
        block[at++] = 0;
    }
    if (at != units) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    block_lock();
    blocks[slot].Address = block;
    block_unlock();
    return block;
}

extern "C" int wit_pal_environment_free(wchar_t *address)
{
    block_lock();
    for (uint32_t i = 0; i < WIT_PAL_ENV_BLOCK_CAPACITY; ++i) {
        if (address && blocks[i].Busy && blocks[i].Address == address) {
            // Keep the slot claimed until actual heap release completes. A new
            // block cannot be published at the same address during this transition.
            blocks[i].Address = nullptr;
            block_unlock();
            delete[] address;
            block_lock();
            blocks[i].Busy = false;
            block_unlock();
            return 1;
        }
    }
    block_unlock();
    SetLastError(ERROR_INVALID_PARAMETER);
    return 0;
}

char *PalCopyTCharAsChar(const TCHAR *input)
{
    if (!input) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return nullptr;
    }
    size_t units = 0;
    while (units <= 32767 && input[units]) {
        ++units;
    }
    if (units > 32767) {
        SetLastError(ERROR_BUFFER_OVERFLOW);
        return nullptr;
    }
    // At most three UTF-8 bytes per UTF-16 unit. This also bounds writes if a
    // caller violates the stable-input requirement between sizing and copying.
    auto output = new (std::nothrow) char[units * 3 + 1];
    if (!output) {
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return nullptr;
    }
    size_t written = 0;
    for (size_t i = 0; i < units && input[i]; ++i) {
        uint32_t point = input[i];
        if (point >= 0xD800 && point <= 0xDBFF && i + 1 < units && input[i + 1] >= 0xDC00 && input[i + 1] <= 0xDFFF) {
            point = 0x10000 + ((point - 0xD800) << 10) + (input[++i] - 0xDC00);
        } else if (point >= 0xD800 && point <= 0xDFFF) {
            point = 0xFFFD;
        }
        if (point < 0x80) {
            output[written++] = (char)point;
        } else if (point < 0x800) {
            output[written++] = (char)(0xC0 | (point >> 6));
            output[written++] = (char)(0x80 | (point & 0x3F));
        } else if (point < 0x10000) {
            output[written++] = (char)(0xE0 | (point >> 12));
            output[written++] = (char)(0x80 | ((point >> 6) & 0x3F));
            output[written++] = (char)(0x80 | (point & 0x3F));
        } else {
            output[written++] = (char)(0xF0 | (point >> 18));
            output[written++] = (char)(0x80 | ((point >> 12) & 0x3F));
            output[written++] = (char)(0x80 | ((point >> 6) & 0x3F));
            output[written++] = (char)(0x80 | (point & 0x3F));
        }
    }
    output[written] = 0;
    return output;
}
