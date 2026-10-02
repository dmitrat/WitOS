#include "x64.h"
#include "user.h"
#if defined(WITOS_TEST_RUNTIME_BOOT)
#include "runtime_boot_image.h"
#include "runtime_report.h"
#include "witos/platform.h"
static WitUserProcess process;
static WitPeImage plan;

static void require(int ok, const char *message)
{
    if (!ok) {
        wit_panic(message);
    }
}

static void memory_profile(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    const WitU32 owned = process.Space.OwnedCount;
    WitU64 events[WIT_RUNTIME_EVENT_CAPACITY], extra = 99;
    const WitU32 handles = process.Handles.Count;
    for (WitU32 i = 0; i < WIT_RUNTIME_EVENT_CAPACITY; ++i) {
        require(wit_event_create(&process.Events, &process.Handles, 0, WIT_RIGHT_WAIT | WIT_RIGHT_SIGNAL, &events[i]) ==
                WIT_STATUS_OK,
            "Runtime event quota too small");
    }
    require(wit_event_create(&process.Events, &process.Handles, 0, WIT_RIGHT_WAIT, &extra) == WIT_STATUS_NO_MEMORY &&
            !extra,
        "Runtime event exhaustion was not atomic");
    for (WitU32 i = 0; i < WIT_RUNTIME_EVENT_CAPACITY; ++i) {
        require(wit_event_remove(&process.Events, &process.Handles, events[i]) == WIT_STATUS_OK,
            "Runtime event close failed");
    }
    WitU64 held[WIT_RUNTIME_HANDLE_CAPACITY];
    WitU32 count = 0;
    while (process.Handles.Count < process.Handles.Limit) {
        held[count] = wit_handle_grant(&process.Handles, WIT_HANDLE_SELF, 0);
        require(held[count] != 0, "Runtime handle admission failed");
        ++count;
    }
    require(!wit_handle_grant(&process.Handles, WIT_HANDLE_SELF, 0), "Runtime handle exhaustion missing");
    while (count) {
        require(wit_handle_close(&process.Handles, held[--count]) == WIT_STATUS_OK, "Runtime handle close failed");
    }
    require(process.Handles.Count == handles && !process.Events.Count, "Runtime resource quota probe leaked");
    WitU64 base = 0;
    const WitU64 span = WIT_RUNTIME_PAGE_CAPACITY * 4096ULL;
    require(wit_user_memory_reserve(&process.Space, span, 4096, &base) == WIT_STATUS_OK &&
            wit_pages_free_count(pages) == before,
        "Runtime reserve consumed backing pages");
    require(wit_user_memory_commit(&process.Space, base, span, 3) == WIT_STATUS_NO_MEMORY &&
            process.Space.OwnedCount == owned &&
            wit_pages_free_count(pages) == before,
        "Runtime quota failure leaked partial commitment/tables");
    require(wit_user_memory_commit(&process.Space, base, 129 * 4096ULL, 0) == WIT_STATUS_OK &&
            !wit_user_space_physical(&process.Space, base, 0, 0),
        "Runtime no-access commitment failed");
    require(wit_user_memory_query(&process.Space, WIT_USER_DATA, sizeof(WitUserMemoryInfo), WIT_MEMORY_INFO_VERSION) ==
            WIT_STATUS_OK,
        "Runtime memory snapshot failed");
    const WitUserMemoryInfo *info =
        (const WitUserMemoryInfo *)wit_user_space_physical(&process.Space, WIT_USER_DATA, 0, 0);
    require(info &&
            info->OwnedLimitBytes == span &&
            info->ReservationCapacity == WIT_RUNTIME_RESERVATION_CAPACITY &&
            info->DynamicCommittedBytes == 129 * 4096ULL,
        "Runtime memory quotas/accounting were hidden");
    require(wit_user_memory_reset(&process.Space, base, 129 * 4096ULL) == WIT_STATUS_OK &&
            wit_user_memory_release(&process.Space, base) == WIT_STATUS_OK &&
            process.Space.OwnedCount == owned &&
            wit_pages_free_count(pages) == before,
        "Runtime reset/release leaked backing");
}

