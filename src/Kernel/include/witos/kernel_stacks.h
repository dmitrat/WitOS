#ifndef WITOS_KERNEL_STACKS_H
#define WITOS_KERNEL_STACKS_H

#include "types.h"
#include "limits.h"

/* The kernel stacks of user threads (plan step P1.5): one 64 KiB stack for each thread of every registry slot, in a
 * window of the kernel half that every address space shares (top-level entry 320 on the three ISAs, above the package
 * and the root task image, whose subtree each user root shares), a guard page below and above each, which is never
 * mapped. A stack's pages come from the physical allocator when a thread of its slot and index is created and go back
 * once no processor runs on it any more; the window's tables are made at boot, before any user root, so that a mapping
 * at run time changes leaves alone and needs no allocation of its own. The static arrays per slot, 28 MiB of the x64
 * image with twelve slots, are gone. */
#define WIT_KERNEL_STACK_WINDOW 0xFFFFA00040000000ULL
#define WIT_KERNEL_STACK_BYTES (64U * 1024U)
#define WIT_KERNEL_STACK_PAGES (WIT_KERNEL_STACK_BYTES / 4096U)
#define WIT_KERNEL_STACK_STRIDE (WIT_KERNEL_STACK_BYTES + 8192U) /* the guard below, the stack, the guard above */
#define WIT_KERNEL_STACK_WINDOW_SIZE \
    ((WitU64)WIT_PROCESS_CAPACITY * WIT_PROCESS_THREAD_CAPACITY * WIT_KERNEL_STACK_STRIDE)

/* The lowest address of the stack of a slot's thread; its top lies WIT_KERNEL_STACK_BYTES above. */
WitU64 wit_kernel_stack_low(WitU32 slot, WitU32 thread);
/* The stack of a slot's thread has its pages, from the allocator or kept since its last thread: 0 when the allocator
 * has none left, which refuses the thread. */
int wit_kernel_stack_ensure(WitU32 slot, WitU32 thread);
/* The stack's thread is gone, or every thread of a slot (WIT_PROCESS_THREAD_CAPACITY): its pages go once no processor
 * runs on it (wit_kernel_stacks_collect). */
void wit_kernel_stack_release(WitU32 slot, WitU32 thread);
void wit_kernel_stack_release_slot(WitU32 slot);
/* The stack the calling processor runs on from now, or none (WIT_PROCESS_CAPACITY as the slot): the dispatcher names
 * it when it takes a thread and when it idles or returns to the host. */
void wit_kernel_stack_running(WitU32 slot, WitU32 thread);
/* Returns the pages of every released stack no processor runs on; each processor's translation of them goes first. */
void wit_kernel_stacks_collect(void);
/* Pages the stacks hold, for the self-tests. */
WitU64 wit_kernel_stacks_pages(void);

#endif
