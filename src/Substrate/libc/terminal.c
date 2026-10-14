#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/channels.h"
#include "witos/memory_object.h"
#include "witos/terminal.h"
#include "witos/wait_objects.h"
#include <errno.h>
#include <poll.h>
#include <signal.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/prctl.h>
#include <termios.h>

/* The standard streams over the terminal service (plan step N4.3b, RFC 0020 sections 6 and 8). A process whose start
 * message carried the terminal's clients' endpoint (witos/terminal.h) reads its standard input from the terminal and
 * writes its standard output and error to it; without one, the streams stay the kernel log and the empty input (R4.1,
 * syscall.c). The libc keeps nothing of the discipline: a read asks the service for what the discipline gives (a line
 * in canonical mode, VMIN's bytes otherwise) and waits for it, a write hands the bytes to the service, and the termios
 * calls (tcgetattr, tcsetattr, tcflush) and TIOCGWINSZ are the service's attributes and size, copied as they are,
 * since the service keeps Linux's generic values. Two sessions serve the process: the reader's, whose reads wait and
 * which takes the events, and the control session for the writes, the attributes and poll, so that a write never
 * waits for a read. Each session serves one request at a time: a thread takes the session's Busy word, fills the
 * window, sends, waits on the channel without any library lock held (a wait never holds one, S3) and takes the reply;
 * an activation that interrupts the wait ran its handler, and the request goes on, as a read restarted after a
 * handler does, unless the service answered EINTR for a signal character. The terminal thread, started with the
 * reader's session as the reaper is (process.c), waits on the events channel: input bumps the poll word (pipe.c), so
 * that a parked poll looks again, and a signal character raises its signal on the thread itself, so that a handler
 * runs there, as Linux runs it on some thread that does not block the signal. */

#define WINDOW WIT_TERMINAL_WINDOW_BYTES
#define WINDOW_RIGHTS (WIT_RIGHT_MAP | WIT_RIGHT_WRITE | WIT_RIGHT_TRANSFER)
#define FUTEX_WAIT 0
#define FUTEX_WAKE 1
#define FUTEX_PRIVATE 128
#define NOT_A_TERMINAL_ERROR (-EIO)

typedef struct Session {
    volatile int Busy; /* a request in flight: the next request's thread waits on this word */
    WitU64 Channel; /* the library's end of the session channel */
    WitU64 Events; /* the library's end of the events channel; zero for the control session */
    WitU64 Object; /* the window's memory object */
    unsigned char *Window; /* the window, mapped */
    int Connected;
} Session;

static Session reader, control;
static volatile int connect_lock;

static void close_handle(WitU64 handle)
{
    WitU64 result = 0;
    if (handle) {
        wit_syscall(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, &result);
    }
}

int __wit_terminal_present(void)
{
    return __wit_process.Terminal != 0;
}

/* The poll word's bump and the signals of the events the service sends: the terminal thread. */
static void *events_thread(void *argument)
{
    const unsigned long raised =
        (1UL << (SIGINT - 1)) | (1UL << (SIGQUIT - 1)) | (1UL << (SIGWINCH - 1)) | (1UL << (SIGHUP - 1));
    WitTerminalEvent event;
    WitChannelMessage message;
    WitUserWaitRequest wait;
    WitU64 result = 0;
    (void)argument;
    __wit_rt_sigprocmask(SIG_UNBLOCK, &raised, 0, 8); /* the signals are raised here: their handlers run here */
    __wit_prctl(PR_SET_NAME, (unsigned long)"wit-terminal");
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)&event;
    message.Bytes = sizeof(event);
    memset(&wait, 0, sizeof(wait));
    wait.Version = WIT_WAIT_OBJECTS_VERSION;
    wait.Size = sizeof(wait);
    wait.Handles = (WitU64)&reader.Events;
    wait.Count = 1;
    wait.Deadline = WIT_WAIT_INFINITE;
    for (;;) {
        WitU64 status;
        while ((status = wit_syscall(WIT_CALL_CHANNEL_RECEIVE, reader.Events, (WitU64)&message, sizeof(message),
                    &result)) == WIT_STATUS_OK) {
            if ((result & 0xFFFFFFFFULL) != sizeof(event) ||
                event.Version != WIT_TERMINAL_VERSION ||
                event.Size != sizeof(event)) {
                continue;
            }
            switch (event.Kind) {
            case WIT_TERMINAL_EVENT_INPUT:
                __wit_poll_bump();
                break;
            case WIT_TERMINAL_EVENT_INTERRUPT:
                __wit_tkill((int)__wit_gettid(), SIGINT);
                break;
            case WIT_TERMINAL_EVENT_QUIT:
                __wit_tkill((int)__wit_gettid(), SIGQUIT);
                break;
            case WIT_TERMINAL_EVENT_SIZE:
                __wit_tkill((int)__wit_gettid(), SIGWINCH);
                break;
            case WIT_TERMINAL_EVENT_HANGUP:
                __wit_tkill((int)__wit_gettid(), SIGHUP);
                break;
            default:
                break;
            }
        }
        if (status != WIT_STATUS_TIMED_OUT) {
            return 0; /* the service went */
        }
        status = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
        if (status != WIT_STATUS_OK && status != WIT_STATUS_INTERRUPTED) {
            return 0;
        }
    }
}

