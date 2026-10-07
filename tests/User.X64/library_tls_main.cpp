#include "native_process.h"
extern "C" {
#include "library.h"
extern unsigned _tls_index;
}
#include "../User/protocol.h"
[[msvc::no_tls_guard]] static __declspec(thread) int mainValue = 911;
static WitU64 getValue, setValue, getAddress;
static WitU64 parentAddress;

static WitU64 *report()
{
    return (WitU64 *)WIT_GC_INFO_REPORT;
}

static bool snapshot(WitUserMemoryInfo &v)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (WitU64)&v, sizeof(v), WIT_MEMORY_INFO_VERSION, nullptr) ==
        WIT_STATUS_OK;
}

static bool same(const WitUserMemoryInfo &a, const WitUserMemoryInfo &b)
{
    return a.OwnedBytes == b.OwnedBytes &&
        a.PrivatePageTableBytes == b.PrivatePageTableBytes &&
        a.PhysicalAvailableBytes == b.PhysicalAvailableBytes &&
        a.ReservedBytes == b.ReservedBytes &&
        a.ReservationCount == b.ReservationCount;
}

static WitU64 worker(WitU64)
{
    if (_tls_index ||
        mainValue != 911 ||
        ((int (*)())getValue)() != 736 ||
        (WitU64)((void *(*)())getAddress)() == parentAddress) {
        return 3501;
    }
    mainValue = 17;
    ((void (*)(int))setValue)(7);
    return ((int (*)())getValue)() == 12 ? 42 : 3502;
}

extern "C" WitU64 wit_library_tls_main_probe(unsigned mode, WitU64 remainingPages)
{
    WitUserThreadInfo initial, after;
    WitUserMemoryInfo baseline, loaded, final;
    if (_tls_index ||
        mainValue != 911 ||
        !snapshot(baseline) ||
        wit_native_thread_query(WIT_THREAD_SELF, &initial) != WIT_STATUS_OK ||
        !initial.CompilerTls) {
        return 3503;
    }
    const WitU64 mainSlot = ((WitU64 *)initial.CompilerTls)[0x80 / 8];
    mainValue = 99;
    const char path[] = "/native/statictls.dll";
    WitU64 module = 99;
    const auto status = wit_native_library_load(path, sizeof(path) - 1, &module);
    if (mode == 21 && status == WIT_STATUS_NO_MEMORY) {
        report()[14] = 1;
        return module == 99 && snapshot(final) && same(baseline, final) ? 43 : 3504;
    }
    if (status != WIT_STATUS_OK) {
        return 3505;
    }
    if (wit_native_library_symbol(module, "TlsValue", 8, 0, &getValue) != WIT_STATUS_OK ||
        wit_native_library_symbol(module, "TlsSet", 6, 0, &setValue) != WIT_STATUS_OK ||
        wit_native_library_symbol(module, "TlsAddress", 10, 0, &getAddress) != WIT_STATUS_OK) {
        return 3506;
    }
    if (((int (*)())getValue)() != 736 ||
        mainValue != 99 ||
        _tls_index ||
        ((WitU64 *)initial.CompilerTls)[0x80 / 8] != mainSlot) {
        return 3507;
    }
    parentAddress = (WitU64)((void *(*)())getAddress)();
    ((void (*)(int))setValue)(20);
    WitU64 filler = 0;
    if (mode == 21) {
        if (wit_native_call(WIT_CALL_MEMORY_RESERVE, baseline.OwnedLimitBytes, 4096, 0, &filler) != WIT_STATUS_OK ||
            wit_native_call(WIT_CALL_MEMORY_COMMIT, filler, 4096, 3, nullptr) != WIT_STATUS_OK ||
            !snapshot(final)) {
            return 3516;
        }
        WitU64 committed = 4096;
        while (final.OwnedLimitBytes - final.OwnedBytes > remainingPages * 4096) {
            const WitU64 prior = final.OwnedBytes;
            if (wit_native_call(WIT_CALL_MEMORY_COMMIT, filler + committed, 4096, 3, nullptr) != WIT_STATUS_OK ||
                !snapshot(final) ||
                final.OwnedBytes != prior + 4096) {
                return 3517;
            }
            committed += 4096;
        }
        if (final.OwnedLimitBytes - final.OwnedBytes != remainingPages * 4096) {
            return 3518;
        }
    }
    if (!snapshot(loaded)) {
        return 3508;
    }
    WitU64 thread = 0;
    WitU32 id = 0xA5A5A5A5;
    const auto created = wit_native_thread_create_reference(worker, 0, 0, 0, (WitU64)&id, &thread);
    if (mode == 21 && created == WIT_STATUS_NO_MEMORY) {
        report()[14] = 2;
        if (thread ||
            id != 0xA5A5A5A5 ||
            mainValue != 99 ||
            ((int (*)())getValue)() != 25 ||
            !snapshot(final) ||
            !same(loaded, final)) {
            return 3509;
        }
        if (filler && wit_native_call(WIT_CALL_MEMORY_RELEASE, filler, 0, 0, nullptr) != WIT_STATUS_OK) {
            return 3519;
        }
        if (wit_native_library_unload(module) != WIT_STATUS_OK || !snapshot(final) || !same(baseline, final)) {
            return 3510;
        }
        return 43;
    }
    if (created != WIT_STATUS_OK || !thread || !id) {
        return 3511;
    }
    WitUserThreadInfo state;
    for (;;) {
        if (wit_native_thread_query((WitU64)thread, &state) != WIT_STATUS_OK) {
            return 3512;
        }
        if (state.State == WIT_THREAD_STATE_EXITED) {
            break;
        }
        (void)wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr);
    }
    if (state.ExitCode != 42 ||
        wit_native_call(WIT_CALL_CLOSE, thread, 0, 0, nullptr) != WIT_STATUS_OK ||
        mainValue != 99 ||
        ((int (*)())getValue)() != 25) {
        return 3513;
    }
    if (filler && wit_native_call(WIT_CALL_MEMORY_RELEASE, filler, 0, 0, nullptr) != WIT_STATUS_OK) {
        return 3519;
    }
    if (wit_native_library_unload(module) != WIT_STATUS_OK ||
        wit_native_thread_query(WIT_THREAD_SELF, &after) != WIT_STATUS_OK ||
        after.CompilerTls != initial.CompilerTls ||
        ((WitU64 *)after.CompilerTls)[0x80 / 8] != mainSlot ||
        _tls_index ||
        mainValue != 99) {
        return 3514;
    }
    if (!snapshot(final) || !same(baseline, final)) {
        return 3515;
    }
    report()[14] = 3;
    return 42;
}
