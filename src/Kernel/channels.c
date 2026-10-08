#include "witos/channels.h"
#include "witos/platform.h"

/* The kernel's channel table (K5.2c): one number names an endpoint in every process, so the two ends of a channel
 * may be held by different processes. A channel is charged to its creator's quota of live channels while the creator
 * lives; the creator's teardown orphans what it created and still lives. */

static WitChannelTable channels;

WitU64 wit_channel_object(WitU32 channel, WitU32 end)
{
    return (WitU64)channel * 2 + end + 1;
}

WitChannel *wit_channel_at(WitU32 index)
{
    return index < WIT_CHANNEL_TABLE_CAPACITY ? &channels.Entries[index] : 0;
}

WitChannel *wit_channel_slot(WitU64 object, WitU32 *end)
{
    *end = 0;
    if (!object || object > (WitU64)WIT_CHANNEL_TABLE_CAPACITY * 2) {
        return 0;
    }
    WitChannel *channel = &channels.Entries[(object - 1) / 2];
    *end = (WitU32)((object - 1) % 2);
    return channel->Live && channel->Ends[*end].Live ? channel : 0;
}

WitU64 wit_channel_get(WitHandleTable *handles, WitU64 handle, WitU32 rights, WitChannel **channel, WitU32 *end)
{
    WitU64 object = 0;
    WitU32 granted = 0;
    *channel = 0;
    *end = 0;
    const WitU64 status = wit_handle_check(handles, handle, WIT_HANDLE_CHANNEL_ENDPOINT, rights);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!wit_handle_describe(handles, handle, WIT_HANDLE_CHANNEL_ENDPOINT, &object, &granted) ||
        !(*channel = wit_channel_slot(object, end))) {
        return WIT_STATUS_BAD_HANDLE;
    }
    return WIT_STATUS_OK;
}

WitU32 wit_channels_live(void)
{
    return channels.Count;
}

WitU32 wit_channels_charged(const struct WitUserProcess *creator)
{
    WitU32 count = 0;
    for (WitU32 i = 0; i < WIT_CHANNEL_TABLE_CAPACITY; ++i) {
        if (channels.Entries[i].Live && channels.Entries[i].Creator == creator) {
            ++count;
        }
    }
    return count;
}

WitChannel *wit_channel_reserve(struct WitUserProcess *creator, WitU32 *index)
{
    *index = 0;
    if (wit_channels_charged(creator) >= WIT_CHANNEL_CAPACITY) {
        return 0;
    }
    for (WitU32 i = 0; i < WIT_CHANNEL_TABLE_CAPACITY; ++i) {
        if (!channels.Entries[i].Live) {
            *index = i;
            return &channels.Entries[i];
        }
    }
    return 0;
}

void wit_channel_publish(WitChannel *channel, struct WitUserProcess *creator)
{
    for (WitU32 end = 0; end < 2; ++end) {
        channel->Ends[end].Live = 1;
        channel->Ends[end].Handles = 1;
        channel->Ends[end].Head = 0;
        channel->Ends[end].Count = 0;
        channel->Ends[end].Bytes = 0;
        channel->Ends[end].Reserved = 0;
    }
    channel->Creator = creator;
    channel->Reserved = 0;
    channel->Live = 1;
    ++channels.Count;
}

void wit_channel_retire(WitChannel *channel)
{
    if (!channel->Live || channel->Ends[0].Live || channel->Ends[1].Live) {
        wit_panic("Retiring a channel with a live end");
    }
    channel->Live = 0;
    channel->Creator = 0;
    --channels.Count;
}

void wit_channels_orphan(struct WitUserProcess *creator)
{
    for (WitU32 i = 0; i < WIT_CHANNEL_TABLE_CAPACITY; ++i) {
        if (channels.Entries[i].Live && channels.Entries[i].Creator == creator) {
            channels.Entries[i].Creator = 0;
        }
    }
}
