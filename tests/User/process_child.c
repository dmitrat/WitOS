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
 * that is closed, and "stdin" exits with 0 when standard input is closed (S6.2). "cat" copies its standard input to
 * its standard output until the end of the input and exits with 0, 2 for a failed read and 3 for a failed write;
 * "expect BYTES" reads its input to the end and exits with 0 when it read BYTES bytes of the pattern (byte i is
 * i & 0xFF); "fill BYTES" writes that pattern to its output (N4.2). Anything else exits with 100. */

extern char **environ;

/* Writes every byte, in pieces as the pipe takes them. */
static int write_all(int fd, const unsigned char *buffer, size_t bytes)
{
    while (bytes) {
        const ssize_t written = write(fd, buffer, bytes);
        if (written <= 0) {
            return -1;
        }
        buffer += written;
        bytes -= (size_t)written;
    }
    return 0;
}

int main(int argc, char **argv)
{
    if (argc == 3 && strcmp(argv[1], "ids") == 0) {
        /* Process ids (E4.3): the parent the argument names is this process's parent, and the exit status is the low
         * bits of its own id, which the parent compares with what posix_spawn answered. */
        if (getppid() != (pid_t)atoi(argv[2]) || getpid() <= 1 || getpid() == getppid()) {
            return 200;
        }
        return getpid() % 128;
    }
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
    if (argc == 2 && strcmp(argv[1], "cat") == 0) {
        unsigned char buffer[4096];
        for (;;) {
            const ssize_t got = read(0, buffer, sizeof(buffer));
            if (got < 0) {
                return 2;
            }
            if (got == 0) {
                return 0;
            }
            if (write_all(1, buffer, (size_t)got) < 0) {
                return 3;
            }
        }
    }
    if (argc == 3 && strcmp(argv[1], "expect") == 0) {
        unsigned char buffer[4096];
        const unsigned long expected = strtoul(argv[2], 0, 10);
        unsigned long total = 0;
        for (;;) {
            const ssize_t got = read(0, buffer, sizeof(buffer));
            if (got < 0) {
                return 2;
            }
            if (got == 0) {
                return total == expected ? 0 : 1;
            }
            for (ssize_t i = 0; i < got; ++i) {
                if (buffer[i] != (unsigned char)((total + (unsigned long)i) & 0xFF)) {
                    return 1;
                }
            }
            total += (unsigned long)got;
        }
    }
    if (argc == 3 && strcmp(argv[1], "fill") == 0) {
        unsigned char buffer[4096];
        const unsigned long bytes = strtoul(argv[2], 0, 10);
        for (unsigned long total = 0; total < bytes;) {
            const size_t piece = bytes - total < sizeof(buffer) ? (size_t)(bytes - total) : sizeof(buffer);
            for (size_t i = 0; i < piece; ++i) {
                buffer[i] = (unsigned char)((total + i) & 0xFF);
            }
            if (write_all(1, buffer, piece) < 0) {
                return 3;
            }
            total += piece;
        }
        return 0;
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