/* A session connected: its channels, its window and, for the reader's, the events channel and the terminal thread. */
static long connect_session(Session *s, int events)
{
    WitU64 session[2] = {0, 0}, channel[2] = {0, 0}, handles[3] = {0, 0, 0}, result = 0, status;
    WitTerminalRequest request;
    WitChannelMessage message;
    WitMemoryMapRequest map;
    long error = 0;
    __wit_lock(&connect_lock);
    if (s->Connected) {
        __wit_unlock(&connect_lock);
        return 0;
    }
    status = wit_syscall(WIT_CALL_CHANNEL_CREATE, (WitU64)session, 0, 0, &result);
    if (status == WIT_STATUS_OK && events) {
        status = wit_syscall(WIT_CALL_CHANNEL_CREATE, (WitU64)channel, 0, 0, &result);
    }
    if (status == WIT_STATUS_OK) {
        status = wit_syscall(WIT_CALL_MEMORY_OBJECT_CREATE, WINDOW, 0, 0, &s->Object);
    }
    if (status == WIT_STATUS_OK) {
        memset(&map, 0, sizeof(map));
        map.Version = WIT_MEMORY_MAP_VERSION;
        map.Size = sizeof(map);
        map.Object = s->Object;
        map.Bytes = WINDOW;
        map.Protection = WIT_MEMORY_READ | WIT_MEMORY_WRITE;
        map.Target = WIT_PROCESS_SELF;
        status = wit_syscall(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&map, sizeof(map), 0, &result);
        s->Window = status == WIT_STATUS_OK ? (unsigned char *)result : 0;
    }
    if (status == WIT_STATUS_OK) {
        status = wit_syscall(WIT_CALL_HANDLE_DUPLICATE, s->Object, (WitU64)&handles[1], WINDOW_RIGHTS, &result);
    }
    if (status == WIT_STATUS_OK) {
        handles[0] = session[1];
        handles[2] = channel[1];
        memset(&request, 0, sizeof(request));
        request.Version = WIT_TERMINAL_VERSION;
        request.Size = sizeof(request);
        request.Operation = WIT_TERMINAL_CONNECT;
        request.Length = WINDOW;
        memset(&message, 0, sizeof(message));
        message.Version = WIT_CHANNEL_MESSAGE_VERSION;
        message.Size = sizeof(message);
        message.Data = (WitU64)&request;
        message.Bytes = sizeof(request);
        message.Handles = (WitU64)handles;
        message.HandleCount = events ? 3 : 2;
        status = wit_syscall(WIT_CALL_CHANNEL_SEND, __wit_process.Terminal, (WitU64)&message, sizeof(message), &result);
        if (status == WIT_STATUS_OK) {
            handles[0] = handles[1] = handles[2] = 0; /* moved */
        }
    }
    if (status != WIT_STATUS_OK) {
        error = __wit_errno(status);
    } else if (events) {
        s->Events = channel[0];
        error = __wit_thread_start_detached(events_thread, 0) ? -EAGAIN : 0;
    }
    if (error) {
        close_handle(handles[1]);
        close_handle(session[0]);
        close_handle(session[1]);
        close_handle(channel[0]);
        close_handle(channel[1]);
        if (s->Window) {
            wit_syscall(WIT_CALL_MEMORY_RELEASE, (WitU64)s->Window, 0, 0, &result);
            s->Window = 0;
        }
        close_handle(s->Object);
        s->Object = 0;
        s->Events = 0;
        __wit_unlock(&connect_lock);
        return error;
    }
    s->Channel = session[0];
    s->Connected = 1;
    __wit_unlock(&connect_lock);
    return 0;
}

/* The session's turn: one request at a time, the window the request's meanwhile. */
static long acquire(Session *s, int events)
{
    const long connected = connect_session(s, events);
    if (connected < 0) {
        return connected;
    }
    while (__atomic_exchange_n(&s->Busy, 1, __ATOMIC_ACQUIRE)) {
        __wit_futex(&s->Busy, FUTEX_WAIT | FUTEX_PRIVATE, 1, 0, 0, 0);
    }
    return 0;
}

