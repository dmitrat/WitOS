#define _GNU_SOURCE
#include <spawn.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

/* The child of the process scenario (plan step S6.1, /bin/child): a static program the process manager starts on
 * behalf of /bin/init or of another child. argv[1] names what it does: "exit N" exits with N, "env NAME VALUE" exits
 * with 0 when its environment holds NAME=VALUE, "sleep MS N" sleeps and exits with N, "abort" aborts, and "spawn"
 * starts a grandchild through its own capability to the manager, waits for it and exits with the grandchild's status
 * plus one. Anything else exits with 100. */

extern char **environ;

int main(int argc, char **argv)
{
    if (argc == 3 && strcmp(argv[1], "exit") == 0) {
        return atoi(argv[2]);
    }
    if (argc == 4 && strcmp(argv[1], "env") == 0) {
        const char *value = getenv(argv[2]);
        return value && strcmp(value, argv[3]) == 0 ? 0 : 1;
    }
    if (argc == 4 && strcmp(argv[1], "sleep") == 0) {
        const long milliseconds = atol(argv[2]);
        const struct timespec time = {milliseconds / 1000, (milliseconds % 1000) * 1000000L};
        nanosleep(&time, 0);
        return atoi(argv[3]);
    }
    if (argc == 2 && strcmp(argv[1], "abort") == 0) {
        abort();
    }
    if (argc == 2 && strcmp(argv[1], "spawn") == 0) {
        char *grandchild[] = {"/bin/child", "exit", "4", 0};
        pid_t pid = 0;
        int status = 0;
        if (posix_spawn(&pid, grandchild[0], 0, 0, grandchild, environ) != 0 ||
            waitpid(pid, &status, 0) != pid ||
            !WIFEXITED(status)) {
            return 99;
        }
        return WEXITSTATUS(status) + 1;
    }
    return 100;
}
