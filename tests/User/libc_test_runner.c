#define _GNU_SOURCE
#include <spawn.h>
#include <stdio.h>
#include <string.h>
#include <sys/wait.h>

/* The runner of musl's libc-test on WitOS (plan step S7.1), /bin/init under the system layer's root task. As
 * libc-test's own runner does, every selected test runs in a process of its own, built twice — linked statically and
 * dynamically against libc.so — and each passes when it exits with zero, which is the t_status its main returns. The
 * package keeps libc-test's layout under /libc-test, and a test starts there, as runtest runs it from libc-test's
 * root: by its package path, so that t_pathrel finds a test's shared library beside it, in the directory /libc-test,
 * for the libraries a test opens by a path relative to that root, and with LD_LIBRARY_PATH naming the directories of
 * the libraries a test needs at its start. There is no timeout and no resource limit, which libc-test's runtest sets:
 * a test that hangs stops the boot. One line per run and a summary the boot scenario requires; the runner exits with
 * zero only when every run passed. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#else
#define ISA_NAME "aarch64"
#endif

typedef struct Run {
    const char *Name;
    const char *Form;
    const char *Path;
} Run;

#include "libc_test_runs.h" /* generated: const Run libc_runs[]; LIBC_RUN_COUNT */

int main(void)
{
    static char *environment[] = {"LD_LIBRARY_PATH=/libc-test/src/functional:/libc-test/src/regression", 0};
    posix_spawn_file_actions_t actions;
    int passed = 0;
    if (posix_spawn_file_actions_init(&actions) != 0 ||
        posix_spawn_file_actions_addchdir_np(&actions, "/libc-test") != 0) {
        printf("[LIBC-TEST] the file actions could not be made\n");
        return 1;
    }
    printf("[LIBC-TEST] musl 1.2.5 on " ISA_NAME ": %d runs, a process each\n", LIBC_RUN_COUNT);
    for (int i = 0; i < LIBC_RUN_COUNT; ++i) {
        char *argv[] = {(char *)libc_runs[i].Path, 0};
        pid_t pid = 0;
        int status = -1;
        const int error = posix_spawn(&pid, libc_runs[i].Path, &actions, 0, argv, environment);
        if (error) {
            printf("[LIBC-TEST] %s (%s): FAILED to start: %s\n", libc_runs[i].Name, libc_runs[i].Form, strerror(error));
            continue;
        }
        if (waitpid(pid, &status, 0) != pid) {
            printf("[LIBC-TEST] %s (%s): FAILED to wait\n", libc_runs[i].Name, libc_runs[i].Form);
            continue;
        }
        if (WIFEXITED(status) && WEXITSTATUS(status) == 0) {
            ++passed;
            printf("[LIBC-TEST] %s (%s): ok\n", libc_runs[i].Name, libc_runs[i].Form);
        } else if (WIFEXITED(status)) {
            printf("[LIBC-TEST] %s (%s): FAILED with %d\n", libc_runs[i].Name, libc_runs[i].Form, WEXITSTATUS(status));
        } else {
            printf(
                "[LIBC-TEST] %s (%s): FAILED by signal %d\n", libc_runs[i].Name, libc_runs[i].Form, WTERMSIG(status));
        }
    }
    if (passed == LIBC_RUN_COUNT) {
        printf("[LIBC-TEST] musl 1.2.5 on " ISA_NAME ": all %d tests passed\n", passed);
        return 0;
    }
    printf("[LIBC-TEST] musl 1.2.5 on " ISA_NAME ": %d of %d passed\n", passed, LIBC_RUN_COUNT);
    return 1;
}
