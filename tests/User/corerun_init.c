/* The CoreCLR host check (plan step R3.2), /bin/init of the runtime-corerun and runtime-coreclr-acceptance scenarios:
 * starts upstream's corerun from the boot package with CoreCLR's runtime, CoreLib and the framework's libraries under
 * /coreclr, which corerun's TPA list takes, and the program's assembly (CORERUN_ASSEMBLY, which the package's build
 * names), then reports how corerun ended. */
#include <spawn.h>
#include <stdio.h>
#include <sys/wait.h>

extern char **environ;

#if defined(__x86_64__)
#define RID "witos-x64"
#else
#define RID "witos-arm64"
#endif

#ifndef CORERUN_ASSEMBLY
#define CORERUN_ASSEMBLY "/coreclr/CoreRun.dll"
#endif

int main(void)
{
    /* The properties a host gives the runtime from a program's runtimeconfig.json: the system layer has no ICU, so
     * globalization is invariant (RFC 0015 section 8), and the RID a published program carries. */
    char *arguments[] = {"corerun", "-c", "/coreclr", "-p", "System.Globalization.Invariant=true", "-p",
        "RUNTIME_IDENTIFIER=" RID, CORERUN_ASSEMBLY, 0};
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
