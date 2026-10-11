/* The hosts check (plan step R5), /bin/init of the runtime-dotnet and runtime-portable-app scenarios: starts
 * upstream's dotnet muxer from the shared-framework layout under /dotnet on the program under /app (DOTNET_APP, which
 * the package's build names), then reports how it ended. */
#include <spawn.h>
#include <stdio.h>
#include <sys/wait.h>

extern char **environ;

#ifndef DOTNET_APP
#define DOTNET_APP "/app/App.dll"
#endif

int main(void)
{
    char *arguments[] = {"dotnet", DOTNET_APP, "witos", 0};
    pid_t child = 0;
    const int error = posix_spawn(&child, "/dotnet/dotnet", 0, 0, arguments, environ);
    if (error) {
        printf("[DOTNET] FAILED: posix_spawn of dotnet: %d\n", error);
        return 1;
    }
    int status = 0;
    if (waitpid(child, &status, 0) != child) {
        printf("[DOTNET] FAILED: waitpid\n");
        return 1;
    }
    if (WIFSIGNALED(status)) {
        printf("[DOTNET] dotnet ended by signal %d\n", WTERMSIG(status));
        return 1;
    }
    printf("[DOTNET] dotnet exited with %d\n", WEXITSTATUS(status));
    return WEXITSTATUS(status);
}
