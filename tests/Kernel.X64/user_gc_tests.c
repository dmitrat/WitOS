#include "x64.h"
#include "q35.h"
#include "user.h"
#include "cpu_cache.h"
#include "witos/platform.h"
#include "protocol.h"
#include "gc_memory_image.h"
#include "self_test.h"

static WitUserProcess process;
static WitPageAllocator limited_pages;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void run(WitPageAllocator *pages, WitU64 mode, WitU64 base)
{
    const WitU64 free_before = wit_pages_free_count(pages);
    WitU32 owned, table_pages = 0;
    WitU64 free_active;
    WitUserTestConfig *config;
    require(wit_user_create_pe(&process, pages, 0, wit_gc_memory_image, sizeof(wit_gc_memory_image), base) == WitPeOk,
        "GC memory fixture load failed");
    config = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    config->Mode = mode;
    if (mode == WIT_GC_TEST_HPET_READ) {
        config->KernelProbe = WIT_Q35_HPET_BASE;
    }
    if (mode == WIT_GC_TEST_THREAD_ID) {
        config->KernelProbe = WIT_USER_TLS;
    }
    owned = process.Space.OwnedCount;
    free_active = wit_pages_free_count(pages);
    for (WitU32 i = 0; i < owned; ++i) {
        if (!process.Space.OwnedVirtual[i]) {
            ++table_pages;
        }
    }
    wit_user_run(&process);
    if ((mode >= WIT_NATIVE_TEST_HEAP && mode <= WIT_NATIVE_TEST_HEAP_THREADS) ||
        mode == WIT_GC_TEST_RESET ||
        mode == WIT_GC_TEST_NORMAL ||
        mode == WIT_GC_TEST_DISCOVERY ||
        (mode >= WIT_GC_TEST_EVENT_STATE && mode <= WIT_GC_TEST_EVENT_CONTENTION) ||
        (mode >= WIT_GC_TEST_CLOCK && mode <= WIT_GC_TEST_TIME_ARITHMETIC) ||
        (mode >= WIT_GC_TEST_THREAD_ID && mode <= WIT_GC_TEST_CRST)) {
        if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("GC memory fixture state/code: ");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
            wit_panic("GC memory adapter contract failed");
        }
        if (mode == WIT_GC_TEST_DISCOVERY) {
            const WitUserMemoryInfo *info =
                (const WitUserMemoryInfo *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
            require(info && *(const WitU64 *)(info + 1) == wit_arch_cache_size(),
                "GC cache size differs from kernel discovery");
            require(info &&
                    info->Version == WIT_MEMORY_INFO_VERSION &&
                    info->Size == sizeof(*info) &&
                    info->PhysicalTotalBytes == pages->TotalPages * WIT_PAGE_SIZE &&
                    info->PhysicalAvailableBytes == free_active * WIT_PAGE_SIZE &&
                    info->OwnedLimitBytes == WIT_USER_PAGE_CAPACITY * WIT_PAGE_SIZE &&
                    info->OwnedBytes == owned * WIT_PAGE_SIZE &&
                    info->PrivatePageTableBytes == table_pages * WIT_PAGE_SIZE &&
                    info->VirtualBase == WIT_USER_MEMORY_BASE &&
                    info->VirtualBytes == WIT_USER_MEMORY_LIMIT - WIT_USER_MEMORY_BASE &&
                    info->ReservationCapacity == WIT_USER_RESERVATION_CAPACITY &&
                    !info->ReservationCount &&
                    !info->ReservedBytes &&
                    !info->DynamicCommittedBytes,
                "Guest memory snapshot disagrees with allocator");
        }
        if (mode == WIT_GC_TEST_EVENT_MANUAL || mode == WIT_GC_TEST_EVENT_AUTO || mode == WIT_GC_TEST_EVENT_CLOSE) {
            require(process.ThreadCreates == 3 && process.ThreadJoins == 2 && process.ThreadReaps == 2,
                "GC event workers were not joined and reaped");
        }
        if (mode == WIT_GC_TEST_EVENT_CONTENTION) {
            require(process.ThreadCreates == 4 &&
                    process.ThreadJoins == 3 &&
                    process.ThreadReaps == 3 &&
                    process.ThreadSwitches >= 2,
                "GC native lock did not exercise a yielding contender");
        }
        if (mode == WIT_GC_TEST_TIMED_SIGNAL) {
            require(process.ThreadCreates == 2 && process.ThreadJoins == 1 && process.ThreadReaps == 1,
                "Timed GC signal worker leaked");
        }
        if (mode == WIT_GC_TEST_THREAD_ID) {
            const WitU64 report = wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
            require(report &&
                    *(const WitU64 *)report == process.Threads[0].Handle &&
                    process.ThreadCreates == 3 &&
                    process.ThreadJoins == 2 &&
                    process.ThreadReaps == 2,
                "Kernel thread identity or slot reuse failed");
        }
        if (mode == WIT_GC_TEST_MUTEX_BLOCKING) {
            require(process.ThreadCreates == 2 &&
                    process.ThreadJoins == 1 &&
                    process.ThreadReaps == 1 &&
                    process.EventParks > 0 &&
                    process.EventWakes > 0,
                "Mutex contender did not park/wake");
        }
        if (mode == WIT_GC_TEST_MUTEX_STRESS) {
            require(process.ThreadCreates == 3 &&
                    process.ThreadJoins == 2 &&
                    process.ThreadReaps == 2 &&
                    process.EventParks > 0 &&
                    process.EventWakes > 0,
                "Mutex stress did not block contenders");
        }
        if (mode == WIT_NATIVE_TEST_HEAP_THREADS) {
            require(process.ThreadCreates == 3 &&
                    process.ThreadJoins == 2 &&
                    process.ThreadReaps == 2 &&
                    process.ThreadSwitches >= 2,
                "Native heap workers were not switched/joined/reaped");
        }
        require(process.Space.OwnedCount == owned, "GC adapter leaked backing or private page tables");
        for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
            require(!process.Space.Reservations[i].Size, "GC adapter leaked reservation");
        }
    } else if (mode == WIT_NATIVE_TEST_HEAP_BAD_FREE || mode == WIT_NATIVE_TEST_HEAP_DOUBLE_FREE) {
        const WitU64 report = wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
        require(process.State == WitUserExited &&
                process.ExitCode == WIT_GC_TEST_FAIL_FAST_EXIT &&
                report &&
                *(const WitU64 *)report == mode,
            "Invalid native free did not fail at its boundary");
    } else if (mode == WIT_NATIVE_TEST_HEAP_NX || mode == WIT_NATIVE_TEST_HEAP_FREED) {
        /* The block's own page faults; the heap's metadata pages come first in its reservation. */
        const WitU64 error = mode == WIT_NATIVE_TEST_HEAP_NX ? 21 : 4;
        const WitU64 report = wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
        require(wit_test_faulted(&process) &&
                process.FaultVector == 14 &&
                process.FaultError == error &&
                report &&
                process.FaultAddress == ((const WitU64 *)report)[1] &&
                process.FaultAddress > WIT_USER_MEMORY_BASE &&
                process.FaultAddress < WIT_USER_MEMORY_LIMIT &&
                !(process.FaultAddress & 4095) &&
                process.FaultState.Cs == WIT_USER_CS &&
                process.FaultState.Ss == WIT_USER_SS,
            "Native heap hardware protection failed");
    } else if (mode >= WIT_GC_TEST_MUTEX_OWNER_FAIL && mode <= WIT_GC_TEST_MUTEX_EVENT_FAIL) {
        const WitU64 report = wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
        require(process.State == WitUserExited &&
                process.ExitCode == WIT_GC_TEST_FAIL_FAST_EXIT &&
                report &&
                *(const WitU64 *)report == mode,
            "Mutex misuse did not fail at the intended boundary");
        if (mode == WIT_GC_TEST_MUTEX_OWNER_FAIL || mode == WIT_GC_TEST_MUTEX_EVENT_FAIL) {
            require(process.ThreadCreates == 2 && process.Space.OwnedCount > owned, "Mutex failure had no live worker");
        }
    } else if (mode == WIT_GC_TEST_RESET_RO || mode == WIT_GC_TEST_RESET_NONE) {
        const WitU64 error = mode == WIT_GC_TEST_RESET_RO ? 7 : 6;
        require(wit_test_faulted(&process) &&
                process.FaultVector == 14 &&
                process.FaultError == error &&
                process.FaultAddress == WIT_USER_MEMORY_BASE &&
                process.FaultState.Cs == WIT_USER_CS &&
                process.FaultState.Ss == WIT_USER_SS,
            "Reset changed hardware page protection");
    } else if (mode == WIT_GC_TEST_HPET_READ) {
        require(wit_test_faulted(&process) &&
                process.FaultVector == 14 &&
                process.FaultError == 5 &&
                process.FaultAddress == WIT_Q35_HPET_BASE &&
                process.FaultState.Cs == WIT_USER_CS &&
                process.FaultState.Ss == WIT_USER_SS,
            "HPET MMIO was accessible from user mode");
    } else if (mode == WIT_GC_TEST_EVENT_FAIL_FAST) {
        const WitU64 evidence = wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
        require(process.State == WitUserExited &&
                process.ExitCode == WIT_GC_TEST_FAIL_FAST_EXIT &&
                process.ThreadCreates == 2 &&
                process.Space.OwnedCount > owned &&
                evidence &&
                *(const WitU64 *)evidence == 0x475345564641494CULL,
            "Invalid GC event operation did not fail fast with live resources");
    } else {
        const WitU64 error = mode == WIT_GC_TEST_RESERVED ? 6 : mode == WIT_GC_TEST_NX ? 21 : 4;
        require(wit_test_faulted(&process) &&
                process.FaultVector == 14 &&
                process.FaultError == error &&
                process.FaultAddress == WIT_USER_MEMORY_BASE + (mode == WIT_GC_TEST_ROLLBACK ? 4096 : 0) &&
                process.FaultState.Cs == WIT_USER_CS &&
                process.FaultState.Ss == WIT_USER_SS,
            "GC adapter memory protection fault mismatch");
        if (mode == WIT_GC_TEST_ROLLBACK) {
            const WitU64 physical = wit_user_space_physical(&process.Space, WIT_USER_MEMORY_BASE, 1, 0);
            require(physical && *(WitU8 *)physical == 0xCE && process.Space.OwnedCount == owned + 4,
                "Failed GC commit changed existing page or leaked backing/tables");
            for (WitU64 i = 1; i < 128; ++i) {
                require(!wit_user_space_physical(&process.Space, WIT_USER_MEMORY_BASE + i * 4096, 0, 0),
                    "Failed GC commit retained a new page");
            }
        } else if (mode != WIT_GC_TEST_NX) {
            require(process.Space.OwnedCount == owned, "GC reserve/decommit retained backing pages");
        }
    }
    require(!process.Handles.Count && !process.Events.Count, "GC adapter component left handles/events");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == free_before, "GC adapter teardown leaked physical memory");
}

