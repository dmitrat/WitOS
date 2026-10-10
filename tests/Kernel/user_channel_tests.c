#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "self_test.h"
#include "user_channel_image.h"

/* Channels (RFC 0011 section 7.6) from user mode on both ISAs. The channel fixture creates channels, sends and
 * receives messages with their boundaries, moves events, endpoints and a thread handle through them, waits on an
 * endpoint for a message and for the peer's close from another thread, exhausts the channel, queue and handle quotas
 * without losing anything, and drops a queued message with the capability it carried. */

static WitUserProcess process;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void create(WitPageAllocator *pages, WitU64 mode)
{
    WitUserTestConfig *info;
    require(wit_test_create_fixture(&process, pages, 0, wit_user_channel_image, sizeof(wit_user_channel_image)),
        "Channel test process creation failed");
    info = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    info->Mode = mode;
}

void wit_user_channel_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    static const char *names[] = {"ChannelBasic", "ChannelWait", "ChannelTransfer", "ChannelLimits", "ChannelDrop"};
    for (WitU64 mode = 0; mode < sizeof(names) / sizeof(names[0]); ++mode) {
        create(pages, mode);
        wit_user_run(&process);
        if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("Channel test mode/state/code: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write(" last status: "); /* The fixture records the status its failed check saw. */
            wit_console_write_u64(*(const WitU64 *)wit_user_space_physical(&process.Space, WIT_USER_DATA + 1304, 0, 0));
            wit_console_write("\n");
            wit_panic("User channel test failed");
        }
        require(process.Handles.Count == 0 &&
                process.Events.Count == 0 &&
                wit_channels_live() == 0 &&
                wit_channels_charged(&process) == 0,
            "Channel scenario left objects behind");
        for (WitU32 i = 0; i < WIT_CHANNEL_TABLE_CAPACITY; ++i) {
            require(!wit_channel_at(i)->Live && !wit_channel_at(i)->Ends[0].Live && !wit_channel_at(i)->Ends[1].Live,
                "Channel slot survived its last handle");
        }
        if (mode == WIT_CHANNEL_TEST_BASIC) {
            /* Two framed messages, four of the depth test and two of the peer-close test are sent; six are received,
             * and the two left in the closed peer's queue are dropped. */
            require(process.ChannelSends == 8 && process.ChannelReceives == 6 && process.ChannelDrops == 2,
                "Channel message accounting failed");
        }
        if (mode == WIT_CHANNEL_TEST_WAIT) {
            /* The receiver parks twice, for the message and for the peer's close; the sender yields between them. */
            require(process.EventParks >= 2 &&
                    process.EventWakes >= 2 &&
                    process.ThreadReaps == 1 &&
                    process.ChannelSends == 1 &&
                    process.ChannelReceives == 1,
                "Endpoint wait did not park and wake");
        }
        if (mode == WIT_CHANNEL_TEST_TRANSFER) {
            require(process.ThreadReaps == 1 && process.ChannelDrops == 0, "Capability transfer accounting failed");
        }
        if (mode == WIT_CHANNEL_TEST_DROP) {
            require(process.ChannelDrops == 1 && process.ChannelReceives == 0, "Dropped message was not counted");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Channel scenario leaked physical pages");
        wit_console_write("[TEST-PASS] User.");
        wit_console_write(names[mode]);
        wit_console_write("\n");
    }
}
