#include "fixture.h"

/* Channel fixture over the channels of ABI-1 (RFC 0011 v3 section 7.6, plan steps K2 and K8.3): CHANNEL_CREATE,
 * CHANNEL_SEND and CHANNEL_RECEIVE with message boundaries, the rights of endpoint handles, the queue depth, the peer's
 * close, capabilities (events, endpoints, a thread handle) moved with a message, a wait on an endpoint from another
 * thread, the channel and handle quotas and a message dropped with its endpoint. The other threads are of the one form
 * on stacks of the fixture's reservations; the channel a sender uses lies in the data page at 0. */

#define ALL_RIGHTS (WIT_RIGHT_WAIT | WIT_RIGHT_SIGNAL | WIT_RIGHT_TRANSFER)
#define PATTERN 0xAAAAAAAAAAAAAAAAULL
#define SHARED_ENDS ((volatile WitU64 *)WIT_USER_DATA)

/* The payload: byte i holds i + 1. */
static void fill(WitU8 *payload)
{
    for (WitU32 i = 0; i < 256; ++i) {
        ((volatile WitU8 *)payload)[i] = (WitU8)(i + 1);
    }
}

FIXTURE_CALL WitU64 send(
    WitU64 endpoint, const WitU8 *payload, WitU32 bytes, WitU64 *handles, WitU32 count, WitU64 expected)
{
    WitChannelMessage message;
    fixture_message(&message, (WitU64)payload, bytes, handles, count);
    return fixture_expect(WIT_CALL_CHANNEL_SEND, endpoint, (WitU64)&message, sizeof(message), expected);
}

/* CHANNEL_RECEIVE into buffer and handles: the bytes in the low word, the handles in the high. */
FIXTURE_CALL WitU64 receive(
    WitU64 endpoint, WitU8 *buffer, WitU32 bytes, WitU64 *handles, WitU32 count, WitU64 expected)
{
    WitChannelMessage message;
    fixture_message(&message, (WitU64)buffer, bytes, handles, count);
    return fixture_expect(WIT_CALL_CHANNEL_RECEIVE, endpoint, (WitU64)&message, sizeof(message), expected);
}

FIXTURE_CALL void channel(WitU64 *ends)
{
    fixture_expect(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, WIT_STATUS_OK);
}

FIXTURE_CALL WitU64 event(WitU64 rights)
{
    return fixture_expect(WIT_CALL_EVENT_CREATE, 0, rights, 0, WIT_STATUS_OK);
}

