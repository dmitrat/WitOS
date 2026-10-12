#define _GNU_SOURCE
#include <errno.h>
#include <spawn.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>

/* /bin/init of the runtime-compatibility scenario (plan step N1): the root task starts it after the device manager and
 * the namespace service answered, so the data disk's ext4 volume is /data. It makes /data/tmp, the temporary directory
 * the suite's files go to, since the boot package is read-only and has no /tmp (TMPDIR, as Linux's programs take it);
 * then it starts the package's dotnet with the compatibility suite, an ordinary portable application, and waits for
 * it. */

extern char **environ;

#define ENVIRONMENT 32

int main(void)
{
    if (mkdir("/data/tmp", 0755) != 0 && errno != EEXIST) {
        printf("[COMPATIBILITY-INIT] /data/tmp could not be made (errno %d)\n", errno);
        return 1;
    }
    char *envp[ENVIRONMENT];
    int count = 0;
    for (char **entry = environ; *entry && count < ENVIRONMENT - 2; ++entry) {
        if (strncmp(*entry, "TMPDIR=", 7) != 0) {
            envp[count++] = *entry;
        }
    }
    envp[count++] = "TMPDIR=/data/tmp/";
    envp[count] = 0;
    char *argv[] = {"/dotnet/dotnet", "/app/DotNetCompatibility.dll", 0};
    pid_t child = 0;
    int status = 0;
    const int error = posix_spawn(&child, argv[0], 0, 0, argv, envp);
    if (error || waitpid(child, &status, 0) != child) {
        printf("[COMPATIBILITY-INIT] /dotnet/dotnet did not start (errno %d)\n", error ? error : errno);
        return 1;
    }
    const int code = WIFEXITED(status) ? WEXITSTATUS(status) : -1;
    printf("[COMPATIBILITY-INIT] /dotnet/dotnet exited with %d\n", code);
    return code ? 1 : 0;
}
