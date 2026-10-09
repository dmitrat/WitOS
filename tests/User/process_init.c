#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
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
 * refusals of a missing file and a file that is no program (S6.1); its own current directory through chdir and fchdir,
 * a child that inherits it or starts where a chdir action puts it, a program path relative to that directory, and the
 * standard streams a child starts without or rebinds through file actions, with the actions that are not there refused
 * (S6.2). The last line names the ISA and the checks; a failed check exits with 1, which the root task reports as a
 * failed boot. */

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

    /* The current directory (S6.2): chdir, relative paths and "..", fchdir, and the refusals. */
    char directory[PATH_MAX];
    check(getcwd(directory, sizeof(directory)) && strcmp(directory, "/") == 0, "the initial directory");
    check(chdir("/test") == 0 && getcwd(directory, sizeof(directory)) && strcmp(directory, "/test") == 0, "chdir");
    check(access("hello.txt", F_OK) == 0 && access("dir/a.txt", F_OK) == 0, "relative paths");
    check(chdir("dir") == 0 && access("../hello.txt", F_OK) == 0 && chdir("..") == 0, "a relative chdir and ..");
    check(chdir("hello.txt") == -1 && errno == ENOTDIR && chdir("/none") == -1 && errno == ENOENT, "chdir refusals");
    const int dir = open("/test/dir", O_RDONLY | O_DIRECTORY);
    check(dir >= 0 &&
            fchdir(dir) == 0 &&
            getcwd(directory, sizeof(directory)) &&
            strcmp(directory, "/test/dir") == 0 &&
            chdir("/test") == 0,
        "fchdir");
    check(openat(dir, "../hello.txt", O_RDONLY) >= 0, "a path from a directory descriptor through ..");

    /* A child inherits the directory, a chdir or fchdir action moves it, and a relative program path starts there. */
    char *in_test[] = {"child", "cwd", "/test", "hello.txt", 0};
    status = finish(start(in_test, environ));
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "a child in the inherited directory");
    posix_spawn_file_actions_t actions;
    char *in_dir[] = {"child", "cwd", "/test/dir", "a.txt", 0};
    check(posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_addchdir_np(&actions, "dir") == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, in_dir, environ) == 0,
        "a chdir action");
    posix_spawn_file_actions_destroy(&actions);
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "a child where the chdir action put it");
    const int root = open("/", O_RDONLY | O_DIRECTORY);
    char *in_root[] = {"child", "cwd", "/", "bin/child", 0};
    check(root >= 0 &&
            posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_addfchdir_np(&actions, root) == 0 &&
            posix_spawn(&pid, "bin/child", &actions, 0, in_root, environ) == 0,
        "an fchdir action and a program path relative to it");
    posix_spawn_file_actions_destroy(&actions);
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "a child in the root through a relative program path");

    /* Standard streams as the log capability, one by one: closed, rebound, and the actions that are not there. */
    char *streams[] = {"child", "streams", 0};
    status = finish(start(streams, environ));
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "both streams inherited");
    check(posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_addclose(&actions, 1) == 0 &&
            posix_spawn_file_actions_addclose(&actions, 7) == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, streams, environ) == 0,
        "a closed output");
    posix_spawn_file_actions_destroy(&actions);
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 1, "a child without standard output");
    check(posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_addclose(&actions, 2) == 0 &&
            posix_spawn_file_actions_adddup2(&actions, 1, 2) == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, streams, environ) == 0,
        "an error rebound to the output");
    posix_spawn_file_actions_destroy(&actions);
    status = finish(pid);
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "a child with its error rebound");
    check(posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_adddup2(&actions, dir, 1) == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, streams, environ) == ENOSYS,
        "a descriptor as a stream refused");
    posix_spawn_file_actions_destroy(&actions);
    check(posix_spawn_file_actions_init(&actions) == 0 &&
            posix_spawn_file_actions_addopen(&actions, 0, "/test/hello.txt", O_RDONLY, 0) == 0 &&
            posix_spawn(&pid, CHILD, &actions, 0, streams, environ) == ENOSYS,
        "a file as a stream refused");
    posix_spawn_file_actions_destroy(&actions);
    char byte;
    check(close(0) == 0 && read(0, &byte, 1) == -1 && errno == EBADF && close(0) == -1 && errno == EBADF,
        "the own standard input closed");
    char *closed_input[] = {"child", "stdin", 0};
    status = finish(start(closed_input, environ));
    check(WIFEXITED(status) && WEXITSTATUS(status) == 0, "a child inherits the closed input");
    check(waitpid(-1, &status, WNOHANG) == -1 && errno == ECHILD, "no child left");

    printf("[INIT] process manager on " ISA_NAME ": %d checks passed\n", checks);
    return 0;
}
