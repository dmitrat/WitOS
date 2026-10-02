#include "user.h"
#include "witos/platform.h"
#if defined(WITOS_TEST_CORECLR_MEMORY)
#include "coreclr_memory_image.h"
#include "coreclr_mapper_image.h"
#include "protocol.h"
static WitUserProcess process;

static void require(int value, const char *message)
{
    if (!value) {
        wit_panic(message);
    }
}

static void mapper_adapter(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    for (WitU32 run = 0; run < 14; ++run) {
        require(
            wit_user_create_pe_profile(&process, pages, 0, wit_coreclr_mapper_image, sizeof(wit_coreclr_mapper_image),
                WIT_USER_IMAGE_BASE, "boot:/CoreClrMapperFixture.pe", WIT_PE_UNWIND_RUNTIME) == WitPeOk,
            "VMToOS fixture load failed");
        WitUserTestConfig *config = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
        config->Mode = run >= 7 ? run - 5 : run ? 1 : 0;
        if (run && run < 7) {
            process.Space.PageLimit = process.Space.OwnedCount + run - 1;
        }
        wit_user_run(&process);
        const WitU64 expected = run >= 7 ? 0xFFFF0001ULL : 42;
        if (!run) {
            require(process.ExceptionContinuations == 3 && process.HardwareNullReads == 5,
                "Dynamic fault did not continue through actual exception dispatch");
        }
        if (run >= 7) {
            const WitU64 *report = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
            require(report && *report == run - 5, "Dynamic failure did not reach unwind boundary");
        }
        if (run >= 9) {
            const WitU64 *report = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
            require(report &&
                    report[4] == 1 &&
                    report[5] == run - 5 &&
                    process.State == WitUserFaulted &&
                    process.FaultVector == 14 &&
                    process.HardwareNullReads == 1 &&
                    !process.ExceptionContinuations,
                "Malformed collided dispatcher escaped containment");
            wit_user_destroy(&process);
            require(wit_pages_free_count(pages) == before, "Rejected collided dispatcher leaked");
            continue;
        }
        if (process.State != WitUserExited || process.ExitCode != expected) {
            wit_console_write("VMToOS run/state/code: ");
            wit_console_write_u64(run);
            wit_console_write("/");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
            const WitU64 *diagnostic =
                (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
            if (diagnostic) {
                wit_console_write("Dynamic cleanup trace: ");
                wit_console_write_u64(diagnostic[4]);
                wit_console_write("\n");
            }
            wit_panic("VMToOS mapper guest failed");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "VMToOS component leaked");
    }
    require(wit_user_create_pe_profile(&process, pages, 0, wit_coreclr_mapper_image, sizeof(wit_coreclr_mapper_image),
                WIT_USER_IMAGE_BASE, "boot:/CoreClrMapperFixture.pe", WIT_PE_UNWIND_RUNTIME) == WitPeOk,
        "Module reader exhaustion fixture failed");
    ((WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0))->Mode = 20;
    wit_user_run(&process);
    const WitU64 *report = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
    require(report &&
            report[6] >= 2 &&
            report[7] == 20 &&
            process.State == WitUserExited &&
            process.ExitCode == 0xFFFF0001ULL,
        "Module reader exhaustion was hidden as leaf unwind");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Module reader exhaustion leaked images");
    wit_console_write(
        "[TEST-PASS] Code.VMToOSMapper\n[TEST-PASS] Code.VMToOSMapperRollback\n[TEST-PASS] "
        "Code.DynamicFrameUnwind\n[TEST-PASS] Code.ForeignDynamicUnwind\n[TEST-PASS] "
        "Code.DynamicExceptionDispatch\n[TEST-PASS] Code.DynamicTargetUnwind\n[TEST-PASS] "
        "Code.CoreClrCollidedDispatch\n[TEST-PASS] Code.CollidedContextRejection\n[TEST-PASS] "
        "Code.DynamicUnwindRejection\n[TEST-PASS] Code.ModuleUnwind\n[TEST-PASS] Code.ForeignModuleUnwind\n");
}

static void sparse_views(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    require(wit_user_create(&process, pages, 0, wit_coreclr_memory_image, sizeof(wit_coreclr_memory_image)),
        "Sparse code fixture create failed");
    WitUserSpace *space = &process.Space;
    WitU64 backing = 0, rx = 0, rw = 0;
    const WitU32 initial = space->OwnedCount, limit = space->PageLimit;
    require(wit_user_memory_reserve(space, 65536, 65536, &backing) == WIT_STATUS_OK &&
            wit_user_code_reserve(space, 65536, 65536, 0, ~0ULL, &rx) == WIT_STATUS_OK &&
            wit_user_memory_reserve(space, 65536, 65536, &rw) == WIT_STATUS_OK,
        "Sparse reservations failed");
    const WitU64 free = wit_pages_free_count(pages);
    require(wit_user_code_map_sparse(space, rx, backing, 65536, 5) == WIT_STATUS_OK &&
            wit_user_code_map_sparse(space, rw, backing, 65536, 3) == WIT_STATUS_OK &&
            space->OwnedCount == initial &&
            !space->AliasCount &&
            wit_pages_free_count(pages) == free,
        "Sparse views consumed backing/tables");
    require(wit_user_memory_release(space, backing) == WIT_STATUS_BUSY &&
            wit_user_memory_commit(space, rx, 4096, 3) == WIT_STATUS_DENIED,
        "Sparse ownership contract bypassed");
    int committed = 0;
    WitU32 failures = 0;
    for (WitU32 extra = 0; extra < 10; ++extra) {
        space->PageLimit = initial + extra;
        const WitU64 status = wit_user_memory_commit(space, backing, 8192, 0);
        if (status == WIT_STATUS_OK) {
            committed = 1;
            break;
        }
        require(status == WIT_STATUS_NO_MEMORY &&
                space->OwnedCount == initial &&
                !space->AliasCount &&
                wit_pages_free_count(pages) == free &&
                !wit_user_space_physical(space, rx, 0, 0) &&
                !wit_user_space_physical(space, rw, 0, 0),
            "Sparse multi-view commit did not roll back atomically");
        ++failures;
    }
    space->PageLimit = limit;
    require(committed &&
            failures >= 3 &&
            space->AliasCount == 4 &&
            !wit_user_space_physical(space, backing, 0, 0) &&
            wit_user_space_physical(space, rx, 0, 1) == wit_user_space_physical(space, rw, 1, 0) &&
            wit_user_space_physical(space, rx + 4096, 0, 1) == wit_user_space_physical(space, rw + 4096, 1, 0),
        "Sparse commit did not publish both views");
    const WitU8 code[] = {0xB8, 42, 0, 0, 0, 0xC3};
    require(wit_user_copy_to(space, rw, code, sizeof(code)), "Sparse RW write failed");
    const WitU32 held = space->OwnedCount;
    const WitU64 heldFree = wit_pages_free_count(pages);
    space->PageLimit = held;
    require(wit_user_memory_commit(space, backing, 12288, 0) == WIT_STATUS_NO_MEMORY &&
            space->OwnedCount == held &&
            space->AliasCount == 4 &&
            wit_pages_free_count(pages) == heldFree &&
            *(WitU8 *)wit_user_space_physical(space, rx, 0, 0) == 0xB8,
        "Late failed commit changed existing backing");
    space->PageLimit = limit;
    require(
        wit_user_memory_release(space, rw) == WIT_STATUS_OK && space->AliasCount == 2, "RW sparse view release failed");
    require(wit_user_memory_commit(space, backing + 8192, 4096, 0) == WIT_STATUS_OK &&
            space->AliasCount == 3 &&
            wit_user_space_physical(space, rx + 8192, 0, 1),
        "Late commit did not update surviving RX view");
    require(wit_user_memory_reserve(space, 65536, 65536, &rw) == WIT_STATUS_OK &&
            wit_user_code_map_sparse(space, rw, backing, 65536, 3) == WIT_STATUS_OK &&
            space->AliasCount == 6 &&
            wit_user_space_physical(space, rx + 8192, 0, 1) == wit_user_space_physical(space, rw + 8192, 1, 0),
        "New sparse view missed existing commitment");
    WitCodeMemoryRequest request = {
        WIT_CODE_MEMORY_VERSION, sizeof(request), WIT_CODE_PUBLISH, 0, rx, 0, sizeof(code), 0, 0, 0};
    const WitU64 data[] = {rx, 0};
    require(wit_user_copy_to(space, WIT_USER_DATA, (const WitU8 *)data, sizeof(data)) &&
            wit_user_copy_to(space, WIT_USER_DATA + 128, (const WitU8 *)&request, sizeof(request)),
        "Sparse fixture input failed");
    wit_user_run(&process);
    require(process.State == WitUserExited && process.ExitCode == 42, "Sparse committed code did not execute");
    require(wit_user_memory_release(space, backing) == WIT_STATUS_BUSY &&
            wit_user_memory_release(space, rw) == WIT_STATUS_OK &&
            wit_user_memory_release(space, rx) == WIT_STATUS_OK &&
            wit_user_memory_release(space, backing) == WIT_STATUS_OK &&
            space->OwnedCount == initial &&
            !space->AliasCount,
        "Sparse view/backing teardown leaked");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Sparse component teardown leaked");
    wit_console_write("[TEST-PASS] Code.SparseViewsAndLateCommit\n[TEST-PASS] Code.SparseCommitRollback\n");
}

void wit_user_code_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    for (WitU64 aliases = 0; aliases < 2; ++aliases) {
        for (WitU64 mode = 0; mode < 3; ++mode) {
            require(wit_user_create(&process, pages, 0, wit_coreclr_memory_image, sizeof(wit_coreclr_memory_image)),
                "Code fixture creation failed");
            WitUserSpace *space = &process.Space;
            WitU64 address = 0, aliasReservation = 0, entryAddress = 0;
            const WitU32 fixedOwned = space->OwnedCount;
            const WitU64 gapFree = wit_pages_free_count(pages);
            WitU64 near = 99;
            require(wit_user_code_reserve(space, 65536, 65536, WIT_USER_CODE_BASE + 1, WIT_USER_CODE_BASE + 0x1000000,
                        &near) == WIT_STATUS_OK &&
                    near == WIT_USER_CODE_BASE + 0x10000 &&
                    space->OwnedCount == fixedOwned &&
                    wit_pages_free_count(pages) == gapFree,
                "Bounded code reservation consumed backing or missed alignment");
            require(wit_user_memory_release(space, near) == WIT_STATUS_OK, "Bounded reservation release failed");
            near = 99;
            require(wit_user_code_reserve(space, 8192, 4096, WIT_USER_CODE_BASE, WIT_USER_CODE_BASE + 4096, &near) ==
                        WIT_STATUS_NO_MEMORY &&
                    !near &&
                    space->OwnedCount == fixedOwned,
                "Out-of-window reservation mutated ownership");
            require(wit_user_memory_reserve(space, 8192, 4096, &address) == WIT_STATUS_OK, "Code reserve failed");
            require(wit_user_memory_commit(space, address, 4096, 3) == WIT_STATUS_OK,
                "Code initial writable commit failed");
            const WitU8 code[] = {0xB8, 42, 0, 0, 0, 0xC3}; // mov eax,42; ret -- x64 fixture, not a JIT claim.
            require(wit_user_copy_to(space, address, code, sizeof(code)), "Code initialization failed");
            const WitU64 physical = wit_user_space_physical(space, address, 1, 0);
            const WitU32 owned = space->OwnedCount;
            require(wit_user_code_protect(space, address, 8192, 5) == WIT_STATUS_NOT_COMMITTED &&
                    wit_user_space_physical(space, address, 1, 0) == physical &&
                    !wit_user_space_physical(space, address, 0, 1),
                "Code partial protection was not atomic");
            require(wit_user_code_protect(space, address, 4096, 7) == WIT_STATUS_INVALID_ARGUMENT &&
                    wit_user_memory_protect(space, address, 4096, 5) == WIT_STATUS_INVALID_ARGUMENT,
                "Writable-executable or ordinary executable allocation accepted");
            require(wit_user_code_publish(space, address, sizeof(code)) == WIT_STATUS_DENIED,
                "Writable code was published");
            require(wit_user_code_protect(space, address, 4096, 5) == WIT_STATUS_OK &&
                    wit_user_space_physical(space, address, 0, 1) == physical &&
                    !wit_user_space_physical(space, address, 1, 0),
                "RX protection failed");
            require(wit_user_memory_reset(space, address, 4096) == WIT_STATUS_DENIED && *(WitU8 *)physical == code[0],
                "Reset modified executable bytes");
            require(wit_user_code_publish(space, address, 8192) == WIT_STATUS_NOT_COMMITTED &&
                    wit_user_code_publish(space, address, ~0ULL) == WIT_STATUS_BAD_ADDRESS &&
                    wit_user_code_publish(space, address, 0) == WIT_STATUS_BAD_ADDRESS,
                "Invalid publication range accepted");
            require(wit_user_code_publish(space, address, sizeof(code)) == WIT_STATUS_OK && space->OwnedCount == owned,
                "Code publication changed ownership");
            entryAddress = address;
            if (aliases) {
                require(wit_user_memory_protect(space, address, 4096, 3) == WIT_STATUS_OK &&
                        wit_user_memory_commit(space, address + 4096, 4096, 3) == WIT_STATUS_OK,
                    "Alias owner setup failed");
                require(wit_user_code_reserve(space, 2 * 1024 * 1024 + 8192, 2 * 1024 * 1024, 0, ~0ULL,
                            &aliasReservation) == WIT_STATUS_OK,
                    "Alias reservation failed");
                entryAddress = aliasReservation + 2 * 1024 * 1024 - 4096; // Cross two private PTs.
                const WitU32 limit = space->PageLimit, aliasOwned = space->OwnedCount;
                const WitU64 aliasFree = wit_pages_free_count(pages);
                space->PageLimit = aliasOwned + 1;
                require(wit_user_code_alias(space, entryAddress, address, 8192, 5) == WIT_STATUS_NO_MEMORY &&
                        !space->AliasCount &&
                        space->OwnedCount == aliasOwned &&
                        wit_pages_free_count(pages) == aliasFree &&
                        !wit_user_space_physical(space, entryAddress, 0, 0),
                    "Partial alias mapping did not roll back");
                space->PageLimit = limit;
                require(wit_user_code_alias(space, entryAddress, address, 8192, 7) == WIT_STATUS_INVALID_ARGUMENT &&
                        wit_user_code_alias(space, entryAddress, WIT_USER_CODE, 4096, 5) == WIT_STATUS_BAD_ADDRESS,
                    "RWX/fixed image alias accepted");
                require(wit_user_code_alias(space, entryAddress, address, 8192, 5) == WIT_STATUS_OK &&
                        space->AliasCount == 2 &&
                        wit_user_space_physical(space, entryAddress, 0, 1) == physical &&
                        !wit_user_space_physical(space, entryAddress, 1, 0) &&
                        !wit_user_space_physical(space, address, 0, 1),
                    "Separate RW/RX aliases incorrect");
                require(
                    wit_user_code_alias(space, entryAddress + 8192, entryAddress, 4096, 5) == WIT_STATUS_NOT_COMMITTED,
                    "Alias chain accepted");
                require(wit_user_memory_decommit(space, address, 8192) == WIT_STATUS_BUSY &&
                        wit_user_memory_release(space, address) == WIT_STATUS_BUSY,
                    "Aliased backing was released");
                ((WitU8 *)physical)[1] = 17;
                require(((WitU8 *)wit_user_space_physical(space, entryAddress, 0, 0))[1] == 17,
                    "Code aliases do not share backing");
                ((WitU8 *)physical)[1] = 42;
                require(entryAddress > process.ImageBase && entryAddress - process.ImageBase < 0x80000000ULL,
                    "Code alias escaped rel32 reach");
                require(wit_user_code_publish(space, entryAddress, sizeof(code)) == WIT_STATUS_OK,
                    "RX alias publication failed");
                require(wit_user_memory_query(
                            space, WIT_USER_DATA, sizeof(WitUserMemoryInfo), WIT_MEMORY_INFO_VERSION) == WIT_STATUS_OK,
                    "Code memory snapshot failed");
                const WitUserMemoryInfo *info =
                    (const WitUserMemoryInfo *)wit_user_space_physical(space, WIT_USER_DATA, 0, 0);
                require(info->CodeVirtualBase == WIT_USER_CODE_BASE &&
                        info->CodeVirtualBytes == WIT_USER_CODE_LIMIT - WIT_USER_CODE_BASE &&
                        info->ReservedBytes == 2 * 1024 * 1024 + 16384 &&
                        info->DynamicCommittedBytes == 8192,
                    "Code discovery double-counted aliases or hid the near arena");
            }
            WitCodeMemoryRequest request = {
                WIT_CODE_MEMORY_VERSION, sizeof(request), WIT_CODE_PUBLISH, 0, entryAddress, 0, sizeof(code), 0, 0, 0};
            require(wit_user_copy_to(space, WIT_USER_DATA + 128, (const WitU8 *)&request, sizeof(request)),
                "Code syscall request publication failed");
            WitU64 callResult = 99;
            require(wit_user_code_call(&process, WIT_USER_DATA + 128, sizeof(request) - 1, 0, &callResult) ==
                        WIT_STATUS_INVALID_ARGUMENT &&
                    !callResult,
                "Code syscall size validation failed");
            require(wit_user_code_call(&process, WIT_USER_DATA + 128, sizeof(request), 1, &callResult) ==
                        WIT_STATUS_INVALID_ARGUMENT &&
                    !callResult,
                "Code syscall reserved validation failed");
            require(wit_user_code_call(&process, WIT_USER_DATA_END - 32, sizeof(request), 0, &callResult) ==
                        WIT_STATUS_BAD_ADDRESS &&
                    !callResult,
                "Code syscall incomplete input accepted");
            WitU64 data[] = {entryAddress, mode};
            require(
                wit_user_copy_to(space, WIT_USER_DATA, (const WitU8 *)data, sizeof(data)), "Code fixture input failed");
            wit_user_run(&process);
            if (mode == 0) {
                require(process.State == WitUserExited && process.ExitCode == 42,
                    "Dynamic RX code did not execute in user mode");
            } else {
                require(process.State == WitUserFaulted &&
                        process.FaultVector == 14 &&
                        process.FaultCs == WIT_USER_CS &&
                        process.FaultAddress == entryAddress &&
                        process.FaultError == (mode == 1 ? 7U : 21U),
                    "RX write/NX revocation was not enforced by CPU");
            }
            require(!process.Handles.Count && !process.Events.Count, "Code fixture handles leaked");
            if (aliases) {
                require(wit_user_memory_release(space, address) == WIT_STATUS_BUSY,
                    "Live alias lost its owner after execution");
                require(wit_user_memory_release(space, aliasReservation) == WIT_STATUS_OK && !space->AliasCount,
                    "Alias view teardown failed");
            }
            require(wit_user_memory_release(space, address) == WIT_STATUS_OK && space->OwnedCount == fixedOwned,
                "Backing/table ownership was not restored exactly");
            wit_user_destroy(&process);
            require(wit_pages_free_count(pages) == before, "Code fixture leaked backing/tables");
        }
    }
    mapper_adapter(pages);
    sparse_views(pages);
    wit_console_write(
        "[TEST-PASS] Code.OwnershipAndAtomicProtection\n[TEST-PASS] Code.PublicationAndExecution\n[TEST-PASS] "
        "Code.WriteAndNxFaults\n[TEST-PASS] Code.AliasesAndLifetime\n[TEST-PASS] Code.Teardown\n");
}
#else
void wit_user_code_self_test(WitPageAllocator *pages)
{
    (void)pages;
}
#endif
