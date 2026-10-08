#ifndef WITOS_ROOT_TASK_H
#define WITOS_ROOT_TASK_H
#include "witos/flat.h"
#include "witos/memory.h"
#include "witos/user_layout.h"

struct WitBootInfo;
struct WitUserProcess;

/* Kernel-internal: the flat image of the root task and its start (plan step K4). */

/* The fixed window a flat image may occupy: the component's image window above the kernel-provided pages (the
 * startup block, the data pages, the stacks and TLS) and below the component's limit; the PE loader's window, which
 * the root task replaces. */
#define WIT_USER_FLAT_BASE WIT_USER_IMAGE_BASE

/* A validated flat image: its segments as read, the entry, and the file they came from. */
typedef struct WitFlatLayout {
    WitFlatSegment Segments[WIT_FLAT_SEGMENTS];
    WitU64 Entry;
    WitU32 SegmentCount, Reserved;
    const WitU8 *File;
    WitU64 Size;
} WitFlatLayout;

/* Checks the whole image; on success fills the layout and returns nonzero; a failed check changes nothing. */
int wit_flat_validate(const WitU8 *file, WitU64 size, WitFlatLayout *layout);

/* Creates the root task in the slot from a validated flat image with its startup descriptor: the kernel log,
 * the boot package object over the boot extents and the device table with ACQUIRE. Returns nonzero when the
 * process is ready to run; on failure the process is destroyed. */
int wit_root_task_create(struct WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot,
    const struct WitBootInfo *boot, const WitFlatLayout *layout);

/* The release kernel's path: the root task image from the boot contract, run to its exit; returns its exit code,
 * or does not return when the component faults. Without a root task image returns nonzero and does nothing. */
int wit_root_task_run(const struct WitBootInfo *boot, WitPageAllocator *allocator, WitU64 *exit_code);
#endif
