#include <spawn.h>
#include <stdio.h>
#include <string.h>
#include <sys/wait.h>

/* /bin/init of the runtime's try_run measurement (plan step R1.2b). dotnet/runtime's configure answers some of its
 * questions by running a program it compiled for the target (try_run); a cross build cannot, so eng/native/tryrun.cmake
 * holds each platform's answers. WitOS's answers are measured here: the configure kept every probe it compiled for
 * witos, the package lays them out under /tryrun with their names in /tryrun/probes, and this program runs each in a
 * process of its own and prints how it ended, which WitOS.Dev compares with what tryrun.cmake answers. A probe runs from
 * the root directory with no environment; one that hangs stops the boot. The last line counts the probes that ran, and
 * the program exits with zero only when every probe started and ended. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#else
#define ISA_NAME "aarch64"
#endif

int main(void)
{
    static char *environment[] = {0};
    char name[128];
    char path[160];
    int count = 0;
    int ran = 0;
    FILE *list = fopen("/tryrun/probes", "r");
    if (!list) {
        printf("[TRYRUN] /tryrun/probes could not be opened\n");
        return 1;
    }
    while (fgets(name, sizeof name, list)) {
        name[strcspn(name, "\n")] = 0;
        if (!name[0]) {
            continue;
        }
        ++count;
        snprintf(path, sizeof path, "/tryrun/%s", name);
        char *argv[] = {path, 0};
        pid_t pid = 0;
        int status = -1;
        const int error = posix_spawn(&pid, path, 0, 0, argv, environment);
        if (error) {
            printf("[TRYRUN] %s not started: %s\n", name, strerror(error));
            continue;
        }
        if (waitpid(pid, &status, 0) != pid) {
            printf("[TRYRUN] %s not waited for\n", name);
            continue;
        }
        ++ran;
        if (WIFEXITED(status)) {
            printf("[TRYRUN] %s exit %d\n", name, WEXITSTATUS(status));
        } else {
            printf("[TRYRUN] %s signal %d\n", name, WTERMSIG(status));
        }
    }
    fclose(list);
    printf("[TRYRUN] %d probes on " ISA_NAME ": %d ran\n", count, ran);
    return count > 0 && ran == count ? 0 : 1;
}
