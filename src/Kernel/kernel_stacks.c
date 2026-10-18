#include "witos/kernel_stacks.h"
#include "witos/arch.h"
#include "witos/cpu.h"
#include "witos/memory.h"
#include "witos/platform.h"
#include "witos/processor.h"

/* The kernel stacks of user threads in their window (plan step P1.5, witos/kernel_stacks.h). A stack is backed while
 * any thread of its slot and index may run on it: its pages are taken when such a thread is created and kept for the
 * next one; once released (its thread was reaped or its slot torn down) they go back at a point where no processor runs
 * on it — never at the release itself, since the processor that reaps an exiting thread, or abandons the thread of a
 * component another processor ended, still runs on that thread's stack. Each processor names the stack it runs on;
 * the dispatcher changes it only under the kernel lock, which a processor gives up once it runs on the next frame's
 * stack, so a collection under the lock sees where every processor runs. Every processor drops a collected page's
 * translation before the page goes back, since the thread may have run anywhere. */

#define NONE (WIT_PROCESS_CAPACITY * WIT_PROCESS_THREAD_CAPACITY)

static WitU8 backed[WIT_PROCESS_CAPACITY][WIT_PROCESS_THREAD_CAPACITY];
static WitU8 released[WIT_PROCESS_CAPACITY][WIT_PROCESS_THREAD_CAPACITY];
static WitU32 running[WIT_PROCESSOR_CAPACITY] = {NONE, NONE, NONE, NONE, NONE, NONE, NONE, NONE};
static WitU32 releases; /* stacks released and not collected */
static WitU64 pages_held;

WIT_STATIC_ASSERT(WIT_PROCESSOR_CAPACITY == 8U, "running names a stack for each of eight processors");
WIT_STATIC_ASSERT(WIT_KERNEL_STACK_WINDOW_SIZE <= 0x40000000ULL, "The stack window fits its gigabyte");

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

WitU64 wit_kernel_stack_low(WitU32 slot, WitU32 thread)
{
    require(slot < WIT_PROCESS_CAPACITY && thread < WIT_PROCESS_THREAD_CAPACITY, "A kernel stack outside the window");
    return WIT_KERNEL_STACK_WINDOW +
        ((WitU64)slot * WIT_PROCESS_THREAD_CAPACITY + thread) * WIT_KERNEL_STACK_STRIDE +
        4096U;
}

int wit_kernel_stack_ensure(WitU32 slot, WitU32 thread)
{
    const WitU64 low = wit_kernel_stack_low(slot, thread);
    if (backed[slot][thread]) {
        if (released[slot][thread]) {
            released[slot][thread] = 0; /* kept for the slot's new thread */
            --releases;
        }
        return 1;
    }
    WitU64 frames[WIT_KERNEL_STACK_PAGES];
    for (WitU32 i = 0; i < WIT_KERNEL_STACK_PAGES; ++i) {
        if (!wit_page_allocate(wit_physical_pages(), &frames[i])) {
            while (i) {
                require(wit_page_free(wit_physical_pages(), frames[--i]), "A kernel stack page went missing");
            }
            return 0;
        }
    }
    for (WitU32 i = 0; i < WIT_KERNEL_STACK_PAGES; ++i) {
        wit_arch_kernel_window_set(low + (WitU64)i * 4096U, frames[i]);
    }
    backed[slot][thread] = 1;
    pages_held += WIT_KERNEL_STACK_PAGES;
    return 1;
}

void wit_kernel_stack_release(WitU32 slot, WitU32 thread)
{
    require(slot < WIT_PROCESS_CAPACITY && thread < WIT_PROCESS_THREAD_CAPACITY, "A kernel stack outside the window");
    if (backed[slot][thread] && !released[slot][thread]) {
        released[slot][thread] = 1;
        ++releases;
    }
}

void wit_kernel_stack_release_slot(WitU32 slot)
{
    for (WitU32 thread = 0; thread < WIT_PROCESS_THREAD_CAPACITY; ++thread) {
        wit_kernel_stack_release(slot, thread);
    }
}

void wit_kernel_stack_running(WitU32 slot, WitU32 thread)
{
    running[wit_processors_current()] = slot < WIT_PROCESS_CAPACITY && thread < WIT_PROCESS_THREAD_CAPACITY
        ? slot * WIT_PROCESS_THREAD_CAPACITY + thread
        : NONE;
}

static int run_on(WitU32 index)
{
    for (WitU32 i = 0; i < WIT_PROCESSOR_CAPACITY; ++i) {
        if (running[i] == index && wit_cpus_is_online(i)) {
            return 1;
        }
    }
    return 0;
}

void wit_kernel_stacks_collect(void)
{
    if (!releases) {
        return;
    }
    const int held = wit_cpus_lock_held(); /* the host's calls between runs come without it */
    if (!held) {
        wit_cpus_lock();
    }
    for (WitU32 index = 0; index < NONE && releases; ++index) {
        const WitU32 slot = index / WIT_PROCESS_THREAD_CAPACITY, thread = index % WIT_PROCESS_THREAD_CAPACITY;
        if (!released[slot][thread] || run_on(index)) {
            continue;
        }
        const WitU64 low = wit_kernel_stack_low(slot, thread);
        WitU64 frames[WIT_KERNEL_STACK_PAGES];
        for (WitU32 i = 0; i < WIT_KERNEL_STACK_PAGES; ++i) {
            frames[i] = wit_arch_kernel_window_clear(low + (WitU64)i * 4096U);
            if (wit_cpus_online() > 1) {
                wit_cpus_invalidate_all(low + (WitU64)i * 4096U);
            }
        }
        for (WitU32 i = 0; i < WIT_KERNEL_STACK_PAGES; ++i) {
            require(wit_page_free(wit_physical_pages(), frames[i]), "A kernel stack page went missing");
        }
        backed[slot][thread] = 0;
        released[slot][thread] = 0;
        pages_held -= WIT_KERNEL_STACK_PAGES;
        --releases;
    }
    if (!held) {
        wit_cpus_unlock();
    }
}

WitU64 wit_kernel_stacks_pages(void)
{
    return pages_held;
}
