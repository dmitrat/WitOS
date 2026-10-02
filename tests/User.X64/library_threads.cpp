#include <atomic>
#include "native_process.h"
extern "C" {
#include "library.h"
}
#include "protocol.h"
static WitU64 mainId;
static unsigned probeMode;
static std::atomic<WitU64> attached{0}, detached{0};
static std::atomic<WitU32> nativeId{0};
[[msvc::no_tls_guard]] static __declspec(thread) bool entered, notified, constructed, destroyed;
[[msvc::no_tls_guard]] static __declspec(thread) WitU64 callbackOwner;

struct ThreadCleanup {
    ThreadCleanup()
    {
        constructed = true;
    }

    ~ThreadCleanup()
    {
        destroyed = true;
    }
};

static thread_local ThreadCleanup cleanup;

static WitU64 *report()
{
    return (WitU64 *)WIT_GC_INFO_REPORT;
}

static void require(bool v)
{
    if (!v) {
        wit_native_fail_fast(0xFFFF1040ULL);
    }
}

static WitUserThreadInfo current()
{
    WitUserThreadInfo i;
    require(wit_native_call(WIT_CALL_THREAD_QUERY, (WitU64)&i, sizeof(i), WIT_THREAD_INFO_VERSION, nullptr) ==
            WIT_STATUS_OK &&
        i.ThreadId &&
        i.CompilerTls);
    return i;
}

static void yield()
{
    require(wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) == WIT_STATUS_OK);
}

static void notification(unsigned reason)
{
    const auto i = current();
    if (reason == 2) {
        require(i.ThreadId != mainId && !entered && !notified && !constructed && !destroyed && !callbackOwner);
        callbackOwner = i.ThreadId;
        attached.store(i.ThreadId, std::memory_order_release);
        nativeId.store(i.NativeId, std::memory_order_release);
        ++report()[10];
    } else if (reason == 3) {
        require(i.ThreadId != mainId && entered && notified && constructed && destroyed && callbackOwner == i.ThreadId);
        detached.store(i.ThreadId, std::memory_order_release);
        ++report()[12];
    } else if (!reason) {
        require(i.ThreadId == mainId);
        ++report()[13];
    } else {
        require(false);
    }
}

static void runtime_exit(void *)
{
    require(entered && !notified && constructed && destroyed && callbackOwner == current().ThreadId);
    notified = true;
}

static WitU64 worker(WitU64)
{
    const auto i = current();
    require(callbackOwner == i.ThreadId && !entered && !notified && constructed && !destroyed);
    entered = true;
    require(wit_native_thread_on_exit(runtime_exit, nullptr) == WIT_STATUS_OK);
    if (probeMode == 18 || probeMode == 19) {
        report()[11] = i.ThreadId;
        (void)wit_native_call(probeMode == 18 ? WIT_CALL_THREAD_EXIT : WIT_CALL_THREAD_COMPLETE, 77, 0, 0, nullptr);
        require(false);
    }
    return 42;
}

static WitThreadReferenceInfo await_exit(WitU64 handle)
{
    WitThreadReferenceInfo info;
    for (;;) {
        require(wit_native_call(WIT_CALL_THREAD_REFERENCE_QUERY, handle, (WitU64)&info, sizeof(info), nullptr) ==
            WIT_STATUS_OK);
        if (info.State == WIT_THREAD_REFERENCE_EXITED) {
            return info;
        }
        yield();
    }
}

extern "C" WitU64 wit_library_threads_probe(unsigned mode)
{
    probeMode = mode;
    mainId = current().ThreadId;
    report()[10] = report()[11] = report()[12] = report()[13] = 0;
    WitU64 module = 0, set = 0, get = 0;
    const char path[] = "/native/threadnotify.dll";
    require(wit_native_library_load(path, sizeof(path) - 1, &module) == WIT_STATUS_OK);
    require(wit_native_library_symbol(module, "SetNotification", 15, 0, &set) == WIT_STATUS_OK &&
        wit_native_library_symbol(module, "ThreadCounts", 12, 0, &get) == WIT_STATUS_OK);
    require(((int (*)(void (*)(unsigned)))set)(notification) == 1);
    auto counts = (unsigned (*)(void))get;
    require(!counts());
    WitU64 handle = 0, result = 0;
    require(wit_native_thread_create(worker, 0, &handle) == WIT_STATUS_OK);
    require(wit_native_call(WIT_CALL_THREAD_JOIN, handle, 0, 0, &result) == WIT_STATUS_OK && result == 42);
    require(mode == 17 &&
        counts() == 0x10001 &&
        attached.load(std::memory_order_acquire) == handle &&
        detached.load(std::memory_order_acquire) == handle);
    WitU32 id = 0;
    WitU64 previous = 0;
    require(wit_native_thread_create_reference(worker, 0, 0, WIT_THREAD_START_SUSPENDED, (WitU64)&id, &handle) ==
            WIT_STATUS_OK &&
        id);
    for (unsigned i = 0; i < 3; ++i) {
        yield();
    }
    require(counts() == 0x10001);
    require(wit_native_call(WIT_CALL_THREAD_RESUME, handle, 0, 0, &previous) == WIT_STATUS_OK && previous == 1);
    auto info = await_exit(handle);
    require(info.ExitCode == 42 &&
        counts() == 0x20002 &&
        attached.load(std::memory_order_acquire) == info.ThreadId &&
        detached.load(std::memory_order_acquire) == info.ThreadId &&
        nativeId.load(std::memory_order_acquire) == id);
    require(wit_native_call(WIT_CALL_CLOSE, handle, 0, 0, nullptr) == WIT_STATUS_OK);
    require(wit_native_thread_create_reference(worker, 0, 0, 0, (WitU64)&id, &handle) == WIT_STATUS_OK);
    info = await_exit(handle);
    require(info.ExitCode == 42 &&
        counts() == 0x30003 &&
        attached.load(std::memory_order_acquire) == info.ThreadId &&
        detached.load(std::memory_order_acquire) == info.ThreadId);
    require(wit_native_call(WIT_CALL_CLOSE, handle, 0, 0, nullptr) == WIT_STATUS_OK);
    const WitU64 previousDetached = detached.load(std::memory_order_acquire);
    require(wit_native_thread_create_detached(worker, 0) == WIT_STATUS_OK);
    for (;;) {
        const WitU64 idNow = detached.load(std::memory_order_acquire);
        if (idNow && idNow != previousDetached) {
            const auto closed = wit_native_call(WIT_CALL_CLOSE, idNow, 0, 0, nullptr);
            if (closed == WIT_STATUS_BAD_HANDLE) {
                break;
            }
            require(closed == WIT_STATUS_BUSY);
        }
        yield();
    }
    require(counts() == 0x40004);
    require(wit_native_library_unload(module) == WIT_STATUS_OK &&
        report()[10] == 4 &&
        report()[12] == 4 &&
        report()[13] == 1);
    return 42;
}