static void release(Session *s)
{
    __atomic_store_n(&s->Busy, 0, __ATOMIC_RELEASE);
    __wit_futex(&s->Busy, FUTEX_WAKE | FUTEX_PRIVATE, 1, 0, 0, 0);
}

/* The request sent and its reply received, the wait between them without any lock: 0, or -EIO when the service went
 * or broke the protocol; the reply's own error stays the caller's to read. */
static long exchange(Session *s, WitU32 operation, WitU32 flags, WitU64 length, WitU64 offset, WitTerminalReply *reply)
{
    WitTerminalRequest request;
    WitChannelMessage message;
    WitUserWaitRequest wait;
    WitU64 result = 0, status;
    memset(&request, 0, sizeof(request));
    request.Version = WIT_TERMINAL_VERSION;
    request.Size = sizeof(request);
    request.Operation = operation;
    request.Flags = flags;
    request.Length = length;
    request.Offset = offset;
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)&request;
    message.Bytes = sizeof(request);
    status = wit_syscall(WIT_CALL_CHANNEL_SEND, s->Channel, (WitU64)&message, sizeof(message), &result);
    if (status != WIT_STATUS_OK) {
        return NOT_A_TERMINAL_ERROR;
    }
    memset(&wait, 0, sizeof(wait));
    wait.Version = WIT_WAIT_OBJECTS_VERSION;
    wait.Size = sizeof(wait);
    wait.Handles = (WitU64)&s->Channel;
    wait.Count = 1;
    wait.Deadline = WIT_WAIT_INFINITE;
    message.Data = (WitU64)reply;
    message.Bytes = sizeof(*reply);
    while ((status = wit_syscall(WIT_CALL_CHANNEL_RECEIVE, s->Channel, (WitU64)&message, sizeof(message), &result)) ==
        WIT_STATUS_TIMED_OUT) {
        status = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
        if (status != WIT_STATUS_OK && status != WIT_STATUS_INTERRUPTED) {
            return NOT_A_TERMINAL_ERROR;
        }
    }
    if (status != WIT_STATUS_OK ||
        (result & 0xFFFFFFFFULL) != sizeof(*reply) ||
        reply->Version != WIT_TERMINAL_VERSION ||
        reply->Size != sizeof(*reply) ||
        reply->Operation != operation) {
        return NOT_A_TERMINAL_ERROR;
    }
    return 0;
}

/* read of the standard input: what the discipline gives, waited for; 0 at the end of the file; -EINTR when a signal
 * character came first. */
long __wit_terminal_read(void *buffer, long bytes)
{
    WitTerminalReply reply;
    if (bytes == 0) {
        return 0;
    }
    if (__wit_cancel_requested()) {
        return -EINTR; /* a cancellation point about to wait with its cancel word set (thread.c) */
    }
    long error = acquire(&reader, 1);
    if (error < 0) {
        return error;
    }
    const WitU64 limit = (WitU64)bytes < WINDOW ? (WitU64)bytes : WINDOW;
    error = exchange(&reader, WIT_TERMINAL_READ, 0, limit, 0, &reply);
    if (!error && !reply.Error && reply.Length) {
        memcpy(buffer, reader.Window, reply.Length);
    }
    release(&reader);
    return error ? error : reply.Error ? -(long)reply.Error : (long)reply.Length;
}

/* write of a standard output: the bytes through the discipline to the device, in pieces of the window. */
long __wit_terminal_write(const void *buffer, long bytes)
{
    WitTerminalReply reply;
    long done = 0;
    while (done < bytes) {
        const WitU64 piece = (WitU64)(bytes - done) < WINDOW ? (WitU64)(bytes - done) : WINDOW;
        long error = acquire(&control, 0);
        if (error < 0) {
            return done ? done : error;
        }
        memcpy(control.Window, (const unsigned char *)buffer + done, piece);
        error = exchange(&control, WIT_TERMINAL_WRITE, 0, piece, 0, &reply);
        release(&control);
        if (error || reply.Error) {
            return done ? done : error ? error : -(long)reply.Error;
        }
        done += (long)reply.Length;
        if (reply.Length < piece) {
            break;
        }
    }
    return done;
}

