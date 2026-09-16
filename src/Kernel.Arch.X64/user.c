#include "user.h"
#include "witos/platform.h"

unsigned __int64 __readcr3(void);
#pragma intrinsic(__readcr3)

static WitUserProcess *current_user;
static WitUserProcess *slot_owners[2];
static WitU32 next_id = 1;

static void require(int condition, const char *message)
{
    if (!condition) wit_panic(message);
}

static WitU64 stack_low(const WitUserProcess *process)
{
    return (WitU64)wit_x64_user_kernel_stacks[process->Slot] + 4096;
}

static int frame_inside_kernel_stack(const void *frame, WitU64 size)
{
    const WitU64 low = stack_low(current_user);
    return (WitU64)frame >= low && (WitU64)frame <= low + WIT_KERNEL_STACK_SIZE - size;
}

static WIT_NORETURN void finish(WitUserState state, WitU64 code)
{
    require(current_user != 0 && current_user->State == WitUserRunning, "No current user component");
    wit_x64_timer_stop();
    current_user->State = state;
    current_user->ExitCode = code;
    wit_handles_close_all(&current_user->Handles);
    current_user = 0;
    wit_x64_leave_user();
}

static void validate_return(WitInterruptContext *context, int syscall)
{
    require(frame_inside_kernel_stack(context, sizeof(*context)) &&
        ((WitU64)context & 15) == 0, "User trap outside kernel stack");
    if (context->Cs != WIT_USER_CS || context->Ss != WIT_USER_SS ||
        context->Rsp < WIT_USER_STACK_BOTTOM || context->Rsp >= WIT_USER_STACK_TOP ||
        !wit_user_space_physical(&current_user->Space, context->Rsp, 1, 0) ||
        !wit_user_space_physical(&current_user->Space, context->Rip, 0, 1)) {
        finish(WitUserBadReturn, 0);
    }
    /* Do not return NT/IOPL/VM/TF or a noncanonical stack to IRETQ.
     * Timer returns preserve condition codes and DF; syscall flags are clobbered. */
    context->Rflags = syscall ? 0x202 : (context->Rflags & 0x200CD5ULL) | 0x202;
}

int wit_user_create(WitUserProcess *process, WitPageAllocator *allocator,
    WitU32 slot, const WitU8 *code, WitU32 code_size)
{
    WitUserStartup *startup;
    WitInterruptContext *context;
    WitU64 physical;
    if (current_user || slot >= 2 || slot_owners[slot] || !next_id ||
        code == 0 || code_size == 0 || code_size > 4096) return 0;
    process->Id = next_id++;
    process->Slot = slot;
    process->State = WitUserEmpty;
    process->Writes = 0;
    process->Ticks = 0;
    process->ExitCode = 0;
    process->FaultVector = 0;
    process->FaultError = 0;
    process->FaultAddress = 0;
    process->FaultRip = 0;
    process->FaultCs = 0;
    process->FaultSs = 0;
    wit_handles_initialize(&process->Handles, process->Id);
    slot_owners[slot] = process;
    if (!wit_user_space_create(&process->Space, allocator) ||
        !wit_user_space_map(&process->Space, WIT_USER_CODE, 0, 1) ||
        !wit_user_space_map(&process->Space, WIT_USER_INFO, 0, 0)) goto failed;
    for (WitU64 page = WIT_USER_DATA; page < WIT_USER_DATA_END; page += 4096)
        if (!wit_user_space_map(&process->Space, page, 1, 0)) goto failed;
    for (WitU64 page = WIT_USER_STACK_BOTTOM; page < WIT_USER_STACK_TOP; page += 4096)
        if (!wit_user_space_map(&process->Space, page, 1, 0)) goto failed;

    physical = wit_user_space_physical(&process->Space, WIT_USER_CODE, 0, 1);
    for (WitU32 i = 0; i < code_size; ++i) ((WitU8 *)physical)[i] = code[i];
    startup = (WitUserStartup *)wit_user_space_physical(&process->Space, WIT_USER_INFO, 0, 0);
    startup->Version = WIT_ABI_VERSION;
    startup->Size = sizeof(*startup);
    startup->ConsoleHandle = wit_handle_grant(&process->Handles, WIT_HANDLE_CONSOLE, WIT_RIGHT_WRITE);
    if (!startup->ConsoleHandle) goto failed;

    context = (WitInterruptContext *)(stack_low(process) + WIT_KERNEL_STACK_SIZE - 4096);
    for (WitU32 i = 0; i < sizeof(*context); ++i) ((WitU8 *)context)[i] = 0;
    /* A clean architectural FXSAVE image; do not expose kernel SIMD registers. */
    context->FxState[0] = 0x7F;
    context->FxState[1] = 0x03;
    context->FxState[24] = 0x80;
    context->FxState[25] = 0x1F;
    context->Rcx = WIT_USER_INFO;
    context->Rip = WIT_USER_CODE;
    context->Cs = WIT_USER_CS;
    context->Ss = WIT_USER_SS;
    context->Rflags = 0x202;
    context->Rsp = WIT_USER_STACK_TOP - 40;
    process->InitialContext = context;
    process->State = WitUserReady;
    return 1;
failed:
    wit_user_destroy(process);
    return 0;
}

