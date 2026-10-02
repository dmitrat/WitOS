#include <stdint.h>
#include "minipal.h" // Hash-verified pinned upstream VMToOSInterface.
extern "C" {
#include "bootstrap.h"
}
extern bool wit_coreclr_code_registered(uint64_t, uint64_t);
void wit_coreclr_code_gate_enter();
void wit_coreclr_code_gate_leave();

namespace {
constexpr unsigned MapperCount = 4, ViewCount = 16;
constexpr WitU64 Maximum = 64ULL * 1024 * 1024, Granularity = 65536;

struct Mapper {
    WitU64 Token, Backing;
    bool Open;
};

struct View {
    WitU64 Token, Base, Offset, Bytes;
    bool Write;
};

Mapper mappers[MapperCount];
View views[ViewCount];
WitU64 nextToken = 1;
volatile WitU32 gate;

struct Guard {
    Guard()
    {
        wit_coreclr_code_gate_enter();
    }

    ~Guard()
    {
        wit_coreclr_code_gate_leave();
    }
};

Mapper *find(WitU64 token, bool open = true)
{
    for (auto &m : mappers) {
        if (m.Token == token && token && (!open || m.Open)) {
            return &m;
        }
    }
    return nullptr;
}

View *slot()
{
    for (auto &v : views) {
        if (!v.Base) {
            return &v;
        }
    }
    return nullptr;
}

bool extent(WitU64 offset, WitU64 bytes)
{
    return bytes && !(bytes & 4095) && !(offset & 4095) && offset < Maximum && bytes <= Maximum - offset;
}

bool overlap(WitU64 a, WitU64 n, WitU64 b, WitU64 m)
{
    return a < b + m && b < a + n;
}

WitU64 code(WitU32 operation, WitU64 address, WitU64 source, WitU64 bytes, WitU32 protection = 0, WitU64 alignment = 0,
    WitU64 minimum = 0, WitU64 maximum = 0, WitU64 *result = nullptr)
{
    WitCodeMemoryRequest request = {WIT_CODE_MEMORY_VERSION, sizeof(request), operation, protection, address, source,
        bytes, alignment, minimum, maximum};
    return wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&request, sizeof(request), 0, result);
}

void release(WitU64 base)
{
    if (wit_native_call(WIT_CALL_MEMORY_RELEASE, base, 0, 0, nullptr) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
}

void reap(Mapper &m)
{
    if (m.Open) {
        return;
    }
    for (const auto &v : views) {
        if (v.Base && v.Token == m.Token) {
            return;
        }
    }
    release(m.Backing);
    m = {};
}
}

void wit_coreclr_code_gate_enter()
{
    while (!wit_native_try_lock(&gate)) {
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
    }
}

void wit_coreclr_code_gate_leave()
{
    wit_native_unlock(&gate);
}

bool VMToOSInterface::CreateDoubleMemoryMapper(void **handle, size_t *maximum)
{
    if (!handle || !maximum) {
        return false;
    }
    Guard guard;
    Mapper *free = nullptr;
    for (auto &m : mappers) {
        if (!m.Token) {
            free = &m;
            break;
        }
    }
    if (!free || !nextToken) {
        return false;
    }
    WitU64 backing = 0;
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, Maximum, Granularity, 0, &backing) != WIT_STATUS_OK) {
        return false;
    }
    *free = {nextToken++, backing, true};
    *handle = (void *)free->Token;
    *maximum = (size_t)Maximum;
    return true;
}

void VMToOSInterface::DestroyDoubleMemoryMapper(void *handle)
{
    Guard guard;
    auto m = find((WitU64)handle);
    if (!m) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    m->Open = false;
    reap(*m); // Existing views retain backing after handle close.
}

void *VMToOSInterface::ReserveDoubleMappedMemory(
    void *handle, size_t offset, size_t bytes, const void *minimum, const void *maximum)
{
    Guard guard;
    auto m = find((WitU64)handle);
    auto v = slot();
    if (!m || !v || !extent(offset, bytes) || (offset & (Granularity - 1))) {
        return nullptr;
    }
    for (const auto &other : views) {
        if (other.Base && other.Token == m->Token && overlap(offset, bytes, other.Offset, other.Bytes)) {
            return nullptr;
        }
    }
    WitU64 base = 0;
    if (code(WIT_CODE_RESERVE, 0, 0, bytes, 0, Granularity, (WitU64)minimum, maximum ? (WitU64)maximum : ~0ULL,
            &base) != WIT_STATUS_OK) {
        return nullptr;
    }
    if (code(WIT_CODE_MAP_SPARSE, base, m->Backing + offset, bytes, WIT_CODE_READ_EXECUTE) != WIT_STATUS_OK) {
        release(base);
        return nullptr;
    }
    *v = {m->Token, base, offset, bytes, false};
    return (void *)base;
}

