#include "bootstrap.h"
#include "protocol.h"

#pragma section(".tls", long, read, write)
#pragma section(".tls$ZZZ", long, read, write)
__declspec(allocate(".tls")) char _tls_start = 0;
__declspec(allocate(".tls$ZZZ")) char _tls_end = 0;
WitU32 _tls_index = 99; /* Loader must assign the supported module index. */
typedef struct TlsDirectory {
    const void *Start, *End, *Index, *Callbacks;
    WitU32 ZeroFill, Characteristics;
} TlsDirectory;
const WitU64 tls_callbacks[1] = { 0 };
const TlsDirectory _tls_used = { &_tls_start, &_tls_end, &_tls_index, tls_callbacks, 32, 0x00500000 };
WitU64 tls_marker = 0xABCDEF;
__declspec(thread) volatile WitU64 tls_value = 0x12345678;
__declspec(thread) volatile WitU64 tls_zero;
__declspec(thread) WitU64 *tls_pointer = &tls_marker;
WitU64 wit_tls_read(void);
void wit_tls_write(WitU64 value);
WitU64 wit_tls_address(void);
WitU64 wit_tls_zeros(void);
WitU64 wit_tls_pointer(void);
WitU64 __readgsqword(unsigned long offset);
#pragma intrinsic(__readgsqword)
static WitU64 addresses[3];
static WitU64 call(WitU64 op, WitU64 a, WitU64 b, WitU64 *result)
{
    return wit_native_call(op, a, b, 0, result);
}
static void done(WitU64 code)
{
    (void)call(WIT_CALL_THREAD_EXIT, code, 0, 0);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}
static void worker(WitU64 index)
{
    WitU64 start, now;
    if (wit_tls_read() != 0x12345678 || wit_tls_zeros() || wit_tls_pointer() != (WitU64)&tls_marker) done(901);
    addresses[index] = wit_tls_address();
    wit_tls_write(100 + index);
    if (index < 2) {
        if (call(WIT_CALL_CLOCK_READ, 0, 0, &start) != WIT_STATUS_OK) done(902);
        do {
            if (wit_tls_read() != 100 + index || wit_tls_pointer() != (WitU64)&tls_marker) done(903);
            if (call(WIT_CALL_CLOCK_READ, 0, 0, &now) != WIT_STATUS_OK) done(904);
        } while (now - start < 2);
    }
    tls_zero = 999;
    done(WIT_TEST_EXIT_CODE);
}
WitU64 wit_native_main(const WitUserStartup *startup)
{
    const WitUserTestConfig *config = (const WitUserTestConfig *)startup;
    WitU64 handles[2], result, block;
    if (_tls_index != 0 || wit_tls_read() != 0x12345678 || wit_tls_zeros() ||
        wit_tls_pointer() != (WitU64)&tls_marker) return 905;
    block = *(const WitU64 *)__readgsqword(0x58);
    for (WitU32 i = 0; i < 32; ++i) if (*(volatile WitU8 *)(block + config->InstanceId + i)) return 906;
    if (config->Mode == 1) {
        *(volatile WitU8 *)block = 0xC3;
        ((void(*)(void))block)();
        return 907;
    }
    if (config->Mode == 2) {
        *(volatile WitU64 *)(config->ReadOnlyHandle + 0x58) = config->ForeignHandle;
        return wit_tls_read(); /* Forged GS data cannot grant supervisor access. */
    }
    wit_tls_write(77);
    for (WitU64 i = 0; i < 2; ++i)
        if (call(WIT_CALL_THREAD_CREATE, (WitU64)worker, i, &handles[i]) != WIT_STATUS_OK) return 908;
    for (WitU64 i = 0; i < 2; ++i)
        if (call(WIT_CALL_THREAD_JOIN, handles[i], 0, &result) != WIT_STATUS_OK || result != WIT_TEST_EXIT_CODE) return 909;
    if (addresses[0] == addresses[1] || addresses[0] == wit_tls_address() || addresses[1] == wit_tls_address() ||
        wit_tls_read() != 77 || wit_tls_zeros()) return 910;
    if (call(WIT_CALL_THREAD_CREATE, (WitU64)worker, 2, &handles[0]) != WIT_STATUS_OK ||
        call(WIT_CALL_THREAD_JOIN, handles[0], 0, &result) != WIT_STATUS_OK || result != WIT_TEST_EXIT_CODE ||
        addresses[2] != addresses[0]) return 911;
    // Parking the only runnable thread exercises GS restoration after kernel idle.
    if (call(WIT_CALL_CLOCK_READ, 0, 0, &result) != WIT_STATUS_OK ||
        call(WIT_CALL_THREAD_SLEEP, result + 1, 0, 0) != WIT_STATUS_OK || wit_tls_read() != 77) return 912;
    return WIT_TEST_EXIT_CODE;
}