static void basic_test(const WitU8 *payload)
{
    WitU64 ends[2] = {0, 0}, slots[4];
    WitU8 buffer[256];
    WitChannelMessage message;
    /* Creation validates its flags and the output before it takes a channel. */
    fixture_expect(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 1, 0, WIT_STATUS_INVALID_ARGUMENT);
    fixture_expect(WIT_CALL_CHANNEL_CREATE, 0, 0, 0, WIT_STATUS_BAD_ADDRESS);
    channel(ends);
    const WitU64 first = ends[0], second = ends[1];
    fixture_check(first != 0 && second != 0 && first != second, 10);
    fixture_check(receive(first, buffer, 256, slots, 4, WIT_STATUS_TIMED_OUT) == 0, 11);
    /* Two framed messages; the quotas and the request format are checked before anything is queued. */
    send(first, payload, 3, 0, 0, WIT_STATUS_OK);
    send(first, payload, 256, 0, 0, WIT_STATUS_OK);
    send(first, payload, 257, 0, 0, WIT_STATUS_TOO_LARGE);
    send(first, payload, 1, 0, 5, WIT_STATUS_TOO_LARGE);
    fixture_message(&message, (WitU64)payload, 1, 0, 0);
    message.Version = 2;
    fixture_expect(WIT_CALL_CHANNEL_SEND, first, (WitU64)&message, sizeof(message), WIT_STATUS_UNSUPPORTED);
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    fixture_expect(WIT_CALL_CHANNEL_SEND, first, (WitU64)&message, sizeof(message) - 1, WIT_STATUS_INVALID_ARGUMENT);
    message.Flags = 1;
    fixture_expect(WIT_CALL_CHANNEL_SEND, first, (WitU64)&message, sizeof(message), WIT_STATUS_INVALID_ARGUMENT);
    message.Flags = 0;
    message.Data = 0; /* unmapped data */
    fixture_expect(WIT_CALL_CHANNEL_SEND, first, (WitU64)&message, sizeof(message), WIT_STATUS_BAD_ADDRESS);
    message.Data = (WitU64)payload;
    message.Handles = 0; /* unmapped handles */
    message.HandleCount = 1;
    fixture_expect(WIT_CALL_CHANNEL_SEND, first, (WitU64)&message, sizeof(message), WIT_STATUS_BAD_ADDRESS);
    /* Delivery keeps the boundaries: a buffer too small leaves the message, each receive takes one message whole. */
    receive(second, buffer, 2, 0, 0, WIT_STATUS_TOO_LARGE);
    buffer[3] = 0;
    fixture_check(receive(second, buffer, 256, 0, 0, WIT_STATUS_OK) == 3, 12);
    fixture_check(buffer[0] == 1 && buffer[2] == 3 && buffer[3] == 0, 13);
    buffer[255] = 0;
    fixture_check(receive(second, buffer, 256, 0, 0, WIT_STATUS_OK) == 256, 14);
    fixture_check(buffer[254] == 255 && buffer[255] == 0, 15);
    receive(second, buffer, 256, 0, 0, WIT_STATUS_TIMED_OUT);
    /* Rights: a handle without SEND cannot send, one without DUPLICATE cannot be duplicated, one without WAIT cannot be
     * waited for, and a right outside the endpoint's is unsupported. */
    WitU64 copy = fixture_duplicate(first, WIT_RIGHT_WAIT | WIT_RIGHT_RECEIVE, WIT_STATUS_OK);
    send(copy, payload, 1, 0, 0, WIT_STATUS_DENIED);
    receive(copy, buffer, 256, 0, 0, WIT_STATUS_TIMED_OUT);
    fixture_close(copy);
    fixture_duplicate(first, WIT_RIGHT_SIGNAL, WIT_STATUS_UNSUPPORTED);
    copy = fixture_duplicate(first, WIT_RIGHT_SEND, WIT_STATUS_OK);
    fixture_duplicate(copy, 0, WIT_STATUS_DENIED);
    fixture_close(copy);
    copy = fixture_duplicate(first, WIT_RIGHT_SEND | WIT_RIGHT_RECEIVE, WIT_STATUS_OK);
    fixture_wait(&copy, 1, 0, WIT_STATUS_DENIED);
    fixture_close(copy);
    /* The queue holds four messages; the fifth waits for room. */
    for (WitU32 i = 0; i < 4; ++i) {
        send(first, payload, 1, 0, 0, WIT_STATUS_OK);
    }
    send(first, payload, 1, 0, 0, WIT_STATUS_BUSY);
    for (WitU32 i = 0; i < 4; ++i) {
        fixture_check(receive(second, buffer, 256, 0, 0, WIT_STATUS_OK) == 1, 16);
    }
    receive(second, buffer, 256, 0, 0, WIT_STATUS_TIMED_OUT);
    /* The peer's close drops what it had not received; the survivor learns PEER_CLOSED both ways. */
    send(first, payload, 1, 0, 0, WIT_STATUS_OK);
    send(first, payload, 1, 0, 0, WIT_STATUS_OK);
    fixture_close(second);
    send(first, payload, 1, 0, 0, WIT_STATUS_PEER_CLOSED);
    receive(first, buffer, 256, 0, 0, WIT_STATUS_PEER_CLOSED);
    send(second, payload, 1, 0, 0, WIT_STATUS_BAD_HANDLE);
    fixture_close(first);
    receive(first, buffer, 256, 0, 0, WIT_STATUS_BAD_HANDLE);
}

/* Sends one message through the shared channel's second end, lets the receiver take it and park again, then closes its
 * end. */
static FIXTURE_THREAD void wait_sender(WitU64 stack)
{
    WitU8 payload[1] = {7};
    send(SHARED_ENDS[1], payload, 1, 0, 0, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_THREAD_YIELD, 0, 0, 0, WIT_STATUS_OK);
    fixture_close(SHARED_ENDS[1]);
    fixture_thread_exit(WIT_TEST_EXIT_CODE, stack);
}

