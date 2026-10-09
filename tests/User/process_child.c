#define _GNU_SOURCE
#include <errno.h>
#include <limits.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

/* The child of the process scenario (plan step S6.1, /bin/child): a static program the process manager starts on
 * behalf of /bin/init or of another child. argv[1] names what it does: "exit N" exits with N, "env NAME VALUE" exits
 * with 0 when its environment holds NAME=VALUE, "sleep MS N" sleeps and exits with N, "abort" aborts, and "spawn"
 * starts a grandchild through its own capability to the manager, waits for it and exits with the grandchild's status
 * plus one (S6.1). "cwd DIRECTORY FILE" exits with 0 when its current directory is DIRECTORY and FILE opens relative
 * to it, "streams" writes to standard output and error and exits with bit 0 set for an output and bit 1 for an error
 * that is closed, and "stdin" exits with 0 when standard input is closed (S6.2). Anything else exits with 100. */

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
    if (argc == 4 && strcmp(argv[1], "cwd") == 0) {
        char directory[PATH_MAX];
        FILE *file = fopen(argv[3], "r");
        const int found = file != 0;
        if (file) {
            fclose(file);
        }
        return getcwd(directory, sizeof(directory)) && strcmp(directory, argv[2]) == 0 && found ? 0 : 1;
    }
    if (argc == 2 && strcmp(argv[1], "streams") == 0) {
        int closed = 0;
        if (write(1, "[CHILD] standard output\n", 24) < 0 && errno == EBADF) {
            closed |= 1;
        }
        if (write(2, "[CHILD] standard error\n", 23) < 0 && errno == EBADF) {
            closed |= 2;
        }
        return closed;
    }
    if (argc == 2 && strcmp(argv[1], "stdin") == 0) {
        char byte;
        return read(0, &byte, 1) < 0 && errno == EBADF ? 0 : 1;
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
