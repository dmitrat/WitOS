#define _GNU_SOURCE
#include <errno.h>
#include <spawn.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>

/* /bin/init of the scenarios that run ordinary .NET applications beside the device services (plan steps N1 and N2.1):
 * the root task starts it after the device manager and the namespace service answered, so the data disk's ext4 volume
 * is /data. It makes /data/tmp, the temporary directory the applications' files go to, since the boot package is
 * read-only and has no /tmp (TMPDIR, as Linux's programs take it); then it runs each command of /etc/apps in turn, one a
 * line, its words separated by spaces, waits for each and reports its exit. It exits with zero when every command
 * did. */

extern char **environ;

#define ENVIRONMENT 32
#define WORDS 16
#define LINE 512

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
    pid_t child = 0;
    int status = 0;
    const int error = posix_spawn(&child, argv[0], 0, 0, argv, envp);
    if (error || waitpid(child, &status, 0) != child) {
        printf("[APPS-INIT] %s did not start (errno %d)\n", argv[0], error ? error : errno);
        return -1;
    }
    const int code = WIFEXITED(status) ? WEXITSTATUS(status) : -1;
    printf("[APPS-INIT] %s %s exited with %d\n", argv[0], count > 1 ? argv[1] : "", code);
    return code;
}

int main(void)
{
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
    char line[LINE];
    int commands = 0, failed = 0;
    while (fgets(line, sizeof(line), apps)) {
        if (line[0] == '\n' || line[0] == '#') {
            continue;
        }
        ++commands;
        failed += run(line, envp) != 0;
    }
    fclose(apps);
    printf("[APPS-INIT] %d commands, %d failed\n", commands, failed);
    return failed ? 1 : 0;
}
