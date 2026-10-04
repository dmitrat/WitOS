#include <stdint.h>
#include "minipal.h"
extern "C" {
#include "bootstrap.h"
#include "../User/protocol.h"
}
extern "C" bool wit_dynamic_unwind_probe(unsigned);
extern "C" WitU64 wit_module_unwind_probe(unsigned);
extern "C" WitU64 wit_cxx_exceptions_probe();
static bool (*volatile createMapper)(void **, size_t *) = &VMToOSInterface::CreateDoubleMemoryMapper;

static bool snapshot(WitUserMemoryInfo &value)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (WitU64)&value, sizeof(value), WIT_MEMORY_INFO_VERSION, nullptr) ==
        WIT_STATUS_OK;
}

static bool same(const WitUserMemoryInfo &a, const WitUserMemoryInfo &b)
{
    return a.OwnedBytes == b.OwnedBytes &&
        a.PrivatePageTableBytes == b.PrivatePageTableBytes &&
        a.ReservedBytes == b.ReservedBytes &&
        a.DynamicCommittedBytes == b.DynamicCommittedBytes &&
        a.PhysicalAvailableBytes == b.PhysicalAvailableBytes &&
        a.ReservationCount == b.ReservationCount;
}

extern "C" WitU64 wit_native_main(const WitUserStartup *startup)
{
    if (!startup || startup->Version != WIT_ABI_VERSION || startup->Size != sizeof(*startup)) {
        return 1800;
    }
    wit_native_process_image_initialize(startup);
    const auto mode = ((const WitUserTestConfig *)startup)->Mode;
    if (mode == 20) {
        return wit_module_unwind_probe(20);
    }
    if (mode == 21) {
        return wit_cxx_exceptions_probe();
    }
    if (mode >= 2) {
        return wit_dynamic_unwind_probe((unsigned)mode) ? 42 : 1815;
    }
    WitUserMemoryInfo baseline = {}, reserved = {}, after = {};
    if (!snapshot(baseline)) {
        return 1801;
    }
    void *mapper = nullptr;
    size_t maximum = 0;
    if (!createMapper(&mapper, &maximum) || maximum != 64 * 1024 * 1024 || !mapper) {
        return 1802;
    }
    auto rx = (unsigned char *)VMToOSInterface::ReserveDoubleMappedMemory(mapper, 0, 65536, nullptr, nullptr);
    auto rw = (unsigned char *)VMToOSInterface::GetRWMapping(mapper, rx, 0, 65536);
    if (!rx ||
        !rw ||
        rx == rw ||
        !snapshot(reserved) ||
        reserved.OwnedBytes != baseline.OwnedBytes ||
        reserved.DynamicCommittedBytes != baseline.DynamicCommittedBytes) {
        return 1803;
    }
    if (mode) {
        if (VMToOSInterface::CommitDoubleMappedMemory(rx, 4096, true) || !snapshot(after) || !same(reserved, after)) {
            return 1804;
        }
    } else {
        if (VMToOSInterface::CommitDoubleMappedMemory(rx, 4096, true) != rx) {
            return 1805;
        }
        const unsigned char code[] = {0xB8, 42, 0, 0, 0, 0xC3};
        for (unsigned i = 0; i < sizeof(code); ++i) {
            rw[i] = code[i];
        }
        WitCodeMemoryRequest publish = {
            WIT_CODE_MEMORY_VERSION, sizeof(publish), WIT_CODE_PUBLISH, 0, (WitU64)rx, 0, sizeof(code), 0, 0, 0};
        if (wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&publish, sizeof(publish), 0, nullptr) != WIT_STATUS_OK ||
            ((int (*)())rx)() != 42) {
            return 1806;
        }
        if (VMToOSInterface::CommitDoubleMappedMemory(rx + 4096, 4096, true) != rx + 4096) {
            return 1807;
        }
        rw[4096] = 73;
        if (rx[4096] != 73) {
            return 1808;
        }
        if (VMToOSInterface::ReleaseDoubleMappedMemory(mapper, rx, 0, 65536)) {
            return 1809; // Live writer must be released first.
        }
    }
    if (VMToOSInterface::ReleaseRWMapping(rw, 4096) ||
        !VMToOSInterface::ReleaseRWMapping(rw, 65536) ||
        VMToOSInterface::ReleaseRWMapping(rw, 65536) ||
        !VMToOSInterface::ReleaseDoubleMappedMemory(mapper, rx, 0, 65536)) {
        return 1810;
    }
    if (!mode) {
        auto reused = (unsigned char *)VMToOSInterface::ReserveDoubleMappedMemory(mapper, 0, 65536, nullptr, nullptr);
        if (!reused ||
            VMToOSInterface::CommitDoubleMappedMemory(reused, 8192, true) != reused ||
            reused[0] ||
            reused[4096]) {
            return 1811;
        }
        VMToOSInterface::DestroyDoubleMemoryMapper(mapper);
        if (VMToOSInterface::ReserveDoubleMappedMemory(mapper, 65536, 65536, nullptr, nullptr) ||
            !VMToOSInterface::ReleaseDoubleMappedMemory(mapper, reused, 0, 65536)) {
            return 1812;
        }
    } else {
        VMToOSInterface::DestroyDoubleMemoryMapper(mapper);
    }
    void *next = nullptr;
    if (!createMapper(&next, &maximum) ||
        next == mapper ||
        VMToOSInterface::ReserveDoubleMappedMemory(mapper, 0, 65536, nullptr, nullptr)) {
        return 1813;
    }
    VMToOSInterface::DestroyDoubleMemoryMapper(next);
    if (!mode && !wit_dynamic_unwind_probe(0)) {
        return 1815;
    }
    if (!mode) {
        const auto module = wit_module_unwind_probe(0);
        if (module != 42) {
            return module;
        }
    }
    return snapshot(after) && same(baseline, after) ? 42 : 1814;
}
