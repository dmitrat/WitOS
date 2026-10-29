#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/channels.h"
#include "witos/memory_object.h"
#include "witos/terminal.h"
#include "witos/wait_objects.h"
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/prctl.h>
#include <sys/syscall.h>
#include <sys/uio.h>
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
 * runs there, as Linux runs it on some thread that does not block the signal.
 *
 * The process's terminal is the console, or the slave side of a pseudo-terminal when its creator started it there (E4.1:
 * a WitStreamMessage of WIT_STREAM_TERMINAL, start.c), whose number its sessions name in their CONNECT; it is the
 * process's controlling terminal, whose signals it raises. The pseudo-terminals' own descriptors follow below. */

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
static int process_pseudo = -1; /* the pseudo-terminal the process's standard streams are, -1 for the console (E4.1) */

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

/* The pseudo-terminal the creator started the process on (start.c): before any session connects. */
void __wit_terminal_assign(int number)
{
    process_pseudo = number;
}

long __wit_terminal_pseudo(void)
{
    return process_pseudo;
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

/* A session connected: its channels, its window and, with events, the events channel — and the terminal thread when
 * thread says so. The request is CONNECT (of the console, or of a pseudo-terminal's slave with PSEUDO and the number in
 * offset) or OPEN_PSEUDO; the service answers nothing, and a refusal closes the session's end, which the session's
 * first request meets. */
static long connect_session(Session *s, int events, int thread, WitU32 operation, WitU32 flags, WitU64 offset)
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
        request.Operation = operation;
        request.Flags = flags;
        request.Length = WINDOW;
        request.Offset = offset;
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
        error = thread && __wit_thread_start_detached(events_thread, 0) ? -EAGAIN : 0;
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
static void take_turn(Session *s)
{
    while (__atomic_exchange_n(&s->Busy, 1, __ATOMIC_ACQUIRE)) {
        __wit_futex(&s->Busy, FUTEX_WAIT | FUTEX_PRIVATE, 1, 0, 0, 0);
    }
}

/* The process terminal's session connected and its turn taken. */
static long acquire(Session *s, int events)
{
    const long connected = connect_session(s, events, events, WIT_TERMINAL_CONNECT,
        process_pseudo >= 0 ? WIT_TERMINAL_PSEUDO : 0, process_pseudo >= 0 ? (WitU64)process_pseudo : 0);
    if (connected < 0) {
        return connected;
    }
    take_turn(s);
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

/* A write in pieces of the window on a session already connected: the bytes moved, or the error when none moved. */
static long session_write(Session *s, const void *buffer, long bytes)
{
    WitTerminalReply reply;
    long done = 0;
    while (done < bytes) {
        const WitU64 piece = (WitU64)(bytes - done) < WINDOW ? (WitU64)(bytes - done) : WINDOW;
        take_turn(s);
        memcpy(s->Window, (const unsigned char *)buffer + done, piece);
        const long error = exchange(s, WIT_TERMINAL_WRITE, 0, piece, 0, &reply);
        release(s);
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

/* write of a standard output: the bytes through the discipline to the device, in pieces of the window. */
long __wit_terminal_write(const void *buffer, long bytes)
{
    const long connected = bytes > 0
        ? connect_session(&control, 0, 0, WIT_TERMINAL_CONNECT, process_pseudo >= 0 ? WIT_TERMINAL_PSEUDO : 0,
              process_pseudo >= 0 ? (WitU64)process_pseudo : 0)
        : 0;
    return connected < 0 ? connected : session_write(&control, buffer, bytes);
}

/* A request without bytes of its own on a session already connected: the reply's error as a negative errno. */
static long session_request(Session *s, WitU32 operation, WitU32 flags, WitU64 length, WitTerminalReply *reply)
{
    take_turn(s);
    const long error = exchange(s, operation, flags, length, 0, reply);
    release(s);
    return error ? error : reply->Error ? -(long)reply->Error : 0;
}

static long control_request(WitU32 operation, WitU32 flags, WitU64 length, WitTerminalReply *reply)
{
    const long connected = connect_session(&control, 0, 0, WIT_TERMINAL_CONNECT,
        process_pseudo >= 0 ? WIT_TERMINAL_PSEUDO : 0, process_pseudo >= 0 ? (WitU64)process_pseudo : 0);
    return connected < 0 ? connected : session_request(&control, operation, flags, length, reply);
}

/* The termios calls and the size (musl's ioctl requests) on a session already connected: the attributes copied as
 * they are, since the service keeps Linux's generic values, which musl's termios holds on every ISA; TIOCSWINSZ is a
 * pseudo-terminal's (E4.1), ENOTTY on the console. FIONREAD asks the service when input says the descriptor reads. */
static long session_ioctl(Session *s, int input, long request, void *argument)
{
    WitTerminalReply reply;
    long error;
    switch (request) {
    case TCGETS: {
        struct termios *attributes = argument;
        take_turn(s);
        error = exchange(s, WIT_TERMINAL_GET_ATTRIBUTES, 0, sizeof(WitTerminalAttributes), 0, &reply);
        if (!error && !reply.Error) {
            const WitTerminalAttributes *got = (const WitTerminalAttributes *)s->Window;
            memset(attributes, 0, sizeof(*attributes));
            attributes->c_iflag = got->Input;
            attributes->c_oflag = got->Output;
            attributes->c_cflag = got->Control;
            attributes->c_lflag = got->Local;
            memcpy(attributes->c_cc, got->Characters, NCCS < WIT_TERMINAL_CONTROLS ? NCCS : WIT_TERMINAL_CONTROLS);
        }
        release(s);
        return error ? error : reply.Error ? -(long)reply.Error : 0;
    }
    case TCSETS:
    case TCSETSW:
    case TCSETSF: {
        const struct termios *attributes = argument;
        const WitU32 when = request == TCSETS ? WIT_TERMINAL_NOW
            : request == TCSETSW              ? WIT_TERMINAL_AFTER_OUTPUT
                                              : WIT_TERMINAL_AFTER_FLUSH;
        take_turn(s);
        WitTerminalAttributes *set = (WitTerminalAttributes *)s->Window;
        memset(set, 0, sizeof(*set));
        set->Input = attributes->c_iflag;
        set->Output = attributes->c_oflag;
        set->Control = attributes->c_cflag;
        set->Local = attributes->c_lflag;
        memcpy(set->Characters, attributes->c_cc, NCCS < WIT_TERMINAL_CONTROLS ? NCCS : WIT_TERMINAL_CONTROLS);
        error = exchange(s, WIT_TERMINAL_SET_ATTRIBUTES, when, sizeof(WitTerminalAttributes), 0, &reply);
        release(s);
        return error ? error : reply.Error ? -(long)reply.Error : 0;
    }
    case TIOCGWINSZ: {
        struct winsize *size = argument;
        error = session_request(s, WIT_TERMINAL_GET_SIZE, 0, 0, &reply);
        if (error) {
            return error;
        }
        size->ws_row = reply.Rows;
        size->ws_col = reply.Columns;
        size->ws_xpixel = 0;
        size->ws_ypixel = 0;
        return 0;
    }
    case TIOCSWINSZ: {
        const struct winsize *size = argument;
        if (!size->ws_col || !size->ws_row) {
            return 0; /* Linux takes a size of nothing; the service keeps a terminal at least one cell big */
        }
        return session_request(
            s, WIT_TERMINAL_SET_SIZE, (WitU32)size->ws_col | ((WitU32)size->ws_row << 16), 0, &reply);
    }
    case TCFLSH: {
        const long queue = (long)argument;
        if (queue != TCIFLUSH && queue != TCOFLUSH && queue != TCIOFLUSH) {
            return -EINVAL;
        }
        return session_request(s, WIT_TERMINAL_FLUSH,
            (queue == TCOFLUSH ? 0 : WIT_TERMINAL_INPUT) | (queue == TCIFLUSH ? 0 : WIT_TERMINAL_OUTPUT), 0, &reply);
    }
    case FIONREAD:
        if (!input) {
            *(int *)argument = 0;
            return 0;
        }
        error = session_request(s, WIT_TERMINAL_POLL, 0, 0, &reply);
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

/* The termios calls and the size on a standard stream. */
long __wit_terminal_ioctl(long stream, long request, void *argument)
{
    const long connected = connect_session(&control, 0, 0, WIT_TERMINAL_CONNECT,
        process_pseudo >= 0 ? WIT_TERMINAL_PSEUDO : 0, process_pseudo >= 0 ? (WitU64)process_pseudo : 0);
    return connected < 0 ? connected : session_ioctl(&control, stream == 0, request, argument);
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

/* Pseudo-terminals (plan step E4.1, witos/terminal.h): /dev/ptmx opens a master, /dev/pts/<number> a pseudo-terminal's
 * slave side, each a descriptor (files.c's KindPseudo) over a record here that its descriptors and the calls in flight
 * reference; the last reference closes the record's session, which the service meets as the master's or the slave
 * session's end. A record has one session with an events channel and no thread: a read asks with NONBLOCK, and with
 * nothing there it takes the events off the channel, asks again and waits on the channel for a slice of 10 ms, so that
 * no wakeup is lost and the descriptor's close never waits for a reader; poll and epoll look every 10 ms while a record
 * lives (__wit_poll_sources). An opened slave is no controlling terminal: its signal events are taken and dropped. The
 * creator's posix_spawn gives a child a slave as its standard streams (process.c's WIT_STREAM_TERMINAL). */

#define PSEUDOS 8 /* masters and opened slaves together */
#define SLICE_NANOSECONDS 10000000ULL

typedef struct Pseudo {
    int References; /* the descriptors and the calls in flight; zero when free */
    int Master; /* /dev/ptmx's; otherwise an opened slave */
    int Number; /* the pseudo-terminal's */
    Session Session;
} Pseudo;

static Pseudo pseudos[PSEUDOS];
static volatile int pseudo_lock;

/* What the last reference leaves: the session's channels closed and its window let go. */
static void tear_down(Pseudo *p)
{
    WitU64 result = 0;
    close_handle(p->Session.Channel);
    close_handle(p->Session.Events);
    if (p->Session.Window) {
        wit_syscall(WIT_CALL_MEMORY_RELEASE, (WitU64)p->Session.Window, 0, 0, &result);
    }
    close_handle(p->Session.Object);
    if (p->Session.Connected) {
        __wit_poll_sources(-1);
    }
    memset(&p->Session, 0, sizeof(p->Session));
}

void __wit_pseudo_reference(WitU32 index)
{
    __wit_lock(&pseudo_lock);
    ++pseudos[index].References;
    __wit_unlock(&pseudo_lock);
}

void __wit_pseudo_release(WitU32 index)
{
    __wit_lock(&pseudo_lock);
    Pseudo *p = &pseudos[index];
    if (--p->References == 0) {
        tear_down(p);
    }
    __wit_unlock(&pseudo_lock);
}

/* The pseudo-terminal a slave record is; -1 for a master (process.c's posix_spawn). Under the table lock. */
long __wit_pseudo_slave_number(WitU32 index)
{
    return pseudos[index].Master ? -1 : pseudos[index].Number;
}

static long done(WitU32 index, long result)
{
    __wit_pseudo_release(index);
    return result;
}

/* The number of /dev/pts/<number>, -1 for any other text. */
static long slave_number(const char *path)
{
    long number = 0;
    const char *digits = path + 9;
    if (strncmp(path, "/dev/pts/", 9) != 0 || !*digits) {
        return -1;
    }
    for (; *digits; ++digits) {
        if (*digits < '0' || *digits > '9' || number > 1000000) {
            return -1;
        }
        number = number * 10 + (*digits - '0');
    }
    return number;
}

int __wit_is_pseudo_path(const char *path)
{
    return path && (strcmp(path, "/dev/ptmx") == 0 || slave_number(path) >= 0);
}

long __wit_pseudo_path_number(const char *path)
{
    return path ? slave_number(path) : -1;
}

/* open of /dev/ptmx or /dev/pts/<number>, outside the table lock: a record, its session with the service — OPEN_PSEUDO,
 * or CONNECT of the slave side — and GET_NUMBER, which a refused session fails; then a descriptor (syscall.c takes the
 * table lock). -EAGAIN when every pseudo-terminal is taken, -ENOENT for no such slave, as Linux's devpts answers. */
long __wit_pseudo_open(const char *path, long flags)
{
    WitTerminalReply reply;
    const int master = strcmp(path, "/dev/ptmx") == 0;
    const long number = master ? 0 : slave_number(path);
    WitU32 index = PSEUDOS;
    if (!__wit_terminal_present()) {
        return -ENOENT; /* no terminal service, no devpts */
    }
    if (flags & (O_CREAT | O_DIRECTORY)) {
        return flags & O_DIRECTORY ? -ENOTDIR : -EEXIST;
    }
    __wit_lock(&pseudo_lock);
    for (WitU32 i = 0; i < PSEUDOS; ++i) {
        if (!pseudos[i].References) {
            memset(&pseudos[i], 0, sizeof(pseudos[i]));
            pseudos[i].References = 1;
            index = i;
            break;
        }
    }
    __wit_unlock(&pseudo_lock);
    if (index == PSEUDOS) {
        return master ? -EAGAIN : -ENFILE;
    }
    Pseudo *p = &pseudos[index];
    long error = connect_session(&p->Session, 1, 0, master ? WIT_TERMINAL_OPEN_PSEUDO : WIT_TERMINAL_CONNECT,
        master ? 0 : WIT_TERMINAL_PSEUDO, (WitU64)number);
    if (!error) {
        __wit_poll_sources(1);
        error = session_request(&p->Session, WIT_TERMINAL_GET_NUMBER, 0, 0, &reply);
        if (error || (!master && reply.Length != (WitU64)number)) {
            error = master ? -EAGAIN : -ENOENT; /* the service closed the session: every number taken, or no such one */
        }
    }
    if (error) {
        __wit_pseudo_release(index);
        return error;
    }
    p->Master = master;
    p->Number = (int)reply.Length;
    const long fd = __wit_pseudo_install(index, (int)flags);
    if (fd < 0) {
        __wit_pseudo_release(index);
    }
    return fd;
}

/* Takes every event off a record's channel before a look, so that a change after the look leaves one for the wait. */
static void drain_events(Pseudo *p)
{
    WitTerminalEvent event;
    WitChannelMessage message;
    WitU64 result = 0;
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)&event;
    message.Bytes = sizeof(event);
    while (wit_syscall(WIT_CALL_CHANNEL_RECEIVE, p->Session.Events, (WitU64)&message, sizeof(message), &result) ==
        WIT_STATUS_OK) {
    }
}

/* A wait on the record's events for a slice at most: 0, or -EINTR after a handler. */
static long park(Pseudo *p)
{
    WitUserWaitRequest wait;
    WitU64 now = 0, frequency = 0, result = 0;
    if (__wit_cancel_requested()) {
        return -EINTR;
    }
    wit_syscall(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, &now);
    wit_syscall(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, &frequency);
    memset(&wait, 0, sizeof(wait));
    wait.Version = WIT_WAIT_OBJECTS_VERSION;
    wait.Size = sizeof(wait);
    wait.Handles = (WitU64)&p->Session.Events;
    wait.Count = 1;
    wait.Deadline = now + SLICE_NANOSECONDS * frequency / 1000000000ULL;
    return wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result) == WIT_STATUS_INTERRUPTED ? -EINTR
                                                                                                                : 0;
}

/* A read of a record: what the master's queue or the slave's discipline gives, asked without waiting in the service and
 * waited for here. */
static long pseudo_read(Pseudo *p, int nonblocking, void *buffer, long bytes)
{
    WitTerminalReply reply;
    const WitU64 limit = (WitU64)bytes < WINDOW ? (WitU64)bytes : WINDOW;
    int drained = 0;
    if (bytes == 0) {
        return 0;
    }
    for (;;) {
        take_turn(&p->Session);
        const long error = exchange(&p->Session, WIT_TERMINAL_READ, WIT_TERMINAL_NONBLOCK, limit, 0, &reply);
        if (!error && !reply.Error && reply.Length) {
            memcpy(buffer, p->Session.Window, reply.Length);
        }
        release(&p->Session);
        if (error) {
            return error;
        }
        if (reply.Error != EAGAIN) {
            return reply.Error ? -(long)reply.Error : (long)reply.Length;
        }
        if (nonblocking) {
            return -EAGAIN;
        }
        if (!drained) {
            drain_events(p); /* then a look again, before any wait */
            drained = 1;
            continue;
        }
        if (park(p) < 0) {
            return -EINTR;
        }
        drained = 0;
    }
}

/* read, readv, write and writev of a pseudo-terminal's descriptor: WIT_NOT_A_PSEUDO for any other descriptor. A read
 * fills the first vector with room, as the terminal's does. */
long __wit_pseudo_transfer(long n, long fd, long a2, long a3)
{
    WitU32 index;
    int nonblocking;
    if (__wit_pseudo_lookup(fd, &index, &nonblocking) <= 0) {
        return WIT_NOT_A_PSEUDO;
    }
    Pseudo *p = &pseudos[index];
    if (n == SYS_read) {
        return done(index, a3 < 0 ? -EINVAL : pseudo_read(p, nonblocking, (void *)a2, a3));
    }
    if (n == SYS_write) {
        return done(index, a3 < 0 ? -EINVAL : session_write(&p->Session, (const void *)a2, a3));
    }
    const struct iovec *vectors = (const struct iovec *)a2;
    if (a3 < 0 || a3 > 1024) {
        return done(index, -EINVAL);
    }
    long total = 0;
    for (long i = 0; i < a3; ++i) {
        if (vectors[i].iov_len == 0) {
            continue;
        }
        if (n == SYS_readv) {
            return done(index, pseudo_read(p, nonblocking, vectors[i].iov_base, (long)vectors[i].iov_len));
        }
        const long moved = session_write(&p->Session, vectors[i].iov_base, (long)vectors[i].iov_len);
        if (moved < 0) {
            return done(index, total ? total : moved);
        }
        total += moved;
        if ((unsigned long)moved < vectors[i].iov_len) {
            break;
        }
    }
    return done(index, total);
}

/* ioctl of a pseudo-terminal's descriptor: a master's TIOCGPTN and TIOCSPTLCK (ptsname and unlockpt, musl's; a slave
 * opens without the unlock), FIONBIO, and the termios calls and the size, which master and slave alike reach;
 * WIT_NOT_A_PSEUDO for any other descriptor. */
long __wit_pseudo_ioctl(long fd, long command, void *argument)
{
    WitU32 index;
    int nonblocking;
    if (__wit_pseudo_lookup(fd, &index, &nonblocking) <= 0) {
        return WIT_NOT_A_PSEUDO;
    }
    Pseudo *p = &pseudos[index];
    switch (command) {
    case TIOCGPTN:
        if (!p->Master) {
            return done(index, -ENOTTY);
        }
        *(unsigned int *)argument = (unsigned int)p->Number;
        return done(index, 0);
    case TIOCSPTLCK:
        return done(index, p->Master ? 0 : -ENOTTY);
    case FIONBIO:
        return done(index, __wit_socket_nonblocking(fd, *(const int *)argument != 0));
    case TIOCSCTTY:
    case TIOCNOTTY:
        return done(index, 0); /* no job control: the controlling terminal is the process's standard streams' */
    default:
        return done(index, session_ioctl(&p->Session, 1, command, argument));
    }
}

/* poll's readiness of a pseudo-terminal's record, outside the table lock: input when a read would answer now, output
 * always, and the hangup of the other side. */
short __wit_pseudo_poll(WitU32 index, short events)
{
    WitTerminalReply reply;
    Pseudo *p = &pseudos[index];
    if (session_request(&p->Session, WIT_TERMINAL_POLL, 0, 0, &reply) < 0) {
        return POLLERR;
    }
    short revents = (short)(events & (POLLOUT | POLLWRNORM));
    if (reply.Flags & WIT_TERMINAL_INPUT_READY) {
        revents |= (short)(events & (POLLIN | POLLRDNORM));
    }
    if (reply.Flags & WIT_TERMINAL_HANGUP) {
        revents |= POLLHUP;
    }
    return revents;
}
