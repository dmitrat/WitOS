#include "user.h"
#include "witos/platform.h"
#include "self_test.h"

/* Contained faults the tests accepted. A component faults at most once, so repeated checks of the same component
 * count once; the summary then matches the kernel's count only if every contained fault was expected by a test. */
static WitU64 checked_faults;
static WitU32 last_checked_id;

int wit_test_faulted(const WitUserProcess *process)
{
    if (process->State != WitUserFaulted) {
        return 0;
    }
    if (process->Id != last_checked_id) {
        last_checked_id = process->Id;
        ++checked_faults;
    }
    return 1;
}

void wit_test_summary(void)
{
    wit_console_write("[TEST-SUMMARY] faults=");
    wit_console_write_u64(wit_user_contained_faults());
    wit_console_write(" checked=");
    wit_console_write_u64(checked_faults);
    wit_console_write("\n");
}
