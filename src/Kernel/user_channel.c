#include "user.h"
#include "witos/platform.h"

/* Channels (RFC 0011 section 7.6): CHANNEL_CREATE, CHANNEL_SEND and CHANNEL_RECEIVE, the endpoint's close,
 * duplication and readiness. Every call validates its whole request, every buffer and every capability before it
 * changes anything; a capability moves out of the sender's table atomically with the message and into the receiver's
 * table atomically with the delivery, and a message dropped with its endpoint releases what it carried. The kernel
 * knows neither what the bytes mean nor who the peer is: the two endpoints of one component's channel are two
 * handles of that component until processes arrive (K5). */

#define ENDPOINT_RIGHTS (WIT_RIGHT_WAIT | WIT_RIGHT_SEND | WIT_RIGHT_RECEIVE | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER)

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitChannelEndpoint *peer_of(WitChannel *channel, WitU32 end)
{
    return &channel->Ends[end ^ 1];
}

/* A capability a dropped message carried goes back to its object: an event or an endpoint loses a reference, a
 * thread handle's record was its own. */
static void close_endpoint(WitUserProcess *p, WitChannel *channel, WitU32 end);

static void release_capability(WitUserProcess *p, const WitChannelCapability *capability)
{
    if (capability->Kind == WIT_HANDLE_EVENT) {
        wit_event_release(&p->Events, capability->Object);
    } else if (capability->Kind == WIT_HANDLE_CHANNEL_ENDPOINT) {
        WitU32 end = 0;
        WitChannel *channel = wit_channel_slot(&p->Channels, capability->Object, &end);
        require(channel != 0, "Moved endpoint lost its channel");
        if (--channel->Ends[end].Handles == 0) {
            close_endpoint(p, channel, end);
        }
    } else if (capability->Kind == WIT_HANDLE_MEMORY_OBJECT) {
        wit_user_memory_object_release(p, capability->Object);
    }
}

/* The last handle of an end is gone: its queue is dropped with what the messages carried, the peer's waiters learn
 * PEER_CLOSED, and the channel ends with its second end. */
static void close_endpoint(WitUserProcess *p, WitChannel *channel, WitU32 end)
{
    WitChannelEndpoint *endpoint = &channel->Ends[end];
    require(endpoint->Live && !endpoint->Handles, "Closing an endpoint that is still referenced");
    endpoint->Live = 0;
    while (endpoint->Count) {
        const WitChannelQueued *message = &endpoint->Queue[endpoint->Head];
        for (WitU32 i = 0; i < message->HandleCount; ++i) {
            release_capability(p, &message->Handles[i]);
        }
        endpoint->Head = (endpoint->Head + 1) % WIT_CHANNEL_QUEUE_DEPTH;
        --endpoint->Count;
        ++p->ChannelDrops;
    }
    endpoint->Head = 0;
    endpoint->Bytes = 0;
    if (!peer_of(channel, end)->Live) {
        channel->Live = 0;
        --p->Channels.Count;
    } else {
        wit_user_wait_objects_changed(p); /* The peer's handles are ready: their waits see PEER_CLOSED. */
    }
}

WitU64 wit_user_channel_create(WitUserProcess *p, WitU64 output, WitU64 flags, WitU64 reserved)
{
    WitU64 handles[2] = {0, 0};
    if (flags || reserved) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_buffer_writable(&p->Space, output, sizeof(handles))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    for (WitU32 i = 0; i < p->Channels.Limit; ++i) {
        WitChannel *channel = &p->Channels.Entries[i];
        if (channel->Live) {
            continue;
        }
        handles[0] = wit_handle_grant_object(
            &p->Handles, WIT_HANDLE_CHANNEL_ENDPOINT, ENDPOINT_RIGHTS, wit_channel_object(i, 0));
        if (!handles[0]) {
            return WIT_STATUS_NO_MEMORY;
        }
        handles[1] = wit_handle_grant_object(
            &p->Handles, WIT_HANDLE_CHANNEL_ENDPOINT, ENDPOINT_RIGHTS, wit_channel_object(i, 1));
        if (!handles[1]) {
            require(wit_handle_close(&p->Handles, handles[0]) == WIT_STATUS_OK, "Endpoint handle rollback failed");
            return WIT_STATUS_NO_MEMORY;
        }
        for (WitU32 end = 0; end < 2; ++end) {
            channel->Ends[end].Live = 1;
            channel->Ends[end].Handles = 1;
            channel->Ends[end].Head = 0;
            channel->Ends[end].Count = 0;
            channel->Ends[end].Bytes = 0;
        }
        channel->Live = 1;
        ++p->Channels.Count;
        require(wit_user_copy_to(&p->Space, output, (const WitU8 *)handles, sizeof(handles)),
            "Validated channel output changed");
        return WIT_STATUS_OK;
    }
    return WIT_STATUS_NO_MEMORY;
}

