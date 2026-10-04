#include "gcenv.witos.h"
#include "../Runtime.Native/diagnostics.h"
extern "C" void wit_native_gc_breakpoint();

void *GCToOSInterface::VirtualReserveAndCommitLargePages(size_t size, uint16_t node)
{
    (void)size;
    (void)node;
    // This kernel owns ordinary 4 KiB mappings only. Returning ordinary pages
    // here would make upstream skip required commit/decommit operations.
    return nullptr;
}

bool GCToOSInterface::GetWriteWatch(bool reset, void *address, size_t size, void **pages, uintptr_t *count)
{
    (void)reset;
    (void)address;
    (void)size;
    (void)pages;
    (void)count;
    // No OS write-watch facility exists. Preserve every output on refusal.
    // RuntimeGcPolicy verifies that selected upstream heap/card-table paths
    // use software/manual tracking and have no reachable OS watch call site.
    return false;
}

void GCToOSInterface::ResetWriteWatch(void *address, size_t size)
{
    (void)address;
    (void)size;
    // This void interface cannot report unsupported. A reached call indicates
    // a violated GC profile: never silently pretend dirty state was reset.
    wit_native_fail_fast(WIT_NATIVE_GC_WRITE_WATCH_EXIT);
}

void GCToOSInterface::DebugBreak()
{
    wit_native_gc_breakpoint();
}
