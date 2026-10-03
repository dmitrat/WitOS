#include "x64.h"
#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "process_exit_image.h"
#include "self_test.h"

static WitUserProcess process;

static void require(int ok, const char *message)
{
    if (!ok) {
        wit_panic(message);
    }
}

static void run(WitPageAllocator *pages, WitU64 mode, WitU64 base)
{
    const WitU64 before = wit_pages_free_count(pages);
    require(
        wit_user_create_pe(&process, pages, 0, wit_process_exit_image, sizeof(wit_process_exit_image), base) == WitPeOk,
        "Process exit fixture load failed");
    const WitU32 owned = process.Space.OwnedCount;
    WitUserTestConfig *config = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    config->Mode = mode;
    wit_user_run(&process);
    const WitU64 *report = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
    require(report && report[0] == mode, "Process exit fixture missed entry");
    if (mode == 4 || mode == 5 || mode == 9 || (mode >= 12 && mode <= 14)) {
        require(process.State == WitUserExited &&
                process.ExitCode == WIT_GC_TEST_FAIL_FAST_EXIT &&
                report[1] == (mode == 5 ? 64U : 1U) &&
                !report[4],
            "Process exit recursion was not bounded");
    } else if (mode == 6) {
        require(wit_test_faulted(&process) &&
                process.FaultVector == 14 &&
                process.FaultError == 6 &&
                process.FaultAddress == 0 &&
                process.FaultState.Cs == WIT_USER_CS &&
                process.FaultState.Ss == WIT_USER_SS &&
                report[1] == 1 &&
                !report[4],
            "Exit callback fault escaped containment");
    } else {
        if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("Process exit mode/state/code: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
            wit_panic("Native process exit lifecycle failed");
        }
        if (mode == 10 || mode == 11) {
            require(report[6] == 12 &&
                    process.ThreadCreates == 13 &&
                    process.ThreadJoins == (mode == 10 ? 12U : 0U) &&
                    process.ThreadReaps == 12,
                "Thread notifications lost cleanup or reused live resources");
        }
        if (mode == 15) {
            require(!report[6] && process.ThreadJoins == 1 && process.ThreadReaps == 1,
                "Raw thread exit unexpectedly invoked notification");
        }
        if (mode != 7) {
            require(report[7] == 1, "Process thread notification missing");
        }
        if (mode <= 1) {
            require(report[2] == 3241 && report[5] == 1, "Process/TLS cleanup order failed");
        }
        if (mode == 2 || mode == 3) {
            require(report[3] == (mode == 2 ? 32U : 24U), "Exit registrations lost or duplicated");
        }
        if (mode == 3) {
            require(process.ThreadCreates == 4 && process.ThreadJoins == 3 && process.ThreadReaps == 3,
                "Process callbacks missed worker exit");
        }
        if (mode == 8) {
            require(report[2] == 1 && process.ThreadJoins == 1 && process.ThreadReaps == 1,
                "Foreign registration was not rejected outside the callback gate");
        }
        if (mode == 7) {
            require(report[1] == 1 && !report[3] && !report[4] && !report[5] && !report[7],
                "Raw exit unexpectedly invoked cleanup");
        } else if (mode != 1) {
            require(report[4] == 1, "Process shutdown did not return");
        }
    }
    require(!process.Handles.Count && !process.Events.Count, "Process exit handles leaked");
    if (mode != 9) {
        require(process.Space.OwnedCount == owned, "Process exit owned resources leaked");
    }
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Process exit teardown leaked physical pages");
}

void wit_user_process_exit_self_test(WitPageAllocator *pages)
{
    run(pages, 0, WIT_USER_IMAGE_BASE);
    run(pages, 0, WIT_USER_IMAGE_ALTERNATE);
    run(pages, 1, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeProcessExitOrder\n");
    run(pages, 2, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeProcessExitCapacity\n");
    run(pages, 3, WIT_USER_IMAGE_BASE);
    run(pages, 8, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeProcessExitThreads\n");
    run(pages, 4, WIT_USER_IMAGE_BASE);
    run(pages, 5, WIT_USER_IMAGE_BASE);
    run(pages, 9, WIT_USER_IMAGE_BASE);
    run(pages, 0, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeProcessExitFailFast\n");
    run(pages, 6, WIT_USER_IMAGE_BASE);
    run(pages, 0, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeProcessExitFault\n");
    run(pages, 10, WIT_USER_IMAGE_BASE);
    run(pages, 10, WIT_USER_IMAGE_ALTERNATE);
    run(pages, 15, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeThreadExitNotify\n");
    run(pages, 11, WIT_USER_IMAGE_BASE);
    run(pages, 11, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeThreadExitDetached\n");
    run(pages, 12, WIT_USER_IMAGE_BASE);
    run(pages, 13, WIT_USER_IMAGE_BASE);
    run(pages, 14, WIT_USER_IMAGE_BASE);
    run(pages, 0, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeThreadExitFailFast\n");
    run(pages, 7, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.NativeProcessAbruptExit\n");
}