void *VMToOSInterface::CommitDoubleMappedMemory(void *start, size_t bytes, bool executable)
{
    Guard guard;
    const WitU64 address = (WitU64)start;
    if (!bytes || (bytes & 4095) || (address & 4095)) {
        return nullptr;
    }
    for (auto &v : views) {
        if (v.Base &&
            !v.Write &&
            address >= v.Base &&
            address - v.Base < v.Bytes &&
            bytes <= v.Bytes - (address - v.Base)) {
            auto m = find(v.Token, false);
            if (!m) {
                wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
            }
            if (wit_native_call(WIT_CALL_MEMORY_COMMIT, m->Backing + v.Offset + address - v.Base, bytes,
                    WIT_MEMORY_NONE, nullptr) != WIT_STATUS_OK) {
                return nullptr;
            }
            if (!executable && code(WIT_CODE_PROTECT, address, 0, bytes, 3) != WIT_STATUS_OK) {
                wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
            }
            return start;
        }
    }
    return nullptr;
}

void *VMToOSInterface::GetRWMapping(void *handle, void *start, size_t offset, size_t bytes)
{
    Guard guard;
    auto m = find((WitU64)handle);
    auto destination = slot();
    if (!m || !destination || !extent(offset, bytes) || (offset & (Granularity - 1))) {
        return nullptr;
    }
    const WitU64 rx = (WitU64)start;
    bool covered = false;
    for (const auto &v : views) {
        if (v.Base &&
            !v.Write &&
            v.Token == m->Token &&
            rx >= v.Base &&
            rx - v.Base < v.Bytes &&
            bytes <= v.Bytes - (rx - v.Base) &&
            offset == v.Offset + rx - v.Base) {
            covered = true;
            break;
        }
    }
    if (!covered) {
        return nullptr;
    }
    WitU64 base = 0;
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, bytes, Granularity, 0, &base) != WIT_STATUS_OK) {
        return nullptr;
    }
    if (code(WIT_CODE_MAP_SPARSE, base, m->Backing + offset, bytes, 3) != WIT_STATUS_OK) {
        release(base);
        return nullptr;
    }
    *destination = {m->Token, base, offset, bytes, true};
    return (void *)base;
}

bool VMToOSInterface::ReleaseRWMapping(void *start, size_t bytes)
{
    Guard guard;
    for (auto &v : views) {
        if (v.Base == (WitU64)start && v.Write && v.Bytes == bytes) {
            auto m = find(v.Token, false);
            if (!m) {
                wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
            }
            release(v.Base);
            v = {};
            reap(*m);
            return true;
        }
    }
    return false;
}

bool VMToOSInterface::ReleaseDoubleMappedMemory(void *handle, void *start, size_t offset, size_t bytes)
{
    Guard guard;
    auto m = find((WitU64)handle, false);
    if (!m || wit_coreclr_code_registered((uint64_t)start, bytes)) {
        return false;
    }
    for (auto &v : views) {
        if (v.Base == (WitU64)start && !v.Write && v.Token == m->Token && v.Offset == offset && v.Bytes == bytes) {
            for (const auto &other : views) {
                if (other.Base &&
                    other.Write &&
                    other.Token == m->Token &&
                    overlap(offset, bytes, other.Offset, other.Bytes)) {
                    return false;
                }
            }
            release(v.Base);
            v = {};
            if (code(WIT_CODE_RESET_SPARSE, m->Backing + offset, 0, bytes) != WIT_STATUS_OK) {
                wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
            }
            reap(*m);
            return true;
        }
    }
    return false;
}

// These optional template methods are likewise unavailable in the pinned Windows
// implementation. Ordinary generated-code allocations above remain real.
void *VMToOSInterface::CreateTemplate(void *, size_t, void (*)(uint8_t *, uint8_t *, size_t))
{
    return nullptr;
}

bool VMToOSInterface::AllocateThunksFromTemplateRespectsStartAddress()
{
    return false;
}

void *VMToOSInterface::AllocateThunksFromTemplate(void *, size_t, void *, void (*)(uint8_t *, size_t))
{
    return nullptr;
}

bool VMToOSInterface::FreeThunksFromTemplate(void *, size_t)
{
    return false;
}
