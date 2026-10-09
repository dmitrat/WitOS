#include "user.h"
#include "witos/platform.h"
#include "witos/process.h"

/* Processes (RFC 0011 section 7.8, plan step K5.2c): PROCESS_CREATE, PROCESS_KILL and PROCESS_QUERY, and the process
 * handle's lifecycle. A new process is an empty address space with one capability, the channel endpoint its creator
 * named; the creator maps the image and the first stack into it with MEMORY_OBJECT_MAP naming the process handle and
 * starts the first thread with THREAD_CREATE version 3. The kernel reads no image, no name and no path. The records of
 * created processes come from a pool of WIT_PROCESS_CAPACITY; a record lives while the process runs or any handle
 * refers to it, so that PROCESS_QUERY reads the exit code of an ended process until its last handle goes. */

#define PROCESS_RIGHTS \
    (WIT_RIGHT_WAIT | WIT_RIGHT_QUERY | WIT_RIGHT_KILL | WIT_RIGHT_MANAGE | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER)

static WitUserProcess pool[WIT_PROCESS_CAPACITY];

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

/* The handle's object: the record's Id in the high word (a record reused for another process has another Id) and its
 * registry slot plus one in the low word, informational. */
static WitU64 object_of(const WitUserProcess *process)
{
    return ((WitU64)process->Id << 32) | (process->Slot + 1);
}

static WitUserProcess *by_object(WitU64 object)
{
    const WitU32 id = (WitU32)(object >> 32);
    for (WitU32 i = 0; i < WIT_PROCESS_CAPACITY; ++i) {
        if (pool[i].Pooled && pool[i].Id == id) {
            return &pool[i];
        }
    }
    return 0;
}

WitU32 wit_user_processes_pooled(void)
{
    WitU32 count = 0;
    for (WitU32 i = 0; i < WIT_PROCESS_CAPACITY; ++i) {
        if (pool[i].Pooled) {
            ++count;
        }
    }
    return count;
}

/* The record behind a live handle of the process kind with the rights, whatever its state. */
static WitU64 get(WitUserProcess *p, WitU64 handle, WitU32 rights, WitUserProcess **result, WitU64 *object)
{
    WitU32 granted = 0;
    *result = 0;
    *object = 0;
    const WitU64 status = wit_handle_check(&p->Handles, handle, WIT_HANDLE_PROCESS, rights);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!wit_handle_describe(&p->Handles, handle, WIT_HANDLE_PROCESS, object, &granted) ||
        !(*result = by_object(*object))) {
        return WIT_STATUS_BAD_HANDLE;
    }
    return WIT_STATUS_OK;
}

static void free_record(WitUserProcess *process)
{
    require(process->Pooled && process->Retired && !process->Holders, "Freeing a process record still in use");
    process->Pooled = 0;
    process->Retired = 0;
    process->State = WitUserEmpty;
}

/* A reference to a record went: the last one frees a retired record, or ends a process nothing can ever start a
 * thread in (no thread, no handle anywhere). */
static void release(WitUserProcess *process)
{
    require(process->Holders != 0, "Released process reference has no holder");
    if (--process->Holders != 0) {
        return;
    }
    if (process->Retired) {
        free_record(process);
        return;
    }
    for (WitU32 i = 0; i < WIT_PROCESS_THREAD_CAPACITY; ++i) {
        if (process->Threads[i].State != WitThreadEmpty) {
            return;
        }
    }
    wit_user_end(process, WitUserExited, 0);
}

/* A pooled record was torn down (user.c): its waiters saw the end; it is freed once nothing refers to it. */
void wit_user_process_retired(WitUserProcess *process)
{
    require(process->Pooled && process->Retired, "Retiring a process record that is not pooled");
    if (!process->Holders) {
        free_record(process);
    }
}

WitU64 wit_user_process_target(WitUserProcess *p, WitU64 handle, WitU32 rights, WitUserProcess **target)
{
    WitU64 object = 0;
    *target = 0;
    if (handle == WIT_PROCESS_SELF) {
        *target = p;
        return WIT_STATUS_OK;
    }
    const WitU64 status = get(p, handle, rights, target, &object);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if ((*target)->Retired || (*target)->State != WitUserRunning) {
        *target = 0;
        return WIT_STATUS_CLOSED;
    }
    return WIT_STATUS_OK;
}

