#ifndef WITOS_INPUT_H
#define WITOS_INPUT_H
#include "witos/types.h"

/* The input protocol (plan step C2.2, ADR 0025): what the driver of an input device — the virtio keyboard,
 * /system/Drivers/VirtioInput — serves on the channel whose other end moves with its ready answer (witos/driver.h), to
 * the one client that takes it: the display service, whose terminal the keys reach. The client attaches a window first
 * (ATTACH: a duplicate of a memory object of its own in the one handle slot, Length its bytes), then keeps one READ
 * outstanding: the driver answers it with the events the device reported, into the window at Window, each a
 * WitInputEvent of 8 bytes, at least one and at most Length / 8 of them, once there are any — unless NONBLOCK asks for
 * an answer at once with what there is. The events carry Linux's evdev values: a key's code (KEY_*) with the value 1
 * for down, 0 for up and 2 for a repeat; a synchronization event ends a report. The driver keeps at most WIT_INPUT_QUEUE
 * events for a client that reads slowly and drops the oldest beyond them. Errors are errno values as the serial
 * protocol's: EINVAL for a request the driver cannot serve as it is, EOPNOTSUPP for an unknown operation, EPROTO for
 * bytes that are no request of this version, EBUSY for a second READ while one waits. Nothing of the protocol is an
 * application API: applications see Console.ReadKey through the terminal. */
#define WIT_INPUT_VERSION 1U
#define WIT_INPUT_ATTACH 1U /* the window, in the one handle slot (MAP, WRITE, TRANSFER); Length its bytes */
#define WIT_INPUT_READ 2U /* up to Length / 8 events into the window at Window */

#define WIT_INPUT_NONBLOCK 1U /* READ: answer at once, with no events when none came */

#define WIT_INPUT_WINDOW_BYTES 4096U /* the window: one page */
#define WIT_INPUT_QUEUE 256U /* the events the driver keeps for a client that reads slowly */
#define WIT_INPUT_EVENT_BYTES 8U

/* Event types, as evdev's. */
#define WIT_INPUT_SYN 0U
#define WIT_INPUT_KEY 1U

typedef struct WitInputRequest {
    WitU32 Version, Size; /* WIT_INPUT_VERSION, the size of this structure */
    WitU32 Operation, Flags;
    WitU64 Length; /* ATTACH: the window's bytes; READ: the bytes the events may fill */
    WitU64 Window; /* the offset of the events in the window */
} WitInputRequest;

typedef struct WitInputReply {
    WitU32 Version, Size; /* WIT_INPUT_VERSION, the size of this structure */
    WitU32 Operation, Error; /* the request's operation; zero or an errno value */
    WitU64 Length; /* the bytes of the events written; ATTACH: the window's */
    WitU64 Reserved;
} WitInputReply;

typedef struct WitInputEvent {
    WitU16 Type, Code; /* WIT_INPUT_KEY and the key's code */
    WitU32 Value; /* 1 down, 0 up, 2 repeat */
} WitInputEvent;

WIT_STATIC_ASSERT(sizeof(WitInputRequest) == 32, "Input request");
WIT_STATIC_ASSERT(sizeof(WitInputReply) == 32, "Input reply");
WIT_STATIC_ASSERT(sizeof(WitInputEvent) == WIT_INPUT_EVENT_BYTES, "Input event");
#endif
