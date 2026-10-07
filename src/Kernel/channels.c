#include "witos/channels.h"

void wit_channels_initialize(WitChannelTable *table)
{
    table->Count = 0;
    table->Limit = WIT_CHANNEL_CAPACITY;
    for (WitU32 i = 0; i < WIT_CHANNEL_CAPACITY; ++i) {
        WitChannel *channel = &table->Entries[i];
        channel->Live = 0;
        channel->Reserved = 0;
        for (WitU32 end = 0; end < 2; ++end) {
            channel->Ends[end].Live = 0;
            channel->Ends[end].Handles = 0;
            channel->Ends[end].Head = 0;
            channel->Ends[end].Count = 0;
            channel->Ends[end].Bytes = 0;
            channel->Ends[end].Reserved = 0;
        }
    }
}

WitU64 wit_channel_object(WitU32 channel, WitU32 end)
{
    return (WitU64)channel * 2 + end + 1;
}

WitChannel *wit_channel_slot(WitChannelTable *table, WitU64 object, WitU32 *end)
{
    *end = 0;
    if (!object || object > (WitU64)table->Limit * 2) {
        return 0;
    }
    WitChannel *channel = &table->Entries[(object - 1) / 2];
    *end = (WitU32)((object - 1) % 2);
    return channel->Live && channel->Ends[*end].Live ? channel : 0;
}

WitU64 wit_channel_get(
    WitChannelTable *table, WitHandleTable *handles, WitU64 handle, WitU32 rights, WitChannel **channel, WitU32 *end)
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
        !(*channel = wit_channel_slot(table, object, end))) {
        return WIT_STATUS_BAD_HANDLE;
    }
    return WIT_STATUS_OK;
}
