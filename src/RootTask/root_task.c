#include "witos/capabilities.h"
#include "witos/device_manager.h"
#include "witos/limits.h"
#include "witos/manager.h"
#include "witos/memory.h"
#include "witos/root.h"
#include "witos/spawn.h"
#include "witos/time.h"
#include "witos/user_abi.h"
#include "witos/user_layout.h"
#include <stdio.h>
#include <string.h>
#include <sys/sysinfo.h>
#include <unistd.h>

/* The root task of the system layer (plan step S6.1, RFC 0011 v3 sections 7.11 and 9.3): the first component the
 * kernel starts, a static program over the libc and libwitos. It runs the process manager: it starts /bin/init from
 * the boot package with the initial environment, serves the spawn requests of every process that descends from it,
 * and once /bin/init ends, stops the services it started (D6.1) and ends with /bin/init's status; the kernel ends every
 * remaining process with the root task. A root task that exits with anything but zero, or whose services did not
 * stop, is a failed boot. When the package has the device manager (D1.1, ADR
 * 0025), the root task starts it first and moves the device table to it over the channel it grants it, keeps no
 * device authority, and starts /bin/init once the device manager answered or ended. Where the system keeps time (H7.4:
 * a board without a real-time clock, or a package that names time servers), the time service follows the device
 * manager with the clock capability, which the root task gives on and keeps no more, so that UTC holds the best time
 * the system has before /bin/init starts. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#elif defined(__aarch64__)
#define ISA_NAME "aarch64"
#else
#define ISA_NAME "riscv64"
#endif

#define INIT "/bin/init"
#define STOP_SECONDS 60U /* how long the services may take to stop, in guest time */

/* The default environment of every process (N4.1): the KEY=VALUE lines of /etc/environment, which the install writes
 * into the package, or PATH alone when the package has none; a comment line starts with '#'. */
#define ENVIRONMENT_FILE "/etc/environment"
#define ENVIRONMENT_ENTRIES 32U
#define ENVIRONMENT_BYTES 4096U
static char environment_bytes[ENVIRONMENT_BYTES];
static char *envp[ENVIRONMENT_ENTRIES + 2];

static WitU32 read_environment(void)
{
    WitU32 count = 0;
    FILE *file = fopen(ENVIRONMENT_FILE, "r");
    if (file) {
        char *at = environment_bytes;
        while (count < ENVIRONMENT_ENTRIES &&
            at < environment_bytes + ENVIRONMENT_BYTES - 2 &&
            fgets(at, (int)(environment_bytes + ENVIRONMENT_BYTES - at), file)) {
            const size_t length = strcspn(at, "\r\n");
            at[length] = 0;
            if (length == 0 || at[0] == '#' || !strchr(at, '=')) {
                continue; /* the next line takes the same place */
            }
            envp[count++] = at;
            at += length + 1;
        }
        fclose(file);
    }
    if (count == 0) {
        envp[count++] = "PATH=/bin:/usr/bin";
    }
    return count;
}

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

/* The time service's (H7.4): its channel, with the clock capability already sent over it; 0 when the package has no
 * time service or the system has no time to keep (a board with a real-time clock and no time servers named), an errno
 * value when the channel could not be prepared. */
static int prepare_time(witos_service *service)
{
    const WitRootStartup *startup = (const WitRootStartup *)WIT_USER_INFO;
    const WitTimeMessage start = {WIT_TIME_VERSION, sizeof(start), WIT_TIME_START, 0};
    const WitU64 clock = startup->Handles[WIT_ROOT_HANDLE_CLOCK];
    WitU64 utc = 0, mine = 0, theirs = 0;
    memset(service, 0, sizeof(*service));
    if (access(WIT_TIME_PATH, F_OK) != 0 || (witos_clock_utc(&utc) == 0 && access(WIT_TIME_SERVERS, F_OK) != 0)) {
        return 0;
    }
    int error = witos_channel_create(&mine, &theirs);
    if (!error) {
        error = witos_channel_send(mine, &start, sizeof(start), &clock, 1); /* the time service holds it now */
    }
    if (error) {
        witos_handle_close(mine);
        witos_handle_close(theirs);
        return error;
    }
    service->path = WIT_TIME_PATH;
    service->grant = theirs;
    service->ready = mine;
    return 0;
}

