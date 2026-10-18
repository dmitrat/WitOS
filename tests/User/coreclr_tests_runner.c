#define _GNU_SOURCE
#include <errno.h>
#include <spawn.h>
#include <stdio.h>
#include <string.h>
#include <sys/wait.h>

/* The runner of slices of CoreCLR's src/tests on WitOS (plan step R7.2b), /bin/init under the system layer's root task.
 * It makes two kinds of runs, each in a process of its own under upstream's corerun, with CoreCLR, CoreLib and the
 * libraries the tests reach under /coreclr and each test's assemblies beside it:
 *
 * - a group of /tests/groups, one name a line, is one of upstream's merged test runners: an assembly whose generated
 *   Main runs every test merged into it, prints "Passed test: <name>" or "Failed test: <name>" for each, writes its
 *   logs into the current directory, /dev/shm (R7.2a), and returns 100; TestExclusionListPath names the tests the
 *   selection leaves out,
 *   which the group skips, and runtime-witos reads from the log whether each test passed, since a group returns 100
 *   whatever its tests did;
 * - a test of /tests/standalone, one assembly path a line with the variables its script exports before it (NAME=VALUE)
 *   and the arguments its script gives after it, is a process-isolated test, which a merged runner starts through a
 *   script WitOS has no shell for: it starts in its own directory with those variables, as the script changes to it and
 *   exports them, and returns 100 when it passed, as the script expects.
 *
 * There is no timeout: a run that hangs stops the boot. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#define RID "witos-x64"
#elif defined(__aarch64__)
#define ISA_NAME "aarch64"
#define RID "witos-arm64"
#else
#define ISA_NAME "riscv64"
#define RID "witos-riscv64"
#endif

#define NAME_MAX_BYTES 512
#define PATH_MAX_BYTES (2 * NAME_MAX_BYTES + 16)
#define ENVIRONMENT_MAX 32
#define ARGUMENTS_MAX 16

extern char **environ;

static char *environment[ENVIRONMENT_MAX];

/* corerun on an assembly and its arguments, with the properties a host gives the runtime from a program's
 * runtimeconfig.json, as corerun_init.c gives them, and the run's own variables after the environment; 1 when it exited
 * with 100. */
static int run(const char *kind, const char *name, const char *directory, char *assembly, char *const *extra,
    int extra_count, char *const *variables, int variable_count)
{
    static char program[] = "/coreclr/corerun";
    char *arguments[8 + ARGUMENTS_MAX] = {program, "-c", "/coreclr", "-p", "System.Globalization.Invariant=true", "-p",
        "RUNTIME_IDENTIFIER=" RID, assembly};
    for (int i = 0; i < extra_count; ++i) {
        arguments[8 + i] = extra[i];
    }
    arguments[8 + extra_count] = 0;
    char *run_environment[ENVIRONMENT_MAX + ARGUMENTS_MAX];
    int entries = 0;
    while (environment[entries]) {
        run_environment[entries] = environment[entries];
        ++entries;
    }
    for (int i = 0; i < variable_count; ++i) {
        run_environment[entries++] = variables[i];
    }
    run_environment[entries] = 0;
    posix_spawn_file_actions_t actions;
    pid_t pid = 0;
    int status = -1;
    if (posix_spawn_file_actions_init(&actions) != 0 ||
        posix_spawn_file_actions_addchdir_np(&actions, directory) != 0) {
        printf("[CORECLR-TESTS] %s %s: FAILED to make its file actions\n", kind, name);
        return 0;
    }
    const int error = posix_spawn(&pid, program, &actions, 0, arguments, run_environment);
    posix_spawn_file_actions_destroy(&actions);
    if (error) {
        printf("[CORECLR-TESTS] %s %s: FAILED to start: %s\n", kind, name, strerror(error));
        return 0;
    }
    if (waitpid(pid, &status, 0) != pid) {
        printf("[CORECLR-TESTS] %s %s: FAILED to wait\n", kind, name);
        return 0;
    }
    if (WIFEXITED(status) && WEXITSTATUS(status) == 100) {
        printf("[CORECLR-TESTS] %s %s: ok\n", kind, name);
        return 1;
    }
    if (WIFEXITED(status)) {
        printf("[CORECLR-TESTS] %s %s: FAILED with %d\n", kind, name, WEXITSTATUS(status));
    } else {
        printf("[CORECLR-TESTS] %s %s: FAILED by signal %d\n", kind, name, WTERMSIG(status));
    }
    return 0;
}

