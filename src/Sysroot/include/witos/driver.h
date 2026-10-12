#ifndef WITOS_DRIVER_H
#define WITOS_DRIVER_H
#include "witos/device.h"
#include "witos/types.h"

/* The driver protocol (plan step D1.2, ADR 0025): the messages on the channel the device manager grants a driver it
 * starts (witos/start.h). The device manager sends the start first: the device's descriptor in the bytes and, in the
 * one handle slot, the device handle (BIND, QUERY, DUPLICATE, TRANSFER), the device manager's last reference to the
 * device, so that the driver becomes its holder (ABI-1 1.2). The driver answers with a WitServiceStatus
 * (witos/manager.h) once it took the device, or with the errno value of what it could not do; a ready answer carries
 * in its one handle slot the endpoint of the service the driver offers, if any (D3.2: a block driver's block channel,
 * witos/block.h). A driver is a NativeAOT program of the boot package under /system/Drivers; nothing of the protocol is
 * an application API. */
#define WIT_DRIVER_VERSION 1U
#define WIT_DRIVER_START 1U /* the device moves to the driver */

typedef struct WitDriverStart {
    WitU32 Version, Size; /* WIT_DRIVER_VERSION, the size of this structure */
    WitU32 Operation; /* WIT_DRIVER_START */
    WitU32 Index; /* the descriptor's index in the device table */
    WitDeviceDescriptor Descriptor; /* the platform's descriptor, as the table holds it */
} WitDriverStart;

WIT_STATIC_ASSERT(sizeof(WitDriverStart) == 16 + WIT_DEVICE_DESCRIPTOR_SIZE, "Driver start");
#endif