int main(void)
{
    static char *argv[] = {INIT, 0};
    /* .NET finds ICU's libraries in /usr/lib (R4.3), so a program without a setting of its own has culture data, as on
     * Linux; a package without them runs its programs with invariant globalization by their own configuration. The GC
     * learns the memory a process may have (N2.2): the smaller of a system layer process's page quota and the memory
     * free when the system started, which .NET's own knob gives the GC as its physical memory, where a Linux container's
     * limit would, so that it keeps its heap to 75% of it and an allocation past the heap is an OutOfMemoryException,
     * not the end of the process. The knob turns GC.RefreshMemoryLimit off; without it, where the quota is above the
     * machine's memory, a process meets the machine's end before the GC's (upstream's GC test 494226 ended with
     * SIGABRT in a 512 MiB guest). */
    unsigned long long limit = (unsigned long long)WIT_PROCESS_PAGE_CAPACITY * (unsigned long long)WIT_PAGE_SIZE;
    struct sysinfo memory_info;
    if (sysinfo(&memory_info) == 0 && (unsigned long long)memory_info.freeram * memory_info.mem_unit < limit) {
        limit = (unsigned long long)memory_info.freeram * memory_info.mem_unit;
    }
    static char memory[64];
    snprintf(memory, sizeof(memory), "DOTNET_GCTotalPhysicalMemory=%llx", limit);
    WitU32 entries = read_environment();
    envp[entries++] = memory;
    envp[entries] = 0;
    WitProcessInfo info;
    witos_service services[2], prepared;
    WitU32 count = 0;
    int stopped = 0;
    memset(&info, 0, sizeof(info));
    memset(services, 0, sizeof(services));
    /* The device manager first, whose ready answer brings the endpoints the time service needs, then the time service. */
    int error = prepare_device_manager(&prepared);
    if (error) {
        printf("[ROOT-TASK] " WIT_DEVICE_MANAGER_PATH " could not be prepared: %s\n", strerror(error));
    } else if (prepared.path) {
        services[count++] = prepared;
    }
    error = prepare_time(&prepared);
    if (error) {
        printf("[ROOT-TASK] " WIT_TIME_PATH " could not be prepared: %s\n", strerror(error));
    } else if (prepared.path) {
        services[count++] = prepared;
    }
    printf("[ROOT-TASK] process manager on " ISA_NAME ", starting %s%s" INIT "\n", count ? services[0].path : "",
        count == 2  ? ", " WIT_TIME_PATH " and "
            : count ? " and "
                    : "");
    error = witos_manager_run_services(services, count, INIT, argv, envp, &info);
    int ready[2] = {0, 0};
    for (WitU32 i = 0; i < count; ++i) {
        ready[i] = !services[i].status;
        if (ready[i]) {
            printf("[ROOT-TASK] %s was ready before " INIT "\n", services[i].path);
        } else {
            printf("[ROOT-TASK] %s was not ready: %s\n", services[i].path, strerror(services[i].status));
        }
    }
    if (count) {
        /* The services stop before the root task ends, which would end them where they stand, the last first: the time
         * service keeps the time on /data (H7.4), then the device manager stops the namespace service, which unmounts
         * its volumes, and the drivers (D6.1). A service that was not ready is not asked. */
        stopped = witos_manager_stop_services(services, count, STOP_SECONDS);
        for (WitU32 i = count; i-- > 0;) {
            if (!ready[i]) {
                continue;
            }
            if (services[i].status) {
                printf("[ROOT-TASK] %s did not stop: %s\n", services[i].path, strerror(services[i].status));
            } else {
                printf("[ROOT-TASK] %s stopped\n", services[i].path);
            }
        }
    }
    for (WitU32 i = 0; i < count; ++i) {
        witos_handle_close(services[i].ready);
        witos_handle_close(services[i].process);
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
