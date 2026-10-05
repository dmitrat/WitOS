#if !defined(UNICODE)
#error WitOS environment adapter requires the pinned Windows Unicode PAL profile.
#endif
#include "pal.witos.h"
#include "pal_environment.witos.h"
#include <new>
static_assert(sizeof(TCHAR) == 2, "The selected PAL uses UTF-16 TCHAR.");

/* The environment is the process's (P6.4.j3a): the kernel keeps it for every module of the component, which reads and
 * changes it through these functions, so each module's copy of this adapter sees one state. An image's startup may
 * seed it from a readonly table of defaults. Names here are printable ASCII without '=', which the kernel compares
 * with ASCII case folding. */
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

namespace {
WitU64 state(WitU32 operation, const wchar_t *name, uint32_t nameUnits, const wchar_t *value, uint32_t valueUnits,
    wchar_t *buffer, uint32_t capacity, WitU64 *units)
{
    WitProcessStateRequest request = {};
    request.Version = WIT_PROCESS_STATE_VERSION;
    request.Size = sizeof(request);
    request.Operation = operation;
    request.Name = (WitU64)name;
    request.NameUnits = nameUnits;
    request.Value = (WitU64)value;
    request.ValueUnits = valueUnits;
    request.Buffer = (WitU64)buffer;
    request.BufferBytes = (WitU64)capacity * sizeof(wchar_t);
    return wit_native_call(WIT_CALL_PROCESS_STATE, (WitU64)&request, sizeof(request), 0, units);
}

// The length of a valid name, or 0 for an empty, overlong or invalid one.
uint32_t name_length(LPCWSTR name)
{
    uint32_t length = 0;
    while (name && length <= WIT_PAL_ENV_NAME_MAX && name[length]) {
        if (!name_character(name[length])) {
            return 0;
        }
        ++length;
    }
    return length <= WIT_PAL_ENV_NAME_MAX ? length : 0;
}
} // namespace

bool wit_pal_environment_initialize(const WitPalEnvironmentEntry *entries, uint32_t count)
{
    // Startup is externally serialized. The table seeds the process's environment with defaults: a variable the
    // process already has, from its creator, stays. Failed validation or seeding publishes nothing and can be
    // retried; after success, no replacement is supported.
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
    bool seeded[WIT_PAL_ENV_CAPACITY] = {};
    for (uint32_t i = 0; i < count; ++i) {
        const auto &entry = entries[i];
        WitU64 units = 0;
        WitU64 status = state(WIT_PROCESS_ENV_GET, entry.Name, entry.NameLength, nullptr, 0, nullptr, 0, &units);
        if (status == WIT_STATUS_NOT_FOUND) {
            status = state(WIT_PROCESS_ENV_SET, entry.Name, entry.NameLength, entry.ValueLength ? entry.Value : L"",
                entry.ValueLength, nullptr, 0, &units);
            seeded[i] = status == WIT_STATUS_OK;
        }
        if (status != WIT_STATUS_OK) {
            for (uint32_t j = 0; j < i; ++j) {
                if (seeded[j] &&
                    state(WIT_PROCESS_ENV_SET, entries[j].Name, entries[j].NameLength, nullptr, 0, nullptr, 0,
                        &units) != WIT_STATUS_OK) {
                    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
                }
            }
            SetLastError(status == WIT_STATUS_NO_MEMORY ? ERROR_NOT_ENOUGH_MEMORY : ERROR_INVALID_PARAMETER);
            return false;
        }
    }
    environment_ready = true;
    return true;
}

uint32_t PalGetEnvironmentVariable(LPCWSTR name, LPWSTR buffer, uint32_t size)
{
    const uint32_t length = name_length(name);
    if (!length || (size && !buffer)) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return 0;
    }
    // The kernel copies the value only when it fits whole, leaving room for the terminator.
    WitU64 units = 0;
    const WitU64 status =
        state(WIT_PROCESS_ENV_GET, name, length, nullptr, 0, size ? buffer : nullptr, size ? size - 1 : 0, &units);
    if (status == WIT_STATUS_NOT_FOUND) {
        SetLastError(ERROR_ENVVAR_NOT_FOUND);
        return 0;
    }
    if (status != WIT_STATUS_OK) {
        SetLastError(status == WIT_STATUS_BAD_ADDRESS ? ERROR_NOACCESS : ERROR_INVALID_PARAMETER);
        return 0;
    }
    if (units >= size) {
        return (uint32_t)units + 1;
    }
    buffer[units] = 0;
    // Empty values are present, return zero and preserve prior last-error.
    return (uint32_t)units;
}

extern "C" uint32_t wit_pal_environment_get(LPCWSTR name, LPWSTR buffer, uint32_t size)
{
    return PalGetEnvironmentVariable(name, buffer, size);
}

/* SetEnvironmentVariableW: a null value removes the variable, an absent one too, as on Windows; success preserves
 * the last error. A null name, which Windows dereferences, is invalid here. */
extern "C" BOOL WINAPI wit_pal_environment_set(LPCWSTR name, LPCWSTR value)
{
    const uint32_t length = name_length(name);
    if (!length) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return FALSE;
    }
    uint32_t units = 0;
    while (value && units <= WIT_ENVIRONMENT_UNITS && value[units]) {
        ++units;
    }
    WitU64 ignored = 0;
    const WitU64 status = units > WIT_ENVIRONMENT_UNITS
        ? WIT_STATUS_NO_MEMORY
        : state(
              WIT_PROCESS_ENV_SET, name, length, value ? (units ? value : L"") : nullptr, units, nullptr, 0, &ignored);
    if (status == WIT_STATUS_OK || (!value && status == WIT_STATUS_NOT_FOUND)) {
        return TRUE;
    }
    SetLastError(status == WIT_STATUS_NO_MEMORY ? ERROR_NOT_ENOUGH_MEMORY : ERROR_INVALID_PARAMETER);
    return FALSE;
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
    wit_native_lock(&blocks_gate);
}

void block_unlock()
{
    wit_native_unlock(&blocks_gate);
}
} // namespace

/* GetEnvironmentStringsW: a copy of the process's block, "Name=Value\0" records and a final "\0", with a second "\0"
 * when it has no record, as Windows returns it. Another thread may change the environment between the size and the
 * copy, so the copy is retried a few times. */
extern "C" wchar_t *wit_pal_environment_strings()
{
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
    wchar_t *block = nullptr;
    for (int attempt = 0; attempt < 4 && !block; ++attempt) {
        WitU64 units = 0;
        if (state(WIT_PROCESS_ENV_BLOCK, nullptr, 0, nullptr, 0, nullptr, 0, &units) != WIT_STATUS_OK || !units) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        const uint32_t capacity = (uint32_t)units + (units == 1 ? 1 : 0);
        block = new (std::nothrow) wchar_t[capacity];
        if (!block) {
            break;
        }
        WitU64 copied = 0;
        if (state(WIT_PROCESS_ENV_BLOCK, nullptr, 0, nullptr, 0, block, capacity, &copied) != WIT_STATUS_OK) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        if (copied == 1 && capacity == 2) {
            block[1] = 0;
        } else if (copied != units) {
            delete[] block; // changed in between
            block = nullptr;
        }
    }
    if (!block) {
        block_lock();
        blocks[slot] = {nullptr, false};
        block_unlock();
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return nullptr;
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
