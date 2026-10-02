#include "x64.h"
#include "user.h"
#if defined(WITOS_TEST_RUNTIME_CONFIG)
#include "witos/platform.h"
#include "witos/random.h"
#include "protocol.h"
#include "../System.Native/diagnostics.h"
#include "runtime_config_image.h"
#include "runtime_cpu_image.h"
#include "runtime_threads_image.h"
#include "runtime_com_image.h"
#include "runtime_context_mutation_image.h"
#include "runtime_unwind_image.h"

static WitUserProcess process;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void reference_creation_rollback(WitPageAllocator *pages)
{
    const WitU32 owned = process.Space.OwnedCount, limit = process.Space.PageLimit, handles = process.Handles.Count;
    const WitU64 creates = process.ThreadCreates, detached = process.DetachedCreates;
    const WitU64 requestAddress = process.Threads[0].StackBottom, idAddress = requestAddress + 64;
    WitThreadCreateRequest request = {WIT_THREAD_CREATE_REFERENCE_VERSION, sizeof(request), process.ImageEntry, 0, 0,
        idAddress, WIT_THREAD_START_SUSPENDED, 0};
    const WitU32 sentinel = 0xA5A5A5A5U;
    require(process.TlsBytes != 0, "Reference rollback requires actual compiler TLS");
    require(wit_user_copy_to(&process.Space, requestAddress, (const WitU8 *)&request, sizeof(request)) &&
            wit_user_copy_to(&process.Space, idAddress, (const WitU8 *)&sentinel, sizeof(sentinel)),
        "Reference rollback request setup failed");
    const WitU32 allocations = (WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM) / 4096 + 2;
    for (WitU32 remaining = 0; remaining < allocations; ++remaining) {
        process.Space.PageLimit = owned + remaining;
        const WitU64 freeBefore = wit_pages_free_count(pages);
        WitU64 result = 99;
        WitU32 after = 0;
        require(wit_user_thread_create_reference(&process, requestAddress, sizeof(request), &result) ==
                    WIT_STATUS_NO_MEMORY &&
                !result &&
                process.Space.OwnedCount == owned &&
                process.Handles.Count == handles &&
                process.ThreadCreates == creates &&
                process.DetachedCreates == detached &&
                process.Threads[1].State == WitThreadEmpty &&
                wit_pages_free_count(pages) == freeBefore &&
                !wit_user_space_physical(&process.Space, WIT_USER_STACK_BOTTOM + WIT_USER_THREAD_STRIDE, 0, 0) &&
                !wit_user_space_physical(&process.Space, WIT_USER_TLS + WIT_USER_THREAD_STRIDE, 0, 0) &&
                !wit_user_space_physical(&process.Space, WIT_USER_TLS + WIT_USER_THREAD_STRIDE + 4096, 0, 0) &&
                wit_user_copy_from(&process.Space, idAddress, (WitU8 *)&after, sizeof(after)) &&
                after == sentinel,
            "Reference-bearing thread allocation rollback failed");
        for (WitU32 n = 0; n < process.Handles.Limit; ++n) {
            require(!process.ThreadReferences[n].Handle, "Failed creation published a reference");
        }
    }
    process.Space.PageLimit = limit;
}

