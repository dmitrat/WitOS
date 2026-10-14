#include "witos_libc.h"
#include "witos/channels.h"
#include "witos/start.h"
#include "witos/wait_objects.h"
#include <elf.h>
#include <string.h>

/* The process context (witos/libc_context.h) and the start of a program another process started (plan step S5.2).
 * The creator mapped the program and its first stack and built on the stack what a Linux kernel builds: argc, argv,
 * envp and the auxiliary vector, whose WIT_AT_START entry is the endpoint PROCESS_CREATE installed. Before it started
 * the thread it sent the start message on the other end (witos/start.h): the kernel log and the boot package in the
 * handle slots and the package's size in the bytes. The library takes them into its context and closes the endpoint;
 * a process the process manager starts finds the manager's endpoint after them (S6.1), and a service the capability
 * its creator granted it (D1.1), each where the message's Capabilities say. A process given standard streams as pipes
 * (N4.2) finds them on the grant, one WitStreamMessage each, and closes the grant once they arrived. A start without
 * the vector entry or a valid message ends the process with 127, the status a shell reports for a program that could
 * not run. */

WitProcessContext __wit_process;

static __attribute__((__noreturn__)) void refuse(void)
{
    WitU64 result = 0;
    for (;;) {
        wit_syscall(WIT_CALL_PROCESS_EXIT, 127, 0, 0, &result);
    }
}

/* The value of WIT_AT_START in the auxiliary vector above argc, argv and envp; zero when absent. */
static WitU64 start_endpoint(const unsigned long *stack)
{
    const unsigned long *p = stack + 1 + stack[0] + 1;
    while (*p) {
        ++p;
    }
    for (++p; p[0] != AT_NULL; p += 2) {
        if (p[0] == WIT_AT_START) {
            return p[1];
        }
    }
    return 0;
}

/* One message on the endpoint into the buffers, waiting for it: the creator sends before it starts the thread; a
 * creator of another protocol may send later. The status, with the bytes and handles received in *result. */
static WitU64 receive(WitU64 endpoint, void *data, WitU32 bytes, WitU64 *handles, WitU32 count, WitU64 *result)
{
    WitChannelMessage request;
    WitUserWaitRequest wait;
    WitU64 status;
    memset(&request, 0, sizeof(request));
    request.Version = WIT_CHANNEL_MESSAGE_VERSION;
    request.Size = sizeof(request);
    request.Data = (WitU64)data;
    request.Handles = (WitU64)handles;
    request.Bytes = bytes;
    request.HandleCount = count;
    memset(&wait, 0, sizeof(wait));
    wait.Version = WIT_WAIT_OBJECTS_VERSION;
    wait.Size = sizeof(wait);
    wait.Handles = (WitU64)&endpoint;
    wait.Count = 1;
    wait.Deadline = WIT_WAIT_INFINITE;
    while ((status = wit_syscall(WIT_CALL_CHANNEL_RECEIVE, endpoint, (WitU64)&request, sizeof(request), result)) ==
        WIT_STATUS_TIMED_OUT) {
        if (wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, result) != WIT_STATUS_OK) {
            refuse();
        }
    }
    return status;
}

/* The standard streams given as pipes (N4.2): one WitStreamMessage per stream on the grant, each with the ring's
 * object and the end's endpoint, installed at its descriptor (files.c); then the grant goes, since it carried the
 * streams alone. */
static void receive_streams(WitU32 count)
{
    WitU64 result = 0;
    for (WitU32 i = 0; i < count; ++i) {
        WitStreamMessage stream;
        WitU64 handles[2] = {0, 0};
        const WitU64 status = receive(__wit_process.Grant, &stream, sizeof(stream), handles, 2, &result);
        if (status != WIT_STATUS_OK ||
            (result & 0xFFFFFFFFULL) != sizeof(stream) ||
            (result >> 32) != 2 ||
            stream.Version != WIT_START_VERSION ||
            stream.Size != sizeof(stream) ||
            stream.Descriptor >= WIT_START_STREAMS_MAXIMUM ||
            stream.End > WIT_STREAM_WRITE_END ||
            (stream.Flags & ~WIT_STREAM_NONBLOCKING) ||
            stream.Reserved ||
            __wit_install_stream(stream.Descriptor, handles[0], handles[1], stream.End == WIT_STREAM_WRITE_END,
                (stream.Flags & WIT_STREAM_NONBLOCKING) != 0) < 0) {
            refuse();
        }
    }
    wit_syscall(WIT_CALL_HANDLE_CLOSE, __wit_process.Grant, 0, 0, &result);
    __wit_process.Grant = 0;
}

