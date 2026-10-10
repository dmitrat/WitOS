/* The CoreCLR host check (plan step R3.2), /bin/init of the runtime-corerun scenario: starts upstream's corerun from the
 * boot package with CoreCLR's runtime, CoreLib and the framework's libraries under /coreclr, which corerun's TPA list
 * takes, and the program's assembly, then reports how corerun ended. */
#include <spawn.h>
#include <stdio.h>
#include <sys/wait.h>

extern char **environ;

int main(void)
{
    char *arguments[] = {"corerun", "-c", "/coreclr", "/coreclr/CoreRun.dll", 0};
    pid_t child = 0;
    const int error = posix_spawn(&child, "/coreclr/corerun", 0, 0, arguments, environ);
    if (error) {
        printf("[CORERUN] FAILED: posix_spawn of corerun: %d\n", error);
        return 1;
    }
    int status = 0;
    if (waitpid(child, &status, 0) != child) {
        printf("[CORERUN] FAILED: waitpid\n");
        return 1;
    }
    if (WIFSIGNALED(status)) {
        printf("[CORERUN] corerun ended by signal %d\n", WTERMSIG(status));
        return 1;
    }
    printf("[CORERUN] corerun exited with %d\n", WEXITSTATUS(status));
    return WEXITSTATUS(status);
}
