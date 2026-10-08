#define _GNU_SOURCE
#include "witos/manager.h"
#include "witos/memory_info.h"
#include "witos/spawn.h"
#include "witos/syscall.h"
#include "witos/user_abi.h"
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

/* The root task of the spawn scenario (plan steps S5.2 and S5.3): libwitos's ELF loader starts the package's
 * programs in processes of their own. A static program (tests/User/spawn_child.c) with arguments and an environment
 * exits with the code it was told, two run at once, one aborts, one is killed; a dynamic program
 * (tests/User/dynamic_main.c) runs under musl's dynamic linker, once mapped by the loader with its interpreter and
 * once loaded by the linker run as a command; a missing file, a file that is no program and arguments beyond the
 * first stack are refused before any process exists; and once every process ended and its handle is closed, the
 * kernel's free memory and the root task's reservations are what they were. The last line names the ISA and the
 * count of checks. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#else
#define ISA_NAME "aarch64"
#endif

#define CHILD "/bin/child"
#define DYNAMIC "/bin/dynamic"
#define INTERPRETER "/lib/ld-musl-" ISA_NAME ".so.1"

static int checks;

static void check(int condition, const char *what)
{
    ++checks;
    if (!condition) {
        printf("[SPAWN] FAILED: %s\n", what);
        exit(1);
    }
}

static WitU64 start(char *const argv[], char *const envp[])
{
    WitU64 process = 0;
    const int error = witos_spawn(&process, argv[0], argv, envp);
    if (error) {
        printf("[SPAWN] witos_spawn: %s\n", strerror(error));
    }
    check(error == 0 && process != 0, "witos_spawn of the program");
    return process;
}

/* Waits for the process, closes its handle and returns its record. */
static WitProcessInfo finish(WitU64 process)
{
    WitProcessInfo info;
    WitU64 result = 0;
    check(witos_wait(process, &info) == 0, "witos_wait");
    check(wit_syscall(WIT_CALL_HANDLE_CLOSE, process, 0, 0, &result) == WIT_STATUS_OK, "closing the process handle");
    return info;
}

static WitUserMemoryInfo memory(void)
{
    WitUserMemoryInfo info;
    WitU64 result = 0;
    memset(&info, 0, sizeof(info));
    check(wit_syscall(WIT_CALL_MEMORY_QUERY, (WitU64)&info, sizeof(info), WIT_MEMORY_INFO_VERSION, &result) ==
            WIT_STATUS_OK,
        "MEMORY_QUERY");
    return info;
}

int main(void)
{
    static char *run_seven[] = {CHILD, "run", "7", 0};
    static char *run_nine[] = {CHILD, "run", "9", 0};
    static char *abort_args[] = {CHILD, "abort", 0};
    static char *wait_args[] = {CHILD, "wait", 0};
    static char *environment[] = {"WITOS_SPAWN=loader", 0};
    WitU64 process = 0, result = 0;
    printf("[SPAWN] root task over the libc and libwitos\n");

    /* Refusals, before any process exists. */
    check(witos_spawn(&process, "/bin/none", run_seven, environment) == ENOENT && process == 0, "a missing file");
    check(witos_spawn(&process, "/test/hello.txt", run_seven, environment) == ENOEXEC, "a file that is no ELF");
    check(witos_spawn(&process, "/dev/zero", run_seven, environment) == ENOEXEC, "a device");
    char *large = malloc(80 * 1024);
    check(large != 0, "malloc");
    memset(large, 'a', 80 * 1024 - 1);
    large[80 * 1024 - 1] = 0;
    char *large_args[] = {CHILD, large, 0};
    check(witos_spawn(&process, CHILD, large_args, environment) == E2BIG, "arguments beyond the first stack");
    free(large);
    const WitUserMemoryInfo before = memory();

    /* A program with arguments and an environment ends with its code. */
    WitProcessInfo info = finish(start(run_seven, environment));
    check(info.State == WIT_PROCESS_STATE_EXITED && info.ExitCode == 7, "the exit code of a program");

    /* Two at once, each in its own address space at the same addresses. */
    const WitU64 first = start(run_seven, environment), second = start(run_nine, environment);
    info = finish(second);
    check(info.State == WIT_PROCESS_STATE_EXITED && info.ExitCode == 9, "the second of two programs");
    info = finish(first);
    check(info.State == WIT_PROCESS_STATE_EXITED && info.ExitCode == 7, "the first of two programs");

    /* abort() ends the process as the libc's default action reports SIGABRT. */
    info = finish(start(abort_args, 0));
    check(info.State == WIT_PROCESS_STATE_EXITED && info.ExitCode == WIT_EXIT_SIGNAL(6), "an aborted program");

    /* A dynamic program (S5.3): the loader maps it and its interpreter, which loads the program's library. */
    static char *dynamic_args[] = {DYNAMIC, "5", 0};
    info = finish(start(dynamic_args, environment));
    check(info.State == WIT_PROCESS_STATE_EXITED && info.ExitCode == 5, "a dynamic program");

    /* The dynamic linker run as a command: a static image for the loader, which maps the program itself. */
    static char *command_args[] = {INTERPRETER, DYNAMIC, "6", 0};
    info = finish(start(command_args, 0));
    check(info.State == WIT_PROCESS_STATE_EXITED && info.ExitCode == 6, "the dynamic linker as a command");

    /* The acceptance of phase S (S5.4): a dynamic C++ program with its library over the shared C++ runtime, and the
     * C++ scenarios of S4 as a dynamic program. */
    static char *acceptance_args[] = {"/bin/acceptance", 0};
    info = finish(start(acceptance_args, 0));
    check(info.State == WIT_PROCESS_STATE_EXITED && info.ExitCode == 0, "the dynamic C++ program");
    static char *cxx_args[] = {"/bin/cxx", 0};
    info = finish(start(cxx_args, 0));
    check(info.State == WIT_PROCESS_STATE_EXITED && info.ExitCode == 0, "the C++ scenarios as a dynamic program");

    /* The creator ends a waiting program with PROCESS_KILL, once it had the time to start and wait. */
    process = start(wait_args, 0);
    const struct timespec pause_time = {0, 50000000};
    check(nanosleep(&pause_time, 0) == 0, "nanosleep");
    check(wit_syscall(WIT_CALL_PROCESS_KILL, process, 99, 0, &result) == WIT_STATUS_OK, "PROCESS_KILL");
    info = finish(process);
    check(info.State == WIT_PROCESS_STATE_EXITED && info.ExitCode == 99, "a killed program");

    /* Nothing remains: the pages of the images, stacks and processes are free again, and the loader's own views are
     * gone. */
    const WitUserMemoryInfo after = memory();
    check(after.PhysicalAvailableBytes == before.PhysicalAvailableBytes, "the kernel's free memory");
    check(after.ReservationCount == before.ReservationCount, "the root task's reservations");
    printf("[SPAWN] ELF loader on " ISA_NAME ": %d checks passed\n", checks);
    return 0;
}
