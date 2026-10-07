# K2 — Channels (ABI v56)

Plan step K2 brings the channel family of [RFC 0011 v3 §7.6](../RFC-0011-Kernel-Architecture-and-ABI.md) into the
kernel: `CHANNEL_CREATE` (70), `CHANNEL_SEND` (71) and `CHANNEL_RECEIVE` (72), the endpoint as a waitable object, the
rights `SEND`, `RECEIVE`, `DUPLICATE` and `TRANSFER`, the status `PEER_CLOSED` (18), and the first feature bit `QUERY`
reports (`CHANNELS`). The call-by-call reference is [ABI-Reference.md](ABI-Reference.md); the design it follows is
[RFC 0006 §11–14 and §30–36](../RFC-0006-IPC-and-Communication-Model.md).

## What changed

**Two endpoints, bounded queues.** A channel is two endpoints, each a queue of the messages the other sent it:
`WIT_CHANNEL_QUEUE_DEPTH` (4) messages and `WIT_CHANNEL_QUEUE_BYTES` (1024) inline bytes per endpoint,
`WIT_CHANNEL_CAPACITY` (4) channels per component. `CHANNEL_CREATE` writes two endpoint handles with every endpoint
right; an endpoint is closed when its last handle is, the channel ends with its second endpoint.

**Messages keep their boundaries.** A message is up to `WIT_CHANNEL_MESSAGE_BYTES` (256) inline bytes and up to
`WIT_CHANNEL_MESSAGE_HANDLES` (4) capabilities, described by `WitChannelMessage` (40 bytes: version, size, the data
and handle buffers and their counts or capacities). `CHANNEL_RECEIVE` delivers the oldest message whole and returns
the counts in one value; a buffer smaller than the message is `TOO_LARGE` and the message stays, an empty queue is
`TIMED_OUT`, an empty queue with a closed peer is `PEER_CLOSED`. The kernel knows nothing of what the bytes mean (RFC
0006 §11): no method names, interfaces or serialization.

**Capabilities move atomically.** Every handle a message names must be live, carry `TRANSFER` and be of a movable
kind: an event, a channel endpoint or a thread handle (the frozen line's files and libraries, the console and the
private thread identity are `UNSUPPORTED`); a handle named twice is `INVALID_ARGUMENT`. `CHANNEL_SEND` validates all
of it and the peer's queue before it changes anything, then closes the sender's entries atomically with queuing the
message (a wait parked on a moved handle ends with `CLOSED`); the object lives on by the message's reference. The
receiver's table must have room for every handle, and a thread handle's record must fit too, before
`CHANNEL_RECEIVE` dequeues; the handles enter the receiver's table with the rights the sender had, which
`HANDLE_DUPLICATE` attenuates before sending (RFC 0006 §34), and a thread handle brings its record. A message that
is still queued when its endpoint closes is dropped with what it carried: an event or endpoint loses the reference
and ends with its last one.

**The endpoint waits.** An endpoint handle with `WAIT` is ready in `OBJECT_WAIT` when a message is queued for it or
its peer is closed; a receive consumes nothing in the wait. `CHANNEL_SEND` wakes the peer's waiters; closing the
last handle of an endpoint wakes the waiters of the other endpoint. `HANDLE_DUPLICATE` of an endpoint needs the
`DUPLICATE` right and grants the same or fewer rights.

**Rights and the feature mask.** `SEND` (512), `RECEIVE` (1024), `DUPLICATE` (2048) and `TRANSFER` (4096) join the
rights; a thread handle gains `DUPLICATE` and `TRANSFER` (`WIT_RIGHT_THREAD_ALL` is 6644), and `EVENT_CREATE` accepts
them in the requested rights while the default stays `WAIT` and `SIGNAL`. `WIT_ABI_FEATURES` carries `CHANNELS`, so
`QUERY` reports it in the high half of its value; the frozen line's GC environment compares the version in the low
half alone.

## Kernel

- `channels.h`, `channels.c`: the request structure, the endpoint and channel tables, the object numbering of
  endpoint handles.
- `user_channel.c`: the three calls, the endpoint's close (drop, wake, free), duplication and readiness; the counters
  `ChannelSends`, `ChannelReceives` and `ChannelDrops`.
- `events.c`: `wit_event_release` for a reference a dropped message held; `EVENT_CREATE` with `DUPLICATE` and
  `TRANSFER`. `handles.c`: `wit_handles_free_count`. `user_reference.c`: `wit_user_reference_free_count` and
  `wit_user_reference_attach` for a moved thread handle.
- `user_objects.c` and `user_wait.c`: endpoint readiness in the one wait and its accounting with the object waits;
  `user.c`: the endpoint's close in `HANDLE_CLOSE` and the table's reset with the component.

## Fixtures

`tests/User.X64/channels.asm` and `tests/User.A64/channels.asm`, driven by the common
`tests/Kernel/user_channel_tests.c` in five modes: `User.ChannelBasic` (validation of creation and requests, two
framed messages, a buffer too small, the rights of attenuated handles, the queue depth, the peer's close both ways),
`User.ChannelWait` (a wait on an endpoint woken by a send and by the peer's close from another thread),
`User.ChannelTransfer` (an event, an attenuated event, a refused event without `TRANSFER`, a handle named twice, an
endpoint and a thread handle moved through a channel), `User.ChannelLimits` (four channels then `NO_MEMORY` with the
output untouched, a handle table filled with duplicates, a moved handle freeing its slot, a message whose handle
would not fit staying queued) and `User.ChannelDrop` (a queued message dropped with its event, which the event quota
then shows freed).

## Not in this slice

Waiting for room in a full queue (`CHANNEL_SEND` answers `BUSY` and the sender retries; RFC 0011 §7.6 names the
wait for a writable endpoint, which needs a second readiness of the same handle and waits for a consumer); the
process boundary (both endpoints belong to one component until K5); memory objects in messages (K5); the frozen
Windows-form line, which has no channel API.

## Evidence

Guest acceptance on both ISAs: `test` (x64, QEMU q35, 20 scenarios) and `test --arch arm64` (QEMU virt, 14 scenarios)
pass with the five channel modes on both. The frozen line's chains pass over the new ABI and the feature mask:
`runtime-config` (four boots), `runtime-boot-run` (four boots), `coreclr-memory` and `coreclr-storage` (two boots
each) and `coreclr-host-guest` (no unresolved external). The host tests check the call table and the ABI reference
against the header, and `format-check` is clean.