void __wit_start_program(const unsigned long *stack)
{
    WitU64 endpoint = start_endpoint(stack), result = 0, status;
    WitU64 handles[WIT_START_HANDLES_MAXIMUM] = {0, 0, 0, 0};

    union {
        WitStartMessage Header;
        unsigned char Bytes[WIT_START_SIZE + WIT_START_DIRECTORY_MAXIMUM];
    } received;

    WitStartMessage message;
    if (!endpoint) {
        refuse();
    }
    status = receive(endpoint, &received, sizeof(received), handles, WIT_START_HANDLES_MAXIMUM, &result);
    memcpy(&message, &received.Header, sizeof(message));
    const WitU64 optional = (message.Capabilities & WIT_START_MANAGER ? 1 : 0) +
        (message.Capabilities & WIT_START_GRANT ? 1 : 0) +
        (message.Capabilities & WIT_START_NAMESPACE ? 1 : 0);
    if (status != WIT_STATUS_OK ||
        (result & 0xFFFFFFFFULL) < sizeof(message) ||
        message.Version != WIT_START_VERSION ||
        message.Size != sizeof(message) ||
        (message.Capabilities & ~WIT_START_CAPABILITIES) ||
        (message.Capabilities & WIT_START_NAMESPACE_NEXT && message.Capabilities & WIT_START_NAMESPACE) ||
        message.Streams > WIT_START_STREAMS_MAXIMUM ||
        (message.Streams && !(message.Capabilities & WIT_START_GRANT)) ||
        (result >> 32) != WIT_START_HANDLES + optional ||
        message.DirectoryBytes > WIT_START_DIRECTORY_MAXIMUM ||
        (result & 0xFFFFFFFFULL) != sizeof(message) + message.DirectoryBytes ||
        (message.ClosedStreams & ~7U) ||
        __wit_set_directory((const char *)received.Bytes + sizeof(message), message.DirectoryBytes) < 0) {
        refuse();
    }
    WitU32 next = WIT_START_HANDLES;
    __wit_process.Log = handles[WIT_START_HANDLE_LOG];
    __wit_process.Package = handles[WIT_START_HANDLE_PACKAGE];
    __wit_process.PackageBytes = message.PackageBytes;
    __wit_process.Manager = message.Capabilities & WIT_START_MANAGER ? handles[next++] : 0;
    __wit_process.Grant = message.Capabilities & WIT_START_GRANT ? handles[next++] : 0;
    __wit_process.Namespace = message.Capabilities & WIT_START_NAMESPACE ? handles[next++] : 0;
    __wit_process.ClosedStreams = message.ClosedStreams;
    if (message.Capabilities & WIT_START_NAMESPACE_NEXT) {
        /* The namespace's endpoint in a second message, since the first had no room for it (N4.2). */
        WitU64 names = 0;
        if (receive(endpoint, 0, 0, &names, 1, &result) != WIT_STATUS_OK ||
            (result & 0xFFFFFFFFULL) != 0 ||
            (result >> 32) != 1) {
            refuse();
        }
        __wit_process.Namespace = names;
    }
    wit_syscall(WIT_CALL_HANDLE_CLOSE, endpoint, 0, 0, &result);
    if (message.Streams) {
        receive_streams(message.Streams);
    }
    __wit_thread_init(); /* the main thread's record and handle (S2, S3) */
    __wit_signal_init(); /* the fault callback every signal arrives through (S3) */
}