static WitU64 read_request(WitUserProcess *p, WitU64 address, WitU64 size, WitChannelMessage *request)
{
    if (size != sizeof(*request)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_copy_from(&p->Space, address, (WitU8 *)request, sizeof(*request))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request->Version != WIT_CHANNEL_MESSAGE_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (request->Size != sizeof(*request) || request->Flags || request->Reserved) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    return WIT_STATUS_OK;
}

/* A capability the sender names: live, with the TRANSFER right, of a kind the kernel can move, named once. */
static WitU64 inspect_capability(WitUserProcess *p, const WitU64 *handles, WitU32 index, WitChannelCapability *out)
{
    const WitU64 handle = handles[index];
    WitU64 object = 0;
    WitU32 rights = 0;
    for (WitU32 i = 0; i < index; ++i) {
        if (handles[i] == handle) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
    }
    out->Kind = 0;
    out->Rights = 0;
    out->Object = 0;
    out->ThreadId = 0;
    out->ExitCode = 0;
    out->Exited = 0;
    out->Reserved = 0;
    if (wit_handle_describe(&p->Handles, handle, WIT_HANDLE_EVENT, &object, &rights)) {
        out->Kind = WIT_HANDLE_EVENT;
    } else if (wit_handle_describe(&p->Handles, handle, WIT_HANDLE_CHANNEL_ENDPOINT, &object, &rights)) {
        out->Kind = WIT_HANDLE_CHANNEL_ENDPOINT;
    } else if (wit_handle_describe(&p->Handles, handle, WIT_HANDLE_MEMORY_OBJECT, &object, &rights)) {
        out->Kind = WIT_HANDLE_MEMORY_OBJECT;
    } else if (wit_handle_describe(&p->Handles, handle, WIT_HANDLE_THREAD_REFERENCE, &object, &rights)) {
        const WitUserThreadReference *reference;
        require(wit_user_reference_describe(p, handle, 0, &reference) == WIT_STATUS_OK, "Thread handle lost record");
        out->Kind = WIT_HANDLE_THREAD_REFERENCE;
        out->ThreadId = reference->ThreadId;
        out->ExitCode = reference->ExitCode;
        out->Exited = reference->Exited;
    } else {
        /* An absent handle, or a kind that is never moved (the frozen line's files and libraries, the private thread
         * identity and the console): the right decides first where the handle exists. */
        WitU64 status = wit_handle_check(&p->Handles, handle, 0, WIT_RIGHT_TRANSFER);
        return status == WIT_STATUS_BAD_HANDLE ? status : status == WIT_STATUS_DENIED ? status : WIT_STATUS_UNSUPPORTED;
    }
    if (!(rights & WIT_RIGHT_TRANSFER)) {
        return WIT_STATUS_DENIED;
    }
    out->Rights = rights;
    out->Object = object;
    return WIT_STATUS_OK;
}

/* Moves a validated capability out of the sender's table; the object keeps the reference the message now holds. */
static void detach_capability(WitUserProcess *p, WitU64 handle, const WitChannelCapability *capability)
{
    if (capability->Kind == WIT_HANDLE_THREAD_REFERENCE) {
        require(wit_user_reference_close(p, handle) == WIT_STATUS_OK, "Thread handle move failed");
    } else {
        wit_user_wait_handle_closed(p, handle);
        require(wit_handle_close(&p->Handles, handle) == WIT_STATUS_OK, "Capability move failed");
    }
}

WitU64 wit_user_channel_send(WitUserProcess *p, WitU64 endpoint, WitU64 address, WitU64 size)
{
    WitChannel *channel;
    WitU32 end = 0;
    WitChannelMessage request;
    WitU64 handles[WIT_CHANNEL_MESSAGE_HANDLES];
    WitChannelCapability capabilities[WIT_CHANNEL_MESSAGE_HANDLES];
    WitU8 data[WIT_CHANNEL_MESSAGE_BYTES];
    WitU64 status = wit_channel_get(&p->Channels, &p->Handles, endpoint, WIT_RIGHT_SEND, &channel, &end);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    status = read_request(p, address, size, &request);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (request.Bytes > WIT_CHANNEL_MESSAGE_BYTES || request.HandleCount > WIT_CHANNEL_MESSAGE_HANDLES) {
        return WIT_STATUS_TOO_LARGE;
    }
    if (request.Bytes && !wit_user_copy_from(&p->Space, request.Data, data, request.Bytes)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request.HandleCount &&
        !wit_user_copy_from(&p->Space, request.Handles, (WitU8 *)handles, request.HandleCount * sizeof(WitU64))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    for (WitU32 i = 0; i < request.HandleCount; ++i) {
        status = inspect_capability(p, handles, i, &capabilities[i]);
        if (status != WIT_STATUS_OK) {
            return status;
        }
    }
    WitChannelEndpoint *peer = peer_of(channel, end);
    if (!peer->Live) {
        return WIT_STATUS_PEER_CLOSED;
    }
    if (peer->Count == WIT_CHANNEL_QUEUE_DEPTH || peer->Bytes + request.Bytes > WIT_CHANNEL_QUEUE_BYTES) {
        return WIT_STATUS_BUSY;
    }
    /* Everything is valid: move the capabilities, queue the message, wake the peer's waiters. */
    WitChannelQueued *message = &peer->Queue[(peer->Head + peer->Count) % WIT_CHANNEL_QUEUE_DEPTH];
    for (WitU32 i = 0; i < request.HandleCount; ++i) {
        detach_capability(p, handles[i], &capabilities[i]);
        message->Handles[i] = capabilities[i];
    }
    for (WitU32 i = 0; i < request.Bytes; ++i) {
        message->Data[i] = data[i];
    }
    message->Bytes = request.Bytes;
    message->HandleCount = request.HandleCount;
    ++peer->Count;
    peer->Bytes += request.Bytes;
    ++p->ChannelSends;
    wit_user_wait_objects_changed(p);
    return WIT_STATUS_OK;
}

/* Grants a moved capability to the receiver; validated to fit beforehand. */
static WitU64 attach_capability(WitUserProcess *p, const WitChannelCapability *capability)
{
    WitU64 handle = 0;
    if (capability->Kind == WIT_HANDLE_THREAD_REFERENCE) {
        WitUserThreadReference snapshot;
        snapshot.Handle = 0;
        snapshot.ThreadId = capability->ThreadId;
        snapshot.ExitCode = capability->ExitCode;
        snapshot.Exited = capability->Exited;
        snapshot.Rights = capability->Rights;
        require(wit_user_reference_attach(p, &snapshot, &handle) == WIT_STATUS_OK, "Thread handle delivery failed");
    } else {
        handle = wit_handle_grant_object(&p->Handles, capability->Kind, capability->Rights, capability->Object);
        require(handle != 0, "Capability delivery failed");
    }
    return handle;
}

WitU64 wit_user_channel_receive(WitUserProcess *p, WitU64 endpoint, WitU64 address, WitU64 size, WitU64 *value)
{
    WitChannel *channel;
    WitU32 end = 0, threads = 0;
    WitChannelMessage request;
    WitU64 received[WIT_CHANNEL_MESSAGE_HANDLES];
    *value = 0;
    WitU64 status = wit_channel_get(&p->Channels, &p->Handles, endpoint, WIT_RIGHT_RECEIVE, &channel, &end);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    status = read_request(p, address, size, &request);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (request.Bytes > WIT_CHANNEL_MESSAGE_BYTES || request.HandleCount > WIT_CHANNEL_MESSAGE_HANDLES) {
        return WIT_STATUS_TOO_LARGE;
    }
    if ((request.Bytes && !wit_user_buffer_writable(&p->Space, request.Data, request.Bytes)) ||
        (request.HandleCount &&
            !wit_user_buffer_writable(&p->Space, request.Handles, request.HandleCount * sizeof(WitU64)))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    WitChannelEndpoint *mine = &channel->Ends[end];
    if (!mine->Count) {
        return peer_of(channel, end)->Live ? WIT_STATUS_TIMED_OUT : WIT_STATUS_PEER_CLOSED;
    }
    WitChannelQueued *message = &mine->Queue[mine->Head];
    if (message->Bytes > request.Bytes || message->HandleCount > request.HandleCount) {
        return WIT_STATUS_TOO_LARGE;
    }
    for (WitU32 i = 0; i < message->HandleCount; ++i) {
        if (message->Handles[i].Kind == WIT_HANDLE_THREAD_REFERENCE) {
            ++threads;
        }
    }
    if (wit_handles_free_count(&p->Handles) < message->HandleCount || wit_user_reference_free_count(p) < threads) {
        return WIT_STATUS_NO_MEMORY;
    }
    /* Everything fits: deliver the capabilities, copy the bytes and the handles out, dequeue. */
    for (WitU32 i = 0; i < message->HandleCount; ++i) {
        received[i] = attach_capability(p, &message->Handles[i]);
    }
    require(!message->Bytes || wit_user_copy_to(&p->Space, request.Data, message->Data, message->Bytes),
        "Validated channel data buffer changed");
    require(!message->HandleCount ||
            wit_user_copy_to(
                &p->Space, request.Handles, (const WitU8 *)received, message->HandleCount * sizeof(WitU64)),
        "Validated channel handle buffer changed");
    *value = (WitU64)message->Bytes | ((WitU64)message->HandleCount << 32);
    mine->Bytes -= message->Bytes;
    mine->Head = (mine->Head + 1) % WIT_CHANNEL_QUEUE_DEPTH;
    --mine->Count;
    ++p->ChannelReceives;
    return WIT_STATUS_OK;
}

WitU64 wit_user_channel_close(WitUserProcess *p, WitU64 handle)
{
    WitChannel *channel;
    WitU32 end = 0;
    const WitU64 status = wit_channel_get(&p->Channels, &p->Handles, handle, 0, &channel, &end);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    wit_user_wait_handle_closed(p, handle);
    require(wit_handle_close(&p->Handles, handle) == WIT_STATUS_OK, "Endpoint handle close failed");
    if (--channel->Ends[end].Handles == 0) {
        close_endpoint(p, channel, end);
    }
    return WIT_STATUS_OK;
}

WitU64 wit_user_channel_duplicate(WitUserProcess *p, WitU64 source, WitU64 output, WitU64 requested)
{
    WitChannel *channel;
    WitU32 end = 0, granted = 0;
    WitU64 object = 0, handle = 0;
    if (requested & ~(WitU64)ENDPOINT_RIGHTS) {
        return WIT_STATUS_UNSUPPORTED;
    }
    WitU64 status = wit_channel_get(&p->Channels, &p->Handles, source, WIT_RIGHT_DUPLICATE, &channel, &end);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    require(wit_handle_describe(&p->Handles, source, WIT_HANDLE_CHANNEL_ENDPOINT, &object, &granted),
        "Endpoint handle lost its entry");
    const WitU32 rights = requested ? (WitU32)requested : granted;
    if ((rights & granted) != rights) {
        return WIT_STATUS_DENIED;
    }
    if (!wit_user_buffer_writable(&p->Space, output, sizeof(handle))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    handle = wit_handle_grant_object(&p->Handles, WIT_HANDLE_CHANNEL_ENDPOINT, rights, object);
    if (!handle) {
        return WIT_STATUS_NO_MEMORY;
    }
    ++channel->Ends[end].Handles;
    require(wit_user_copy_to(&p->Space, output, (const WitU8 *)&handle, sizeof(handle)),
        "Validated duplicate output changed");
    return WIT_STATUS_OK;
}

WitU64 wit_user_channel_signaled(WitUserProcess *p, WitU64 handle, int *signaled)
{
    WitChannel *channel;
    WitU32 end = 0;
    *signaled = 0;
    const WitU64 status = wit_channel_get(&p->Channels, &p->Handles, handle, WIT_RIGHT_WAIT, &channel, &end);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    *signaled = channel->Ends[end].Count != 0 || !peer_of(channel, end)->Live;
    return WIT_STATUS_OK;
}

int wit_user_channel_handle(WitUserProcess *p, WitU64 handle)
{
    return wit_handle_check(&p->Handles, handle, WIT_HANDLE_CHANNEL_ENDPOINT, 0) != WIT_STATUS_WRONG_TYPE;
}

void wit_user_channels_thread_exited(WitUserProcess *p, WitU64 thread_id, WitU64 code)
{
    for (WitU32 c = 0; c < p->Channels.Limit; ++c) {
        WitChannel *channel = &p->Channels.Entries[c];
        for (WitU32 end = 0; channel->Live && end < 2; ++end) {
            WitChannelEndpoint *endpoint = &channel->Ends[end];
            for (WitU32 m = 0; m < endpoint->Count; ++m) {
                WitChannelQueued *message = &endpoint->Queue[(endpoint->Head + m) % WIT_CHANNEL_QUEUE_DEPTH];
                for (WitU32 i = 0; i < message->HandleCount; ++i) {
                    WitChannelCapability *capability = &message->Handles[i];
                    if (capability->Kind == WIT_HANDLE_THREAD_REFERENCE && capability->ThreadId == thread_id) {
                        capability->ExitCode = code;
                        capability->Exited = 1;
                    }
                }
            }
        }
    }
}