/* A request without bytes of its own on the control session: the reply's error as a negative errno. */
static long control_request(WitU32 operation, WitU32 flags, WitU64 length, WitTerminalReply *reply)
{
    long error = acquire(&control, 0);
    if (error < 0) {
        return error;
    }
    error = exchange(&control, operation, flags, length, 0, reply);
    release(&control);
    return error ? error : reply->Error ? -(long)reply->Error : 0;
}

/* The termios calls and the size (musl's ioctl requests): the attributes copied as they are, since the service keeps
 * Linux's generic values, which musl's termios holds on both ISAs. */
long __wit_terminal_ioctl(long stream, long request, void *argument)
{
    WitTerminalReply reply;
    long error;
    switch (request) {
    case TCGETS: {
        struct termios *attributes = argument;
        error = acquire(&control, 0);
        if (error < 0) {
            return error;
        }
        error = exchange(&control, WIT_TERMINAL_GET_ATTRIBUTES, 0, sizeof(WitTerminalAttributes), 0, &reply);
        if (!error && !reply.Error) {
            const WitTerminalAttributes *got = (const WitTerminalAttributes *)control.Window;
            memset(attributes, 0, sizeof(*attributes));
            attributes->c_iflag = got->Input;
            attributes->c_oflag = got->Output;
            attributes->c_cflag = got->Control;
            attributes->c_lflag = got->Local;
            memcpy(attributes->c_cc, got->Characters, NCCS < WIT_TERMINAL_CONTROLS ? NCCS : WIT_TERMINAL_CONTROLS);
        }
        release(&control);
        return error ? error : reply.Error ? -(long)reply.Error : 0;
    }
    case TCSETS:
    case TCSETSW:
    case TCSETSF: {
        const struct termios *attributes = argument;
        const WitU32 when = request == TCSETS ? WIT_TERMINAL_NOW
            : request == TCSETSW              ? WIT_TERMINAL_AFTER_OUTPUT
                                              : WIT_TERMINAL_AFTER_FLUSH;
        error = acquire(&control, 0);
        if (error < 0) {
            return error;
        }
        WitTerminalAttributes *set = (WitTerminalAttributes *)control.Window;
        memset(set, 0, sizeof(*set));
        set->Input = attributes->c_iflag;
        set->Output = attributes->c_oflag;
        set->Control = attributes->c_cflag;
        set->Local = attributes->c_lflag;
        memcpy(set->Characters, attributes->c_cc, NCCS < WIT_TERMINAL_CONTROLS ? NCCS : WIT_TERMINAL_CONTROLS);
        error = exchange(&control, WIT_TERMINAL_SET_ATTRIBUTES, when, sizeof(WitTerminalAttributes), 0, &reply);
        release(&control);
        return error ? error : reply.Error ? -(long)reply.Error : 0;
    }
    case TIOCGWINSZ: {
        struct winsize *size = argument;
        error = control_request(WIT_TERMINAL_GET_SIZE, 0, 0, &reply);
        if (error) {
            return error;
        }
        size->ws_row = reply.Rows;
        size->ws_col = reply.Columns;
        size->ws_xpixel = 0;
        size->ws_ypixel = 0;
        return 0;
    }
    case TCFLSH: {
        const long queue = (long)argument;
        if (queue != TCIFLUSH && queue != TCOFLUSH && queue != TCIOFLUSH) {
            return -EINVAL;
        }
        return control_request(WIT_TERMINAL_FLUSH,
            (queue == TCOFLUSH ? 0 : WIT_TERMINAL_INPUT) | (queue == TCIFLUSH ? 0 : WIT_TERMINAL_OUTPUT), 0, &reply);
    }
    case FIONREAD:
        if (stream != 0) {
            *(int *)argument = 0;
            return 0;
        }
        error = control_request(WIT_TERMINAL_POLL, 0, 0, &reply);
        if (error) {
            return error;
        }
        *(int *)argument =
            (reply.Flags & WIT_TERMINAL_INPUT_READY) ? 1 : 0; /* a line or a byte waits; how many is the service's */
        return 0;
    case TCSBRK:
    case TCXONC:
        return 0; /* no break, no flow control: nothing to do and nothing refused */
    default:
        return -ENOTTY;
    }
}

/* poll's readiness of a standard stream (syscall.c): the input when the discipline would give something now, an output
 * always. */
short __wit_terminal_poll(long stream, short events)
{
    WitTerminalReply reply;
    if (stream != 0) {
        return (short)(events & (POLLOUT | POLLWRNORM));
    }
    if (control_request(WIT_TERMINAL_POLL, 0, 0, &reply) < 0) {
        return POLLERR;
    }
    return (reply.Flags & WIT_TERMINAL_INPUT_READY) ? (short)(events & (POLLIN | POLLRDNORM)) : 0;
}