static void wait_test(void)
{
    /* Another thread sends a message and then closes its endpoint: each wakes the parked wait. */
    WitU64 ends[2] = {0, 0}, stack = 0;
    WitU8 buffer[256];
    channel(ends);
    SHARED_ENDS[0] = ends[0];
    SHARED_ENDS[1] = ends[1];
    const WitU64 sender = fixture_thread_create(wait_sender, &stack, 0, WIT_STATUS_OK);
    fixture_check(fixture_wait(&ends[0], 1, WIT_WAIT_INFINITE, WIT_STATUS_OK) == 0, 20);
    fixture_check(receive(ends[0], buffer, 256, 0, 0, WIT_STATUS_OK) == 1 && buffer[0] == 7, 21);
    fixture_wait(&ends[0], 1, WIT_WAIT_INFINITE, WIT_STATUS_OK);
    receive(ends[0], buffer, 256, 0, 0, WIT_STATUS_PEER_CLOSED);
    fixture_check(fixture_join(sender) == WIT_TEST_EXIT_CODE, 22);
    fixture_close(ends[0]);
}

static FIXTURE_THREAD void exiting(WitU64 stack)
{
    fixture_thread_exit(WIT_TEST_EXIT_CODE, stack);
}

static void transfer_test(const WitU8 *payload)
{
    WitU64 ends[2] = {0, 0}, other[2] = {0, 0}, moved[2] = {0, 0}, received = 0, stack = 0;
    WitU8 buffer[256];
    channel(ends);
    /* An event moves with a message: the sender's handle is gone, the receiver's works. */
    const WitU64 full = event(ALL_RIGHTS);
    moved[0] = full;
    send(ends[0], payload, 1, moved, 1, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_EVENT_SET, full, 0, 0, WIT_STATUS_BAD_HANDLE);
    fixture_check(receive(ends[1], buffer, 256, &received, 1, WIT_STATUS_OK) == (1ULL | 1ULL << 32), 30);
    const WitU64 arrived = received;
    fixture_check(arrived != full, 31);
    fixture_expect(WIT_CALL_EVENT_SET, arrived, 0, 0, WIT_STATUS_OK);
    fixture_wait(&arrived, 1, 0, WIT_STATUS_OK);
    /* Attenuated before sending: the receiver cannot signal, but sees the signal of the full handle. */
    moved[0] = fixture_duplicate(arrived, WIT_RIGHT_WAIT | WIT_RIGHT_TRANSFER, WIT_STATUS_OK);
    send(ends[0], payload, 0, moved, 1, WIT_STATUS_OK);
    fixture_check(receive(ends[1], buffer, 256, &received, 1, WIT_STATUS_OK) == 1ULL << 32, 32);
    const WitU64 waiter = received;
    fixture_expect(WIT_CALL_EVENT_SET, waiter, 0, 0, WIT_STATUS_DENIED);
    fixture_wait(&waiter, 1, 0, WIT_STATUS_TIMED_OUT);
    fixture_expect(WIT_CALL_EVENT_SET, arrived, 0, 0, WIT_STATUS_OK);
    fixture_wait(&waiter, 1, 0, WIT_STATUS_OK);
    fixture_close(waiter);
    fixture_close(arrived);
    /* Without TRANSFER a handle stays where it is; a handle named twice is refused whole. */
    moved[0] = event(0);
    send(ends[0], payload, 1, moved, 1, WIT_STATUS_DENIED);
    fixture_expect(WIT_CALL_EVENT_SET, moved[0], 0, 0, WIT_STATUS_OK);
    fixture_close(moved[0]);
    moved[0] = moved[1] = event(ALL_RIGHTS);
    send(ends[0], payload, 1, moved, 2, WIT_STATUS_INVALID_ARGUMENT);
    fixture_expect(WIT_CALL_EVENT_SET, moved[0], 0, 0, WIT_STATUS_OK);
    fixture_close(moved[0]);
    /* An endpoint moves too: the received handle talks to the other end of its own channel. */
    channel(other);
    moved[0] = other[1];
    send(ends[0], payload, 0, moved, 1, WIT_STATUS_OK);
    send(other[1], payload, 1, 0, 0, WIT_STATUS_BAD_HANDLE);
    receive(ends[1], buffer, 256, &received, 1, WIT_STATUS_OK);
    const WitU8 nine[1] = {9};
    send(other[0], nine, 1, 0, 0, WIT_STATUS_OK);
    fixture_check(receive(received, buffer, 256, 0, 0, WIT_STATUS_OK) == 1 && buffer[0] == 9, 33);
    fixture_close(other[0]);
    receive(received, buffer, 256, 0, 0, WIT_STATUS_PEER_CLOSED);
    fixture_close(received);
    /* A thread handle moves with its record: the receiver joins the thread. */
    moved[0] = fixture_thread_create(exiting, &stack, 0, WIT_STATUS_OK);
    const WitU64 thread = moved[0];
    send(ends[0], payload, 0, moved, 1, WIT_STATUS_OK);
    fixture_wait(&thread, 1, WIT_WAIT_INFINITE, WIT_STATUS_BAD_HANDLE);
    receive(ends[1], buffer, 256, &received, 1, WIT_STATUS_OK);
    fixture_check(fixture_join(received) == WIT_TEST_EXIT_CODE, 34);
    fixture_close(ends[0]);
    fixture_close(ends[1]);
}