static void run(WitPageAllocator *pages, WitU64 mode, WitU64 base)
{
    const WitU64 before = wit_pages_free_count(pages);
    const WitU8 *image = (mode == 8 || mode == 11 || mode == 13 || mode == 16 || mode == 18 || mode == 23)
        ? wit_runtime_config_raw_image
        : wit_runtime_config_image;
    const WitU32 size = (mode == 8 || mode == 11 || mode == 13 || mode == 16 || mode == 18 || mode == 23)
        ? sizeof(wit_runtime_config_raw_image)
        : sizeof(wit_runtime_config_image);
    const WitU8 *selected = mode == 168 ? wit_runtime_threads_image
        : mode >= 106
        ? ((mode == 111 || mode == 121 || mode == 132) ? wit_runtime_unwind_raw_image : wit_runtime_unwind_image)
        : mode >= 94 ? ((mode == 95 || (mode >= 97 && mode != 100 && mode != 103 && mode != 105))
                               ? wit_runtime_context_mutation_raw_image
                               : wit_runtime_context_mutation_image)
        : mode >= 78 ? ((mode == 80 ||
                            mode == 83 ||
                            mode == 84 ||
                            mode == 85 ||
                            mode == 87 ||
                            mode == 88 ||
                            mode == 90 ||
                            mode == 92 ||
                            mode == 93)
                               ? wit_runtime_com_raw_image
                               : wit_runtime_com_image)
        : mode >= 64 ? ((mode == 65 || mode == 67 || mode == 69 || mode == 72 || mode == 73 || mode == 75 || mode == 77)
                               ? wit_runtime_threads_raw_image
                               : wit_runtime_threads_image)
        : mode >= 24 ? ((mode == 25 || mode == 28 || mode == 30 || mode == 54 || mode == 59 || mode == 61 || mode == 63)
                               ? wit_runtime_cpu_raw_image
                               : wit_runtime_cpu_image)
                     : image;
    const WitU32 selectedSize = mode == 168 ? sizeof(wit_runtime_threads_image)
        : mode >= 106 ? ((mode == 111 || mode == 121 || mode == 132) ? sizeof(wit_runtime_unwind_raw_image)
                                                                     : sizeof(wit_runtime_unwind_image))
        : mode >= 94  ? ((mode == 95 || (mode >= 97 && mode != 100 && mode != 103 && mode != 105))
                                ? sizeof(wit_runtime_context_mutation_raw_image)
                                : sizeof(wit_runtime_context_mutation_image))
        : mode >= 78  ? ((mode == 80 ||
                            mode == 83 ||
                            mode == 84 ||
                            mode == 85 ||
                            mode == 87 ||
                            mode == 88 ||
                            mode == 90 ||
                            mode == 92 ||
                            mode == 93)
                                ? sizeof(wit_runtime_com_raw_image)
                                : sizeof(wit_runtime_com_image))
        : mode >= 64 ? ((mode == 65 || mode == 67 || mode == 69 || mode == 72 || mode == 73 || mode == 75 || mode == 77)
                               ? sizeof(wit_runtime_threads_raw_image)
                               : sizeof(wit_runtime_threads_image))
        : mode >= 24 ? ((mode == 25 || mode == 28 || mode == 30 || mode == 54 || mode == 59 || mode == 61 || mode == 63)
                               ? sizeof(wit_runtime_cpu_raw_image)
                               : sizeof(wit_runtime_cpu_image))
                     : size;
    char resource[] = "boot:/RuntimeThreadFixture.pe";
    if (mode == 71) {
        const char *invalid[] = {"", "wrong:/Image.pe", "boot:/", "boot:/nested/image.pe", "boot:/bad name.pe"};
        for (WitU32 i = 0; i < sizeof(invalid) / sizeof(invalid[0]); ++i) {
            require(wit_user_create_named_pe(&process, pages, 0, selected, selectedSize, base, invalid[i]) ==
                        WitPeInvalidImage &&
                    wit_pages_free_count(pages) == before,
                "Invalid resource name consumed memory or was accepted");
        }
        char oversized[WIT_IMAGE_RESOURCE_CAPACITY];
        for (WitU32 i = 0; i < sizeof(oversized); ++i) {
            oversized[i] = 'x';
        }
        require(wit_user_create_named_pe(&process, pages, 0, selected, selectedSize, base, oversized) ==
                    WitPeInvalidImage &&
                wit_pages_free_count(pages) == before,
            "Unterminated resource name was accepted");
    }
    const WitPeStatus loaded = wit_user_create_pe_profile(&process, pages, 0, selected, selectedSize, base,
        (mode == 71 || mode == 72) ? resource : 0, (mode < 24 || mode >= 94) ? WIT_PE_UNWIND_RUNTIME : 0);
    if (loaded != WitPeOk) {
        wit_console_write("Runtime PE mode/status: ");
        wit_console_write_u64(mode);
        wit_console_write("/");
        wit_console_write_u64(loaded);
        wit_console_write("\n");
    }
    require(loaded == WitPeOk, "Runtime configuration fixture load failed");
    if (mode == 71 || mode == 72) {
        const WitU64 address = WIT_USER_INFO + WIT_USER_IMAGE_INFO_OFFSET;
        const WitUserImageInfo *info = (const WitUserImageInfo *)wit_user_space_physical(&process.Space, address, 0, 0);
        require(info &&
                info->ResourceNameLength == sizeof(resource) - 1 &&
                !wit_user_space_physical(&process.Space, address, 1, 0) &&
                !wit_user_space_physical(&process.Space, address + sizeof(*info) - 1, 1, 0),
            "Resource descriptor is writable or incorrectly sized");
        for (WitU32 i = 0; i < sizeof(resource); ++i) {
            require(info->ResourceName[i] == (WitU8)resource[i], "Resource handoff changed the label");
        }
        resource[0] = 'X'; // The published descriptor must retain its owned copy.
        require(info->ResourceName[0] == 'b', "Resource handoff borrowed mutable kernel storage");
    }
    if (mode == 168) {
        reference_creation_rollback(pages);
    }
    const WitU32 owned = process.Space.OwnedCount;
    WitUserTestConfig *config = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    config->Mode = mode;
    const WitU64 generation = wit_random_generation();
    wit_user_run(&process);
    const WitU64 *report = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
    if (mode == 168) {
        if (process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("Thread creation fixture exit: ");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
        }
        require(report &&
                report[0] == 168 &&
                report[1] == 1 &&
                report[2] == 6 &&
                report[3] == 6 &&
                report[4] == 1 &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                process.ThreadCreates == 7 &&
                process.DetachedCreates == 6 &&
                process.DetachedReaps == 6 &&
                process.OrderlyThreadExits == 6 &&
                process.Space.OwnedCount == owned &&
                !process.Handles.Count &&
                !process.Events.Count,
            "Native reference-bearing creation failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Native thread creation teardown leaked");
        return;
    }
    if (mode == 167) {
        require(report &&
                report[0] == mode &&
                report[1] == 562949953421312ULL &&
                !report[2] &&
                !report[11] &&
                process.State == WitUserFaulted &&
                process.FaultVector == 13 &&
                !process.FaultError &&
                process.FaultState.Rip == report[12] &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned,
            "Unsupported GP lost original fault or invoked handlers");
        for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
            require(!process.Threads[i].Exception.Token && !process.Threads[i].ExceptionDepth,
                "Unsupported GP left pending state");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Unsupported GP leaked pages");
        return;
    }
    if (mode >= 137 && mode <= 166) {
        const int corrupt = mode == 157 || mode == 158 || (mode >= 160 && mode <= 165 && mode != 163);
        const WitU64 expected = corrupt ? WIT_NATIVE_GS_FAILURE_EXIT : WIT_TEST_EXIT_CODE;
        if (process.ExitCode != expected) {
            wit_console_write("SEH mode/state/exit: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_hex(process.ExitCode);
            wit_console_write("\n");
        }
        require(report &&
                report[0] == mode &&
                report[1] == 562949953421312ULL &&
                report[2] == (corrupt ? 0U : 1U) &&
                process.State == WitUserExited &&
                process.ExitCode == expected &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned,
            "Compiler SEH execution failed");
        if (mode == 166) {
            require(report[11] == 6 && report[13] == 1, "GP record translation/continuation/compiler catch failed");
        }
        if (corrupt) {
            require(report[7] == ((mode == 158 || mode == 165) ? 1U : 0U) && !report[8] && !report[9],
                "GS cookie failure ran a protected filter/finally/catch");
        }
        for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
            require(!process.Threads[i].Exception.Token && !process.Threads[i].ExceptionDepth,
                "SEH left pending kernel state");
        }
        for (WitU32 i = 0; i < WIT_STACK_LEASE_CAPACITY; ++i) {
            require(!process.StackLeases[i].Token, "SEH leaked walk lease");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "SEH leaked pages");
        return;
    }
    if (mode >= 129 && mode <= 136) {
        const WitU64 expected = (mode == 129 || mode == 132 || mode == 134) ? 0xC0000602ULL : 0xE0426789ULL;
        if (process.ExitCode != expected) {
            wit_console_write("Fail-fast mode/state/exit: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_hex(process.ExitCode);
            wit_console_write("\n");
        }
        require(report &&
                report[0] == mode &&
                report[1] == 281474976710656ULL &&
                !report[2] &&
                process.State == WitUserExited &&
                process.ExitCode == expected &&
                !process.Handles.Count &&
                !process.Events.Count &&
                process.Space.OwnedCount == owned &&
                !process.FatalArmed &&
                process.Fatal.Code == expected,
            "Native fail-fast exit/cleanup contract failed");
        if (mode == 129 || mode == 131 || mode == 132) {
            require(process.Fatal.Address >= process.ImageBase &&
                    process.Fatal.Address < process.ImageBase + process.ImageSize,
                "Fail-fast generated address missing");
        }
        if (mode == 130 || mode == 133) {
            require(process.Fatal.Address == 0x12345678 &&
                    process.Fatal.Context.Rip == 0x23456789 &&
                    process.Fatal.Context.Rsp == 0x34567890 &&
                    process.Fatal.Context.R12 == 0x1122334455667788ULL &&
                    process.Fatal.Parameters[0] == 0xABCDEF,
                "Fail-fast lost supplied diagnostic context");
        }
        if (mode == 135 || mode == 136) {
            require(
                process.Fatal.Address == 0x12345678, "Fail-fast lost cause before invalid context or nested dispatch");
        }
        require(process.Writes == ((mode == 133 || mode == 134 || mode == 135) ? 0U : 1U),
            "Fail-fast diagnostic output mismatch");
        require(report[3] == (mode == 136 ? 1U : 0U), "Fail-fast invoked vectored handler unexpectedly");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Fail-fast leaked pages");
        return;
    }
    if (mode >= 125 && mode <= 128) {
        const WitU64 expected = (mode == 125 || mode == 126) ? WIT_TEST_EXIT_CODE
            : mode == 127                                    ? WIT_GC_TEST_FAIL_FAST_EXIT
                                                             : WIT_EXCEPTION_SOFTWARE_FAILURE_EXIT;
        if (process.ExitCode != expected) {
            wit_console_write("Raise mode/state/exit: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
        }
        require(report &&
                report[0] == mode &&
                report[1] == 140737488355328ULL &&
                process.State == WitUserExited &&
                process.ExitCode == expected &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned,
            "Software exception lifecycle failed");
        if (mode == 125) {
            require(report[2] == 3 && report[3] == 4, "Software exception nesting/parameters failed");
        }
        if (mode == 128) {
            if (report[4] != 1 || report[5] != 1) {
                wit_console_write("Noncontinuable secondary/frame: ");
                wit_console_write_u64(report[4]);
                wit_console_write("/");
                wit_console_write_u64(report[5]);
                wit_console_write("\n");
            }
            require(report[4] == 1 && report[5] == 1, "Noncontinuable exception was not dispatched");
        }
        for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
            require(!process.Threads[i].Exception.Token && !process.Threads[i].ExceptionDepth,
                "Software pending state survived exit");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Software exception leaked pages");
        return;
    }
    if (mode >= 117 && mode <= 124) {
        if ((mode == 117 || mode == 121) && process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("VEH mode/state/exit: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
        }
        require(report &&
                report[0] == mode &&
                report[1] == 70368744177664ULL &&
                report[2] == 1 &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned,
            "VEH resource contract failed");
        if (mode == 117 || mode == 121) {
            require(process.State == WitUserExited && process.ExitCode == WIT_TEST_EXIT_CODE,
                "VEH continuation/prerequisite failed");
        } else if (mode == 124) {
            require(process.State == WitUserExited && process.ExitCode == WIT_NATIVE_GS_FAILURE_EXIT && report[4] == 1,
                "Native dispatch skipped actual GS cookie failure");
        } else {
            require(process.State == WitUserFaulted &&
                    process.FaultVector == (mode == 122 ? 13U : 6U) &&
                    process.FaultState.Rip == report[3],
                "VEH failure lost original fault");
        }
        if (mode == 122) {
            require(report[6] == 1, "Unhandled GP did not reach the search handler");
        }
        require(!process.ExceptionCallback, "VEH callback survived component exit");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "VEH leaked pages");
        return;
    }
    if (mode >= 110 && mode <= 116) {
        if ((mode <= 111 && process.ExitCode != WIT_TEST_EXIT_CODE) ||
            (mode >= 112 && process.State != WitUserFaulted)) {
            wit_console_write("Exception mode/state/exit: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
        }
        require(report &&
                report[0] == mode &&
                report[1] == 35184372088832ULL &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned,
            "Exception upcall resource contract failed");
        if (mode <= 111) {
            require(process.State == WitUserExited &&
                    process.ExitCode == WIT_TEST_EXIT_CODE &&
                    report[2] == (mode == 110 ? 7U : 6U) &&
                    report[3] == 1 &&
                    process.ThreadCreates == (mode == 110 ? 2U : 1U) &&
                    process.ThreadJoins == (mode == 110 ? 1U : 0U),
                "Exception continuation failed");
        } else {
            require(process.State == WitUserFaulted &&
                    process.FaultVector == 6 &&
                    process.FaultError == 0 &&
                    process.FaultState.Rip == report[3] &&
                    report[2] == (mode <= 114 ? 1U : 0U),
                "Exception failure lost original cause");
        }
        require(!process.ExceptionCallback, "Exception callback survived component completion");
        for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
            require(!process.Threads[i].Exception.Token, "Pending exception survived completion");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Exception handling leaked pages");
        return;
    }
    if (mode >= 106 && mode <= 109) {
        const WitU64 expected = (mode == 106 || mode == 109) ? WIT_TEST_EXIT_CODE
            : mode == 107                                    ? WIT_NATIVE_GS_FAILURE_EXIT
                                                             : WIT_GC_TEST_FAIL_FAST_EXIT;
        if (process.ExitCode != expected) {
            wit_console_write("Guest unwind mode/exit: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
        }
        require(report &&
                report[0] == mode &&
                report[1] == 17592186044416ULL &&
                report[2] ==
                    (mode == 106          ? 35U
                            : mode == 109 ? 2U
                                          : 0x1234U) &&
                process.State == WitUserExited &&
                process.ExitCode == expected &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned,
            "Actual archived guest unwinder failed");
        require(process.ThreadCreates == (mode == 109 ? 2U : 1U) &&
                process.ThreadJoins == (mode == 109 ? 1U : 0U) &&
                process.ThreadReaps == (mode == 109 ? 1U : 0U),
            "Guest unwind thread lifecycle failed");
        require(report[4] &&
                wit_user_space_physical(&process.Space, report[4], 0, 0) &&
                !wit_user_space_physical(&process.Space, report[4], 1, 0),
            "Unwind import slot is not readonly");
        for (WitU32 i = 0; i < WIT_STACK_LEASE_CAPACITY; ++i) {
            require(!process.StackLeases[i].Token, "Guest unwinder leaked stack lease");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Guest unwinder leaked pages");
        return;
    }
    if (mode >= 103 && mode <= 105) {
        const WitU64 expected = mode == 105 ? WIT_GC_TEST_FAIL_FAST_EXIT : WIT_TEST_EXIT_CODE;
        if (process.ExitCode != expected) {
            wit_console_write("Unwind scope mode/exit: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
        }
        require(report &&
                report[0] == mode &&
                report[1] == 8796093022208ULL &&
                report[2] == (mode == 105 ? 0x1234U : 1U) &&
                process.State == WitUserExited &&
                process.ExitCode == expected &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 103 ? 3U : 1U) &&
                process.ThreadJoins == (mode == 103 ? 2U : 0U) &&
                process.ThreadReaps == (mode == 103 ? 2U : 0U),
            "Native unwind scope lifecycle failed");
        for (WitU32 i = 0; i < WIT_STACK_LEASE_CAPACITY; ++i) {
            require(
                !process.StackLeases[i].Token && !process.StackLeases[i].OwnerId && !process.StackLeases[i].ThreadId,
                "Native scope left kernel leases");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Native unwind scope leaked pages");
        return;
    }
    if (mode >= 100 && mode <= 102) {
        if (process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("Stack lease mode/exit: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
        }
        require(report &&
                report[0] == mode &&
                report[1] == 8796093022208ULL &&
                report[2] &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 100 ? 4U : 1U) &&
                process.ThreadJoins == (mode == 100 ? 3U : 0U) &&
                process.ThreadReaps == (mode == 100 ? 3U : 0U),
            "Stack lease lifecycle failed");
        for (WitU32 i = 0; i < WIT_STACK_LEASE_CAPACITY; ++i) {
            require(
                !process.StackLeases[i].Token && !process.StackLeases[i].OwnerId && !process.StackLeases[i].ThreadId,
                "Stack lease survived component teardown");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Stack lease leaked pages");
        return;
    }
    if (mode >= 94 && mode <= 99) {
        const int threaded = mode == 94 || mode == 96;
        if (mode >= 98) {
            require(report &&
                    report[0] == mode &&
                    report[1] == 4398046511104ULL &&
                    report[2] == 0x1234 &&
                    process.State == WitUserExited &&
                    process.ExitCode == WIT_GC_TEST_FAIL_FAST_EXIT,
                "Invalid PAL context operation returned");
            wit_user_destroy(&process);
            require(wit_pages_free_count(pages) == before, "Invalid PAL context leaked pages");
            return;
        }
        if (process.ExitCode != WIT_TEST_EXIT_CODE) {
            const WitU64 errorAddress =
                wit_user_space_physical(&process.Space, process.Threads[0].Tls + WIT_TLS_LAST_ERROR_OFFSET, 0, 0);
            wit_console_write("Context native error: ");
            wit_console_write_u64(errorAddress ? *(const WitU32 *)errorAddress : 0);
            wit_console_write("\n");
            wit_console_write("Context mutation mode/exit: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
        }
        require(report &&
                report[0] == mode &&
                report[1] == 4398046511104ULL &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (threaded ? 2U : 1U) &&
                process.ThreadJoins == (threaded ? 1U : 0U) &&
                process.ThreadReaps == (threaded ? 1U : 0U),
            "Context mutation contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Context mutation leaked pages");
        return;
    }
    if (mode >= 91 && mode <= 93) {
        if (mode != 93 && process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("Suspend mode/exit: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
        }
        require(report &&
                report[0] == mode &&
                report[1] == 2199023255552ULL &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned,
            "Suspension resource contract failed");
        if (mode == 93) {
            require(process.State == WitUserBudgetExpired && report[2] == 1 && process.IdleHalts && process.IdleTicks,
                "All-suspended idle escaped its budget");
        } else {
            require(process.State == WitUserExited &&
                    process.ExitCode == WIT_TEST_EXIT_CODE &&
                    process.ThreadCreates == (mode == 91 ? 4U : 1U) &&
                    process.ThreadJoins == (mode == 91 ? 3U : 0U) &&
                    process.ThreadReaps == (mode == 91 ? 3U : 0U),
                "Suspension lifecycle failed");
        }
        for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
            require(!process.Threads[i].SuspendCount, "Suspend count survived teardown");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Suspension leaked pages");
        return;
    }
    if (mode == 89 || mode == 90) {
        if (process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("Context capture mode/exit: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
        }
        require(report &&
                report[0] == mode &&
                report[1] == 1099511627776ULL &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 89 ? 4U : 1U) &&
                process.ThreadJoins == (mode == 89 ? 3U : 0U) &&
                process.ThreadReaps == (mode == 89 ? 3U : 0U),
            "Register snapshot contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Register snapshots leaked pages");
        return;
    }
    if (mode >= 86 && mode <= 88) {
        require(report &&
                report[0] == mode &&
                report[1] == 549755813888ULL &&
                process.State == WitUserExited &&
                process.ExitCode == (mode == 88 ? WIT_GC_TEST_FAIL_FAST_EXIT : WIT_TEST_EXIT_CODE) &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 86 ? 4U : 1U) &&
                process.ThreadJoins == (mode == 86 ? 3U : 0U) &&
                process.ThreadReaps == (mode == 86 ? 3U : 0U),
            "Native context storage/profile failed");
        if (mode == 88) {
            require(report[2] == 0x1234, "Invalid context output returned");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Context storage teardown leaked pages");
        return;
    }
    if (mode >= 82 && mode <= 85) {
        require(report &&
                report[0] == mode &&
                report[1] == 274877906944ULL &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes,
            "GC policy fixture state invalid");
        if (mode == 85) {
            const WitU64 instruction = wit_user_space_physical(&process.Space, process.FaultState.Rip - 1, 0, 1);
            require(process.State == WitUserFaulted &&
                    process.FaultVector == 3 &&
                    !process.FaultError &&
                    process.FaultState.Cs == WIT_USER_CS &&
                    process.FaultState.Rip == report[2] &&
                    instruction &&
                    *(const WitU8 *)instruction == 0xCC,
                "GC breakpoint was not a user INT3 trap");
        } else if (mode == 84) {
            const WitU64 data = wit_user_space_physical(&process.Space, report[2], 0, 0);
            require(process.State == WitUserExited &&
                    process.ExitCode == WIT_NATIVE_GC_WRITE_WATCH_EXIT &&
                    data &&
                    *(const WitU64 *)data == 0xFEDCBA9876543210ULL,
                "Unsupported write-watch reset returned or changed payload");
        } else {
            require(process.State == WitUserExited &&
                    process.ExitCode == WIT_TEST_EXIT_CODE &&
                    process.Space.OwnedCount == owned &&
                    process.ThreadCreates == (mode == 82 ? 4U : 1U) &&
                    process.ThreadJoins == (mode == 82 ? 3U : 0U) &&
                    process.ThreadReaps == (mode == 82 ? 3U : 0U),
                "GC optional memory policy failed");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "GC policy teardown leaked pages");
        return;
    }
    if (mode >= 78 && mode <= 81) {
        if (process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("COM mode/exit: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
        }
        require(report &&
                report[0] == mode &&
                report[1] == 137438953472ULL &&
                report[2] ==
                    (mode == 78          ? 4U
                            : mode == 79 ? 5U
                            : mode == 81 ? 1U
                                         : 0U) &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode <= 79 ? 4U : 1U) &&
                process.ThreadJoins == (mode <= 79 ? 3U : 0U) &&
                process.ThreadReaps == (mode <= 79 ? 3U : 0U),
            "Native MTA lifecycle failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "MTA lifecycle leaked pages");
        return;
    }
    if (mode == 76 || mode == 77) {
        require(report &&
                report[0] == mode &&
                report[1] == 68719476736ULL &&
                report[2] == 27 &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 76 ? 4U : 1U) &&
                process.ThreadJoins == (mode == 76 ? 3U : 0U) &&
                process.ThreadReaps == (mode == 76 ? 3U : 0U),
            "Native diagnostic services contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Native diagnostics leaked pages");
        return;
    }
    if (mode == 74 || mode == 75) {
        require(report &&
                report[0] == mode &&
                report[1] == 34359738368ULL &&
                report[2] == (mode == 74 ? 4U : 0U) &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 74 ? 5U : 1U) &&
                process.ThreadJoins == (mode == 74 ? 4U : 0U) &&
                process.ThreadReaps == (mode == 74 ? 4U : 0U),
            "Native thread name contract failed");
        for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
            require(!process.Threads[i].NameLength, "Thread name length survived exit");
            for (WitU32 c = 0; c < WIT_THREAD_NAME_CAPACITY; ++c) {
                require(!process.Threads[i].Name[c], "Thread name bytes survived exit");
            }
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Thread names leaked pages");
        return;
    }
    if (mode >= 71 && mode <= 73) {
        require(report &&
                report[0] == mode &&
                report[1] == 17179869184ULL &&
                report[2] == 0x4D4F44554C45ULL &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                !process.Handles.Count &&
                !process.Events.Count &&
                !process.Writes &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 71 ? 4U : 1U) &&
                process.ThreadJoins == (mode == 71 ? 3U : 0U) &&
                process.ThreadReaps == (mode == 71 ? 3U : 0U),
            "Native module/name contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Module/name teardown leaked pages");
        return;
    }
    if (mode >= 68 && mode <= 70) {
        require(report &&
                report[0] == mode &&
                report[1] == 8589934592ULL &&
                process.State == WitUserExited &&
                process.ExitCode == (mode == 70 ? WIT_GC_TEST_FAIL_FAST_EXIT : WIT_TEST_EXIT_CODE) &&
                !process.Handles.Count &&
                !process.Events.Count,
            "Native console/processor execution failed");
        if (mode == 70) {
            const WitU64 prefix = wit_user_space_physical(&process.Space, report[2], 0, 0);
            require(
                prefix && ((const WitU8 *)prefix)[0] == 0xa5 && ((const WitU8 *)prefix)[1] == 0xa5 && !process.Writes,
                "Void processor query partially wrote its destination");
        } else {
            require(report[3] == 0x55544638 &&
                    process.Writes == report[2] &&
                    process.Space.OwnedCount == owned &&
                    process.ThreadCreates == (mode == 68 ? 4U : 1U) &&
                    process.ThreadJoins == (mode == 68 ? 3U : 0U) &&
                    process.ThreadReaps == (mode == 68 ? 3U : 0U),
                "Console count/resources contract failed");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Console/processor teardown leaked pages");
        return;
    }
    if (mode == 66 || mode == 67) {
        require(report &&
                report[0] == mode &&
                report[1] == 4294967296ULL &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 66 ? 10U : 1U) &&
                process.ThreadJoins == (mode == 66 ? 9U : 0U) &&
                process.ThreadReaps == (mode == 66 ? 9U : 0U) &&
                !process.Handles.Count &&
                !process.Events.Count,
            "Native APC/object wait contract failed");
        for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
            require(!process.Threads[i].ApcCount && !process.Threads[i].WaitCount && !process.Threads[i].WaitAlertable,
                "APC/wait state leaked");
        }
        if (mode == 66) {
            require(process.EventParks >= 8 && process.WaitTimeouts && process.WaitCloses,
                "Object wait paths were not exercised");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Object wait teardown leaked pages");
        return;
    }
    if (mode == 64 || mode == 65) {
        require(report &&
                report[0] == mode &&
                report[1] == 2147483648ULL &&
                report[2] == process.Threads[0].NativeId &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 64 ? 4U : 1U) &&
                process.ThreadJoins == (mode == 64 ? 3U : 0U) &&
                process.ThreadReaps == (mode == 64 ? 3U : 0U) &&
                !process.Handles.Count &&
                !process.Events.Count,
            "Native thread reference contract failed");
        for (WitU32 i = 0; i < WIT_HANDLE_CAPACITY; ++i) {
            require(!process.ThreadReferences[i].Handle, "Thread reference survived process exit");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Thread reference teardown leaked pages");
        return;
    }
    if (mode == 62 || mode == 63) {
        require(report &&
                report[0] == mode &&
                report[1] == 1073741824 &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 62 ? 4U : 1U) &&
                process.ThreadJoins == (mode == 62 ? 3U : 0U) &&
                process.ThreadReaps == (mode == 62 ? 3U : 0U) &&
                (process.Threads[0].CompilerTls != 0) == (mode == 62) &&
                !process.Writes &&
                !process.Handles.Count &&
                !process.Events.Count,
            "Native CloseHandle/Sleep contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Native services teardown leaked pages");
        return;
    }
    if (mode == 60 || mode == 61) {
        require(report &&
                report[0] == mode &&
                report[1] == 536870912 &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 60 ? 4U : 1U) &&
                process.ThreadJoins == (mode == 60 ? 3U : 0U) &&
                process.ThreadReaps == (mode == 60 ? 3U : 0U) &&
                (process.Threads[0].CompilerTls != 0) == (mode == 60) &&
                !process.Writes &&
                !process.Handles.Count &&
                !process.Events.Count,
            "Native VirtualAlloc/VirtualFree contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Native memory teardown leaked pages");
        return;
    }
    if (mode == 58 || mode == 59) {
        require(report &&
                report[0] == mode &&
                report[1] == 268435456 &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE,
            "Real entropy/BCrypt/system-cookie execution failed");
        const WitU64 seeds = process.RandomRequests - report[2];
        require(seeds >= 1 &&
                seeds <= 4 &&
                process.RandomBytes == report[3] + 8 * seeds &&
                wit_random_generation() - generation == report[4] + seeds &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 58 ? 4U : 1U) &&
                process.ThreadJoins == (mode == 58 ? 3U : 0U) &&
                process.ThreadReaps == (mode == 58 ? 3U : 0U) &&
                (process.Threads[0].CompilerTls != 0) == (mode == 58) &&
                !process.Writes &&
                !process.Handles.Count &&
                !process.Events.Count,
            "Random rejection advanced state, lost accounting or leaked resources");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Random teardown leaked pages");
        return;
    }
    if (mode >= 45) {
        const int success = mode == 45 || mode == 54;
        require(report &&
                report[0] == mode &&
                report[1] == 134217728 &&
                process.State == WitUserExited &&
                process.ExitCode == (success ? WIT_TEST_EXIT_CODE : WIT_NATIVE_GS_FAILURE_EXIT) &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 45 ? 4U : 1U) &&
                process.ThreadJoins == (mode == 45 ? 3U : 0U) &&
                process.ThreadReaps == (mode == 45 ? 3U : 0U) &&
                (process.Threads[0].CompilerTls != 0) == (mode != 54) &&
                !process.Writes &&
                !process.Handles.Count &&
                !process.Events.Count,
            "GS cookie/ABI/handler validation contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "GS teardown leaked pages");
        return;
    }
    if (mode == 43 || mode == 44) {
        require(report &&
                report[0] == mode &&
                report[1] == 67108864 &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 43 ? 4U : 1U) &&
                process.ThreadJoins == (mode == 43 ? 3U : 0U) &&
                process.ThreadReaps == (mode == 43 ? 3U : 0U) &&
                !process.Writes &&
                !process.Handles.Count &&
                !process.Events.Count,
            "Native secure formatting contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Native formatting teardown leaked pages");
        return;
    }
    if (mode == 41 || mode == 42) {
        require(report &&
                report[0] == mode &&
                report[1] == 33554432 &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 41 ? 4U : 1U) &&
                process.ThreadJoins == (mode == 41 ? 3U : 0U) &&
                process.ThreadReaps == (mode == 41 ? 3U : 0U) &&
                !process.Writes &&
                !process.Handles.Count &&
                !process.Events.Count,
            "Native libm log/errno contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Native libm teardown leaked pages");
        return;
    }
    if (mode == 39 || mode == 40) {
        require(report &&
                report[0] == mode &&
                report[1] == 16777216 &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 39 ? 4U : 1U) &&
                process.ThreadJoins == (mode == 39 ? 3U : 0U) &&
                process.ThreadReaps == (mode == 39 ? 3U : 0U) &&
                !process.Writes &&
                !process.Handles.Count &&
                !process.Events.Count,
            "GC affinity parser contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "GC affinity parser teardown leaked pages");
        return;
    }
    if (mode >= 29) {
        const WitU64 expected = mode <= 30
            ? WIT_TEST_EXIT_CODE
            : ((mode == 31 || mode == 33)
                      ? WIT_NATIVE_PURECALL_EXIT
                      : ((mode == 32 || mode == 34) ? WIT_NATIVE_RANGECHECK_EXIT : WIT_GC_TEST_FAIL_FAST_EXIT));
        require(report &&
                report[0] == mode &&
                report[1] == 8388608 &&
                report[2] == (mode == 29 ? 3U : 0U) &&
                process.State == WitUserExited &&
                process.ExitCode == expected &&
                process.Space.OwnedCount == owned &&
                process.Writes == (mode <= 30 ? 3U : (mode == 31 || mode == 32 ? 1U : 0U)) &&
                process.ThreadCreates == 1 &&
                !process.ThreadJoins &&
                !process.ThreadReaps &&
                (process.Threads[0].CompilerTls != 0) == (mode != 30) &&
                !process.Handles.Count &&
                !process.Events.Count,
            "Fatal diagnostic output, raw exit or cleanup contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Fatal diagnostic teardown leaked pages");
        return;
    }
    if (mode == 27 || mode == 28) {
        require(report &&
                report[0] == mode &&
                report[1] == 4194304 &&
                process.State == WitUserExited &&
                process.ExitCode == WIT_TEST_EXIT_CODE &&
                process.Space.OwnedCount == owned &&
                process.ThreadCreates == (mode == 27 ? 4U : 1U) &&
                process.ThreadJoins == (mode == 27 ? 3U : 0U) &&
                process.ThreadReaps == (mode == 27 ? 3U : 0U) &&
                (process.Threads[0].CompilerTls != 0) == (mode == 27) &&
                !process.Handles.Count &&
                !process.Events.Count,
            "Native clock binding/atomic copy contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Native clock teardown leaked pages");
        return;
    }
    if (mode >= 24) {
        require(report &&
                report[0] == mode &&
                report[1] == 2097152 &&
                process.Space.OwnedCount == owned &&
                !process.Handles.Count &&
                !process.Events.Count,
            "CPU feature fixture lost state/resources");
        if (mode == 26) {
            require(process.State == WitUserFaulted &&
                    process.FaultVector == 6 &&
                    !process.FaultError &&
                    process.FaultState.Cs == WIT_USER_CS &&
                    process.FaultState.Ss == WIT_USER_SS &&
                    process.FaultState.Rip == base + WIT_CPU_AVX_RVA,
                "AVX executed outside the kernel FXSAVE profile");
        } else {
            require(process.State == WitUserExited &&
                    process.ExitCode == WIT_TEST_EXIT_CODE &&
                    process.ThreadCreates == (mode == 24 ? 4U : 1U) &&
                    process.ThreadJoins == (mode == 24 ? 3U : 0U) &&
                    process.ThreadReaps == (mode == 24 ? 3U : 0U) &&
                    (process.Threads[0].CompilerTls != 0) == (mode == 24),
                "Minipal CPU discovery/instruction/thread test failed");
            wit_console_write("[MINIPAL-CPU] features=");
            wit_console_write_u64(report[2]);
            wit_console_write("; avx-hardware=");
            wit_console_write_u64(report[3]);
            wit_console_write("\n");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "CPU feature teardown leaked pages");
        return;
    }
    if (mode == 21 || mode == 22) {
        require(report &&
                report[0] == mode &&
                report[1] == 1048576 &&
                process.State == WitUserFaulted &&
                process.FaultVector == 14 &&
                process.FaultError == 4 &&
                process.FaultState.Cs == WIT_USER_CS &&
                process.FaultState.Ss == WIT_USER_SS &&
                process.FaultAddress >= WIT_USER_STACK_BOTTOM - 4096 &&
                process.FaultAddress < WIT_USER_STACK_BOTTOM &&
                process.FaultState.Rip >= base + WIT_STACK_PROBE_BEGIN &&
                process.FaultState.Rip < base + WIT_STACK_PROBE_END &&
                process.Space.OwnedCount == owned &&
                !process.Handles.Count &&
                !process.Events.Count,
            "Compiler stack probe did not stop at its first guard page");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Stack probe fault teardown leaked pages");
        return;
    }
    if (mode == 11) {
        require(process.State == WitUserExited &&
                process.ExitCode == WIT_GC_TEST_FAIL_FAST_EXIT &&
                report &&
                report[0] == 11 &&
                report[1] == 32768 &&
                !process.Threads[0].CompilerTls &&
                process.Space.OwnedCount == owned &&
                !process.Handles.Count &&
                !process.Events.Count,
            "ThreadStore metadata accessed unavailable compiler TLS");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "ThreadStore rejected-TLS teardown leaked pages");
        return;
    }
    if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
        wit_console_write("Runtime config state/code: ");
        wit_console_write_u64(process.State);
        wit_console_write("/");
        wit_console_write_u64(process.ExitCode);
        wit_console_write("\n");
        wit_panic("Upstream configuration contract failed");
    }
    if (mode == 20 || mode == 23) {
        require(report &&
                report[0] == mode &&
                report[1] == 1048576 &&
                process.ThreadCreates == (mode == 20 ? 7U : 1U) &&
                process.ThreadJoins == (mode == 20 ? 6U : 0U) &&
                process.ThreadReaps == (mode == 20 ? 6U : 0U) &&
                (process.Threads[0].CompilerTls != 0) == (mode == 20) &&
                process.Space.OwnedCount == owned &&
                !process.Handles.Count &&
                !process.Events.Count,
            "Compiler stack probe ABI/frame/resource contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Stack probe teardown leaked pages");
        return;
    }
    if (mode == 18 || mode == 19) {
        require(report &&
                report[0] == mode &&
                report[1] == 524288 &&
                process.ThreadCreates == (mode == 18 ? 1U : 7U) &&
                process.ThreadJoins == (mode == 18 ? 0U : 6U) &&
                process.ThreadReaps == (mode == 18 ? 0U : 6U) &&
                (process.Threads[0].CompilerTls != 0) == (mode == 19) &&
                process.Space.OwnedCount == owned &&
                !process.Handles.Count &&
                !process.Events.Count,
            "Native CRT range/parse/resource contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Native CRT teardown leaked pages");
        return;
    }
    if (mode >= 15 && mode <= 17) {
        require(report &&
                report[0] == mode &&
                report[1] == 262144 &&
                process.ThreadCreates == (mode == 16 ? 1U : 7U) &&
                process.ThreadJoins == (mode == 16 ? 0U : 6U) &&
                process.ThreadReaps == (mode == 16 ? 0U : 6U) &&
                (process.Threads[0].CompilerTls != 0) == (mode != 16) &&
                process.Space.OwnedCount == owned &&
                !process.Handles.Count &&
                !process.Events.Count,
            "Minipal time/TLS or native resource contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Minipal time teardown leaked pages");
        return;
    }
    if (mode == 12 || mode == 13) {
        require(report &&
                report[0] == mode &&
                report[1] == 65536 &&
                process.ProcessWriteBarriers == (mode == 12 ? 50U : 2U) &&
                process.ThreadCreates == (mode == 12 ? 4U : 1U) &&
                process.ThreadJoins == (mode == 12 ? 3U : 0U) &&
                process.ThreadReaps == (mode == 12 ? 3U : 0U) &&
                (process.Threads[0].CompilerTls != 0) == (mode == 12) &&
                process.Space.OwnedCount == owned &&
                !process.Handles.Count &&
                !process.Events.Count,
            "GC/PAL barrier count, thread lifecycle or no-allocation contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Process barrier teardown leaked pages");
        return;
    }
    if (mode == 9 || mode == 10 || mode == 14) {
        require(report && report[0] == mode && report[1] == (mode == 9 ? 8192U : (mode == 14 ? 155648U : 24576U)),
            "Runtime startup report mismatch");
        require(process.ThreadCreates == (mode == 9 ? 1U : (mode == 14 ? 17U : 4U)) &&
                process.ThreadJoins == (mode == 9 ? 0U : (mode == 14 ? 16U : 3U)) &&
                process.ThreadReaps == (mode == 9 ? 0U : (mode == 14 ? 16U : 3U)),
            "Runtime startup thread lifecycle counts mismatch");
        require(process.Space.OwnedCount > owned, "Interface dispatch initialization missed real allocations");
        WitU32 reservations = 0;
        for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
            if (process.Space.Reservations[i].Size) {
                ++reservations;
            }
        }
        require(reservations == 2 && !process.Handles.Count && !process.Events.Count,
            "Interface dispatch retained unexpected resources");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Interface dispatch process teardown leaked pages");
        return;
    }
    if (mode == 3 || mode == 7 || mode == 8) {
        require(report &&
                report[0] == mode &&
                report[1] ==
                    ((mode == 8 || mode == 11 || mode == 13 || mode == 16 || mode == 18 || mode == 23) ? 2048 : 1089) &&
                process.ThreadCreates == 1 &&
                !process.ThreadJoins &&
                !process.ThreadReaps &&
                (process.Threads[0].CompilerTls != 0) == (mode != 8),
            "PAL initialization rejection missed its intended boundary");
    } else {
        require(report &&
                report[0] == mode &&
                report[1] == 5119 &&
                process.ThreadCreates == 7 &&
                process.ThreadJoins == 6 &&
                process.ThreadReaps == 6 &&
                process.ThreadSwitches,
            "Runtime configuration missed required checks or thread reuse");
    }
    require(process.Space.OwnedCount == owned && !process.Handles.Count && !process.Events.Count,
        "Runtime configuration leaked resources");
    for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
        require(!process.Space.Reservations[i].Size, "Runtime configuration leaked a reservation");
    }
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Runtime configuration teardown leaked physical memory");
}

