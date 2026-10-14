#define _GNU_SOURCE
#include <errno.h>
#include <signal.h>
#include <spawn.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/sysinfo.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

/* /bin/init of the scenarios that run ordinary .NET applications beside the device services (plan steps N1, N2.1 and
 * N2.2): the root task starts it after the device manager and the namespace service answered, so the data disk's ext4
 * volume is /data. It makes /data/tmp, the temporary directory the applications' files go to, since the boot package is
 * read-only and has no /tmp (TMPDIR, as Linux's programs take it); then it runs each command of /etc/apps in turn, one a
 * line, its words separated by spaces, waits for each and reports its exit, how long it ran and the memory free after it,
 * which tells a leak across commands from a command's own need. A line "cd <directory>"
 * names the directory the commands after it run in and is reported, so that the log tells whose output follows. It
 * exits with zero when every command did. */

extern char **environ;

#define ENVIRONMENT 32
#define WORDS 256
#define LINE 8192

/* The directory the next commands run in, or empty for the root task's. */
static char directory[LINE];

/* The monotonic clock in whole seconds. */
static long seconds(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (long)now.tv_sec;
}

/* Runs one command line and waits for it: its exit code, or -1. */
static int run(char *line, char **envp)
{
    char *argv[WORDS];
    int count = 0;
    for (char *word = strtok(line, " \n"); word && count < WORDS - 1; word = strtok(0, " \n")) {
        argv[count++] = word;
    }
    argv[count] = 0;
    if (!count) {
        return 0;
    }
    posix_spawn_file_actions_t actions;
    posix_spawn_file_actions_init(&actions);
    if (directory[0]) {
        posix_spawn_file_actions_addchdir_np(&actions, directory);
    }
    pid_t child = 0;
    int status = 0;
    const long start = seconds();
    const int error = posix_spawn(&child, argv[0], &actions, 0, argv, envp);
    posix_spawn_file_actions_destroy(&actions);
    if (error || waitpid(child, &status, 0) != child) {
        printf("[APPS-INIT] %s did not start (errno %d)\n", argv[0], error ? error : errno);
        return -1;
    }
    const int code = WIFEXITED(status) ? WEXITSTATUS(status) : -1;
    struct sysinfo memory;
    const unsigned long free_mib = sysinfo(&memory) == 0 ? memory.freeram / (1024UL * 1024UL) * memory.mem_unit : 0;
    printf("[APPS-INIT] %s %s exited with %d after %ld s, %lu MiB free\n", argv[0], count > 1 ? argv[1] : "", code,
        seconds() - start, free_mib);
    return code;
}

int main(void)
{
    /* A launcher ignores the terminal's interrupt and quit, as a shell running a command does (N4.3): Ctrl-C reaches every
     * process that holds the terminal, since there are no process groups, and the command alone should answer it. */
    signal(SIGINT, SIG_IGN);
    signal(SIGQUIT, SIG_IGN);
    if (mkdir("/data/tmp", 0755) != 0 && errno != EEXIST) {
        printf("[APPS-INIT] /data/tmp could not be made (errno %d)\n", errno);
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
    FILE *apps = fopen("/etc/apps", "r");
    if (!apps) {
        printf("[APPS-INIT] /etc/apps could not be read (errno %d)\n", errno);
        return 1;
    }
    static char line[LINE];
    int commands = 0, failed = 0;
    while (fgets(line, sizeof(line), apps)) {
        if (line[0] == '\n' || line[0] == '#') {
            continue;
        }
        if (strncmp(line, "cd ", 3) == 0) {
            snprintf(directory, sizeof(directory), "%s", line + 3);
            directory[strcspn(directory, "\n")] = 0;
            printf("[APPS-INIT] in %s\n", directory);
            continue;
        }
        ++commands;
        failed += run(line, envp) != 0;
    }
    fclose(apps);
    printf("[APPS-INIT] %d commands, %d failed\n", commands, failed);
    return failed ? 1 : 0;
}
