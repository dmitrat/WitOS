#ifndef WITOS_CHANNELS_H
#define WITOS_CHANNELS_H
#include "handles.h"
#include "limits.h"

/* Channels (RFC 0011 section 7.6, plan step K2): two endpoints, each a bounded queue of the messages the other sent
 * it. A message is an inline payload of up to WIT_CHANNEL_MESSAGE_BYTES and up to WIT_CHANNEL_MESSAGE_HANDLES
 * capabilities, moved out of the sender's table atomically with the message and into the receiver's table
 * atomically with its delivery (RFC 0006 sections 13, 33 and 35). The kernel keeps message boundaries and knows
 * nothing of what the bytes mean. */
#define WIT_CHANNEL_MESSAGE_VERSION 1U
#define WIT_CHANNEL_MESSAGE_SIZE 40U

/* The request of CHANNEL_SEND (the bytes at Data and the handles at Handles to move) and of CHANNEL_RECEIVE (the
 * buffers at Data and Handles with Bytes and HandleCount as their capacities). CHANNEL_RECEIVE returns the bytes
 * received in the low 32 bits of its value and the handles received in the high 32 bits. */
typedef struct WitChannelMessage {
    WitU32 Version, Size;
    WitU64 Data;
    WitU64 Handles;
    WitU32 Bytes, HandleCount;
    WitU32 Flags, Reserved;
} WitChannelMessage;

WIT_STATIC_ASSERT(sizeof(WitChannelMessage) == WIT_CHANNEL_MESSAGE_SIZE, "Channel message request ABI");

/* Kernel-internal: a capability in flight, as the receiver's table will hold it; a thread handle carries its
 * record. */
typedef struct WitChannelCapability {
    WitU32 Kind, Rights;
    WitU64 Object;
    WitU64 ThreadId, ExitCode;
    WitU32 Exited, Reserved;
} WitChannelCapability;

typedef struct WitChannelQueued {
    WitU32 Bytes, HandleCount;
    WitU8 Data[WIT_CHANNEL_MESSAGE_BYTES];
    WitChannelCapability Handles[WIT_CHANNEL_MESSAGE_HANDLES];
} WitChannelQueued;

/* One end of a channel: the handles that refer to it (in tables and in flight), and the messages the peer sent it,
 * oldest first from Head. A closed end has no handles and an empty queue. */
typedef struct WitChannelEndpoint {
    WitU32 Live, Handles, Head, Count, Bytes, Reserved;
    WitChannelQueued Queue[WIT_CHANNEL_QUEUE_DEPTH];
} WitChannelEndpoint;

typedef struct WitChannel {
    WitU32 Live, Reserved;
    WitChannelEndpoint Ends[2];
} WitChannel;

typedef struct WitChannelTable {
    WitChannel Entries[WIT_CHANNEL_CAPACITY];
    WitU32 Count, Limit;
} WitChannelTable;

/* Serialized, component-local state; the handle's Object names the endpoint: channel index times two plus the end
 * plus one. */
void wit_channels_initialize(WitChannelTable *table);
WitU64 wit_channel_object(WitU32 channel, WitU32 end);
/* The channel and end behind a live handle of the endpoint kind with the rights. */
WitU64 wit_channel_get(
    WitChannelTable *table, WitHandleTable *handles, WitU64 handle, WitU32 rights, WitChannel **channel, WitU32 *end);
/* The channel and end an object number names, or zero. */
WitChannel *wit_channel_slot(WitChannelTable *table, WitU64 object, WitU32 *end);
#endif