void wit_user_runtime_config_self_test(WitPageAllocator *pages)
{
    run(pages, 0, WIT_USER_IMAGE_BASE);
    run(pages, 0, WIT_USER_IMAGE_ALTERNATE);
    run(pages, 1, WIT_USER_IMAGE_BASE);
    for (WitU64 mode = 2; mode <= 8; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
    }
    wit_console_write("[TEST-PASS] User.RuntimeConfigCrt\n[TEST-PASS] User.RhConfigPrecedence\n");
    wit_console_write("[TEST-PASS] User.RhConfigStrings\n[TEST-PASS] User.GcConfigValues\n");
    wit_console_write("[TEST-PASS] User.GcConfigRefresh\n[TEST-PASS] User.RuntimeConfigThreads\n");
    wit_console_write("[TEST-PASS] User.PalInitPrerequisites\n[TEST-PASS] User.PalInitPolicy\n");
    wit_console_write("[TEST-PASS] User.PalInitLifecycle\n");
    wit_console_write("[TEST-PASS] User.RuntimeAllocHeap\n");
    run(pages, 9, WIT_USER_IMAGE_BASE);
    run(pages, 9, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.InterfaceDispatchInit\n");
    run(pages, 10, WIT_USER_IMAGE_BASE);
    run(pages, 10, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.RuntimeInstanceStartup\n");
    run(pages, 14, WIT_USER_IMAGE_BASE);
    run(pages, 14, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.RuntimeThreadRecord\n");
    run(pages, 11, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.ThreadStoreTlsPrerequisite\n");
    run(pages, 12, WIT_USER_IMAGE_BASE);
    run(pages, 12, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.GcProcessWriteBarrier\n");
    run(pages, 13, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.ProcessBarrierWithoutTls\n");
    run(pages, 15, WIT_USER_IMAGE_BASE);
    run(pages, 15, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.MinipalTime\n");
    run(pages, 16, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.MinipalTimeWithoutTls\n");
    run(pages, 17, WIT_USER_IMAGE_BASE);
    run(pages, 17, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.RuntimeRandomTls\n");
    run(pages, 18, WIT_USER_IMAGE_BASE);
    run(pages, 18, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.CrtMemoryAndStrings\n");
    run(pages, 19, WIT_USER_IMAGE_BASE);
    run(pages, 19, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.CrtUnsignedLong\n");
    run(pages, 20, WIT_USER_IMAGE_BASE);
    run(pages, 20, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.CompilerStackProbe\n");
    run(pages, 23, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.CompilerStackProbeWithoutTls\n");
    run(pages, 21, WIT_USER_IMAGE_BASE);
    run(pages, 22, WIT_USER_IMAGE_ALTERNATE);
    run(pages, 20, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.CompilerStackProbeGuard\n");
    run(pages, 24, WIT_USER_IMAGE_BASE);
    run(pages, 24, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.MinipalCpuFeatures\n");
    run(pages, 25, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.MinipalCpuWithoutTls\n");
    run(pages, 26, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.AvxDisabled\n");
    run(pages, 27, WIT_USER_IMAGE_BASE);
    run(pages, 27, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeClockBindings\n");
    run(pages, 28, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeClockAtomicCopy\n");
    run(pages, 29, WIT_USER_IMAGE_BASE);
    run(pages, 29, WIT_USER_IMAGE_ALTERNATE);
    run(pages, 30, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.FatalDiagnosticOutput\n");
    for (WitU64 mode = 31; mode <= 34; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
    }
    wit_console_write("[TEST-PASS] User.FatalCrtExit\n");
    for (WitU64 mode = 35; mode <= 38; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_ALTERNATE);
    }
    run(pages, 29, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.FatalDiagnosticRejection\n");
    run(pages, 39, WIT_USER_IMAGE_BASE);
    run(pages, 39, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.GcAffinityParsing\n");
    run(pages, 40, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcAffinityBeforeTlsConstructors\n");
    run(pages, 41, WIT_USER_IMAGE_BASE);
    run(pages, 41, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeMathLog\n");
    run(pages, 42, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeMathBeforeTlsConstructors\n");
    run(pages, 43, WIT_USER_IMAGE_BASE);
    run(pages, 43, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeSecureFormatting\n");
    run(pages, 44, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeFormattingBeforeTlsConstructors\n");
    run(pages, 45, WIT_USER_IMAGE_BASE);
    run(pages, 45, WIT_USER_IMAGE_ALTERNATE);
    run(pages, 54, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.SecurityCookieAbi\n");
    for (WitU64 mode = 46; mode <= 53; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
    }
    for (WitU64 mode = 55; mode <= 57; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_ALTERNATE);
    }
    run(pages, 45, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.SecurityCookieFailClosed\n");
    run(pages, 58, WIT_USER_IMAGE_BASE);
    run(pages, 58, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.CryptographicRandom\n");
    run(pages, 59, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.RandomAtomicCopyAndEarlyCookie\n");
    run(pages, 60, WIT_USER_IMAGE_BASE);
    run(pages, 60, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeVirtualMemory\n");
    run(pages, 61, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeMemoryWithoutCompilerTls\n");
    run(pages, 62, WIT_USER_IMAGE_BASE);
    run(pages, 62, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeCloseAndSleep\n");
    run(pages, 63, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeServicesWithoutCompilerTls\n");
    run(pages, 64, WIT_USER_IMAGE_BASE);
    run(pages, 64, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeThreadReferences\n");
    run(pages, 65, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.ThreadReferencesWithoutCompilerTls\n");
    run(pages, 66, WIT_USER_IMAGE_BASE);
    run(pages, 66, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.AlertableObjectWaits\n");
    run(pages, 67, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.ApcWithoutCompilerTls\n");
    run(pages, 68, WIT_USER_IMAGE_BASE);
    run(pages, 68, WIT_USER_IMAGE_ALTERNATE);
    run(pages, 69, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeConsoleBindings\n");
    wit_console_write("[TEST-PASS] User.NativeUtfConversions\n");
    run(pages, 70, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeProcessorAtomicCopy\n");
    run(pages, 71, WIT_USER_IMAGE_BASE);
    run(pages, 71, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeModuleNames\n");
    run(pages, 72, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeModuleNamesWithoutTls\n");
    run(pages, 73, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.AnonymousModuleIdentity\n");
    run(pages, 74, WIT_USER_IMAGE_BASE);
    run(pages, 74, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeThreadNames\n");
    run(pages, 75, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeThreadNamesWithoutTls\n");
    run(pages, 76, WIT_USER_IMAGE_BASE);
    run(pages, 76, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeDiagnosticServices\n");
    run(pages, 77, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeDiagnosticsWithoutTls\n");
    run(pages, 78, WIT_USER_IMAGE_BASE);
    run(pages, 78, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeMtaLifecycle\n");
    run(pages, 79, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeMtaProcessCleanup\n");
    run(pages, 80, WIT_USER_IMAGE_BASE);
    run(pages, 81, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeMtaPrerequisites\n");
    run(pages, 82, WIT_USER_IMAGE_BASE);
    run(pages, 82, WIT_USER_IMAGE_ALTERNATE);
    run(pages, 83, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcOptionalMemoryPolicy\n");
    run(pages, 84, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcWriteWatchFailClosed\n");
    run(pages, 85, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcArchitecturalBreakpoint\n");
    run(pages, 86, WIT_USER_IMAGE_BASE);
    run(pages, 86, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeContextStorage\n");
    run(pages, 87, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeContextProfileWithoutTls\n");
    run(pages, 88, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeContextInvalidOutput\n");
    run(pages, 89, WIT_USER_IMAGE_BASE);
    run(pages, 89, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.RegisterContextSnapshots\n");
    run(pages, 90, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.RegisterSnapshotsWithoutTls\n");
    run(pages, 91, WIT_USER_IMAGE_BASE);
    run(pages, 91, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.ThreadSuspension\n");
    run(pages, 92, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.SuspensionWithoutTls\n");
    run(pages, 93, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.SuspendedIdleBudget\n");
    run(pages, 94, WIT_USER_IMAGE_BASE);
    run(pages, 94, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.ContextSetAndRestore\n");
    run(pages, 95, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.ContextRestoreWithoutTls\n");
    run(pages, 96, WIT_USER_IMAGE_BASE);
    run(pages, 96, WIT_USER_IMAGE_ALTERNATE);
    run(pages, 97, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.PalContextMapping\n");
    run(pages, 98, WIT_USER_IMAGE_BASE);
    run(pages, 99, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.PalContextFailClosed\n");
    run(pages, 100, WIT_USER_IMAGE_BASE);
    run(pages, 100, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.StackLeaseLifetime\n");
    run(pages, 101, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.StackLeaseWithoutTls\n");
    run(pages, 102, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.StackLeaseRawExit\n");
    run(pages, 103, WIT_USER_IMAGE_BASE);
    run(pages, 103, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeUnwindScope\n");
    run(pages, 104, WIT_USER_IMAGE_BASE);
    run(pages, 105, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeUnwindScopeRejection\n");
    wit_user_runtime_unwind_metadata_self_test(pages);
    run(pages, 106, WIT_USER_IMAGE_BASE);
    run(pages, 106, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.ArchivedNativeUnwinder\n");
    run(pages, 107, WIT_USER_IMAGE_BASE);
    run(pages, 108, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeUnwindFailureAndGs\n");
    run(pages, 109, WIT_USER_IMAGE_BASE);
    run(pages, 109, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeForeignUnwind\n");
    run(pages, 110, WIT_USER_IMAGE_BASE);
    run(pages, 110, WIT_USER_IMAGE_ALTERNATE);
    run(pages, 111, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.ExceptionDeliveryAndContinue\n");
    for (WitU64 mode = 112; mode <= 116; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
    }
    wit_console_write("[TEST-PASS] User.ExceptionFailureContainment\n");
    run(pages, 117, WIT_USER_IMAGE_BASE);
    run(pages, 117, WIT_USER_IMAGE_ALTERNATE);
    run(pages, 121, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeVectoredHandlers\n");
    for (WitU64 mode = 118; mode <= 122; ++mode) {
        if (mode != 121) {
            run(pages, mode, WIT_USER_IMAGE_BASE);
        }
    }
    wit_console_write("[TEST-PASS] User.NativeVectoredFailure\n");
    run(pages, 123, WIT_USER_IMAGE_BASE);
    run(pages, 124, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeExceptionFrameSearch\n");
    run(pages, 125, WIT_USER_IMAGE_BASE);
    run(pages, 125, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeRaiseException\n");
    for (WitU64 mode = 126; mode <= 128; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
    }
    wit_console_write("[TEST-PASS] User.NativeNoncontinuableException\n");
    for (WitU64 mode = 129; mode <= 136; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
    }
    run(pages, 130, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeRaiseFailFastException\n");
    for (WitU64 mode = 137; mode <= 142; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
        run(pages, mode, WIT_USER_IMAGE_ALTERNATE);
    }
    wit_console_write("[TEST-PASS] User.CompilerSehTargetUnwind\n");
    for (WitU64 mode = 143; mode <= 146; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
        run(pages, mode, WIT_USER_IMAGE_ALTERNATE);
    }
    wit_console_write("[TEST-PASS] User.CompilerLocalUnwind\n");
    for (WitU64 mode = 147; mode <= 148; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
        run(pages, mode, WIT_USER_IMAGE_ALTERNATE);
    }
    wit_console_write("[TEST-PASS] User.CompilerNestedSehCallbacks\n");
    run(pages, 149, WIT_USER_IMAGE_BASE);
    run(pages, 149, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.ExceptionScopeTransfer\n");
    for (WitU64 mode = 150; mode <= 154; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
        run(pages, mode, WIT_USER_IMAGE_ALTERNATE);
    }
    wit_console_write("[TEST-PASS] User.CompilerCollidedUnwind\n");
    for (WitU64 mode = 155; mode <= 159; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
        run(pages, mode, WIT_USER_IMAGE_ALTERNATE);
    }
    wit_console_write("[TEST-PASS] User.CompilerGsSeh\n");
    for (WitU64 mode = 160; mode <= 162; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
    }
    wit_console_write("[TEST-PASS] User.CompilerGsSehValidation\n");
    for (WitU64 mode = 163; mode <= 165; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
        run(pages, mode, WIT_USER_IMAGE_ALTERNATE);
    }
    wit_console_write("[TEST-PASS] User.CompilerGsSehAligned\n");
    run(pages, 166, WIT_USER_IMAGE_BASE);
    run(pages, 166, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeGeneralProtection\n");
    run(pages, 167, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeGeneralProtectionUnsupported\n");
    run(pages, 168, WIT_USER_IMAGE_BASE);
    run(pages, 168, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeThreadCreationAndRollback\n");
}
#endif
