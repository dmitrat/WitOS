#include "witos/capabilities.h"
#include "witos/device_manager.h"
#include "witos/manager.h"
#include "witos/root.h"
#include "witos/spawn.h"
#include "witos/user_abi.h"
#include "witos/user_layout.h"
#include <stdio.h>
#include <string.h>
#include <unistd.h>

/* The root task of the system layer (plan step S6.1, RFC 0011 v3 sections 7.11 and 9.3): the first component the
 * kernel starts, a static program over the libc and libwitos. It runs the process manager: it starts /bin/init from
 * the boot package with the initial environment, serves the spawn requests of every process that descends from it,
 * and once /bin/init ends, stops the services it started (D6.1) and ends with /bin/init's status; the kernel ends every
 * remaining process with the root task. A root task that exits with anything but zero, or whose services did not
 * stop, is a failed boot. When the package has the device manager (D1.1, ADR
 * 0025), the root task starts it first and moves the device table to it over the channel it grants it, keeps no
 * device authority, and starts /bin/init once the device manager answered or ended. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#else
#define ISA_NAME "aarch64"
#endif

#define INIT "/bin/init"
#define STOP_SECONDS 60U /* how long the services may take to stop, in guest time */

/* The device manager's service: its channel, with the device table already sent over it; 0 when the package has no
 * device manager, an errno value when the channel or the table could not be prepared. The root task moves its own
 * handle of the table, with the rights the kernel gave it: HANDLE_DUPLICATE keeps ACQUIRE out of a memory object's
 * rights, and the root task keeps no device authority anyway. */
static int prepare_device_manager(witos_service *service)
{
    const WitRootStartup *startup = (const WitRootStartup *)WIT_USER_INFO;
    const WitDeviceManagerMessage start = {WIT_DEVICE_MANAGER_VERSION, sizeof(start), WIT_DEVICE_MANAGER_START, 0};
    const WitU64 table = startup->Handles[WIT_ROOT_HANDLE_DEVICES];
    WitU64 mine = 0, theirs = 0;
    memset(service, 0, sizeof(*service));
    if (access(WIT_DEVICE_MANAGER_PATH, F_OK) != 0) {
        return 0;
    }
    int error = witos_channel_create(&mine, &theirs);
    if (!error) {
        error = witos_channel_send(mine, &start, sizeof(start), &table, 1); /* the device manager holds it now */
    }
    if (error) {
        witos_handle_close(mine);
        witos_handle_close(theirs);
        return error;
    }
    service->path = WIT_DEVICE_MANAGER_PATH;
    service->grant = theirs;
    service->ready = mine;
    return 0;
}

int main(void)
{
    static char *argv[] = {INIT, 0};
    /* .NET finds ICU's libraries in /usr/lib (R4.3), so a program without a setting of its own has culture data, as on
     * Linux; a package without them runs its programs with invariant globalization by their own configuration. */
    static char *envp[] = {"PATH=/bin:/usr/bin", 0};
    WitProcessInfo info;
    witos_service device_manager;
    int stopped = 0;
    memset(&info, 0, sizeof(info));
    const int prepared = prepare_device_manager(&device_manager);
    if (prepared) {
        printf("[ROOT-TASK] " WIT_DEVICE_MANAGER_PATH " could not be prepared: %s\n", strerror(prepared));
    }
    const WitU32 services = device_manager.path ? 1 : 0;
    printf("[ROOT-TASK] process manager on " ISA_NAME ", starting %s" INIT "\n",
        services ? WIT_DEVICE_MANAGER_PATH " and " : "");
    const int error = witos_manager_run_services(&device_manager, services, INIT, argv, envp, &info);
    if (services) {
        if (device_manager.status) {
            printf("[ROOT-TASK] " WIT_DEVICE_MANAGER_PATH " was not ready: %s\n", strerror(device_manager.status));
        } else {
            printf("[ROOT-TASK] " WIT_DEVICE_MANAGER_PATH " was ready before " INIT "\n");
            /* The services stop before the root task ends, which would end them where they stand: the device manager
             * stops the namespace service, which unmounts its volumes, and the drivers (D6.1). */
            stopped = witos_manager_stop_services(&device_manager, services, STOP_SECONDS);
            printf(stopped ? "[ROOT-TASK] " WIT_DEVICE_MANAGER_PATH " did not stop: %s\n"
                           : "[ROOT-TASK] " WIT_DEVICE_MANAGER_PATH " stopped%s\n",
                stopped ? strerror(stopped) : "");
        }
        witos_handle_close(device_manager.ready);
        witos_handle_close(device_manager.process);
    }
    if (error) {
        printf("[ROOT-TASK] " INIT " could not start: %s\n", strerror(error));
        return 1;
    }
    if (info.State != WIT_PROCESS_STATE_EXITED) {
        printf("[ROOT-TASK] " INIT " ended by a fault\n");
        return 1;
    }
    printf("[ROOT-TASK] " INIT " exited with %llu\n", (unsigned long long)info.ExitCode);
    return info.ExitCode == 0 && !stopped ? 0 : 1;
}
