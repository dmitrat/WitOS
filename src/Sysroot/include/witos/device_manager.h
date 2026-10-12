#ifndef WITOS_DEVICE_MANAGER_H
#define WITOS_DEVICE_MANAGER_H
#include "witos/types.h"

/* The device manager's protocol (plan step D1.1, ADR 0025): the messages on the channel its creator granted it
 * (witos/start.h). The root task sends the start first, with the device table in the message's one handle slot (MAP,
 * QUERY, DUPLICATE, TRANSFER and ACQUIRE, as the kernel gave it), and keeps no device authority; the device manager
 * answers with a WitServiceStatus (witos/manager.h) once its devices are discovered. The device manager is the
 * NativeAOT program /system/DeviceManager of the boot package; nothing of the protocol is an application API. */
#define WIT_DEVICE_MANAGER_VERSION 1U
#define WIT_DEVICE_MANAGER_START 1U /* the device table moves to the device manager */
#define WIT_DEVICE_MANAGER_PATH "/system/DeviceManager"

typedef struct WitDeviceManagerMessage {
    WitU32 Version, Size; /* WIT_DEVICE_MANAGER_VERSION, the size of this structure */
    WitU32 Operation, Reserved;
} WitDeviceManagerMessage;

WIT_STATIC_ASSERT(sizeof(WitDeviceManagerMessage) == 16, "Device manager message");
#endif
