#include "bootstrap.h"
#include "../User/protocol.h"

static WitNativeModule module;
static const WitUserStartup *boot;
static WitU64 mode, trace, allocation, event_handle;
static volatile WitU64 worker_results[2];
static WitUserStartup bad_startup;
static WitUserImageInfo bad_image;
static WitNativeInitializer writable_table[1];

static void mark(WitU64 value)
{
    trace = trace * 10 + value;
}

static WitU64 call(WitU64 number, WitU64 a, WitU64 b, WitU64 c, WitU64 *result)
{
    return wit_native_call(number, a, b, c, result);
}

static WitU64 application(WitNativeModule *current);

static WitU64 first(WitNativeModule *current)
{
    mark(1);
    if (current->State != WIT_NATIVE_INITIALIZING ||
        wit_native_module_from_address(current, (WitU64)first) != current->Image->Base ||
        wit_native_module_from_address(current, 0x1000) != 0) {
        return 101;
    }
    if (mode == WIT_BOOTSTRAP_TEST_CONCURRENT && call(WIT_CALL_THREAD_YIELD, 0, 0, 0, 0) != WIT_STATUS_OK) {
        return 102;
    }
    if (call(WIT_CALL_MEMORY_RESERVE, 4096, 4096, 0, &allocation) != WIT_STATUS_OK) {
        return 103;
    }
    if (call(WIT_CALL_MEMORY_COMMIT, allocation, 4096, 3, 0) != WIT_STATUS_OK) {
        (void)call(WIT_CALL_MEMORY_RELEASE, allocation, 0, 0, 0);
        allocation = 0;
        return 104;
    }
    *(WitU64 *)allocation = 0xABCD;
    return 0;
}

static void undo_first(WitNativeModule *current)
{
    mark(1);
    if (current->State != WIT_NATIVE_FINALIZING ||
        call(WIT_CALL_MEMORY_RELEASE, allocation, 0, 0, 0) != WIT_STATUS_OK) {
        trace = 999;
    }
    allocation = 0;
}

static WitU64 second(WitNativeModule *current)
{
    mark(2);
    if (mode == WIT_BOOTSTRAP_TEST_INIT_FAIL) {
        return 77;
    }
    if (mode == WIT_BOOTSTRAP_TEST_INIT_FAULT) {
        *(volatile WitU32 *)&current->Image->Version = 0;
    }
    return call(WIT_CALL_EVENT_CREATE, WIT_EVENT_MANUAL_RESET | WIT_EVENT_INITIAL_SIGNALED, 0, 0, &event_handle);
}

static void undo_second(WitNativeModule *current)
{
    mark(2);
    if (current->State != WIT_NATIVE_FINALIZING || call(WIT_CALL_CLOSE, event_handle, 0, 0, 0) != WIT_STATUS_OK) {
        trace = 999;
    }
    event_handle = 0;
}

static WitU64 third(WitNativeModule *current)
{
    WitU64 ignored = 123;
    mark(3);
    if (wit_native_bootstrap(current, current->Startup, 0, 0, application, &ignored) != WIT_NATIVE_ALREADY_STARTED ||
        ignored != 0 ||
        current->State != WIT_NATIVE_INITIALIZING) {
        return 105;
    }
    return 0;
}

static void undo_third(WitNativeModule *current)
{
    mark(3);
    if (current->State != WIT_NATIVE_FINALIZING) {
        trace = 999;
    }
}

static const WitNativeInitializer initializers[] = {{first, undo_first}, {second, undo_second}, {third, undo_third}};
static const WitNativeInitializer invalid_initializers[] = {{first, undo_first}, {(WitNativeInitialize)&module, 0}};

static WitU64 application(WitNativeModule *current)
{
    static const char message[] = "Hello from native C bootstrap.\n";
    WitU64 written;
    if (current->State != WIT_NATIVE_READY ||
        current->Image->RangeCount == 0 ||
        current->Image->UnwindSize < 12 ||
        current->Image->Entry == 0 ||
        wit_native_module_from_address(current, current->Image->Base) != current->Image->Base ||
        wit_native_module_from_address(current, current->Image->Base + current->Image->ImageSize) != 0) {
        return 201;
    }
    if (mode != WIT_BOOTSTRAP_TEST_EMPTY &&
        (trace != 123 ||
            *(WitU64 *)allocation != 0xABCD ||
            call(WIT_CALL_EVENT_WAIT, event_handle, 0, 0, 0) != WIT_STATUS_OK)) {
        return 202;
    }
    mark(4);
    if (call(WIT_CALL_WRITE, current->Startup->ConsoleHandle, (WitU64)message, sizeof(message) - 1, &written) !=
            WIT_STATUS_OK ||
        written != sizeof(message) - 1) {
        return 203;
    }
    return mode == WIT_BOOTSTRAP_TEST_MAIN_FAIL ? 73 : WIT_TEST_EXIT_CODE;
}

