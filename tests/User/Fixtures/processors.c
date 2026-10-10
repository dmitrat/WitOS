#include "fixture.h"
#include "witos/processor.h"
#include "witos/thread_reference.h"

/* Processor fixture (RFC 0011 v3 section 7.9, plan steps K7.1 and K8.3): PROCESSOR_QUERY in the record form, the
 * refusals of a wrong size and a foreign version; THREAD_AFFINITY of the current thread: the default mask, a set within
 * the table, an empty mask and a mask beyond the table refused, a foreign flag refused, and a duplicate without the
 * right that reads the mask but may not set it. */

static void query(WitProcessorInfo *info, WitU64 size, WitU64 expected)
{
    const WitU64 copied = fixture_expect(WIT_CALL_PROCESSOR_QUERY, (WitU64)info, size, 0, expected);
    fixture_check(expected != WIT_STATUS_OK || copied == WIT_PROCESSOR_INFO_SIZE, 2);
}

static void affinity(WitU64 thread, WitU64 *mask, WitU64 flags, WitU64 expected)
{
    fixture_expect(WIT_CALL_THREAD_AFFINITY, thread, (WitU64)mask, flags, expected);
}

FIXTURE_ENTRY void wit_user_start(const WitUserStartup *startup)
{
    WitProcessorInfo info;
    WitU64 mask = 0, reader = 0;
    fixture_check(startup->Version == WIT_ABI_VERSION, 1);

    /* The record form: the boot processor first, online, among the processors present. */
    info.Version = WIT_PROCESSOR_INFO_VERSION;
    info.Size = WIT_PROCESSOR_INFO_SIZE;
    query(&info, WIT_PROCESSOR_INFO_SIZE, WIT_STATUS_OK);
    fixture_check(info.Version == WIT_PROCESSOR_INFO_VERSION && info.Current == 0, 3);
    fixture_check(info.Count >= 1 && info.Online >= 1 && info.Online <= info.Count, 4);
    fixture_check(
        info.Processors[0].Flags == (WIT_PROCESSOR_ONLINE | WIT_PROCESSOR_BOOT) && info.Processors[0].Number == 0, 5);

    /* A wrong size and a foreign version are refused. */
    query(&info, 100, WIT_STATUS_INVALID_ARGUMENT);
    info.Version = 2;
    query(&info, WIT_PROCESSOR_INFO_SIZE, WIT_STATUS_UNSUPPORTED);

    /* Affinity: the boot processor by default; a set of the same is accepted; an empty mask, a mask beyond the table
     * and a foreign flag are refused. */
    affinity(WIT_THREAD_SELF, &mask, WIT_THREAD_AFFINITY_GET, WIT_STATUS_OK);
    fixture_check(mask == 1, 6);
    affinity(WIT_THREAD_SELF, &mask, WIT_THREAD_AFFINITY_SET, WIT_STATUS_OK);
    mask = 0;
    affinity(WIT_THREAD_SELF, &mask, WIT_THREAD_AFFINITY_SET, WIT_STATUS_INVALID_ARGUMENT);
    mask = 1ULL << 63;
    affinity(WIT_THREAD_SELF, &mask, WIT_THREAD_AFFINITY_SET, WIT_STATUS_INVALID_ARGUMENT);
    mask = 1;
    affinity(WIT_THREAD_SELF, &mask, 2, WIT_STATUS_INVALID_ARGUMENT);

    /* A thread handle without the AFFINITY right reads the mask but may not set it. */
    fixture_expect(WIT_CALL_HANDLE_DUPLICATE, WIT_THREAD_SELF, (WitU64)&reader, WIT_RIGHT_QUERY, WIT_STATUS_OK);
    mask = 0;
    affinity(reader, &mask, WIT_THREAD_AFFINITY_GET, WIT_STATUS_OK);
    fixture_check(mask == 1, 7);
    affinity(reader, &mask, WIT_THREAD_AFFINITY_SET, WIT_STATUS_DENIED);
    fixture_expect(WIT_CALL_HANDLE_CLOSE, reader, 0, 0, WIT_STATUS_OK);
    fixture_exit(WIT_TEST_EXIT_CODE);
}