void wit_user_runtime_boot_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    require(wit_pe_validate_profile(
                wit_runtime_boot_image, sizeof(wit_runtime_boot_image), &plan, WIT_PE_UNWIND_RUNTIME) == WitPeTooLarge,
        "Full runtime accepted by small image profile");
    require(wit_pe_validate_profile(wit_runtime_boot_image, sizeof(wit_runtime_boot_image), &plan,
                WIT_PE_RUNTIME_FULL) == WitPeUnsupportedImage,
        "Full runtime bypassed unwind policy");
    require(wit_user_create_pe_profile(&process, pages, 0, wit_runtime_boot_image, sizeof(wit_runtime_boot_image),
                WIT_USER_IMAGE_BASE, "boot:/WitOS.NativeAotBoot.pe",
                WIT_PE_UNWIND_RUNTIME | WIT_PE_RUNTIME_FULL) == WitPeOk,
        "Runtime rejection fixture admission failed");
    ++((WitUserStartup *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0))->Version;
    wit_user_run(&process);
    const int rejected = process.State == WitUserExited &&
        process.ExitCode == 0xFFFF0001ULL &&
        !process.Writes &&
        !process.Handles.Count &&
        !process.Events.Count;
    wit_user_destroy(&process);
    require(rejected && wit_pages_free_count(pages) == before,
        "Invalid full-runtime handoff was accepted or leaked resources");
    wit_console_write("[TEST-PASS] Runtime.InvalidHandoffTeardown\n");
    const WitU64 bases[] = {WIT_USER_IMAGE_BASE, WIT_USER_IMAGE_ALTERNATE};
    for (WitU32 i = 0; i < 2; ++i) {
        require(wit_user_create_pe_profile(&process, pages, 0, wit_runtime_boot_image, sizeof(wit_runtime_boot_image),
                    bases[i], "boot:/WitOS.NativeAotBoot.native-fault.pe",
                    WIT_PE_UNWIND_RUNTIME | WIT_PE_RUNTIME_FULL) == WitPeOk,
            "Native fault image admission failed");
        wit_console_write("Runtime native fault base: ");
        wit_console_write_hex(bases[i]);
        wit_console_write("\n");
        wit_user_run(&process);
        const int contained = process.State == WitUserExited &&
            process.ExitCode == 0xC000001DULL &&
            process.Fatal.Code == 0xC000001DU &&
            process.Fatal.Context.Rip == process.Fatal.Address &&
            process.Fatal.Address >= process.ImageBase &&
            process.Fatal.Address - process.ImageBase < process.ImageSize &&
            process.HardwareIllegalFaults == 1 &&
            process.ExceptionContinuations == 0 &&
            !process.Handles.Count &&
            !process.Events.Count;
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Native runtime fault teardown leaked pages");
        require(contained, "Native fault did not follow actual runtime fail-fast policy");
        wit_console_write("[TEST-PASS] Runtime.NativeFaultContained\n");
    }
    for (WitU32 i = 0; i < 2; ++i) {
        require(wit_user_create_pe_profile(&process, pages, 0, wit_runtime_boot_image, sizeof(wit_runtime_boot_image),
                    bases[i], "boot:/WitOS.NativeAotBoot.stack-overflow.pe",
                    WIT_PE_UNWIND_RUNTIME | WIT_PE_RUNTIME_FULL) == WitPeOk,
            "Stack overflow image admission failed");
        const WitU64 stackLow = process.Threads[0].StackBottom;
        wit_console_write("Runtime managed stack overflow base: ");
        wit_console_write_hex(bases[i]);
        wit_console_write("\n");
        wit_user_run(&process);
        wit_console_write("Runtime stack fault vector/error/address/low: ");
        wit_console_write_u64(process.FaultVector);
        wit_console_write("/");
        wit_console_write_hex(process.FaultError);
        wit_console_write("/");
        wit_console_write_hex(process.FaultAddress);
        wit_console_write("/");
        wit_console_write_hex(stackLow);
        wit_console_write("\n");
        const int contained = process.State == WitUserFaulted &&
            process.FaultVector == 14 &&
            (process.FaultError == 4 || process.FaultError == 6) &&
            process.FaultAddress < stackLow &&
            process.FaultAddress >= stackLow - 4096 &&
            wit_user_space_physical(&process.Space, process.FaultState.Rip, 0, 1) &&
            !process.Handles.Count &&
            !process.Events.Count;
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Managed stack overflow leaked component backing");
        require(contained, "Managed stack exhaustion missed its fixed guard or escaped containment");
        wit_console_write("[TEST-PASS] Runtime.ManagedStackOverflowContained\n");
    }
    for (WitU32 i = 0; i < 2; ++i) {
        require(wit_user_create_pe_profile(&process, pages, 0, wit_runtime_boot_image, sizeof(wit_runtime_boot_image),
                    bases[i], "boot:/WitOS.NativeAotBoot.init-failure.pe",
                    WIT_PE_UNWIND_RUNTIME | WIT_PE_RUNTIME_FULL) == WitPeOk,
            "GC init-failure admission failed");
        // The real allocator enforces a smaller component budget after image
        // admission. No GC/OS result is replaced with a synthetic failure.
        process.Space.PageLimit = process.Space.OwnedCount + 24;
        wit_console_write("Runtime init failure base: ");
        wit_console_write_hex(bases[i]);
        wit_console_write("\n");
        wit_user_run(&process);
        wit_console_write("Runtime init failure exit/commits: ");
        wit_console_write_hex(process.ExitCode);
        wit_console_write("/");
        wit_console_write_u64(process.MemoryCommitFailures);
        wit_console_write("\n");
        const int failed = process.State == WitUserExited &&
            process.ExitCode == 0xFFFFFFFFULL &&
            process.MemoryCommitFailures > 0 &&
            !process.Handles.Count &&
            !process.Events.Count;
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Failed GC initialization leaked backing");
        require(failed, "GC init-failure did not propagate actual allocation failure");
        wit_console_write("[TEST-PASS] Runtime.GcInitFailureTeardown\n");
    }
    const char *abruptNames[] = {"boot:/WitOS.NativeAotBoot.raw-join.pe", "boot:/WitOS.NativeAotBoot.raw-detached.pe",
        "boot:/WitOS.NativeAotBoot.fault-join.pe", "boot:/WitOS.NativeAotBoot.fault-detached.pe"};
    for (WitU32 mode = 0; mode < 4; ++mode) {
        for (WitU32 i = 0; i < 2; ++i) {
            require(
                wit_user_create_pe_profile(&process, pages, 0, wit_runtime_boot_image, sizeof(wit_runtime_boot_image),
                    bases[i], abruptNames[mode], WIT_PE_UNWIND_RUNTIME | WIT_PE_RUNTIME_FULL) == WitPeOk,
                "Abrupt runtime admission failed");
            wit_console_write("Runtime abrupt mode/base: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_hex(bases[i]);
            wit_console_write("\n");
            wit_user_run(&process);
            WitU64 report[4] = {0};
            const int copied = wit_user_copy_from(
                &process.Space, process.ImageBase + WIT_RUNTIME_ABRUPT_REPORT_RVA, (WitU8 *)report, sizeof(report));
            wit_console_write("Runtime abrupt exit/report: ");
            wit_console_write_hex(process.ExitCode);
            wit_console_write("/");
            for (WitU32 n = 0; n < 4; ++n) {
                wit_console_write_u64(report[n]);
                wit_console_write(n == 3 ? "\n" : "/");
            }
            const int contained = process.State == WitUserExited &&
                process.ExitCode == (mode < 2 ? WIT_PROCESS_ABRUPT_THREAD_EXIT : 0xC000001DULL) &&
                copied &&
                report[0] == 1 &&
                !report[1] &&
                !report[2] &&
                !report[3] &&
                !process.Handles.Count &&
                !process.Events.Count &&
                (mode < 2 ? (process.AbruptThreadId &&
                                process.AbruptThreadId != process.Threads[0].Handle &&
                                process.AbruptThreadCode == 0x1234)
                          : (process.Fatal.Code == 0xC000001DU &&
                                process.Fatal.Context.ThreadId != process.Threads[0].Handle &&
                                process.HardwareIllegalFaults == 1));
            wit_user_destroy(&process);
            require(wit_pages_free_count(pages) == before, "Abrupt runtime teardown leaked pages");
            require(contained, "Attached runtime worker survived abrupt termination or ran cleanup");
            wit_console_write("[TEST-PASS] Runtime.AbruptWorkerContained\n");
        }
    }
    for (WitU32 i = 0; i < 4; ++i) {
        const WitPeStatus status =
            wit_user_create_pe_profile(&process, pages, 0, wit_runtime_boot_image, sizeof(wit_runtime_boot_image),
                bases[i % 2], "boot:/WitOS.NativeAotBoot.pe", WIT_PE_UNWIND_RUNTIME | WIT_PE_RUNTIME_FULL);
        wit_console_write("Runtime boot image base: ");
        wit_console_write_hex(bases[i % 2]);
        wit_console_write("\n");
        wit_console_write("Runtime boot load status: ");
        wit_console_write_u64(status);
        wit_console_write("\n");
        require(status == WitPeOk, "Full runtime image admission failed");
        require(process.Space.PageLimit == WIT_RUNTIME_PAGE_CAPACITY &&
                process.Space.ReservationLimit == WIT_RUNTIME_RESERVATION_CAPACITY &&
                process.Space.OwnedCount > WIT_USER_PAGE_CAPACITY,
            "Full runtime resource profile missing");
        memory_profile(pages);
        wit_console_write("[TEST-PASS] Runtime.MemoryProfile\n");
        wit_user_run(&process);
        wit_console_write("Runtime boot state/exit/rip/owned: ");
        wit_console_write_u64(process.State);
        wit_console_write("/");
        wit_console_write_hex(process.ExitCode);
        wit_console_write("/");
        wit_console_write_hex(process.FaultState.Rip);
        wit_console_write("/");
        wit_console_write_u64(process.Space.OwnedCount);
        wit_console_write("\n");
        if (process.ExitCode != 42) {
            wit_console_write("Runtime fatal code/parameters/rbx/r14/r15: ");
            wit_console_write_hex(process.Fatal.Code);
            wit_console_write("/");
            for (WitU32 k = 0; k < process.Fatal.ParameterCount && k < 2; ++k) {
                wit_console_write_hex(process.Fatal.Parameters[k]);
                wit_console_write("/");
            }
            wit_console_write_hex(process.Fatal.Context.Rbx);
            wit_console_write("/");
            wit_console_write_hex(process.Fatal.Context.R14);
            wit_console_write("/");
            wit_console_write_hex(process.Fatal.Context.R15);
            wit_console_write("\n");
            for (WitU32 k = 0; k < process.Space.ReservationLimit; ++k) {
                if (process.Space.Reservations[k].Size) {
                    wit_console_write("Runtime reservation: ");
                    wit_console_write_hex(process.Space.Reservations[k].Base);
                    wit_console_write("+");
                    wit_console_write_hex(process.Space.Reservations[k].Size);
                    wit_console_write("\n");
                }
            }
        }
        const int faultDelivery = process.HardwareNullReads == 12 &&
            process.HardwareNullWrites == 12 &&
            process.HardwareDivideFaults == 12 &&
            process.ExceptionContinuations == 36;
        wit_console_write("Runtime hardware faults read/write/divide/continue: ");
        wit_console_write_u64(process.HardwareNullReads);
        wit_console_write("/");
        wit_console_write_u64(process.HardwareNullWrites);
        wit_console_write("/");
        wit_console_write_u64(process.HardwareDivideFaults);
        wit_console_write("/");
        wit_console_write_u64(process.ExceptionContinuations);
        wit_console_write("\n");
        const int memoryFailure = process.MemoryCommitFailures > 0;
        wit_console_write("Runtime managed commit failures: ");
        wit_console_write_u64(process.MemoryCommitFailures);
        wit_console_write("\n");
        wit_console_write("Runtime parked foreign object waits: ");
        wit_console_write_u64(process.ForeignObjectWaitSuspends);
        wit_console_write("\n");
        wit_console_write("Runtime managed thread capacity failures: ");
        wit_console_write_u64(process.ReferenceThreadCapacityFailures);
        wit_console_write("\n");
        const int capacityFailure = process.ReferenceThreadCapacityFailures == 4;
        const int parked = process.ForeignObjectWaitSuspends > 0;
        const int orderly = process.OrderlyThreadExits == 43;
        wit_console_write("Runtime orderly thread completions: ");
        wit_console_write_u64(process.OrderlyThreadExits);
        wit_console_write("\n");
        wit_console_write("Runtime execution ticks/limit: ");
        wit_console_write_u64(process.Ticks);
        wit_console_write("/");
        wit_console_write_u64(process.TickLimit);
        wit_console_write("\n");
        const int completed = process.State == WitUserExited && process.ExitCode == 42;
        const int released = !process.Handles.Count && !process.Events.Count;
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Runtime teardown leaked backing or page tables");
        require(released, "Runtime exit retained handles/events");
        require(completed, "Guest managed runtime workload failed");
        require(faultDelivery, "Full runtime hardware fault translation was not exercised");
        require(memoryFailure, "Managed allocation pressure did not reach the real OS adapter");
        require(orderly, "Runtime workers bypassed orderly completion");
        require(parked, "Managed parked contexts were not scanned through counted suspension");
        require(capacityFailure, "Managed Thread.Start did not exercise actual kernel capacity failure");
    }
    wit_console_write("[TEST-PASS] Runtime.ManagedBootAndGc\n[TEST-PASS] Runtime.RelocationAndTeardown\n");
}
#else
void wit_user_runtime_boot_test(WitPageAllocator *pages)
{
    (void)pages;
}
#endif