WitU64 wit_user_process_create(WitUserProcess *p, WitU64 address, WitU64 size, WitU64 output, WitU64 *result)
{
    WitProcessCreateRequest request;
    WitChannel *channel;
    WitU32 end = 0, rights = 0;
    WitU64 endpoint_object = 0;
    *result = 0;
    if (size != sizeof(request)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_copy_from(&p->Space, address, (WitU8 *)&request, sizeof(request))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request.Version != WIT_PROCESS_CREATE_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (request.Size != sizeof(request) || request.Flags || request.Reserved) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (request.Pages > WIT_RUNTIME_PAGE_CAPACITY) {
        return WIT_STATUS_TOO_LARGE;
    }
    if (!wit_user_buffer_writable(&p->Space, output, sizeof(WitU64))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    WitU64 status = wit_channel_get(&p->Handles, request.Endpoint, WIT_RIGHT_TRANSFER, &channel, &end);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    require(wit_handle_describe(&p->Handles, request.Endpoint, WIT_HANDLE_CHANNEL_ENDPOINT, &endpoint_object, &rights),
        "Endpoint handle lost its entry");
    if (!wit_handles_free_count(&p->Handles)) {
        return WIT_STATUS_NO_MEMORY;
    }
    WitUserProcess *child = 0;
    for (WitU32 i = 0; i < WIT_PROCESS_CAPACITY && !child; ++i) {
        if (!pool[i].Pooled) {
            child = &pool[i];
        }
    }
    if (!child) {
        return WIT_STATUS_NO_MEMORY;
    }
    const WitU32 pages = request.Pages ? (WitU32)request.Pages : WIT_RUNTIME_PAGE_CAPACITY;
    if (!wit_user_create_empty(child, p->Space.Allocator, pages, p->TickLimit)) {
        return WIT_STATUS_NO_MEMORY;
    }
    /* Everything exists: the endpoint moves into the child's table, the process handle into the caller's. */
    const WitU64 local = wit_handle_grant_object(&child->Handles, WIT_HANDLE_CHANNEL_ENDPOINT, rights, endpoint_object);
    require(local != 0, "A fresh handle table refused its first handle");
    wit_user_wait_handle_closed(p, request.Endpoint);
    require(wit_handle_close(&p->Handles, request.Endpoint) == WIT_STATUS_OK, "Endpoint move failed");
    child->Pooled = 1;
    child->Retired = 0;
    child->Holders = 1;
    const WitU64 handle = wit_handle_grant_object(&p->Handles, WIT_HANDLE_PROCESS, PROCESS_RIGHTS, object_of(child));
    require(handle != 0, "Process handle grant failed after the free slot check");
    require(
        wit_user_copy_to(&p->Space, output, (const WitU8 *)&local, sizeof(local)), "Validated process output changed");
    *result = handle;
    return WIT_STATUS_OK;
}

WitU64 wit_user_process_kill(WitUserProcess *p, WitU64 handle, WitU64 code, WitU64 reserved)
{
    WitUserProcess *target;
    if (reserved) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (handle == WIT_PROCESS_SELF) {
        wit_user_finish(WitUserExited, code);
    }
    const WitU64 status = wit_user_process_target(p, handle, WIT_RIGHT_KILL, &target);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (target == p) {
        wit_user_finish(WitUserExited, code);
    }
    wit_user_end(target, WitUserExited, code);
    return WIT_STATUS_OK;
}

WitU64 wit_user_process_query(WitUserProcess *p, WitU64 handle, WitU64 address, WitU64 size)
{
    WitProcessInfo info;
    WitUserProcess *target;
    WitU64 object = 0;
    WitU32 header[2];
    if (size != sizeof(info)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_buffer_writable(&p->Space, address, sizeof(info)) ||
        !wit_user_copy_from(&p->Space, address, (WitU8 *)header, sizeof(header))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (header[0] != WIT_PROCESS_INFO_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (header[1] != sizeof(info)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitU64 status = get(p, handle, WIT_RIGHT_QUERY, &target, &object);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    info.Version = WIT_PROCESS_INFO_VERSION;
    info.Size = sizeof(info);
    info.State = target->State == WitUserRunning ? WIT_PROCESS_STATE_LIVE
        : target->State == WitUserFaulted        ? WIT_PROCESS_STATE_FAULTED
                                                 : WIT_PROCESS_STATE_EXITED;
    info.Threads = 0;
    for (WitU32 i = 0; i < WIT_PROCESS_THREAD_CAPACITY; ++i) {
        if (target->Threads[i].State != WitThreadEmpty && target->Threads[i].State != WitThreadExited) {
            ++info.Threads;
        }
    }
    info.ExitCode = target->ExitCode;
    info.ChargedPages = (WitU64)target->Space.OwnedCount + target->Space.ChargedPages;
    info.Reserved = 0;
    require(wit_user_copy_to(&p->Space, address, (const WitU8 *)&info, sizeof(info)), "Validated process info changed");
    return WIT_STATUS_OK;
}

WitU64 wit_user_process_close(WitUserProcess *p, WitU64 handle)
{
    WitUserProcess *target;
    WitU64 object = 0;
    const WitU64 status = get(p, handle, 0, &target, &object);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    wit_user_wait_handle_closed(p, handle);
    require(wit_handle_close(&p->Handles, handle) == WIT_STATUS_OK, "Process handle close failed");
    release(target);
    return WIT_STATUS_OK;
}

WitU64 wit_user_process_duplicate(WitUserProcess *p, WitU64 source, WitU64 output, WitU64 requested)
{
    WitUserProcess *target;
    WitU64 object = 0, handle = 0;
    WitU32 granted = 0;
    if (requested & ~(WitU64)PROCESS_RIGHTS) {
        return WIT_STATUS_UNSUPPORTED;
    }
    const WitU64 status = get(p, source, WIT_RIGHT_DUPLICATE, &target, &object);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    require(wit_handle_describe(&p->Handles, source, WIT_HANDLE_PROCESS, &object, &granted),
        "Process handle lost its entry");
    const WitU32 rights = requested ? (WitU32)requested : granted;
    if ((rights & granted) != rights) {
        return WIT_STATUS_DENIED;
    }
    if (!wit_user_buffer_writable(&p->Space, output, sizeof(handle))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    handle = wit_handle_grant_object(&p->Handles, WIT_HANDLE_PROCESS, rights, object);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    ++target->Holders;
    require(wit_user_copy_to(&p->Space, output, (const WitU8 *)&handle, sizeof(handle)),
        "Validated duplicate output changed");
    return WIT_STATUS_OK;
}

/* A reference a dropped message carried (user_channel.c). */
void wit_user_process_release(WitU64 object)
{
    WitUserProcess *target = by_object(object);
    require(target != 0, "Released process reference has no record");
    release(target);
}

int wit_user_process_handle(WitUserProcess *p, WitU64 handle)
{
    return wit_handle_check(&p->Handles, handle, WIT_HANDLE_PROCESS, 0) != WIT_STATUS_WRONG_TYPE;
}

WitU64 wit_user_process_signaled(WitUserProcess *p, WitU64 handle, int *signaled)
{
    WitUserProcess *target;
    WitU64 object = 0;
    *signaled = 0;
    const WitU64 status = get(p, handle, WIT_RIGHT_WAIT, &target, &object);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    *signaled = target->State != WitUserRunning;
    return WIT_STATUS_OK;
}

/* The component ends (user.c): every process handle it held releases its reference, before the table is wiped. */
void wit_user_process_handles_release(WitUserProcess *p)
{
    for (WitU32 i = 0; i < p->Handles.Limit; ++i) {
        const WitHandleEntry *entry = &p->Handles.Entries[i];
        if (entry->Live && entry->Kind == WIT_HANDLE_PROCESS) {
            WitUserProcess *target = by_object(entry->Object);
            require(target != 0, "Process handle lost its record");
            release(target);
        }
    }
}
