#ifndef WITOS_TIME_H
#define WITOS_TIME_H
#include "witos/types.h"

/* The time service's protocol (plan step H7.4): the message on the channel its creator granted it (witos/start.h). The
 * root task sends the start first, with the clock capability (WRITE, DUPLICATE and TRANSFER, as the kernel gave it,
 * K6) in the message's one handle slot, and keeps no other; the service answers with a WitServiceStatus
 * (witos/manager.h) once UTC holds the best time it has, and stops on a WitServiceStop. The root task starts it after
 * the device manager, so that it has the namespace's and the network service's endpoints, and only where it has work:
 * on a board without a real-time clock, or where the package names time servers (WIT_TIME_SERVERS). The service is the
 * NativeAOT program /system/Time of the boot package; nothing of the protocol is an application API. */
#define WIT_TIME_VERSION 1U
#define WIT_TIME_START 1U /* the clock capability moves to the time service */
#define WIT_TIME_PATH "/system/Time"
#define WIT_TIME_SERVERS "/etc/witos/time-servers" /* the SNTP servers, one "host" or "host:port" a line */

typedef struct WitTimeMessage {
    WitU32 Version, Size; /* WIT_TIME_VERSION, the size of this structure */
    WitU32 Operation, Reserved;
} WitTimeMessage;

WIT_STATIC_ASSERT(sizeof(WitTimeMessage) == 16, "Time service message");
#endif
