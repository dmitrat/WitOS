#include "tls.h"
#include "native_security.h"
#include "native_process.h"
#include "error.h"
#include "pal_environment.witos.h"

// This entry is compiled without GS because it seeds the cookie before entering
// protected runtime code. The runtime archive retains its production GS profile.
using CInitializer = int(__cdecl *)();
using CppInitializer = void(__cdecl *)();
#pragma section(".CRT$XIA", read)
#pragma section(".CRT$XIZ", read)
#pragma section(".CRT$XCA", read)
#pragma section(".CRT$XCZ", read)
extern "C" {
__declspec(allocate(".CRT$XIA")) CInitializer const wit_runtime_c_begin[] = {nullptr};
__declspec(allocate(".CRT$XIZ")) CInitializer const wit_runtime_c_end[] = {nullptr};
__declspec(allocate(".CRT$XCA")) CppInitializer const wit_runtime_cpp_begin[] = {nullptr};
__declspec(allocate(".CRT$XCZ")) CppInitializer const wit_runtime_cpp_end[] = {nullptr};
int __cdecl wmain(int, wchar_t **);
int wit_runtime_worker_acceptance(int (*callback)(int));
int wit_runtime_native_fault(int (*callback)(int));
int wit_runtime_stack_overflow(int (*callback)(int));
int wit_runtime_raw_join(int (*)(int));
int wit_runtime_raw_detached(int (*)(int));
int wit_runtime_fault_join(int (*)(int));
int wit_runtime_fault_detached(int (*)(int));
}

namespace {
volatile WitU32 started;
const WitPalEnvironmentEntry environment[] = {
    {L"DOTNET_GCHeapHardLimit", L"400000", (uint32_t)(sizeof(L"DOTNET_GCHeapHardLimit") / sizeof(wchar_t) - 1), 6}};

void write(const char *text, WitU64 size)
{
    WitU64 written = 0;
    if (wit_native_call(WIT_CALL_WRITE, wit_native_process_console(), (WitU64)text, size, &written) != WIT_STATUS_OK ||
        written != size) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
}

template <WitU64 N> void mark(const char (&text)[N])
{
    write(text, N - 1);
}

bool table(WitU64 first, WitU64 last)
{
    const auto image = wit_native_process_image();
    if (last <= first ||
        last > ~0ULL - 8 ||
        (last - first) % 8 ||
        (last - first) / 8 - 1 > 64 ||
        !wit_native_image_range(
            image, first, last - first + 8, WIT_IMAGE_INFO_READ, WIT_IMAGE_INFO_WRITE | WIT_IMAGE_INFO_EXECUTE, 1)) {
        return false;
    }
    if (*(const WitU64 *)first || *(const WitU64 *)last) {
        return false;
    }
    for (WitU64 at = first + 8; at < last; at += 8) {
        const WitU64 callback = *(const WitU64 *)at;
        if (callback &&
            !wit_native_image_range(
                image, callback, 1, WIT_IMAGE_INFO_READ | WIT_IMAGE_INFO_EXECUTE, WIT_IMAGE_INFO_WRITE, 1)) {
            return false;
        }
    }
    return true;
}
}

extern "C" WitU64 wit_native_main(const WitUserStartup *startup)
{
    if (!wit_native_claim_startup(&started)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    wit_native_process_image_initialize(startup);
    mark("[RUNTIME] image published\n");
    if (!wit_pal_environment_initialize(environment, 1)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    wit_native_security_initialize_system();
    if (!table((WitU64)wit_runtime_c_begin, (WitU64)wit_runtime_c_end) ||
        !table((WitU64)wit_runtime_cpp_begin, (WitU64)wit_runtime_cpp_end)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    wit_native_tls_initialize(startup);
    mark("[RUNTIME] native TLS ready\n");
    for (WitU64 at = (WitU64)wit_runtime_c_begin + 8; at < (WitU64)wit_runtime_c_end; at += 8) {
        const auto callback = *(const CInitializer *)at;
        if (callback && callback() != 0) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
    }
    for (WitU64 at = (WitU64)wit_runtime_cpp_begin + 8; at < (WitU64)wit_runtime_cpp_end; at += 8) {
        const auto callback = *(const CppInitializer *)at;
        if (callback) {
            callback();
        }
    }
    mark("[RUNTIME] native initializers ready\n");
    const auto image = wit_native_process_image();
    const wchar_t faultName[] = L"boot:/WitOS.NativeAotBoot.native-fault.pe";
    bool nativeFault = image->ResourceNameLength == sizeof(faultName) / sizeof(wchar_t) - 1;
    for (WitU32 i = 0; nativeFault && i < image->ResourceNameLength; ++i) {
        nativeFault = image->ResourceName[i] == faultName[i];
    }
    auto entry = nativeFault ? &wit_runtime_native_fault : &wit_runtime_worker_acceptance;
    const wchar_t *labels[] = {L"boot:/WitOS.NativeAotBoot.raw-join.pe", L"boot:/WitOS.NativeAotBoot.raw-detached.pe",
        L"boot:/WitOS.NativeAotBoot.fault-join.pe", L"boot:/WitOS.NativeAotBoot.fault-detached.pe",
        L"boot:/WitOS.NativeAotBoot.stack-overflow.pe"};
    const decltype(entry) entries[] = {&wit_runtime_raw_join, &wit_runtime_raw_detached, &wit_runtime_fault_join,
        &wit_runtime_fault_detached, &wit_runtime_stack_overflow};
    for (unsigned n = 0; n < 5; ++n) {
        unsigned length = 0;
        while (labels[n][length]) {
            ++length;
        }
        bool match = length == image->ResourceNameLength;
        for (unsigned i = 0; match && i < length; ++i) {
            match = image->ResourceName[i] == labels[n][i];
        }
        if (match) {
            entry = entries[n];
        }
    }
    wchar_t name[] = L"WitOS.NativeAotBoot";
    wchar_t fixture[17] = {};
    const wchar_t hex[] = L"0123456789ABCDEF";
    for (unsigned i = 0; i < 16; ++i) {
        fixture[i] = hex[((WitU64)entry >> (60 - i * 4)) & 15];
    }
    wchar_t *arguments[] = {name, fixture, nullptr};
    mark("[RUNTIME] entering upstream wmain\n");
    const int result = wmain(2, arguments);
    const WitU32 error = wit_native_error_get();
    char message[] = "[RUNTIME] wmain returned 0x0000000000000000 last-error=0x0000000000000000\n";
    const char digits[] = "0123456789ABCDEF";
    for (unsigned i = 0; i < 16; ++i) {
        message[sizeof("[RUNTIME] wmain returned 0x") - 1 + i] = digits[((WitU64)(WitU32)result >> (60 - i * 4)) & 15];
        message[sizeof("[RUNTIME] wmain returned 0x0000000000000000 last-error=0x") - 1 + i] =
            digits[((WitU64)error >> (60 - i * 4)) & 15];
    }
    write(message, sizeof(message) - 1);
    wit_native_process_exit((WitU32)result);
}
