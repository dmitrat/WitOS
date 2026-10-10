#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "self_test.h"
#include "user_thread2_image.h"

/* The one thread form and the x64 SYSCALL transport (RFC 0011 sections 7.3 and 6.1, plan step K5.2a) from user mode
 * on both ISAs. The fixture reserves and commits a stack of its own, creates a thread with the version 2 request
 * (the caller's stack pointer and TLS base), the thread checks its argument in both entry conventions, its TLS base
 * and its stack, changes its TLS where the ISA allows, and exits naming its stack reservation, which the kernel
 * releases once the thread no longer runs on it; the creator joins it and sees the reservation gone. Broken requests
 * are refused whole. On x64 every call of the fixture travels through SYSCALL with the SysV registers. */

static WitUserProcess process;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

void wit_user_thread2_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    require(wit_test_create_fixture(&process, pages, 0, wit_user_thread2_image, sizeof(wit_user_thread2_image)),
        "Thread form test process creation failed");
    const WitU32 owned = process.Space.OwnedCount;
    wit_user_run(&process);
    if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
        wit_console_write("Thread form test state/code: ");
        wit_console_write_u64(process.State);
        wit_console_write("/");
        wit_console_write_u64(process.ExitCode);
        wit_console_write(" last status: ");
        wit_console_write_u64(*(const WitU64 *)wit_user_space_physical(&process.Space, WIT_USER_DATA + 1304, 0, 0));
        wit_console_write(" checks passed: ");
        wit_console_write_u64(*(const WitU64 *)wit_user_space_physical(&process.Space, WIT_USER_DATA + 1312, 0, 0));
        wit_console_write("\n");
        wit_panic("User thread form test failed");
    }
    /* The first thread and the worker were created, the worker reaped; its stack reservation went with its exit, so the component owns
     * what it owned before and no reservation remains. */
    require(process.ThreadCreates == 2 && process.ThreadReaps == 1,
        "Thread form accounting failed"); /* the first thread and the worker; the worker reaped */
    for (WitU32 i = 0; i < process.Space.ReservationLimit; ++i) {
        require(process.Space.Reservations[i].Size == 0, "A stack reservation survived the thread's exit");
    }
    require(
        process.Handles.Count == 0 && process.Space.OwnedCount == owned, "Thread form scenario left resources behind");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Thread form scenario leaked physical pages");
    wit_console_write("[TEST-PASS] User.ThreadFormAndTransport\n");
}
