#include "tls.h"
#include "protocol.h"
#include <new>

extern "C" WitU64 wit_dynamic_primitive(void);
extern "C" int __cdecl __tlregdtor(void (__cdecl *)(void));
extern "C" void __cdecl __dyn_tls_on_demand_init() noexcept;
static WitU64 mode, release_event;
static volatile WitU64 reports[4];
__declspec(thread) WitU64 primitive = 77;
static __declspec(thread) WitU64 sequence, constructed, report_index;
static void extra_cleanup(void) { sequence = sequence * 10 + 9; }
static void loop_cleanup(void) { (void)__tlregdtor(loop_cleanup); }
class TlsObject {
public:
    explicit TlsObject(WitU64 id) noexcept : m_id(id), m_data(::operator new(16, std::nothrow))
    {
        if (!m_data || wit_dynamic_primitive() != 77) wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        *(WitU64 *)m_data = id + 100;
        ++constructed;
        sequence = sequence * 10 + id;
        if (mode == 4 && id == 2) {
            *(WitU64 *)WIT_GC_INFO_REPORT = mode;
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
    }
    ~TlsObject() noexcept
    {
        if (*(WitU64 *)m_data != m_id + 100) wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        sequence = sequence * 10 + m_id;
        ::operator delete(m_data);
        if (mode == 8 && m_id == 2) (void)__tlregdtor(extra_cleanup);
        if (mode == 10 && m_id == 2) { *(WitU64 *)WIT_GC_INFO_REPORT = mode; (void)__tlregdtor(loop_cleanup); }
        if (mode == 5 || mode == 7) {
            *(WitU64 *)WIT_GC_INFO_REPORT = mode;
            if (mode == 5) *(volatile WitU64 *)0 = 1;
            wit_native_tls_leave(); // Recursive teardown must fail fast.
        }
        reports[report_index] = sequence;
    }
private:
    WitU64 m_id;
    void *m_data;
};
static thread_local TlsObject first(1);
static thread_local TlsObject second(2);
static void noop(void) { }
extern "C" void wit_dynamic_configure(const WitUserStartup *startup)
{
    mode = ((const WitUserTestConfig *)startup)->Mode;
    if (mode == 9) *(WitU64 *)WIT_GC_INFO_REPORT = mode;
}
static WitU64 worker(WitU64 index)
{
    WitU64 ignored;
    report_index = index;
    if (constructed != 2 || sequence != 12 || primitive != 77) return 1001;
    __dyn_tls_on_demand_init(); wit_native_tls_enter();
    if (constructed != 2) return 1002;
    primitive = 100 + index;
    if (wit_native_call(WIT_CALL_EVENT_WAIT, release_event, WIT_WAIT_INFINITE, 0, &ignored) != WIT_STATUS_OK ||
        primitive != 100 + index) return 1003;
    if (mode == 1) wit_native_thread_exit(WIT_TEST_EXIT_CODE);
    return WIT_TEST_EXIT_CODE;
}
extern "C" void wit_dynamic_lazy_entry(WitU64 index);
extern "C" WitU64 wit_dynamic_lazy_body(WitU64 index)
{
    // No explicit enter: the separately compiled accessor emits the guard/helper.
    if (wit_dynamic_primitive() != 77) return 1004;
    return worker(index);
}
extern "C" WitU64 wit_dynamic_program(const WitUserStartup *startup)
{
    if (constructed != 2 || sequence != 12 || primitive != 77) return 1010;
    if (mode == 2 || mode == 3 || mode == 6) {
        *(WitU64 *)WIT_GC_INFO_REPORT = mode;
        if (mode == 2) for (WitU32 i = 0; i <= WIT_NATIVE_TLS_MAX_DESTRUCTORS; ++i) (void)__tlregdtor(noop);
        if (mode == 3) (void)__tlregdtor((void(__cdecl *)(void))startup);
        if (mode == 6) { wit_native_tls_leave(); wit_native_tls_enter(); }
        return 1011;
    }
    if (mode == 5 || mode == 7 || mode == 10) return WIT_TEST_EXIT_CODE;
    WitU64 handles[3], result;
    if (wit_native_thread_create(nullptr, 0, &result) != WIT_STATUS_BAD_ADDRESS || result ||
        wit_native_thread_create(worker, 0, nullptr) != WIT_STATUS_INVALID_ARGUMENT) return 1012;
    if (wit_native_call(WIT_CALL_EVENT_CREATE, WIT_EVENT_MANUAL_RESET, 0, 0, &release_event) != WIT_STATUS_OK) return 1013;
    for (WitU64 i = 0; i < 3; ++i)
        if (wit_native_thread_create(worker, i + 1, &handles[i]) != WIT_STATUS_OK) return 1014;
    for (WitU32 i = 0; i < 8; ++i)
        if (wit_native_thread_create(worker, 1, &result) != WIT_STATUS_NO_MEMORY || result) return 1015;
    if (wit_native_call(WIT_CALL_EVENT_SET, release_event, 0, 0, nullptr) != WIT_STATUS_OK) return 1016;
    for (WitU64 i = 0; i < 3; ++i)
        if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &result) != WIT_STATUS_OK ||
            result != WIT_TEST_EXIT_CODE || reports[i + 1] != (mode == 8 ? 12291U : 1221U)) return 1017;
    reports[1] = 0;
    if (wit_native_thread_create(worker, 1, &handles[0]) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_THREAD_JOIN, handles[0], 0, 0, &result) != WIT_STATUS_OK ||
        result != WIT_TEST_EXIT_CODE || reports[1] != (mode == 8 ? 12291U : 1221U) || constructed != 2 || sequence != 12 || primitive != 77) return 1018;
    reports[2] = 0;
    if (wit_native_call(WIT_CALL_THREAD_CREATE, (WitU64)wit_dynamic_lazy_entry, 2, 0, &handles[0]) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_THREAD_JOIN, handles[0], 0, 0, &result) != WIT_STATUS_OK || result != WIT_TEST_EXIT_CODE ||
        reports[2] != (mode == 8 ? 12291U : 1221U)) return 1021;
    if (wit_native_call(WIT_CALL_CLOSE, release_event, 0, 0, nullptr) != WIT_STATUS_OK) return 1019;
    return WIT_TEST_EXIT_CODE;
}
extern "C" WitU64 wit_dynamic_finish(WitU64 code)
{
    // Only process-global reports: main-thread TLS objects have been destroyed.
    return code == WIT_TEST_EXIT_CODE && reports[0] == (mode == 8 ? 12291U : 1221U) ? WIT_TEST_EXIT_CODE : 1020;
}
