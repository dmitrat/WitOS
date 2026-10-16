#include "witos/arch.h"
#include "witos/platform.h"
#include "witos/virtual.h"
#include "user.h"

/* What the RISC-V port does not implement yet (plan step V1.1): every architecture operation the common kernel links
 * beyond identity, bring-up, the interrupt flag and halting panics by name, so that nothing pretends to work — the
 * operations of witos/arch.h and the kernel's translation tables of witos/virtual.h (the architecture's, as A64's
 * paging.c). V1.2 replaces this file with the port, operation by operation. */

#pragma clang diagnostic ignored "-Wunused-parameter"

#define NOT_PORTED(name) wit_panic("RV64: " name " is not ported (plan step V1.2)")

void wit_arch_initialize(void)
{
    NOT_PORTED("wit_arch_initialize");
}

void wit_arch_map_device_page(const struct WitBootInfo *boot, WitU64 physical)
{
    NOT_PORTED("wit_arch_map_device_page");
}

void wit_arch_run_user(WitArchFrame *frame, WitU64 root)
{
    NOT_PORTED("wit_arch_run_user");
}

WIT_NORETURN void wit_arch_leave_user(void)
{
    NOT_PORTED("wit_arch_leave_user");
}

void wit_arch_space_switch(WitU64 root)
{
    NOT_PORTED("wit_arch_space_switch");
}

WIT_NORETURN void wit_arch_resume_frame(WitArchFrame *frame)
{
    NOT_PORTED("wit_arch_resume_frame");
}

void wit_arch_select_thread_stack(WitU32 slot, WitU32 thread)
{
    NOT_PORTED("wit_arch_select_thread_stack");
}

void wit_arch_select_boot_stack(void)
{
    NOT_PORTED("wit_arch_select_boot_stack");
}

void wit_arch_set_user_tls(WitU64 address)
{
    NOT_PORTED("wit_arch_set_user_tls");
}

void wit_arch_reset_user_tls(void)
{
    NOT_PORTED("wit_arch_reset_user_tls");
}

int wit_arch_user_tls_is_reset(void)
{
    NOT_PORTED("wit_arch_user_tls_is_reset");
}

int wit_arch_kernel_space_active(void)
{
    NOT_PORTED("wit_arch_kernel_space_active");
}

void wit_arch_idle_once(void)
{
    NOT_PORTED("wit_arch_idle_once");
}

WitArchFrame *wit_arch_frame_create(WitU32 slot, WitU32 thread, WitU64 entry, WitU64 argument, WitU64 stack_top)
{
    NOT_PORTED("wit_arch_frame_create");
}

WitArchFrame *wit_arch_frame_create_at(WitU32 slot, WitU32 thread, WitU64 entry, WitU64 argument, WitU64 stack_pointer)
{
    NOT_PORTED("wit_arch_frame_create_at");
}

int wit_arch_user_tls_settable(void)
{
    NOT_PORTED("wit_arch_user_tls_settable");
}

int wit_arch_kernel_stack_contains(WitU32 slot, WitU32 thread, const void *object, WitU64 size)
{
    NOT_PORTED("wit_arch_kernel_stack_contains");
}

int wit_arch_frame_owned(const WitArchFrame *frame, WitU32 slot, WitU32 thread)
{
    NOT_PORTED("wit_arch_frame_owned");
}

int wit_arch_frame_from_user(const WitArchFrame *frame)
{
    NOT_PORTED("wit_arch_frame_from_user");
}

int wit_arch_frame_returns_to_user(const WitArchFrame *frame)
{
    NOT_PORTED("wit_arch_frame_returns_to_user");
}

int wit_arch_frame_is_idle(const WitArchFrame *frame, WitU32 slot, WitU32 thread)
{
    NOT_PORTED("wit_arch_frame_is_idle");
}

WitU64 wit_arch_frame_pc(const WitArchFrame *frame)
{
    NOT_PORTED("wit_arch_frame_pc");
}

