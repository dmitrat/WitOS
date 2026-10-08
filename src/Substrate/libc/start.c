#include "witos_libc.h"
#include "witos/channels.h"
#include "witos/start.h"
#include "witos/wait_objects.h"
#include <elf.h>

/* The process context (witos/libc_context.h) and the start of a program another process started (plan step S5.2).
 * The creator mapped the program and its first stack and built on the stack what a Linux kernel builds: argc, argv,
 * envp and the auxiliary vector, whose WIT_AT_START entry is the endpoint PROCESS_CREATE installed. Before it started
 * the thread it sent the start message on the other end (witos/start.h): the kernel log and the boot package in the
 * handle slots and the package's size in the bytes. The library takes them into its context and closes the endpoint;
 * a process the process manager starts finds the manager's endpoint as a third capability (S6.1). A start without the
 * vector entry or a valid message ends the process with 127, the status a shell reports for a program that could not
 * run. */

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

void __wit_start_program(const unsigned long *stack)
{
    WitU64 endpoint = start_endpoint(stack), result = 0, status;
    WitU64 handles[WIT_START_HANDLES_MAXIMUM] = {0, 0, 0};
    WitStartMessage message = {0, 0, 0};
    WitChannelMessage request;
    WitUserWaitRequest wait;
    if (!endpoint) {
        refuse();
    }
    request.Version = WIT_CHANNEL_MESSAGE_VERSION;
    request.Size = sizeof(request);
    request.Data = (WitU64)&message;
    request.Handles = (WitU64)handles;
    request.Bytes = sizeof(message);
    request.HandleCount = WIT_START_HANDLES_MAXIMUM;
    request.Flags = 0;
    request.Reserved = 0;
    wait.Version = WIT_WAIT_OBJECTS_VERSION;
    wait.Size = sizeof(wait);
    wait.Handles = (WitU64)&endpoint;
    wait.Count = 1;
    wait.Flags = 0;
    wait.Deadline = WIT_WAIT_INFINITE;
    /* The creator sends before it starts the thread; a creator of another protocol may send later. */
    while ((status = wit_syscall(WIT_CALL_CHANNEL_RECEIVE, endpoint, (WitU64)&request, sizeof(request), &result)) ==
        WIT_STATUS_TIMED_OUT) {
        if (wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result) != WIT_STATUS_OK) {
            refuse();
        }
    }
    if (status != WIT_STATUS_OK ||
        (result & 0xFFFFFFFFULL) != sizeof(message) ||
        (result >> 32) < WIT_START_HANDLES ||
        message.Version != WIT_START_VERSION ||
        message.Size != sizeof(message)) {
        refuse();
    }
    __wit_process.Log = handles[WIT_START_HANDLE_LOG];
    __wit_process.Package = handles[WIT_START_HANDLE_PACKAGE];
    __wit_process.PackageBytes = message.PackageBytes;
    __wit_process.Manager = handles[WIT_START_HANDLE_MANAGER];
    wit_syscall(WIT_CALL_HANDLE_CLOSE, endpoint, 0, 0, &result);
    __wit_thread_init(); /* the main thread's record and handle (S2, S3) */
    __wit_signal_init(); /* the fault callback every signal arrives through (S3) */
}
