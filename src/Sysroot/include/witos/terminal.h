#ifndef WITOS_TERMINAL_H
#define WITOS_TERMINAL_H
#include "witos/types.h"

/* The terminal service's protocol (plan step N4.3, RFC 0020 sections 6 and 8). /system/Terminal owns the one terminal:
 * the console driver's serial channel the device manager gives it before it starts (WIT_TERMINAL_DEVICE,
 * witos/serial.h) and the line discipline — the attributes a termios holds, canonical and raw input, echo, the signal
 * characters, output processing and the size. Its clients hold a duplicate of one endpoint with SEND alone, which the
 * process manager gives every process it starts after the service answered ready (witos/start.h, WIT_START_TERMINAL).
 * A client connects once: CONNECT on that endpoint with three handles, the end of a session channel, the end of an
 * events channel and a window of its own (MAP, WRITE, TRANSFER) of WIT_TERMINAL_WINDOW_BYTES; then it sends one
 * request at a time on the session and receives each reply there, while events arrive on the events channel whenever
 * the service has something to tell. Bytes travel in the window. An error is an errno value. The attributes' flags and
 * control characters carry Linux's generic termios values, which both ISAs share and the libc copies as they are; the
 * libc keeps nothing of the discipline. Every process that connects shares the terminal: a line goes to whoever reads
 * it first, a signal character to every session, as a Linux session without job control sees them. Nothing of the
 * protocol is an application API: applications see System.Console through the libc. */
#define WIT_TERMINAL_VERSION 1U
#define WIT_TERMINAL_PATH "/system/Terminal"

/* On the clients' endpoint. */
#define WIT_TERMINAL_CONNECT 1U /* the session's end, the events' end and the window in the handle slots */
/* On a session. */
#define WIT_TERMINAL_READ \
    2U /* up to Length bytes into the window at Offset, as the discipline gives them: a line, or nothing at the end of
        * the file, in canonical mode, VMIN and VTIME's bytes otherwise; NONBLOCK answers at once, EAGAIN when nothing
        * is there; EINTR when a signal character came first; the reply's Length the bytes */
#define WIT_TERMINAL_WRITE 3U /* Length bytes of the window at Offset through output processing to the device */
#define WIT_TERMINAL_GET_ATTRIBUTES 4U /* a WitTerminalAttributes into the window at Offset */
#define WIT_TERMINAL_SET_ATTRIBUTES 5U /* from a WitTerminalAttributes in the window at Offset; Flags say when */
#define WIT_TERMINAL_GET_SIZE 6U /* the reply's Columns and Rows */
#define WIT_TERMINAL_POLL 7U /* the reply's Flags: INPUT_READY when a READ would answer at once, OUTPUT_READY always */
#define WIT_TERMINAL_FLUSH \
    8U /* Flags INPUT: what was typed and not read is dropped; OUTPUT: nothing, writes complete */
#define WIT_TERMINAL_DRAIN \
    9U /* until every write reached the device: at once, since a write completes before its reply */
#define WIT_TERMINAL_DISCONNECT 10U /* the session ends; closing the session's end does the same */
/* The device manager's message on the channel it granted the service, before the service answers ready: the console
 * driver's serial channel in the one handle slot. */
#define WIT_TERMINAL_DEVICE 16U

#define WIT_TERMINAL_NONBLOCK 1U /* READ */
#define WIT_TERMINAL_INPUT 1U /* FLUSH */
#define WIT_TERMINAL_OUTPUT 2U /* FLUSH */
#define WIT_TERMINAL_INPUT_READY 1U /* POLL's reply */
#define WIT_TERMINAL_OUTPUT_READY 2U /* POLL's reply */
/* SET_ATTRIBUTES' Flags: tcsetattr's optional actions. */
#define WIT_TERMINAL_NOW 0U /* TCSANOW */
#define WIT_TERMINAL_AFTER_OUTPUT 1U /* TCSADRAIN */
#define WIT_TERMINAL_AFTER_FLUSH 2U /* TCSAFLUSH: the input not read is dropped too */

#define WIT_TERMINAL_WINDOW_BYTES (4U * 4096U) /* a client's window */
#define WIT_TERMINAL_CONTROLS 32U /* termios' NCCS */

typedef struct WitTerminalRequest {
    WitU32 Version, Size; /* WIT_TERMINAL_VERSION, the size of this structure */
    WitU32 Operation, Flags;
    WitU64 Length; /* bytes */
    WitU64 Offset; /* in the window */
} WitTerminalRequest;

typedef struct WitTerminalReply {
    WitU32 Version, Size; /* WIT_TERMINAL_VERSION, the size of this structure */
    WitU32 Operation, Error; /* the request's operation; zero or an errno value */
    WitU64 Length; /* READ and WRITE: the bytes */
    WitU32 Flags; /* POLL: the readiness */
    WitU16 Columns, Rows; /* GET_SIZE */
} WitTerminalReply;

/* The discipline's attributes: termios' flag words and control characters with Linux's generic values. */
typedef struct WitTerminalAttributes {
    WitU32 Input, Output, Control, Local; /* c_iflag, c_oflag, c_cflag, c_lflag */
    WitU8 Characters[WIT_TERMINAL_CONTROLS]; /* c_cc */
} WitTerminalAttributes;

/* An event on the events channel: something the service tells a client that asked for nothing. */
#define WIT_TERMINAL_EVENT_INPUT 1U /* bytes arrived: a READ or a POLL sees them */
#define WIT_TERMINAL_EVENT_INTERRUPT 2U /* the interrupt character with ISIG: SIGINT */
#define WIT_TERMINAL_EVENT_QUIT 3U /* the quit character with ISIG: SIGQUIT */
#define WIT_TERMINAL_EVENT_SIZE 4U /* the size changed: SIGWINCH */
#define WIT_TERMINAL_EVENT_HANGUP 5U /* the device went: SIGHUP */

typedef struct WitTerminalEvent {
    WitU32 Version, Size; /* WIT_TERMINAL_VERSION, the size of this structure */
    WitU32 Kind, Value; /* WIT_TERMINAL_EVENT_*; zero */
} WitTerminalEvent;

WIT_STATIC_ASSERT(sizeof(WitTerminalRequest) == 32, "Terminal request");
WIT_STATIC_ASSERT(sizeof(WitTerminalReply) == 32, "Terminal reply");
WIT_STATIC_ASSERT(sizeof(WitTerminalAttributes) == 48, "Terminal attributes");
WIT_STATIC_ASSERT(sizeof(WitTerminalEvent) == 16, "Terminal event");
#endif
