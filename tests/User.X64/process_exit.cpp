#include "native_process.h"
#include "error.h"
extern "C" {
#include "library.h"
}
#include "../User/protocol.h"
#include <stdlib.h>

extern "C" WitU64 wit_library_threads_probe(unsigned);
extern "C" WitU64 wit_library_tls_main_probe(unsigned, WitU64);
static WitU64 mode, sequence, called;
static volatile WitU64 released, attempted;
static __declspec(thread) bool worker_thread;
static __declspec(thread) bool tls_destroyed;

static WitU64 *report()
{
    return (WitU64 *)WIT_GC_INFO_REPORT;
}

static void require(bool value)
{
    if (!value) {
        wit_native_fail_fast(0xFFFF1010ULL);
    }
}

static void tick()
{
    require(wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) == WIT_STATUS_OK);
}

static void count_call()
{
    report()[3] = ++called;
}

static void dll_atexit()
{
    require(tls_destroyed && report()[8] == 73115);
    report()[9] = 1;
}

static void extra()
{
    sequence = sequence * 10 + 4;
}

static void from_tls()
{
    sequence = sequence * 10 + 3;
}

struct Cleanup {
    ~Cleanup() noexcept
    {
        tls_destroyed = true;
        if (!worker_thread) {
            report()[5] = 1;
        }
        if (!worker_thread && (mode == 0 || mode == 1)) {
            require(atexit(from_tls) == 0);
        }
    }
};

static thread_local Cleanup cleanup;

static void first()
{
    require(tls_destroyed);
    sequence = sequence * 10 + 1;
    require(sequence == 3241);
    report()[2] = sequence;
}

static void second()
{
    require(tls_destroyed);
    sequence = sequence * 10 + 2;
    require(atexit(extra) == 0);
}

static void recursive()
{
    report()[1] = 1;
    wit_native_process_shutdown();
}

static void forever()
{
    ++report()[1];
    require(atexit(forever) == 0);
}

static void fault()
{
    report()[1] = 1;
    *(volatile WitU64 *)0 = 1;
}

static WitU64 worker(WitU64)
{
    worker_thread = true;
    wit_native_error_set(0x12345678);
    if (mode == 3) {
        for (WitU32 i = 0; i < 8; ++i) {
            require(atexit(count_call) == 0);
            tick();
        }
    } else {
        while (!released) {
            tick();
        }
        if (mode == 9) {
            report()[1] = 1;
            wit_native_process_shutdown();
        }
        require(atexit(count_call) != 0);
        attempted = 1;
    }
    require(wit_native_error_get() == 0x12345678);
    return WIT_TEST_EXIT_CODE;
}

static void handoff()
{
    released = 1;
    while (!attempted) {
        tick(); // The registry gate must be released here.
    }
    require(called == 0);
    report()[2] = 1;
}

static volatile WitU64 notify_ready[3], notify_release[3], notify_done[3];
static WitU64 notify_ids[3], main_id;

static WitUserThreadInfo current_thread()
{
    WitUserThreadInfo info;
    require(wit_native_thread_query(WIT_THREAD_SELF, &info) == WIT_STATUS_OK);
    require(info.ThreadId && info.CompilerTls);
    return info;
}

static void notification(void *context)
{
    const auto id = current_thread().ThreadId;
    require(tls_destroyed);
    require(wit_native_thread_on_exit(notification, context) == WIT_STATUS_CLOSED);
    if (!worker_thread) {
        require(context == &main_id && id == main_id && !report()[7]);
        require(atexit(count_call) != 0); // atexit is already drained and closed.
        if (mode == 16) {
            require(report()[8] == 73115 && report()[9] == 1);
        }
        if (mode <= 1) {
            require(sequence == 3241);
        }
        report()[7] = 1;
        if (mode == 12) {
            report()[1] = 1;
            wit_native_thread_notify_exit();
        }
        if (mode == 13) {
            report()[1] = 1;
            wit_native_process_shutdown();
        }
        return;
    }
    const auto index = (WitU64 *)context - notify_ids;
    require(index >= 0 && index < 3 && id == notify_ids[index] && id != main_id);
    require(wit_native_error_get() == 0x12345678 && !notify_done[index]);
    notify_ready[index] = 1;
    while (!notify_release[index]) {
        tick();
    }
    require(current_thread().ThreadId == id && wit_native_error_get() == 0x12345678);
    notify_done[index] = 1;
}

