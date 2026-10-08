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
 * record. Origin is the Id of the process whose table it left: a capability of a kind whose record belongs to that
 * process (an event, a device, an interrupt binding, a pin) is received by that process alone and returns to it when
 * dropped (K5.2c); a voided capability (Kind zero) delivers nothing. */
struct WitUserProcess;

typedef struct WitChannelCapability {
    WitU32 Kind, Rights;
    WitU64 Object;
    WitU64 ThreadId, ExitCode;
    WitU32 Exited, Origin;
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

/* A channel is charged to the process that created it while that process lives (Creator; zero once it is torn
 * down, K5.2c). */
typedef struct WitChannel {
    WitU32 Live, Reserved;
    struct WitUserProcess *Creator;
    WitChannelEndpoint Ends[2];
} WitChannel;

/* The table is the kernel's (K5.2c): one number names an endpoint in every process. */
typedef struct WitChannelTable {
    WitChannel Entries[WIT_CHANNEL_TABLE_CAPACITY];
    WitU32 Count, Reserved;
} WitChannelTable;

/* Serialized kernel state; the handle's Object names the endpoint: channel index times two plus the end plus one. */
WitU64 wit_channel_object(WitU32 channel, WitU32 end);
/* A channel record by index, live or not; zero beyond the table. */
WitChannel *wit_channel_at(WitU32 index);
/* The channel and end an object number names, or zero. */
WitChannel *wit_channel_slot(WitU64 object, WitU32 *end);
/* The channel and end behind a live handle of the endpoint kind with the rights. */
WitU64 wit_channel_get(WitHandleTable *handles, WitU64 handle, WitU32 rights, WitChannel **channel, WitU32 *end);
/* A free record within the creator's quota of live channels (WIT_CHANNEL_CAPACITY), or zero; its publication with
 * both ends live and one handle each; its retirement once both ends are closed. */
WitChannel *wit_channel_reserve(struct WitUserProcess *creator, WitU32 *index);
void wit_channel_publish(WitChannel *channel, struct WitUserProcess *creator);
void wit_channel_retire(WitChannel *channel);
/* The live channels kernel-wide, those a process created and still live, and the end of that charge. */
WitU32 wit_channels_live(void);
WitU32 wit_channels_charged(const struct WitUserProcess *creator);
void wit_channels_orphan(struct WitUserProcess *creator);
#endif
