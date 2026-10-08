#define _GNU_SOURCE
#include <errno.h>
#include <signal.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

/* /bin/init of the process scenario (plan step S6.1): the first process the root task's process manager starts. It
 * starts /bin/child (tests/User/process_child.c) with posix_spawn and posix_spawnp and waits with waitpid and wait:
 * exit statuses, the environment it passes, a grandchild started through a child's own capability to the manager, a
 * death by SIGABRT, more children at once than one kernel wait holds, a WNOHANG poll of a running child, and the
 * refusals of a missing file, a file that is no program and file actions. The last line names the ISA and the checks;
 * a failed check exits with 1, which the root task reports as a failed boot. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#else
#define ISA_NAME "aarch64"
#endif

#define CHILD "/bin/child"
#define AT_ONCE 5 /* children at once: the root task, init and five fill seven of the kernel's eight processes */

extern char **environ;
static int checks;

static void check(int condition, const char *what)
{
    ++checks;
    if (!condition) {
        printf("[INIT] FAILED: %s\n", what);
        exit(1);
    }
}

/* Starts the child with the arguments and returns its pid. */
static pid_t start(char *const argv[], char *const envp[])
{
    pid_t pid = 0;
    const int error = posix_spawn(&pid, CHILD, 0, 0, argv, envp);
    if (error) {
        printf("[INIT] posix_spawn: %s\n", strerror(error));
    }
    check(error == 0 && pid > 1, "posix_spawn");
    return pid;
}

static int finish(pid_t pid)
{
    int status = -1;
    check(waitpid(pid, &status, 0) == pid, "waitpid");
    return status;
}

int main(void)
{
    printf("[INIT] started on " ISA_NAME "\n");
    check(getenv("PATH") && strcmp(getenv("PATH"), "/bin:/usr/bin") == 0, "the root task's environment");

    /* An exit status. */
    char *exit_three[] = {"child", "exit", "3", 0};
    int status = finish(start(exit_three, environ));
    check(WIFEXITED(status) && WEXITSTATUS(status) == 3, "an exit status");

    /* posix_spawnp finds the program through PATH. */
    char *exit_zero[] = {"child", "exit", "0", 0};
    pid_t pid = 0;
    check(posix_spawnp(&pid, "child", 0, 0, exit_zero, environ) == 0, "posix_spawnp");
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "posix_spawnp's child");

    /* The environment a child receives is the one passed. */
    char *environment_check[] = {"child", "env", "WITOS_TEST", "spawned", 0};
    char *environment[] = {"WITOS_TEST=spawned", 0};
    status = finish(start(environment_check, environment));
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "the environment passed");

    /* A grandchild through the child's own capability to the manager. */
    char *spawning[] = {"child", "spawn", 0};
    status = finish(start(spawning, environ));
    check(WIFEXITED(status) && WEXITSTATUS(status) == 5, "a grandchild");

    /* A death by a signal's default action. */
    char *aborting[] = {"child", "abort", 0};
    status = finish(start(aborting, environ));
    check(WIFSIGNALED(status) && WTERMSIG(status) == SIGABRT, "a death by SIGABRT");

    /* More children at once than one kernel wait holds: wait reaps each, then reports none left. */
    int seen = 0;
    for (int i = 0; i < AT_ONCE; ++i) {
        char code[4];
        snprintf(code, sizeof(code), "%d", 10 + i);
        char *exiting[] = {"child", "exit", code, 0};
        start(exiting, environ);
    }
    for (int i = 0; i < AT_ONCE; ++i) {
        check(wait(&status) > 1 && WIFEXITED(status), "wait");
        seen |= 1 << (WEXITSTATUS(status) - 10);
    }
    check(seen == (1 << AT_ONCE) - 1 && wait(&status) == -1 && errno == ECHILD, "every child reaped once");

    /* WNOHANG returns at once while the child runs. */
    char *sleeping[] = {"child", "sleep", "50", "9", 0};
    pid = start(sleeping, environ);
    check(waitpid(pid, &status, WNOHANG) == 0, "WNOHANG while the child runs");
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 9, "the slept child");

    /* Refusals. */
    check(posix_spawn(&pid, "/bin/none", 0, 0, exit_zero, environ) == ENOENT, "a missing file");
    check(posix_spawn(&pid, "/test/hello.txt", 0, 0, exit_zero, environ) == ENOEXEC, "a file that is no program");
    posix_spawn_file_actions_t actions;
    check(posix_spawn_file_actions_init(&actions) == 0 && posix_spawn_file_actions_adddup2(&actions, 1, 2) == 0,
        "file actions");
    check(posix_spawn(&pid, CHILD, &actions, 0, exit_zero, environ) == ENOSYS, "file actions refused");
    posix_spawn_file_actions_destroy(&actions);
    check(waitpid(-1, &status, WNOHANG) == -1 && errno == ECHILD, "no child left");

    printf("[INIT] process manager on " ISA_NAME ": %d checks passed\n", checks);
    return 0;
}