static WitU64 notification_worker(WitU64 index)
{
    require(index < 3 && !tls_destroyed);
    worker_thread = true;
    notify_ids[index] = current_thread().ThreadId;
    wit_native_error_set(0x12345678);
    require(wit_native_thread_on_exit(notification, &notify_ids[index]) == WIT_STATUS_OK);
    require(wit_native_thread_on_exit(notification, &notify_ids[index]) == WIT_STATUS_BUSY);
    if (mode == 15) {
        (void)wit_native_call(WIT_CALL_THREAD_EXIT, WIT_TEST_EXIT_CODE, 0, 0, nullptr);
        require(false);
    }
    if (index == 1) {
        wit_native_thread_exit(WIT_TEST_EXIT_CODE);
    }
    return WIT_TEST_EXIT_CODE;
}

static void notification_workers()
{
    WitU64 handles[3], result;
    if (mode == 15) {
        require(wit_native_thread_create(notification_worker, 0, &handles[0]) == WIT_STATUS_OK);
        require(wit_native_thread_join(handles[0], &result) == WIT_STATUS_OK && result == WIT_TEST_EXIT_CODE);
        require(!notify_ready[0] && !notify_done[0]);
        return;
    }
    WitU64 previous[3] = {};
    for (unsigned round = 0; round < 4; ++round) {
        for (unsigned i = 0; i < 3; ++i) {
            notify_ids[i] = notify_ready[i] = notify_release[i] = notify_done[i] = 0;
            require((mode == 11 ? wit_native_thread_create_detached(notification_worker, i)
                                : wit_native_thread_create(notification_worker, i, &handles[i])) == WIT_STATUS_OK);
        }
        while (!notify_ready[0] || !notify_ready[1] || !notify_ready[2]) {
            tick();
        }
        for (unsigned i = 0; i < 3; ++i) {
            require(notify_ids[i] && !notify_done[i]);
            for (unsigned j = 0; j < 3; ++j) {
                require(notify_ids[i] != previous[j]);
            }
            if (mode == 11) {
                // The identity a detached worker reports is no capability: it cannot be closed or waited for.
                result = 0;
                require(wit_native_call(WIT_CALL_CLOSE, notify_ids[i], 0, 0, nullptr) == WIT_STATUS_BUSY);
                require(wit_native_thread_join(notify_ids[i], &result) == WIT_STATUS_WRONG_TYPE && !result);
            }
            notify_release[i] = 1;
        }
        for (unsigned i = 0; i < 3; ++i) {
            if (mode == 11) {
                for (;;) {
                    const auto status = wit_native_call(WIT_CALL_CLOSE, notify_ids[i], 0, 0, nullptr);
                    if (status == WIT_STATUS_BAD_HANDLE) {
                        break;
                    }
                    require(status == WIT_STATUS_BUSY);
                    tick();
                }
            } else {
                require(wit_native_thread_join(handles[i], &result) == WIT_STATUS_OK && result == WIT_TEST_EXIT_CODE);
            }
            require(notify_done[i]);
            previous[i] = notify_ids[i];
            ++report()[6];
        }
    }
}