void wit_user_run(WitUserProcess *process)
{
    require(!current_user && process->State == WitUserReady &&
        slot_owners[process->Slot] == process &&
        (wit_x64_read_flags() & 0x200) == 0, "Invalid user launch");
    current_user = process;
    process->State = WitUserRunning;
    process->Ticks = 0;
    wit_x64_set_kernel_stack(stack_low(process) + WIT_KERNEL_STACK_SIZE);
    wit_x64_timer_start();
    wit_x64_run_user(process->InitialContext, process->Space.Root);
    require(!current_user && process->State != WitUserRunning &&
        (__readcr3() & 0x000FFFFFFFFFF000ULL) == wit_virtual_kernel_root() &&
        (wit_x64_read_flags() & 0x200) == 0, "User return did not restore kernel state");
    wit_x64_set_kernel_stack((WitU64)wit_x64_kernel_stack + 4096 + WIT_KERNEL_STACK_SIZE);
}

void wit_user_destroy(WitUserProcess *process)
{
    require(current_user != process && process->State != WitUserRunning, "Destroying running component");
    wit_handles_close_all(&process->Handles);
    wit_user_space_destroy(&process->Space);
    if (process->Slot < 2 && slot_owners[process->Slot] == process) slot_owners[process->Slot] = 0;
    process->State = WitUserEmpty;
}

int wit_user_is_active(void) { return current_user != 0; }

WitInterruptContext *wit_user_timer_tick(WitInterruptContext *context)
{
    require(current_user != 0, "User timer without component");
    validate_return(context, 0);
    if (++current_user->Ticks >= WIT_USER_TICK_BUDGET) finish(WitUserBudgetExpired, 0);
    return context;
}

WitInterruptContext *wit_x64_user_syscall(WitInterruptContext *context)
{
    WitU8 buffer[WIT_ABI_MAX_WRITE];
    WitU64 status;
    WitU64 call, argument0, argument1, argument2;
    require(current_user != 0 && current_user->State == WitUserRunning, "Syscall without component");
    validate_return(context, 1);
    call = context->Rax;
    argument0 = context->Rcx;
    argument1 = context->Rdx;
    argument2 = context->R8;
    context->Rax = WIT_STATUS_OK;
    context->Rdx = 0;
    switch (call) {
    case WIT_CALL_QUERY:
        context->Rdx = WIT_ABI_VERSION;
        break;
    case WIT_CALL_WRITE:
        status = wit_handle_check(&current_user->Handles, argument0, WIT_HANDLE_CONSOLE, WIT_RIGHT_WRITE);
        if (status != WIT_STATUS_OK) { context->Rax = status; break; }
        if (argument2 > WIT_ABI_MAX_WRITE) { context->Rax = WIT_STATUS_TOO_LARGE; break; }
        if (!wit_user_copy_from(&current_user->Space, argument1, buffer, (WitU32)argument2)) {
            context->Rax = WIT_STATUS_BAD_ADDRESS;
            break;
        }
        if (argument2) {
            wit_console_write("[USER] ");
            wit_console_write_buffer(buffer, (WitU32)argument2);
            ++current_user->Writes;
        }
        context->Rdx = argument2;
        break;
    case WIT_CALL_MEMORY_RESERVE:
        context->Rax = wit_user_memory_reserve(&current_user->Space, argument0, argument1, &context->Rdx);
        break;
    case WIT_CALL_MEMORY_COMMIT:
        context->Rax = wit_user_memory_commit(&current_user->Space, argument0, argument1, argument2);
        break;
    case WIT_CALL_MEMORY_DECOMMIT:
        context->Rax = wit_user_memory_decommit(&current_user->Space, argument0, argument1);
        break;
    case WIT_CALL_MEMORY_PROTECT:
        context->Rax = wit_user_memory_protect(&current_user->Space, argument0, argument1, argument2);
        break;
    case WIT_CALL_MEMORY_RELEASE:
        context->Rax = wit_user_memory_release(&current_user->Space, argument0);
        break;
    case WIT_CALL_EXIT:
        finish(WitUserExited, argument0);
    case WIT_CALL_CLOSE:
        context->Rax = wit_handle_close(&current_user->Handles, argument0);
        break;
    default:
        context->Rax = WIT_STATUS_UNSUPPORTED;
        break;
    }
    return context;
}

WIT_NORETURN void wit_user_fault(const WitExceptionFrame *frame, WitU64 address)
{
    require(current_user != 0 && frame_inside_kernel_stack(frame, sizeof(*frame)) &&
        (frame->Cs & 3) == 3, "Invalid user fault frame");
    current_user->FaultVector = frame->Vector;
    current_user->FaultError = frame->Error;
    current_user->FaultAddress = address;
    current_user->FaultRip = frame->Rip;
    current_user->FaultCs = frame->Cs;
    current_user->FaultSs = frame->Ss;
    wit_console_write("[USER-FAULT] id=");
    wit_console_write_u64(current_user->Id);
    wit_console_write(" vector=");
    wit_console_write_u64(frame->Vector);
    wit_console_write(" error=");
    wit_console_write_hex(frame->Error);
    wit_console_write(" address=");
    wit_console_write_hex(address);
    wit_console_write(" cs=");
    wit_console_write_hex(frame->Cs);
    wit_console_write("\n");
    finish(WitUserFaulted, 0);
}
