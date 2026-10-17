#define _GNU_SOURCE
#include <errno.h>
#include <spawn.h>
#include <stdio.h>
#include <string.h>
#include <sys/sysinfo.h>
#include <sys/wait.h>

/* The runner of slices of the .NET libraries' tests on WitOS (plan step R7.3), /bin/init under the system layer's root
 * task. Each project of /tests/projects, one name a line, is one of upstream's library test assemblies with the xunit
 * console runner beside it, as upstream's RunTests.sh runs it: "dotnet exec" from the install of R5 on the project's
 * runtimeconfig and deps file, xunit.console.dll on the assembly, without the categories upstream's CI leaves out
 * (IgnoreForCI, OuterLoop, failing) and without the test methods of /tests/exclusions, one name or pattern a line. A
 * project runs in a process of its own, in its own directory, with TMPDIR naming /dev/shm (R7.2a), where .NET's
 * temporary files and the runner's results go; it passes when xunit's console exits with zero, which it does when no
 * test failed. runtime-witos reads each project's summary and failed tests from the log. There is no timeout: a
 * project that hangs stops the boot. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#elif defined(__aarch64__)
#define ISA_NAME "aarch64"
#else
#define ISA_NAME "riscv64"
#endif

#define NAME_MAX_BYTES 256
#define PATH_MAX_BYTES (2 * NAME_MAX_BYTES + 32)
#define ENVIRONMENT_MAX 32
#define EXCLUSIONS_MAX 128
#define FIXED_ARGUMENTS 18

extern char **environ;

static char *environment[ENVIRONMENT_MAX];
static char exclusions[EXCLUSIONS_MAX][NAME_MAX_BYTES];
static int exclusion_count;

static int run(const char *name)
{
    static char program[] = "/dotnet/dotnet";
    char directory[PATH_MAX_BYTES], runtimeconfig[PATH_MAX_BYTES], deps[PATH_MAX_BYTES], assembly[PATH_MAX_BYTES];
    char results[PATH_MAX_BYTES];
    char *arguments[FIXED_ARGUMENTS + 2 * EXCLUSIONS_MAX + 1] = {0};
    int count = 0;
    snprintf(directory, sizeof(directory), "/tests/%s", name);
    snprintf(runtimeconfig, sizeof(runtimeconfig), "%s.runtimeconfig.json", name);
    snprintf(deps, sizeof(deps), "%s.deps.json", name);
    snprintf(assembly, sizeof(assembly), "%s.dll", name);
    snprintf(results, sizeof(results), "/dev/shm/%s.xml", name);
    arguments[count++] = program;
    arguments[count++] = "exec";
    arguments[count++] = "--runtimeconfig";
    arguments[count++] = runtimeconfig;
    arguments[count++] = "--depsfile";
    arguments[count++] = deps;
    arguments[count++] = "xunit.console.dll";
    arguments[count++] = assembly;
    arguments[count++] = "-xml";
    arguments[count++] = results;
    arguments[count++] = "-nologo";
    arguments[count++] = "-nocolor";
    arguments[count++] = "-notrait";
    arguments[count++] = "category=IgnoreForCI";
    arguments[count++] = "-notrait";
    arguments[count++] = "category=OuterLoop";
    arguments[count++] = "-notrait";
    arguments[count++] = "category=failing";
    for (int i = 0; i < exclusion_count; ++i) {
        arguments[count++] = "-nomethod";
        arguments[count++] = exclusions[i];
    }
    arguments[count] = 0;

    posix_spawn_file_actions_t actions;
    pid_t pid = 0;
    int status = -1;
    if (posix_spawn_file_actions_init(&actions) != 0 ||
        posix_spawn_file_actions_addchdir_np(&actions, directory) != 0) {
        printf("[LIBRARY-TESTS] project %s: FAILED to make its file actions\n", name);
        return 0;
    }
    const int error = posix_spawn(&pid, program, &actions, 0, arguments, environment);
    posix_spawn_file_actions_destroy(&actions);
    if (error) {
        printf("[LIBRARY-TESTS] project %s: FAILED to start: %s\n", name, strerror(error));
        return 0;
    }
    if (waitpid(pid, &status, 0) != pid) {
        printf("[LIBRARY-TESTS] project %s: FAILED to wait\n", name);
        return 0;
    }
    if (WIFEXITED(status) && WEXITSTATUS(status) == 0) {
        printf("[LIBRARY-TESTS] project %s: ok\n", name);
        return 1;
    }
    if (WIFEXITED(status)) {
        printf("[LIBRARY-TESTS] project %s: FAILED with %d\n", name, WEXITSTATUS(status));
    } else {
        printf("[LIBRARY-TESTS] project %s: FAILED by signal %d\n", name, WTERMSIG(status));
    }
    return 0;
}

static FILE *open_list(const char *path)
{
    FILE *list = fopen(path, "r");
    if (!list) {
        printf("[LIBRARY-TESTS] %s could not be opened: %s\n", path, strerror(errno));
    }
    return list;
}

int main(void)
{
    static char temporary[] = "TMPDIR=/dev/shm/";
    char line[NAME_MAX_BYTES];
    int passed = 0, count = 0, entries = 0;
    FILE *projects = open_list("/tests/projects");
    FILE *excluded = open_list("/tests/exclusions");
    if (!projects || !excluded) {
        return 1;
    }
    while (exclusion_count < EXCLUSIONS_MAX && fgets(exclusions[exclusion_count], NAME_MAX_BYTES, excluded)) {
        exclusions[exclusion_count][strcspn(exclusions[exclusion_count], "\r\n")] = 0;
        if (exclusions[exclusion_count][0]) {
            ++exclusion_count;
        }
    }
    fclose(excluded);
    for (char **variable = environ; *variable && entries < ENVIRONMENT_MAX - 2; ++variable) {
        environment[entries++] = *variable;
    }
    environment[entries++] = temporary;
    environment[entries] = 0;
    printf("[LIBRARY-TESTS] The libraries' tests on " ISA_NAME ": a process a project, %d methods left out\n",
        exclusion_count);
    while (fgets(line, sizeof(line), projects)) {
        line[strcspn(line, "\r\n")] = 0;
        if (!line[0]) {
            continue;
        }
        struct sysinfo memory;
        const unsigned long free_mib = sysinfo(&memory) == 0 ? memory.freeram * memory.mem_unit / (1024 * 1024) : 0;
        printf("[LIBRARY-TESTS] project %s\n", line);
        printf("[LIBRARY-TESTS] %lu MiB of memory free\n", free_mib);
        ++count;
        passed += run(line);
    }
    fclose(projects);
    if (count && passed == count) {
        printf("[LIBRARY-TESTS] The libraries' tests on " ISA_NAME ": all %d projects passed\n", passed);
        return 0;
    }
    printf("[LIBRARY-TESTS] The libraries' tests on " ISA_NAME ": %d of %d projects passed\n", passed, count);
    return 1;
}