static void worker(WitU64 index)
{
    WitU64 exit_code = 0;
    const WitU64 status = wit_native_bootstrap(&module, boot, initializers, 3, application, &exit_code);
    worker_results[index] = status == WIT_NATIVE_OK && exit_code != WIT_TEST_EXIT_CODE ? 99 : status;
    (void)call(WIT_CALL_THREAD_EXIT, WIT_TEST_EXIT_CODE, 0, 0, 0);
    (void)call(WIT_CALL_EXIT, 241, 0, 0, 0);
    for (;;) {
    }
}

WitU64 wit_native_main(const WitUserStartup *startup)
{
    WitU64 result = 0, status;
    const WitNativeInitializer *table = initializers;
    WitU32 count = 3;
    WitNativeMain main = application;
    const WitUserTestConfig *config = (const WitUserTestConfig *)startup;
    boot = startup;
    mode = config->Mode;
    if (startup->Version != WIT_ABI_VERSION || startup->Size != sizeof(*startup) || !startup->ImageInfo) {
        return 241;
    }
    if (mode == WIT_BOOTSTRAP_TEST_WRITE_INFO) {
        *(volatile WitU32 *)startup->ImageInfo = 0;
    }
    if (mode == WIT_BOOTSTRAP_TEST_CONCURRENT) {
        WitU64 handles[2], code;
        worker_results[0] = worker_results[1] = 100;
        for (WitU64 i = 0; i < 2; ++i) {
            if (call(WIT_CALL_THREAD_CREATE, (WitU64)worker, i, 0, &handles[i]) != WIT_STATUS_OK) {
                return 241;
            }
        }
        for (WitU32 i = 0; i < 2; ++i) {
            if (call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &code) != WIT_STATUS_OK || code != WIT_TEST_EXIT_CODE) {
                return 241;
            }
        }
        if (!((worker_results[0] == WIT_NATIVE_OK && worker_results[1] == WIT_NATIVE_ALREADY_STARTED) ||
                (worker_results[1] == WIT_NATIVE_OK && worker_results[0] == WIT_NATIVE_ALREADY_STARTED)) ||
            trace != 1234321 ||
            module.State != WIT_NATIVE_STOPPED ||
            allocation ||
            event_handle) {
            return 241;
        }
        return WIT_TEST_EXIT_CODE;
    }
    if (mode == WIT_BOOTSTRAP_TEST_BAD_INIT) {
        table = invalid_initializers;
        count = 2;
    }
    if (mode == WIT_BOOTSTRAP_TEST_WRITABLE_TABLE) {
        writable_table[0].Initialize = first;
        writable_table[0].Cleanup = undo_first;
        table = writable_table;
        count = 1;
    }
    if (mode == WIT_BOOTSTRAP_TEST_BAD_MAIN) {
        main = (WitNativeMain)&module;
    }
    if (mode == WIT_BOOTSTRAP_TEST_TOO_MANY) {
        count = WIT_NATIVE_MAX_INITIALIZERS + 1;
    }
    if (mode == WIT_BOOTSTRAP_TEST_BAD_IMAGE) {
        const WitUserImageInfo *source = (const WitUserImageInfo *)startup->ImageInfo;
        for (WitU32 i = 0; i < sizeof(bad_image); ++i) {
            ((WitU8 *)&bad_image)[i] = ((const WitU8 *)source)[i];
        }
        bad_image.Version = 0;
        bad_startup.Version = startup->Version;
        bad_startup.Size = startup->Size;
        bad_startup.ConsoleHandle = startup->ConsoleHandle;
        bad_startup.ImageInfo = (WitU64)&bad_image;
        startup = &bad_startup;
    }
    if (mode == WIT_BOOTSTRAP_TEST_EMPTY) {
        table = 0;
        count = 0;
    }
    status = wit_native_bootstrap(&module, startup, table, count, main, &result);
    if (mode >= WIT_BOOTSTRAP_TEST_BAD_INIT && mode <= WIT_BOOTSTRAP_TEST_BAD_IMAGE) {
        if (status != WIT_NATIVE_INVALID_BOOTSTRAP ||
            module.State != WIT_NATIVE_FAILED ||
            trace ||
            result ||
            module.Initialized) {
            return 241;
        }
    } else if (mode == WIT_BOOTSTRAP_TEST_INIT_FAIL) {
        if (status != WIT_NATIVE_INITIALIZER_FAILED ||
            module.State != WIT_NATIVE_FAILED ||
            module.FailureCode != 77 ||
            trace != 121 ||
            result ||
            module.Initialized ||
            allocation) {
            return 241;
        }
    } else {
        if (status != WIT_NATIVE_OK ||
            result != (mode == WIT_BOOTSTRAP_TEST_MAIN_FAIL ? 73ULL : WIT_TEST_EXIT_CODE) ||
            module.State != WIT_NATIVE_STOPPED ||
            module.Initialized ||
            allocation ||
            event_handle ||
            trace != (mode == WIT_BOOTSTRAP_TEST_EMPTY ? 4ULL : 1234321ULL)) {
            return 241;
        }
    }
    result = 999;
    if (wit_native_bootstrap(&module, boot, initializers, 3, application, &result) != WIT_NATIVE_ALREADY_STARTED ||
        result != 0) {
        return 241;
    }
    return WIT_TEST_EXIT_CODE;
}
