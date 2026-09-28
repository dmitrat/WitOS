#include "common.h"
#include "gcenv.h"
#include "gcenv.ee.h"
#include "gcconfig.h"
#include "pal.witos.h"
#include "tls.h"
#include "protocol.h"
#include <errno.h>

static bool cases(int saved)
{
    struct Entry { const char* text; bool ok; size_t first, last, consumed; bool overflow; };
    const Entry entries[] = {
        {"0", true, 0, 0, 1, false}, {"12-34,5", true, 12, 34, 5, false},
        {" +7", true, 7, 7, 3, false}, {"-1", true, 0xffffffff, 0xffffffff, 2, false},
        {"4294967296", true, 0xffffffff, 0xffffffff, 10, true},
        {"1-0", true, 1, 0, 3, false}, {"0x1", true, 0, 0, 1, false},
        {"1-2-3", true, 1, 2, 3, false}, {"", false, 91, 92, 0, false},
        {"x", false, 91, 92, 0, false}, {"2-", false, 91, 92, 0, false},
        {"2-x", false, 91, 92, 0, false}
    };
    for (const auto& e : entries) {
        errno = saved;
        const char* next = e.text;
        size_t first = 91, last = 92;
        if (GCToOSInterface::ParseGCHeapAffinitizeRangesEntry(&next, &first, &last) != e.ok ||
            first != e.first || last != e.last || next != e.text + e.consumed ||
            errno != (e.overflow ? ERANGE : saved)) return false;
    }
    struct Set { const char* text; bool ok; uintptr_t mask; unsigned first, last; bool empty; };
    const Set sets[] = {
        {"0", true, 1, 0, 0, false}, {"0-3,2", true, 15, 0, 3, false},
        {"63-65", true, (1ULL << 63) | 3, 63, 65, false},
        {"1023", true, 1ULL << 63, 1023, 1023, false},
        {"0,", false, 1, 0, 0, false}, {"0,x", false, 1, 0, 0, false},
        {"0:0", false, 1, 0, 0, false}, // Flat parser stops at ':', full parser rejects suffix.
        {"1-0", false, 0, 0, 0, true}, {"1024", false, 0, 0, 0, true},
        {"-1", false, 0, 0, 0, true}, {"", true, 0, 0, 0, true},
        {"x", false, 0, 0, 0, true}, {"0 ", false, 1, 0, 0, false}
    };
    static_assert(MAX_SUPPORTED_CPUS == 1024);
    for (const auto& e : sets) {
        errno = saved;
        AffinitySet set;
        uintptr_t mask = 0;
        if (ParseGCHeapAffinitizeRanges(e.text, &set, mask) != e.ok || mask != e.mask ||
            set.IsEmpty() != e.empty || errno != saved) return false;
        for (unsigned i = 0; i < MAX_SUPPORTED_CPUS; ++i)
            if (set.Contains(i) != (!e.empty && i >= e.first && i <= e.last)) return false;
    }
    errno = saved;
    AffinitySet set;
    uintptr_t mask = 4;
    if (!ParseGCHeapAffinitizeRanges(nullptr, &set, mask) || mask != 4 || !set.IsEmpty() ||
        !ParseGCHeapAffinitizeRanges("invalid", &set, mask) || mask != 4 || !set.IsEmpty() ||
        GCToOSInterface::CanEnableGCCPUGroups()) return false;
    return errno == saved;
}
static WitU64 worker(WitU64 index)
{
    const DWORD error = (DWORD)(800 + index);
    const int saved = (int)(900 + index);
    SetLastError(error); errno = saved;
    for (unsigned i = 0; i < 3; ++i)
        if (!cases(saved) || wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK ||
            errno != saved || GetLastError() != error) return 2001;
    return WIT_TEST_EXIT_CODE;
}
extern "C" bool wit_test_affinity(bool threads)
{
    SetLastError(0x62481357); errno = 77;
    if (!cases(77)) return false;
    WitU64 arena = 0;
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, 8192, 4096, 0, &arena) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_COMMIT, arena, 4096, 3, nullptr) != WIT_STATUS_OK) return false;
    char* edge = (char*)(arena + 4094);
    edge[0] = '0'; edge[1] = 0;
    if (wit_native_call(WIT_CALL_MEMORY_PROTECT, arena, 4096, WIT_MEMORY_READ, nullptr) != WIT_STATUS_OK) return false;
    AffinitySet set;
    uintptr_t mask = 0;
    if (!ParseGCHeapAffinitizeRanges(edge, &set, mask) || mask != 1 || !set.Contains(0) ||
        wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) return false;
    if (threads) {
        WitU64 handles[3], result;
        for (WitU64 i = 0; i < 3; ++i) if (wit_native_thread_create(worker, i, &handles[i]) != WIT_STATUS_OK) return false;
        for (unsigned i = 0; i < 3; ++i)
            if (wit_native_call(WIT_CALL_THREAD_JOIN, handles[i], 0, 0, &result) != WIT_STATUS_OK || result != WIT_TEST_EXIT_CODE) return false;
    }
    return errno == 77 && GetLastError() == 0x62481357;
}