WitU64 wit_arch_frame_sp(const WitArchFrame *frame)
{
    NOT_PORTED("wit_arch_frame_sp");
}

void wit_arch_frame_prepare_return(WitArchFrame *frame, int syscall)
{
    NOT_PORTED("wit_arch_frame_prepare_return");
}

WitU64 *wit_arch_frame_status(WitArchFrame *frame)
{
    NOT_PORTED("wit_arch_frame_status");
}

WitU64 *wit_arch_frame_value(WitArchFrame *frame)
{
    NOT_PORTED("wit_arch_frame_value");
}

void wit_arch_frame_set_result(WitArchFrame *frame, WitU64 status, WitU64 value)
{
    NOT_PORTED("wit_arch_frame_set_result");
}

WitU64 wit_arch_callback_stack(WitU64 sp, WitU32 *call_frame_bytes)
{
    NOT_PORTED("wit_arch_callback_stack");
}

void wit_arch_frame_enter_callback(
    WitArchFrame *frame, WitU64 entry, WitU64 stack, WitU64 argument0, WitU64 argument1, WitU64 argument2)
{
    NOT_PORTED("wit_arch_frame_enter_callback");
}

void wit_arch_frame_describe(const WitArchFrame *frame)
{
    NOT_PORTED("wit_arch_frame_describe");
}

int wit_arch_context_supported(void)
{
    NOT_PORTED("wit_arch_context_supported");
}

WitU32 wit_arch_context_profile(void)
{
    NOT_PORTED("wit_arch_context_profile");
}

void wit_arch_context_describe(WitThreadContext *context)
{
    NOT_PORTED("wit_arch_context_describe");
}

void wit_arch_context_capture(WitThreadContext *context, const WitArchFrame *frame)
{
    NOT_PORTED("wit_arch_context_capture");
}

int wit_arch_context_registers_valid(const WitThreadContext *context)
{
    NOT_PORTED("wit_arch_context_registers_valid");
}

int wit_arch_context_state_valid(const WitThreadContext *context)
{
    NOT_PORTED("wit_arch_context_state_valid");
}

WitU64 wit_arch_context_pc(const WitThreadContext *context)
{
    NOT_PORTED("wit_arch_context_pc");
}

WitU64 wit_arch_context_sp(const WitThreadContext *context)
{
    NOT_PORTED("wit_arch_context_sp");
}

void wit_arch_context_apply(WitArchFrame *frame, const WitThreadContext *context)
{
    NOT_PORTED("wit_arch_context_apply");
}

void wit_arch_cpu_context_describe(WitCpuContextInfo *info, const WitArchFrame *frame)
{
    NOT_PORTED("wit_arch_cpu_context_describe");
}

int wit_arch_exception_deliverable(WitU64 vector)
{
    NOT_PORTED("wit_arch_exception_deliverable");
}

WitArchExceptionKind wit_arch_exception_kind(WitU64 vector, WitU64 error, WitU64 address)
{
    NOT_PORTED("wit_arch_exception_kind");
}

void wit_arch_exception_record(WitUserExceptionInfo *info, const WitArchFrame *frame, WitU64 address)
{
    NOT_PORTED("wit_arch_exception_record");
}

void wit_arch_fault_from_frame(WitArchFaultState *state, const WitArchFrame *frame)
{
    NOT_PORTED("wit_arch_fault_from_frame");
}

void wit_arch_fault_from_record(WitArchFaultState *state, const WitUserExceptionInfo *record)
{
    NOT_PORTED("wit_arch_fault_from_record");
}

int wit_arch_fault_from_user(const WitArchFaultState *state)
{
    NOT_PORTED("wit_arch_fault_from_user");
}

void wit_arch_fault_describe(const WitArchFaultState *state)
{
    NOT_PORTED("wit_arch_fault_describe");
}

WitU64 wit_arch_page_entry_make(WitU64 physical, WitU32 flags)
{
    NOT_PORTED("wit_arch_page_entry_make");
}