static FILE *open_list(const char *path)
{
    FILE *list = fopen(path, "r");
    if (!list) {
        printf("[CORECLR-TESTS] %s could not be opened: %s\n", path, strerror(errno));
    }
    return list;
}

int main(void)
{
    static char exclusions[] = "TestExclusionListPath=/tests/exclusions.txt";
    char line[NAME_MAX_BYTES];
    int passed = 0, count = 0, entries = 0;
    FILE *groups = open_list("/tests/groups");
    FILE *standalone = open_list("/tests/standalone");
    if (!groups || !standalone) {
        return 1;
    }
    for (char **variable = environ; *variable && entries < ENVIRONMENT_MAX - 2; ++variable) {
        environment[entries++] = *variable;
    }
    environment[entries++] = exclusions;
    environment[entries] = 0;
    printf("[CORECLR-TESTS] CoreCLR's tests on " ISA_NAME ": a process a run\n");
    while (fgets(line, sizeof(line), groups)) {
        line[strcspn(line, "\r\n")] = 0;
        if (!line[0]) {
            continue;
        }
        char assembly[PATH_MAX_BYTES];
        const char *leaf = strrchr(line, '/');
        snprintf(assembly, sizeof(assembly), "/tests/%s/%s.dll", line, leaf ? leaf + 1 : line);
        printf("[CORECLR-TESTS] group %s\n", line);
        ++count;
        passed += run("group", line, "/dev/shm", assembly, 0, 0, 0, 0);
    }
    while (fgets(line, sizeof(line), standalone)) {
        line[strcspn(line, "\r\n")] = 0;
        if (!line[0]) {
            continue;
        }
        char *variables[ARGUMENTS_MAX];
        int variable_count = 0;
        char *test = line;
        while (*test && *test != '/' && variable_count < ARGUMENTS_MAX) {
            variables[variable_count++] = test;
            char *space = strchr(test, ' ');
            if (!space) {
                break;
            }
            *space = 0;
            test = space + 1;
            while (*test == ' ') {
                ++test;
            }
        }
        if (*test != '/') {
            printf("[CORECLR-TESTS] %s names no test\n", line);
            ++count;
            continue;
        }
        char *extra[ARGUMENTS_MAX];
        int extra_count = 0;
        char *cursor = strchr(test, ' ');
        while (cursor && extra_count < ARGUMENTS_MAX - 1) {
            *cursor++ = 0;
            while (*cursor == ' ') {
                ++cursor;
            }
            if (!*cursor) {
                break;
            }
            extra[extra_count++] = cursor;
            cursor = strchr(cursor, ' ');
        }
        char directory[NAME_MAX_BYTES];
        snprintf(directory, sizeof(directory), "%s", test);
        char *slash = strrchr(directory, '/');
        if (slash) {
            *slash = 0;
        }
        ++count;
        passed += run("test", test, directory, test, extra, extra_count, variables, variable_count);
    }
    fclose(groups);
    fclose(standalone);
    if (count && passed == count) {
        printf("[CORECLR-TESTS] CoreCLR's tests on " ISA_NAME ": all %d runs exited with 100\n", passed);
        return 0;
    }
    printf("[CORECLR-TESTS] CoreCLR's tests on " ISA_NAME ": %d of %d runs exited with 100\n", passed, count);
    return 1;
}
