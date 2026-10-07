#include "pal.witos.h"
#include "NativeContext.h"
#include "native_security.h"
#include "tls.h"
#include "../User/protocol.h"
#include <new>
#include <errno.h>
extern "C" unsigned wit_test_context_cs();
extern "C" unsigned wit_test_context_ss();

static void hijack_target() {}

static bool state()
{
    WitCpuContextInfo info;
    if (wit_native_call(WIT_CALL_CPU_CONTEXT_QUERY, (WitU64)&info, sizeof(info), WIT_CPU_CONTEXT_VERSION, nullptr) !=
            WIT_STATUS_OK ||
        info.Version != WIT_CPU_CONTEXT_VERSION ||
        info.Size != sizeof(info) ||
        info.EnabledState != WIT_CPU_CONTEXT_LEGACY ||
        info.LegacySaveBytes != 512 ||
        info.CodeSelector != wit_test_context_cs() ||
        info.StackSelector != wit_test_context_ss() ||
        (info.DebugPolicy != WIT_CPU_DEBUG_DISABLED) ||
        !info.FloatControlMask ||
        PalAreShadowStacksEnabled() ||
        PalGetHijackTarget(hijack_target) != hijack_target ||
        PalGetHijackTarget(nullptr)) {
        return false;
    }
    uint8_t *buffer = nullptr;
    NATIVE_CONTEXT *context = PalAllocateCompleteOSContext(&buffer);
    if (!context ||
        !buffer ||
        ((uintptr_t)context & 15) ||
        (uintptr_t)context < (uintptr_t)buffer ||
        (uintptr_t)context - (uintptr_t)buffer >= 16 ||
        context->ctx.ContextFlags != (CONTEXT_FULL | CONTEXT_DEBUG_REGISTERS)) {
        return false;
    }
    for (size_t i = 0; i < sizeof(*context); ++i) {
        if ((i < offsetof(CONTEXT, ContextFlags) || i >= offsetof(CONTEXT, ContextFlags) + sizeof(DWORD)) &&
            ((const uint8_t *)context)[i]) {
            return false;
        }
    }
    context->ctx.Rax = 0x1234567812345678ULL;
    context->ctx.Rip = 0x8765432187654321ULL;
    PopulateControlSegmentRegisters(&context->ctx);
    const bool ok = context->ctx.SegCs == info.CodeSelector &&
        context->ctx.SegSs == info.StackSelector &&
        context->ctx.Rax == 0x1234567812345678ULL &&
        context->ctx.Rip == 0x8765432187654321ULL;
    delete[] buffer;
    return ok;
}

static WitU64 worker(WitU64)
{
    SetLastError(9200);
    errno = 9300;
    return state() && GetLastError() == 9200 && errno == 9300 ? WIT_TEST_EXIT_CODE : 3501;
}

extern "C" WitU64 wit_test_context_storage(const WitUserStartup *startup, WitU64 mode)
{
    auto report = (WitU64 *)WIT_GC_INFO_REPORT;
    report[0] = mode;
    report[1] = 549755813888ULL;
    wit_native_security_initialize_system();
    if (!state()) {
        return 3502; // Kernel-backed profile and allocation before publication/constructors.
    }
    wit_native_process_image_initialize(startup);
    if (mode == 88) {
        report[2] = 0x1234;
        PopulateControlSegmentRegisters(nullptr);
        return 3503;
    }
    const bool tls = mode == 86;
    if (tls) {
        wit_native_tls_initialize(startup);
    }
    SetLastError(0x78123456);
    if (tls) {
        errno = 227;
    }
    if (!state() || GetLastError() != 0x78123456) {
        return 3504;
    }
    WitCpuContextInfo info;
    for (size_t i = 0; i < sizeof(info); ++i) {
        ((WitU8 *)&info)[i] = 0x5A;
    }
    if (wit_native_call(WIT_CALL_CPU_CONTEXT_QUERY, (WitU64)&info, sizeof(info) - 1, WIT_CPU_CONTEXT_VERSION,
            nullptr) != WIT_STATUS_INVALID_ARGUMENT ||
        wit_native_call(WIT_CALL_CPU_CONTEXT_QUERY, (WitU64)&info, sizeof(info), 0, nullptr) !=
            WIT_STATUS_UNSUPPORTED) {
        return 3505;
    }
    for (size_t i = 0; i < sizeof(info); ++i) {
        if (((WitU8 *)&info)[i] != 0x5A) {
            return 3506;
        }
    }
    WitU64 arena = 0;
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, 8192, 4096, 0, &arena) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_COMMIT, arena, 4096, 3, nullptr) != WIT_STATUS_OK) {
        return 3507;
    }
    auto edge = (WitU8 *)(arena + 4092);
    for (unsigned i = 0; i < 4; ++i) {
        edge[i] = 0x5A;
    }
    if (wit_native_call(WIT_CALL_CPU_CONTEXT_QUERY, (WitU64)edge, sizeof(info), WIT_CPU_CONTEXT_VERSION, nullptr) !=
            WIT_STATUS_BAD_ADDRESS ||
        wit_native_call(WIT_CALL_CPU_CONTEXT_QUERY, startup->ImageInfo, sizeof(info), WIT_CPU_CONTEXT_VERSION,
            nullptr) != WIT_STATUS_BAD_ADDRESS) {
        return 3508;
    }
    for (unsigned i = 0; i < 4; ++i) {
        if (edge[i] != 0x5A) {
            return 3509;
        }
    }
    if (wit_native_call(WIT_CALL_CPU_CONTEXT_QUERY, arena + 4096 - sizeof(info), sizeof(info), WIT_CPU_CONTEXT_VERSION,
            nullptr) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) {
        return 3510;
    }
    if (PalAllocateCompleteOSContext(nullptr) || GetLastError() != ERROR_INVALID_PARAMETER) {
        return 3511;
    }
    uint8_t *held[256];
    unsigned count = 0;
    while (count < 256 && (held[count] = new (std::nothrow) uint8_t[1]) != nullptr) {
        ++count;
    }
    uint8_t *output = (uint8_t *)0x1234;
    if (!count ||
        count == 256 ||
        PalAllocateCompleteOSContext(&output) ||
        output ||
        GetLastError() != ERROR_NOT_ENOUGH_MEMORY) {
        return 3512;
    }
    while (count) {
        delete[] held[--count];
    }
    SetLastError(0x78123456);
    if (!state()) {
        return 3513;
    }
    if (tls) {
        WitU64 handles[3], result;
        for (unsigned i = 0; i < 3; ++i) {
            if (wit_native_thread_create(worker, i, &handles[i]) != WIT_STATUS_OK) {
                return 3514;
            }
        }
        for (unsigned i = 0; i < 3; ++i) {
            if (wit_native_thread_join(handles[i], &result) != WIT_STATUS_OK || result != WIT_TEST_EXIT_CODE) {
                return 3515;
            }
        }
        wit_native_tls_leave();
    }
    if (GetLastError() != 0x78123456 || (tls && errno != 227)) {
        return 3516;
    }
    return WIT_TEST_EXIT_CODE;
}
