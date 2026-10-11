#define _GNU_SOURCE
#include <errno.h>
#include <spawn.h>
#include <stdio.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

/* The runner of CoreCLR's PAL suite on WitOS (plan step R7.1), /bin/init under the system layer's root task. As
 * upstream's runpaltests.sh does, every test of the selection runs in a process of its own, `paltests <name>` or the
 * test's own program where its build made one (pal_sxs, beside its libraries), and passes when it exits with zero, the
 * PASS its entry point returns. The selection is upstream's paltestlist.txt less the tests build/runtime/palsuite.json
 * leaves out with a reason; runtime-witos writes it to /paltests/selection, one name a line. A test starts in /dev/shm
 * (R7.2a), the directory in which a process creates files, as runpaltests.sh gives each test a writable directory of
 * its own: every process has its own /dev/shm, so a test finds it empty. There is no timeout: a test that hangs stops
 * the boot. One line per test and a summary the boot scenario requires; the runner exits with zero only when every
 * test of the selection passed. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#else
#define ISA_NAME "aarch64"
#endif

#ifndef PALSUITE_EXCLUDED
#define PALSUITE_EXCLUDED 0
#endif

#define NAME_MAX_BYTES 256
#define PATH_MAX_BYTES (NAME_MAX_BYTES + 16)

extern char **environ;

int main(void)
{
    static char program[] = "/paltests/paltests";
    posix_spawn_file_actions_t actions;
    char name[NAME_MAX_BYTES];
    int passed = 0, count = 0;
    FILE *selection = fopen("/paltests/selection", "r");
    if (!selection) {
        printf("[PAL] the selection could not be opened: %s\n", strerror(errno));
        return 1;
    }
    if (posix_spawn_file_actions_init(&actions) != 0 ||
        posix_spawn_file_actions_addchdir_np(&actions, "/dev/shm") != 0) {
        printf("[PAL] the file actions could not be made\n");
        return 1;
    }
    printf("[PAL] CoreCLR's PAL suite on " ISA_NAME ": a process a test, %d left out\n", PALSUITE_EXCLUDED);
    while (fgets(name, sizeof(name), selection)) {
        name[strcspn(name, "\r\n")] = 0;
        if (!name[0]) {
            continue;
        }
        char own[PATH_MAX_BYTES];
        char *argv[] = {program, name, 0};
        pid_t pid = 0;
        int status = -1;
        ++count;
        snprintf(own, sizeof(own), "/paltests/%s", name);
        if (access(own, F_OK) == 0) { /* the package keeps no execute bits */
            argv[0] = own;
            argv[1] = 0;
        }
        const int error = posix_spawn(&pid, argv[0], &actions, 0, argv, environ);
        if (error) {
            printf("[PAL] %s: FAILED to start: %s\n", name, strerror(error));
            continue;
        }
        if (waitpid(pid, &status, 0) != pid) {
            printf("[PAL] %s: FAILED to wait\n", name);
            continue;
        }
        if (WIFEXITED(status) && WEXITSTATUS(status) == 0) {
            ++passed;
            printf("[PAL] %s: ok\n", name);
        } else if (WIFEXITED(status)) {
            printf("[PAL] %s: FAILED with %d\n", name, WEXITSTATUS(status));
        } else {
            printf("[PAL] %s: FAILED by signal %d\n", name, WTERMSIG(status));
        }
    }
    fclose(selection);
    if (count && passed == count) {
        printf("[PAL] CoreCLR's PAL suite on " ISA_NAME ": all %d tests passed\n", passed);
        return 0;
    }
    printf("[PAL] CoreCLR's PAL suite on " ISA_NAME ": %d of %d passed\n", passed, count);
    return 1;
}
