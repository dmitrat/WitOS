#include "witos/spawn.h"
#include <stdio.h>
#include <string.h>

/* The root task of the system layer (plan step S6.1, RFC 0011 v3 sections 7.11 and 9.3): the first component the
 * kernel starts, a static program over the libc and libwitos. It runs the process manager: it starts /bin/init from
 * the boot package with the initial environment, serves the spawn requests of every process that descends from it,
 * and ends with /bin/init's status once /bin/init ends; the kernel ends every remaining process with the root task. A
 * root task that exits with anything but zero is a failed boot. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#else
#define ISA_NAME "aarch64"
#endif

#define INIT "/bin/init"

int main(void)
{
    static char *argv[] = {INIT, 0};
    static char *envp[] = {"PATH=/bin:/usr/bin", 0};
    WitProcessInfo info;
    memset(&info, 0, sizeof(info));
    printf("[ROOT-TASK] process manager on " ISA_NAME ", starting " INIT "\n");
    const int error = witos_manager_run(INIT, argv, envp, &info);
    if (error) {
        printf("[ROOT-TASK] " INIT " could not start: %s\n", strerror(error));
        return 1;
    }
    if (info.State != WIT_PROCESS_STATE_EXITED) {
        printf("[ROOT-TASK] " INIT " ended by a fault\n");
        return 1;
    }
    printf("[ROOT-TASK] " INIT " exited with %llu\n", (unsigned long long)info.ExitCode);
    return info.ExitCode == 0 ? 0 : 1;
}