static void limits_test(const WitU8 *payload)
{
    WitU64 endpoints[8], ends[2], copies[16], moved = 0, received = 0;
    WitU8 buffer[256];
    WitU32 count = 0;
    /* Four channels, then NO_MEMORY with the output untouched. */
    for (WitU32 i = 0; i < 8; i += 2) {
        channel(&endpoints[i]);
    }
    ends[0] = ends[1] = PATTERN;
    fixture_expect(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, WIT_STATUS_NO_MEMORY);
    fixture_check(ends[0] == PATTERN && ends[1] == PATTERN, 40);
    /* The handle table fills with duplicates: the thread's identity, the eight endpoints and whatever else the kernel
     * holds for the component precede them, so at least four fit. */
    for (;;) {
        WitU64 copy = 0;
        WitU64 result = 0;
        const WitU64 status = wit_syscall(WIT_CALL_HANDLE_DUPLICATE, endpoints[0], (WitU64)&copy, 0, &result);
        ++FIXTURE_CHECKS;
        if (status == WIT_STATUS_NO_MEMORY) {
            break;
        }
        fixture_check(status == WIT_STATUS_OK && count < 16, 41);
        copies[count++] = copy;
    }
    fixture_check(count >= 4, 42);
    /* A moved handle frees its slot, and a message whose handle would not fit stays queued until a slot is free. */
    moved = copies[count - 1];
    send(endpoints[0], payload, 0, &moved, 1, WIT_STATUS_OK);
    receive(endpoints[1], buffer, 256, &received, 1, WIT_STATUS_OK);
    moved = received;
    send(endpoints[0], payload, 0, &moved, 1, WIT_STATUS_OK);
    const WitU64 extra = fixture_duplicate(endpoints[0], 0, WIT_STATUS_OK);
    receive(endpoints[1], buffer, 256, &received, 1, WIT_STATUS_NO_MEMORY);
    fixture_close(extra);
    receive(endpoints[1], buffer, 256, &received, 1, WIT_STATUS_OK);
    copies[count - 1] = received;
    for (WitU32 i = 0; i < count; ++i) {
        fixture_close(copies[i]);
    }
    for (WitU32 i = 0; i < 8; ++i) {
        fixture_close(endpoints[i]);
    }
    channel(ends);
    fixture_close(ends[0]);
    fixture_close(ends[1]);
}

static void drop_test(const WitU8 *payload)
{
    /* A message in a closed endpoint's queue is dropped with the event it carried: the event quota is whole again. */
    WitU64 ends[2] = {0, 0}, events[4], moved = 0;
    channel(ends);
    moved = event(ALL_RIGHTS);
    send(ends[0], payload, 1, &moved, 1, WIT_STATUS_OK);
    fixture_close(ends[1]);
    for (WitU32 i = 0; i < 4; ++i) {
        events[i] = event(0);
    }
    fixture_expect(WIT_CALL_EVENT_CREATE, 0, 0, 0, WIT_STATUS_NO_MEMORY);
    for (WitU32 i = 0; i < 4; ++i) {
        fixture_close(events[i]);
    }
    send(ends[0], payload, 1, 0, 0, WIT_STATUS_PEER_CLOSED);
    fixture_close(ends[0]);
}

FIXTURE_ENTRY void wit_user_start(const WitUserStartup *startup)
{
    const WitUserTestConfig *config = fixture_config(startup);
    WitU8 payload[256];
    fixture_check(startup->Version == WIT_ABI_VERSION, 1);
    fill(payload);
    switch (config->Mode) {
    case WIT_CHANNEL_TEST_BASIC:
        basic_test(payload);
        break;
    case WIT_CHANNEL_TEST_WAIT:
        wait_test();
        break;
    case WIT_CHANNEL_TEST_TRANSFER:
        transfer_test(payload);
        break;
    case WIT_CHANNEL_TEST_LIMITS:
        limits_test(payload);
        break;
    case WIT_CHANNEL_TEST_DROP:
        drop_test(payload);
        break;
    default:
        fixture_failed(2);
    }
    fixture_exit(WIT_TEST_EXIT_CODE);
}
