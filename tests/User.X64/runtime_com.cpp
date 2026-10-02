#include "pal.witos.h"
#include "tls.h"
#include "native_security.h"
#include "native_process.h"
#include "com_counter.witos.h"
#include "protocol.h"
#include <objbase.h>
#include <errno.h>
extern "C" HRESULT WINAPI wit_test_com_initialize(LPVOID, DWORD);
extern "C" HRESULT WINAPI wit_test_com_apartment(APTTYPE *, APTTYPEQUALIFIER *);
extern "C" void WINAPI wit_test_com_uninitialize();
extern "C" const void *const __imp_CoInitializeEx;
static HANDLE readyEvent, leaveEvent;
static volatile WitU64 observed;

static bool apartment(HRESULT expected, APTTYPEQUALIFIER qualifier = APTTYPEQUALIFIER_NONE)
{
    APTTYPE type = (APTTYPE)99;
    APTTYPEQUALIFIER q = (APTTYPEQUALIFIER)99;
    const HRESULT result = CoGetApartmentType(&type, &q);
    return result == expected && type == (expected == S_OK ? APTTYPE_MTA : APTTYPE_CURRENT) && q == qualifier;
}

static void on_exit(void *)
{
    if (!apartment(S_OK)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    CoUninitialize(); // One of two references; platform cleanup must drop the rest later.
    if (!apartment(S_OK)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    ++observed;
}

static void at_exit()
{
    if (!apartment(S_OK)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    ++observed;
}

static void conflict(void *)
{
    ++observed;
}

static WitU64 participant(WitU64)
{
    SetLastError(7200);
    errno = 7300;
    if (!apartment(CO_E_NOTINITIALIZED) ||
        CoInitializeEx(nullptr, 0) != S_OK ||
        CoInitializeEx(nullptr, 0) != S_FALSE ||
        wit_native_thread_on_exit(on_exit, nullptr) != WIT_STATUS_OK ||
        !SetEvent(readyEvent)) {
        return 3301;
    }
    if (WaitForMultipleObjectsEx(1, &leaveEvent, FALSE, INFINITE, FALSE) != WAIT_OBJECT_0 ||
        GetLastError() != 7200 ||
        errno != 7300) {
        return 3302;
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" WitU64 wit_test_com_lifecycle(const WitUserStartup *startup)
{
    const auto mode = ((const WitUserTestConfig *)startup)->Mode;
    auto report = (WitU64 *)WIT_GC_INFO_REPORT;
    report[0] = mode;
    report[1] = 137438953472ULL;
    wit_native_security_initialize_system();
    if (!apartment(CO_E_NOTINITIALIZED) ||
        CoInitializeEx(nullptr, 0) != E_UNEXPECTED ||
        !apartment(CO_E_NOTINITIALIZED)) {
        return 3303;
    }
    wit_native_process_image_initialize(startup);
    if (mode == 80) {
        if (CoInitializeEx(nullptr, 0) != E_UNEXPECTED || !apartment(CO_E_NOTINITIALIZED)) {
            return 3304;
        }
        CoUninitialize();
        return WIT_TEST_EXIT_CODE;
    }
    wit_native_tls_initialize(startup);
    SetLastError(0x56781234);
    errno = 207;
    if (mode == 81) {
        if (wit_native_thread_on_cleanup(conflict, nullptr) != WIT_STATUS_OK ||
            wit_native_thread_on_cleanup(conflict, nullptr) != WIT_STATUS_BUSY ||
            CoInitializeEx(nullptr, 0) != E_UNEXPECTED ||
            !apartment(CO_E_NOTINITIALIZED)) {
            return 3305;
        }
        wit_native_tls_leave();
        wit_native_thread_notify_exit();
        wit_native_thread_notify_exit();
        if (observed != 1 || CoInitializeEx(nullptr, 0) != E_UNEXPECTED || !apartment(CO_E_NOTINITIALIZED)) {
            return 3306;
        }
        report[2] = observed;
        return WIT_TEST_EXIT_CODE;
    }
    uint32_t count = UINT32_MAX;
    if (wit_com_add_reference(count) || count != UINT32_MAX) {
        return 3307;
    }
    count = UINT32_MAX - 1;
    if (!wit_com_add_reference(count) || count != UINT32_MAX) {
        return 3308;
    }
    APTTYPE type = (APTTYPE)99;
    APTTYPEQUALIFIER q = (APTTYPEQUALIFIER)99;
    if (CoGetApartmentType(nullptr, &q) != E_INVALIDARG ||
        q != 99 ||
        CoGetApartmentType(&type, nullptr) != E_INVALIDARG ||
        type != 99 ||
        CoInitializeEx((void *)1, 0) != E_INVALIDARG ||
        CoInitializeEx(nullptr, 0x80000000) != E_INVALIDARG ||
        CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED) != E_NOTIMPL ||
        !apartment(CO_E_NOTINITIALIZED)) {
        return 3309;
    }
    if (wit_test_com_initialize(nullptr, COINIT_DISABLE_OLE1DDE | COINIT_SPEED_OVER_MEMORY) != S_OK ||
        CoInitializeEx(nullptr, 0) != S_FALSE ||
        wit_test_com_apartment(&type, &q) != S_OK ||
        type != APTTYPE_MTA ||
        q != APTTYPEQUALIFIER_NONE ||
        CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED) != RPC_E_CHANGED_MODE) {
        return 3310;
    }
    WitUserThreadInfo info;
    if (wit_native_call(WIT_CALL_THREAD_QUERY, (WitU64)&info, sizeof(info), WIT_THREAD_INFO_VERSION, nullptr) !=
        WIT_STATUS_OK) {
        return 3311;
    }
    auto raw = (WitU64 *)info.RawTls;
    const WitU64 id = raw[1];
    raw[1] = ~id;
    const bool identity = CoInitializeEx(nullptr, 0) == S_FALSE && apartment(S_OK);
    raw[1] = id;
    if (!identity) {
        return 3312;
    }
    CoUninitialize();
    wit_test_com_uninitialize();
    if (!apartment(S_OK)) {
        return 3313;
    }
    CoUninitialize();
    if (!apartment(CO_E_NOTINITIALIZED)) {
        return 3314;
    }
    CoUninitialize();
    if (!apartment(CO_E_NOTINITIALIZED)) {
        return 3315;
    }
    readyEvent = CreateEventExW(nullptr, nullptr, 0, SYNCHRONIZE | EVENT_MODIFY_STATE);
    leaveEvent = CreateEventExW(nullptr, nullptr, 0, SYNCHRONIZE | EVENT_MODIFY_STATE);
    if (!readyEvent || !leaveEvent) {
        return 3316;
    }
    for (unsigned pass = 0; pass < 3; ++pass) {
        WitU64 handle = 0, result = 0;
        if (wit_native_thread_create(participant, pass, &handle) != WIT_STATUS_OK ||
            WaitForMultipleObjectsEx(1, &readyEvent, FALSE, INFINITE, FALSE) != WAIT_OBJECT_0 ||
            !apartment(S_OK, APTTYPEQUALIFIER_IMPLICIT_MTA)) {
            return 3317;
        }
        CoUninitialize();
        if (!apartment(S_OK, APTTYPEQUALIFIER_IMPLICIT_MTA) ||
            !SetEvent(leaveEvent) ||
            wit_native_call(WIT_CALL_THREAD_JOIN, handle, 0, 0, &result) != WIT_STATUS_OK ||
            result != WIT_TEST_EXIT_CODE ||
            !apartment(CO_E_NOTINITIALIZED) ||
            observed != pass + 1) {
            return 3318;
        }
    }
    if (!CloseHandle(readyEvent) ||
        !CloseHandle(leaveEvent) ||
        CoInitializeEx(nullptr, 0) != S_OK ||
        CoInitializeEx(nullptr, 0) != S_FALSE ||
        wit_native_thread_on_exit(on_exit, nullptr) != WIT_STATUS_OK) {
        return 3319;
    }
    if (mode == 79) {
        if (atexit(at_exit)) {
            return 3320;
        }
        wit_native_process_shutdown();
    } else {
        wit_native_tls_leave();
        wit_native_thread_notify_exit();
    }
    wit_native_thread_notify_exit();
    if (!apartment(CO_E_NOTINITIALIZED) ||
        observed != (mode == 79 ? 5U : 4U) ||
        CoInitializeEx(nullptr, 0) != E_UNEXPECTED ||
        GetLastError() != 0x56781234 ||
        errno != 207) {
        return 3321;
    }
    const auto image = wit_native_process_image();
    if (!wit_native_image_range(image, (WitU64)&__imp_CoInitializeEx, 8, WIT_IMAGE_INFO_READ,
            WIT_IMAGE_INFO_WRITE | WIT_IMAGE_INFO_EXECUTE, 1)) {
        return 3322;
    }
    report[2] = observed;
    return WIT_TEST_EXIT_CODE;
}