WitU32 wit_arch_page_entry_flags(WitU64 entry)
{
    NOT_PORTED("wit_arch_page_entry_flags");
}

WitU64 wit_arch_page_entry_physical(WitU64 entry)
{
    NOT_PORTED("wit_arch_page_entry_physical");
}

WitU64 *wit_arch_page_entry(struct WitUserSpace *space, WitU64 address, int create)
{
    NOT_PORTED("wit_arch_page_entry");
}

void wit_arch_page_prune(struct WitUserSpace *space, WitU64 address)
{
    NOT_PORTED("wit_arch_page_prune");
}

WitU64 wit_arch_page_next(const struct WitUserSpace *space, WitU64 address, WitU64 end)
{
    NOT_PORTED("wit_arch_page_next");
}

void wit_arch_page_invalidate(const struct WitUserSpace *space, WitU64 address)
{
    NOT_PORTED("wit_arch_page_invalidate");
}

WitU64 wit_arch_page_translate(WitU64 root, WitU64 address, int write, int execute)
{
    NOT_PORTED("wit_arch_page_translate");
}

int wit_arch_space_kernel_ready(void)
{
    NOT_PORTED("wit_arch_space_kernel_ready");
}

void wit_arch_space_install_kernel(WitU64 root)
{
    NOT_PORTED("wit_arch_space_install_kernel");
}

int wit_arch_space_active(WitU64 root)
{
    NOT_PORTED("wit_arch_space_active");
}

void wit_arch_publish_code_page(WitU64 physical)
{
    NOT_PORTED("wit_arch_publish_code_page");
}

void wit_arch_publish_code(void)
{
    NOT_PORTED("wit_arch_publish_code");
}

WitU64 wit_arch_clock_ticks(void)
{
    NOT_PORTED("wit_arch_clock_ticks");
}

WitU64 wit_arch_cache_size(void)
{
    NOT_PORTED("wit_arch_cache_size");
}

WitU64 wit_arch_processor_id(void)
{
    NOT_PORTED("wit_arch_processor_id");
}

WitU64 wit_arch_processor_features(void)
{
    NOT_PORTED("wit_arch_processor_features");
}

void wit_arch_secondary_prepare(const struct WitBootInfo *boot, WitU32 index, WitU64 hardware_id)
{
    NOT_PORTED("wit_arch_secondary_prepare");
}

int wit_arch_secondary_start(WitU32 index, WitU64 hardware_id)
{
    NOT_PORTED("wit_arch_secondary_start");
}

void wit_arch_ipi(WitU64 hardware_id, WitU32 kind)
{
    NOT_PORTED("wit_arch_ipi");
}

void wit_arch_invalidate_local(WitU64 address)
{
    NOT_PORTED("wit_arch_invalidate_local");
}

void wit_arch_process_write_barrier(void)
{
    NOT_PORTED("wit_arch_process_write_barrier");
}

const WitU8 *wit_virtual_boot_storage(void)
{
    NOT_PORTED("wit_virtual_boot_storage");
}

WitU8 *wit_virtual_view(WitU64 physical)
{
    NOT_PORTED("wit_virtual_view");
}

const WitU8 *wit_virtual_root_task(void)
{
    NOT_PORTED("wit_virtual_root_task");
}

void wit_virtual_initialize(const WitBootInfo *boot, WitPageAllocator *allocator)
{
    NOT_PORTED("wit_virtual_initialize");
}

int wit_virtual_map(WitU64 virtual_address, WitU64 physical_address, int writable)
{
    NOT_PORTED("wit_virtual_map");
}

int wit_virtual_protect(WitU64 virtual_address, int writable)
{
    NOT_PORTED("wit_virtual_protect");
}

int wit_virtual_unmap(WitU64 virtual_address)
{
    NOT_PORTED("wit_virtual_unmap");
}

WitU64 wit_virtual_kernel_root(void)
{
    NOT_PORTED("wit_virtual_kernel_root");
}