static void limited_discovery(WitPageAllocator *pages)
{
    WitU64 borrowed[48];
    WitMemoryRegion regions[48];
    const WitU64 before = wit_pages_free_count(pages);
    for (WitU32 i = 0; i < 48; ++i) {
        require(wit_page_allocate(pages, &borrowed[i]), "Cannot borrow GC discovery frame");
        regions[i].Base = borrowed[i];
        regions[i].Length = WIT_PAGE_SIZE;
        regions[i].Kind = WIT_MEMORY_USABLE;
        regions[i].Reserved = 0;
    }
    require(wit_pages_initialize(&limited_pages, regions, 48), "Cannot initialize limited GC allocator");
    run(&limited_pages, WIT_GC_TEST_DISCOVERY, WIT_USER_IMAGE_BASE);
    require(wit_pages_free_count(&limited_pages) == 48, "Limited GC discovery leaked");
    for (WitU32 i = 0; i < 48; ++i) {
        require(wit_page_free(pages, borrowed[i]), "Cannot return GC discovery frame");
    }
    require(wit_pages_free_count(pages) == before, "GC pressure fixture leaked real RAM");
}

void wit_user_gc_self_test(WitPageAllocator *pages)
{
    run(pages, WIT_GC_TEST_NORMAL, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcMemoryContract\n");
    run(pages, WIT_GC_TEST_ROLLBACK, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcMemoryOwnership\n");
    run(pages, WIT_GC_TEST_NORMAL, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.GcMemoryRelocation\n");
    run(pages, WIT_GC_TEST_RESERVED, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcReserveProtection\n");
    run(pages, WIT_GC_TEST_DECOMMITTED, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcDecommitProtection\n");
    run(pages, WIT_GC_TEST_NX, WIT_USER_IMAGE_BASE);
    run(pages, WIT_GC_TEST_NORMAL, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcMemoryNx\n");
    wit_x64_cache_self_test();
    run(pages, WIT_GC_TEST_DISCOVERY, WIT_USER_IMAGE_BASE);
    run(pages, WIT_GC_TEST_DISCOVERY, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.GcEnvironmentInit\n[TEST-PASS] User.GcMemoryInformation\n[TEST-PASS] "
                      "User.GcInformationBuffers\n");
    limited_discovery(pages);
    wit_console_write("[TEST-PASS] User.GcPhysicalPressure\n");
    run(pages, WIT_GC_TEST_EVENT_STATE, WIT_USER_IMAGE_BASE);
    run(pages, WIT_GC_TEST_EVENT_STATE, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.GcEventState\n");
    run(pages, WIT_GC_TEST_EVENT_CAPACITY, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcEventCapacity\n");
    run(pages, WIT_GC_TEST_EVENT_MANUAL, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcEventManual\n");
    run(pages, WIT_GC_TEST_EVENT_AUTO, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcEventAuto\n");
    run(pages, WIT_GC_TEST_EVENT_CLOSE, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcEventClose\n");
    run(pages, WIT_GC_TEST_EVENT_CONTENTION, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcEventContention\n");
    run(pages, WIT_GC_TEST_EVENT_FAIL_FAST, WIT_USER_IMAGE_BASE);
    run(pages, WIT_GC_TEST_EVENT_STATE, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcEventFailFast\n");
    run(pages, WIT_GC_TEST_CLOCK, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcClockContract\n");
    run(pages, WIT_GC_TEST_TIMED_WAIT, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcTimedWait\n");
    run(pages, WIT_GC_TEST_TIMED_SIGNAL, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcTimedSignal\n");
    run(pages, WIT_GC_TEST_TIME_ARITHMETIC, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.GcTimeArithmetic\n");
    run(pages, WIT_GC_TEST_HPET_READ, WIT_USER_IMAGE_BASE);
    run(pages, WIT_GC_TEST_CLOCK, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcClockIsolation\n");
    run(pages, WIT_GC_TEST_THREAD_ID, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcThreadIdentity\n");
    run(pages, WIT_GC_TEST_MUTEX_BASIC, WIT_USER_IMAGE_BASE);
    run(pages, WIT_GC_TEST_MUTEX_BASIC, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.GcMutexRecursive\n");
    run(pages, WIT_GC_TEST_MUTEX_BLOCKING, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcMutexBlocking\n");
    run(pages, WIT_GC_TEST_MUTEX_STRESS, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcMutexStress\n");
    run(pages, WIT_GC_TEST_MUTEX_CAPACITY, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcMutexCapacity\n");
    run(pages, WIT_GC_TEST_CRST, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcCrst\n");
    for (WitU64 mode = WIT_GC_TEST_MUTEX_OWNER_FAIL; mode <= WIT_GC_TEST_MUTEX_EVENT_FAIL; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
    }
    run(pages, WIT_GC_TEST_MUTEX_BASIC, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcMutexFailFast\n");
    run(pages, WIT_GC_TEST_RESET, WIT_USER_IMAGE_BASE);
    run(pages, WIT_GC_TEST_RESET, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.GcMemoryReset\n");
    run(pages, WIT_GC_TEST_RESET_RO, WIT_USER_IMAGE_BASE);
    run(pages, WIT_GC_TEST_RESET_NONE, WIT_USER_IMAGE_BASE);
    run(pages, WIT_GC_TEST_RESET, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcResetProtection\n");
    run(pages, WIT_NATIVE_TEST_HEAP, WIT_USER_IMAGE_BASE);
    run(pages, WIT_NATIVE_TEST_HEAP, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeHeap\n");
    run(pages, WIT_NATIVE_TEST_HEAP_REUSE, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeHeapReuse\n");
    run(pages, WIT_NATIVE_TEST_HEAP_FAILURE, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeHeapFailure\n");
    run(pages, WIT_NATIVE_TEST_HEAP_THREADS, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeHeapThreads\n");
    run(pages, WIT_NATIVE_TEST_HEAP_BAD_FREE, WIT_USER_IMAGE_BASE);
    run(pages, WIT_NATIVE_TEST_HEAP_DOUBLE_FREE, WIT_USER_IMAGE_BASE);
    run(pages, WIT_NATIVE_TEST_HEAP, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeHeapFailFast\n");
    run(pages, WIT_NATIVE_TEST_HEAP_NX, WIT_USER_IMAGE_BASE);
    run(pages, WIT_NATIVE_TEST_HEAP_FREED, WIT_USER_IMAGE_BASE);
    run(pages, WIT_NATIVE_TEST_HEAP, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeHeapProtection\n");
}