extern "C" WitU64 wit_native_main(const WitUserStartup *startup)
{
    mode = ((const WitUserTestConfig *)startup)->Mode;
    report()[0] = mode;
    require(atexit(count_call) != 0); // No published image yet.
    require(wit_native_thread_on_exit(notification, nullptr) == WIT_STATUS_DENIED);
    wit_native_process_image_initialize(startup);
    if (mode <= 1) {
        require(atexit(first) == 0); // Before compiler TLS constructors.
    }
    wit_native_tls_initialize(startup);
    wit_native_error_set(0xABCDEF12);
    main_id = current_thread().ThreadId;
    require(wit_native_thread_on_exit(nullptr, nullptr) == WIT_STATUS_BAD_ADDRESS &&
        wit_native_thread_on_exit((WitNativeThreadExitCallback)startup, nullptr) == WIT_STATUS_BAD_ADDRESS &&
        wit_native_thread_on_exit((WitNativeThreadExitCallback)&sequence, nullptr) == WIT_STATUS_BAD_ADDRESS);
    require(wit_native_thread_on_exit(notification, &main_id) == WIT_STATUS_OK);
    require(wit_native_thread_on_exit(notification, &main_id) == WIT_STATUS_BUSY);
    if (mode == 14) {
        report()[1] = 1;
        wit_native_thread_notify_exit();
    }
    require(
        atexit(nullptr) != 0 && atexit((void(__cdecl *)())startup) != 0 && atexit((void(__cdecl *)()) & sequence) != 0);
    if (mode == 20 || mode == 21) {
        return wit_library_tls_main_probe((unsigned)mode, ((const WitUserTestConfig *)startup)->KernelProbe);
    }
    if (mode >= 17 && mode <= 19) {
        return wit_library_threads_probe((unsigned)mode);
    }
    if (mode == 16) {
        WitU64 provider = 0, traceAddress = 0, dataAddress = 0, root = 0, reader = 0, pc = 0;
        const char providerPath[] = "/native/WitLibraryFixture.dll", rootPath[] = "/native/initparent.dll";
        require(wit_native_library_load(providerPath, sizeof(providerPath) - 1, &provider) == WIT_STATUS_OK);
        require(wit_native_library_symbol(provider, "LibraryData", 11, 0, &dataAddress) == WIT_STATUS_OK);
        require(wit_native_library_symbol(provider, "LibraryTraceTarget", 18, 0, &traceAddress) == WIT_STATUS_OK);
        *(int *)dataAddress = 731;
        *(WitU64 *)traceAddress = (WitU64)(report() + 8);
        require(
            wit_native_library_load(rootPath, sizeof(rootPath) - 1, &root) == WIT_STATUS_OK && report()[8] == 73115);
        require(wit_native_library_symbol(root, "ParentValue", 11, 0, &pc) == WIT_STATUS_OK);
        WitLibraryInfo info;
        require(wit_native_library_acquire_reader(pc, &info, &reader) == WIT_STATUS_OK);
        require(wit_native_library_shutdown() == WIT_STATUS_BUSY && report()[8] == 73115);
        require(wit_native_library_info(root, &info) == WIT_STATUS_OK && info.References == 1);
        require(wit_native_library_release_reader(reader) == WIT_STATUS_OK && atexit(dll_atexit) == 0);
        wit_native_process_shutdown();
        require(tls_destroyed && report()[5] == 1 && report()[7] == 1 && report()[9] == 1 && report()[8] == 7311584);
        require(wit_native_library_info(root, &info) == WIT_STATUS_BAD_HANDLE &&
            wit_native_library_info(provider, &info) == WIT_STATUS_BAD_HANDLE);
        root = 99;
        require(wit_native_library_load(rootPath, sizeof(rootPath) - 1, &root) == WIT_STATUS_CLOSED && root == 99);
        wit_native_process_shutdown();
        require(wit_native_library_shutdown() == WIT_STATUS_OK && report()[8] == 7311584);
        require(wit_native_error_get() == 0xABCDEF12);
        report()[4] = 1;
        return WIT_TEST_EXIT_CODE;
    }
    WitU64 handles[3] = {}, result;
    if (mode <= 1) {
        require(atexit(second) == 0);
        if (mode == 1) {
            wit_native_process_exit(WIT_TEST_EXIT_CODE);
        }
    } else if (mode == 2) {
        for (WitU32 i = 0; i < WIT_NATIVE_EXIT_MAX_CALLBACKS; ++i) {
            require(atexit(count_call) == 0);
        }
        require(atexit(count_call) != 0);
    } else if (mode == 3) {
        for (WitU32 i = 0; i < 3; ++i) {
            require(wit_native_thread_create(worker, 0, &handles[i]) == WIT_STATUS_OK);
        }
        for (WitU32 i = 0; i < 3; ++i) {
            require(wit_native_thread_join(handles[i], &result) == WIT_STATUS_OK && result == WIT_TEST_EXIT_CODE);
        }
        require(called == 0); // Individual thread exit must not drain process callbacks.
    } else if (mode == 4) {
        require(atexit(recursive) == 0);
    } else if (mode == 5) {
        require(atexit(forever) == 0);
    } else if (mode == 6) {
        require(atexit(fault) == 0);
    } else if (mode == 7) {
        require(atexit(count_call) == 0);
        report()[1] = 1;
        (void)wit_native_call(WIT_CALL_EXIT, WIT_TEST_EXIT_CODE, 0, 0, nullptr);
        require(false);
    } else if (mode == 10 || mode == 11 || mode == 15) {
        notification_workers();
    } else if (mode != 12 && mode != 13) {
        require(wit_native_thread_create(worker, 0, &handles[0]) == WIT_STATUS_OK);
        require(atexit(handoff) == 0);
    }
    wit_native_process_shutdown();
    wit_native_process_shutdown(); // Completed cleanup is idempotent for its owner.
    wit_native_thread_notify_exit(); // Must not repeat the notification.
    require(report()[7] == 1 && wit_native_thread_on_exit(notification, &main_id) == WIT_STATUS_CLOSED);
    require(tls_destroyed && atexit(count_call) != 0 && wit_native_error_get() == 0xABCDEF12);
    if (mode == 2 || mode == 3) {
        require(called == (mode == 2 ? 32U : 24U));
    }
    if (mode == 8) {
        require(wit_native_thread_join(handles[0], &result) == WIT_STATUS_OK && result == WIT_TEST_EXIT_CODE);
    }
    report()[3] = called;
    report()[4] = 1;
    return WIT_TEST_EXIT_CODE;
}
