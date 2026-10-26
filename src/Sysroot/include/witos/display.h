#ifndef WITOS_DISPLAY_H
#define WITOS_DISPLAY_H
#include "witos/device.h"
#include "witos/types.h"

/* The display service's start (plan step C2.2, RFC 0020 section 28, RFC 0009 section 26): /system/Display is the
 * console on the firmware's framebuffer — a grid of cells drawn with a bitmap font, a terminal emulator behind them and
 * the keys of the input driver — which the device manager starts as it starts a driver (witos/driver.h): the
 * framebuffer's descriptor in the bytes and, in the handle slots, the framebuffer device (slot 0, the device manager's
 * last reference to it, so that the service becomes its holder) and the keyboard drivers' input channels (slots 1 and
 * 2, witos/input.h, whose keys the service merges; H5.2: a PC's PS/2 keyboard beside its USB keyboards) when the board
 * has keyboards. The service answers with a WitServiceStatus whose one handle is the end
 * of a channel on which it serves the serial protocol (witos/serial.h): WRITE paints the bytes as a terminal shows them,
 * READ gives the keys as the bytes a terminal sends (the characters of a US layout until the input model of M9, xterm's
 * sequences for the keys that are none, and the answers to the queries the emulator serves), SIZE the grid — so that the
 * terminal service drives the display as it drives the virtio console, as one of its consoles. Nothing of the protocol
 * is an application API. */
#define WIT_DISPLAY_VERSION 1U
#define WIT_DISPLAY_START 1U /* the framebuffer moves to the service, the keyboards' channels with it */
#define WIT_DISPLAY_KEYBOARDS 2U /* the keyboards' channels a start carries at most (H5.2) */
#define WIT_DISPLAY_PATH "/system/Display"
#define WIT_DISPLAY_FONT "/system/fonts/spleen-8x16.bdf" /* the bitmap font of the boot package, by its pin */

typedef struct WitDisplayStart {
    WitU32 Version, Size; /* WIT_DISPLAY_VERSION, the size of this structure */
    WitU32 Operation; /* WIT_DISPLAY_START */
    WitU32 Index; /* the framebuffer descriptor's index in the device table */
    WitDeviceDescriptor Descriptor; /* the platform's descriptor, as the table holds it */
} WitDisplayStart;

WIT_STATIC_ASSERT(sizeof(WitDisplayStart) == 16 + WIT_DEVICE_DESCRIPTOR_SIZE, "Display start");
#endif
