#include <intrin.h>
#include <limits.h>
#include "pal.witos.h"
#include "native_ctype.witos.h"
#include "native_heap.witos.h"

/* Win32 functions the pinned microsoft/STL's separately compiled sources call beyond what the NativeAOT runtime uses
 * (P6.4.i), kept out of its archive and probe images. */
extern "C" DWORD WINAPI wit_native_format_message(DWORD, LPCVOID, DWORD, DWORD, LPWSTR, DWORD, va_list *);

static_assert(WIT_NATIVE_PARKING_EVENTS >= WIT_USER_THREAD_CAPACITY, "every thread of the component can park");

namespace {
DWORD fail(DWORD error)
{
    SetLastError(error);
    return 0;
}

[[noreturn]] void fatal()
{
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

/* Parking (P6.4.i2). The kernel has events, not waits on an address, so a thread that waits for an SRW lock or a
 * condition variable parks here: it queues itself with the address and an auto-reset event under the gate, and the
 * thread that wakes it dequeues it, marks it woken and sets the event under the gate. Each record lives on the parked
 * thread's stack until that thread takes the gate again. Events are created when no idle one is left and kept for
 * reuse; at most one per thread is ever in use, and an event goes back idle only unsignaled. The gate is never held
 * across a wait. */
struct Parked {
    const void *Address;
    Parked *Next;
    WitU64 Event;
    bool Woken;
};

volatile WitU32 gate;
Parked *parked; // first in, first out
WitU64 idle[WIT_NATIVE_PARKING_EVENTS];
unsigned idleCount;

void enter()
{
    wit_native_lock(&gate);
}

void leave()
{
    wit_native_unlock(&gate);
}

WitU64 takeEvent()
{
    if (idleCount) {
        return idle[--idleCount];
    }
    WitU64 event = 0;
    // Auto reset keeps a wake that comes before the wait for it. Without an event the thread cannot park.
    if (wit_native_call(WIT_CALL_EVENT_CREATE, 0, 0, 0, &event) != WIT_STATUS_OK || !event) {
        fatal();
    }
    return event;
}

void giveEvent(WitU64 event)
{
    if (idleCount == WIT_NATIVE_PARKING_EVENTS) {
        fatal(); // more parked threads than the component can have
    }
    idle[idleCount++] = event;
}

bool waiting(const void *address)
{
    for (const Parked *thread = parked; thread; thread = thread->Next) {
        if (thread->Address == address) {
            return true;
        }
    }
    return false;
}

/* Wakes the first thread parked on the address, or every one; under the gate. */
void wake(const void *address, bool all)
{
    for (Parked **at = &parked; *at;) {
        Parked *thread = *at;
        if (thread->Address != address) {
            at = &thread->Next;
            continue;
        }
        *at = thread->Next;
        thread->Woken = true;
        if (wit_native_call(WIT_CALL_EVENT_SET, thread->Event, 0, 0, nullptr) != WIT_STATUS_OK) {
            fatal();
        }
        if (!all) {
            return;
        }
    }
}

extern "C" void WINAPI wit_native_srw_release(PSRWLOCK lock);

/* Parks the calling thread on the address until a wake or the timeout; entered with the gate held, returns without
 * it. A given SRW lock is released once the thread is queued, so a wake that follows the release finds the thread.
 * Returns whether a wake ended the wait. */
bool park(const void *address, DWORD milliseconds, PSRWLOCK release)
{
    Parked self = {address, nullptr, takeEvent(), false};
    Parked **at = &parked;
    while (*at) {
        at = &(*at)->Next;
    }
    *at = &self;
    leave();
    if (release) {
        wit_native_srw_release(release);
    }
    const uint32_t result = PalWaitForSingleObjectEx((HANDLE)(uintptr_t)self.Event, milliseconds, FALSE);
    if (result != WAIT_OBJECT_0 && result != WAIT_TIMEOUT) {
        fatal();
    }
    enter();
    if (!self.Woken) {
        // Only a wake sets the event, and a thread that was not woken is still queued.
        bool found = false;
        for (at = &parked; *at; at = &(*at)->Next) {
            if (*at == &self) {
                *at = self.Next;
                found = true;
                break;
            }
        }
        if (result == WAIT_OBJECT_0 || !found) {
            fatal();
        }
    } else if (result == WAIT_TIMEOUT &&
        wit_native_call(WIT_CALL_EVENT_RESET, self.Event, 0, 0, nullptr) != WIT_STATUS_OK) {
        fatal(); // the wake came after the timeout; its signal must not reach the event's next user
    }
    giveEvent(self.Event);
    leave();
    return self.Woken;
}

/* SRW lock state: the lock bit and, while threads may be parked on the lock, the contended bit, which changes only
 * under the gate. Shared mode is not implemented, so any other bit means the memory is not a lock of this kind. */
constexpr LONG64 LOCKED = 1, CONTENDED = 2;

volatile LONG64 &state(PSRWLOCK lock)
{
    return *reinterpret_cast<volatile LONG64 *>(&lock->Ptr);
}

LONG64 read(PSRWLOCK lock)
{
    const LONG64 value = state(lock);
    if (value & ~(LOCKED | CONTENDED)) {
        fatal();
    }
    return value;
}
} // namespace

/* FormatMessageA over the wide catalogue, as Windows builds the ANSI form over the wide one: the wide call validates
 * the request in its own order and formats into a buffer it allocates, which this narrows. The catalogue is ASCII, so
 * narrowing each character is exact. */
extern "C" DWORD WINAPI wit_native_format_message_ansi(
    DWORD flags, LPCVOID source, DWORD id, DWORD language, LPSTR buffer, DWORD size, va_list *arguments)
{
    LPWSTR wide = nullptr;
    const DWORD length = wit_native_format_message(
        flags | FORMAT_MESSAGE_ALLOCATE_BUFFER, source, id, language, (LPWSTR)&wide, 0, arguments);
    if (!length) {
        return 0; // the wide call's error
    }
    char *output = buffer;
    DWORD error = ERROR_SUCCESS;
    if (!buffer) {
        error = ERROR_INVALID_PARAMETER;
    } else if (flags & FORMAT_MESSAGE_ALLOCATE_BUFFER) {
        output = (char *)wit_native_local_allocate(size > length + 1 ? size : length + 1);
        error = output ? ERROR_SUCCESS : ERROR_NOT_ENOUGH_MEMORY;
    } else if (size <= length) {
        error = ERROR_INSUFFICIENT_BUFFER;
    }
    if (error == ERROR_SUCCESS) {
        for (DWORD i = 0; i <= length; ++i) {
            output[i] = (char)wide[i];
        }
        // As in the wide form, the caller's pointer slot receives the buffer only once it holds the message.
        if (flags & FORMAT_MESSAGE_ALLOCATE_BUFFER) {
            *(char **)buffer = output;
        }
    }
    wit_native_local_release(wide);
    return error == ERROR_SUCCESS ? length : fail(error);
}

/* The guest has no locale database; the STL's system_category falls back to the neutral language without it. */
extern "C" int WINAPI wit_native_locale_info(LPCWSTR, LCTYPE, LPWSTR, int)
{
    return (int)fail(ERROR_NOT_SUPPORTED);
}

/* Exclusive SRW locks. A contender marks the lock contended and parks; the release of a contended lock wakes the
 * first parked thread, which tries again, so a new arrival may take the lock first, as on Windows. Releasing a lock
 * that is not held ends the process, where Windows raises STATUS_RESOURCE_NOT_OWNED. */
extern "C" BOOLEAN WINAPI wit_native_srw_try_acquire(PSRWLOCK lock)
{
    for (;;) {
        const LONG64 value = read(lock);
        if (value & LOCKED) {
            return FALSE;
        }
        if (_InterlockedCompareExchange64(&state(lock), value | LOCKED, value) == value) {
            return TRUE;
        }
    }
}

extern "C" void WINAPI wit_native_srw_acquire(PSRWLOCK lock)
{
    while (!wit_native_srw_try_acquire(lock)) {
        enter();
        const LONG64 value = read(lock);
        // A lock released meanwhile is tried again; otherwise its release must look for this thread.
        if ((value & LOCKED) &&
            ((value & CONTENDED) || _InterlockedCompareExchange64(&state(lock), value | CONTENDED, value) == value)) {
            (void)park(lock, INFINITE, nullptr);
        } else {
            leave();
        }
    }
}

extern "C" void WINAPI wit_native_srw_release(PSRWLOCK lock)
{
    const LONG64 value = read(lock);
    if (!(value & LOCKED)) {
        fatal();
    }
    if (value == LOCKED && _InterlockedCompareExchange64(&state(lock), 0, LOCKED) == LOCKED) {
        return;
    }
    // Contended: a thread that set the bit was queued before it left the gate.
    enter();
    wake(lock, false);
    _InterlockedExchange64(&state(lock), waiting(lock) ? CONTENDED : 0);
    leave();
}

/* Condition variables with exclusive SRW locks. The variable's own memory is not used: its address keys the parked
 * threads. A wait may end without a wake only at its timeout; shared-mode waits are not implemented. */
extern "C" BOOL WINAPI wit_native_condition_sleep(
    PCONDITION_VARIABLE condition, PSRWLOCK lock, DWORD milliseconds, ULONG flags)
{
    if (flags) {
        SetLastError(ERROR_NOT_SUPPORTED);
        return FALSE;
    }
    if (!condition || !(read(lock) & LOCKED)) {
        fatal(); // the caller must hold the lock
    }
    enter();
    const bool woken = park(condition, milliseconds, lock);
    wit_native_srw_acquire(lock);
    if (!woken) {
        SetLastError(ERROR_TIMEOUT);
        return FALSE;
    }
    return TRUE;
}

extern "C" void WINAPI wit_native_condition_wake(PCONDITION_VARIABLE condition)
{
    enter();
    wake(condition, false);
    leave();
}

extern "C" void WINAPI wit_native_condition_wake_all(PCONDITION_VARIABLE condition)
{
    enter();
    wake(condition, true);
    leave();
}

/* The exit code of a thread reference: STILL_ACTIVE until the thread exits. */
extern "C" BOOL WINAPI wit_native_thread_exit_code(HANDLE thread, LPDWORD code)
{
    if (!code) {
        return (BOOL)fail(ERROR_INVALID_PARAMETER);
    }
    WitThreadReferenceInfo info;
    WitU64 copied = 0;
    if (!wit_pal_result(
            wit_native_call(WIT_CALL_THREAD_REFERENCE_QUERY, (WitU64)thread, (WitU64)&info, sizeof(info), &copied))) {
        return FALSE;
    }
    if (copied != sizeof(info) || info.Version != WIT_THREAD_REFERENCE_VERSION || info.Size != sizeof(info)) {
        fatal();
    }
    *code = info.State == WIT_THREAD_REFERENCE_EXITED ? (DWORD)info.ExitCode : STILL_ACTIVE;
    return TRUE;
}

/* The processors and memory the component sees: the kernel's online processors and page size, the reservation
 * alignment of VirtualAlloc and the dynamic arenas as the application address range; level and revision come from
 * CPUID as Windows reports them on x64. */
extern "C" void WINAPI wit_native_system_info(LPSYSTEM_INFO info)
{
    WitUserMemoryInfo memory;
    WitU64 copied = 0;
    if (!info ||
        wit_native_call(WIT_CALL_MEMORY_QUERY, (WitU64)&memory, sizeof(memory), WIT_MEMORY_INFO_VERSION, &copied) !=
            WIT_STATUS_OK ||
        copied != sizeof(memory) ||
        memory.Version != WIT_MEMORY_INFO_VERSION ||
        memory.Size != sizeof(memory) ||
        !memory.ProcessorCount ||
        memory.ProcessorCount > 64 ||
        !memory.VirtualBytes) {
        fatal();
    }
    WitU64 low = memory.VirtualBase, high = memory.VirtualBase + memory.VirtualBytes;
    if (memory.CodeVirtualBytes) {
        low = memory.CodeVirtualBase < low ? memory.CodeVirtualBase : low;
        const WitU64 end = memory.CodeVirtualBase + memory.CodeVirtualBytes;
        high = end > high ? end : high;
    }
    int cpu[4];
    __cpuid(cpu, 1);
    const unsigned signature = (unsigned)cpu[0];
    unsigned family = (signature >> 8) & 0xF, model = (signature >> 4) & 0xF;
    if (family == 0xF) {
        family += (signature >> 20) & 0xFF;
    }
    if (family == 0x6 || family >= 0xF) {
        model += ((signature >> 16) & 0xF) << 4;
    }
    *info = {};
    info->wProcessorArchitecture = PROCESSOR_ARCHITECTURE_AMD64;
    info->dwPageSize = memory.PageSize;
    info->lpMinimumApplicationAddress = (LPVOID)(uintptr_t)low;
    info->lpMaximumApplicationAddress = (LPVOID)(uintptr_t)(high - 1);
    info->dwActiveProcessorMask =
        memory.ProcessorCount == 64 ? ~(DWORD_PTR)0 : ((DWORD_PTR)1 << memory.ProcessorCount) - 1;
    info->dwNumberOfProcessors = memory.ProcessorCount;
    info->dwProcessorType = PROCESSOR_AMD_X8664;
    info->dwAllocationGranularity = 65536;
    info->wProcessorLevel = (WORD)family;
    info->wProcessorRevision = (WORD)((model << 8) | (signature & 0xF));
}

extern "C" BOOL WINAPI wit_native_switch_to_thread()
{
    return (BOOL)PalSwitchToThread(); // nonzero only when another thread ran
}

/* Critical sections (P6.4.i3b), which the STL's locale locks use: an exclusive SRW lock in the LockSemaphore field
 * with the owner's thread identifier and a recursion count, so the owner may enter again. Initialization sets the
 * fields as Windows does; leaving one the thread does not own, or deleting one that is held, ends the process.
 * LockCount stays as initialization set it. */
namespace {
constexpr DWORD SECTION_FLAGS = 0x1F000000; // RTL_CRITICAL_SECTION_FLAG_NO_DEBUG_INFO ... FORCE_DEBUG_INFO

PSRWLOCK sectionLock(LPCRITICAL_SECTION section)
{
    return reinterpret_cast<PSRWLOCK>(&section->LockSemaphore);
}

HANDLE self()
{
    return reinterpret_cast<HANDLE>(static_cast<uintptr_t>(GetCurrentThreadId()));
}
} // namespace

extern "C" BOOL WINAPI wit_native_section_initialize(LPCRITICAL_SECTION section, DWORD spin, DWORD flags)
{
    if (!section || (flags & ~SECTION_FLAGS)) {
        return (BOOL)fail(ERROR_INVALID_PARAMETER);
    }
    section->DebugInfo = reinterpret_cast<PRTL_CRITICAL_SECTION_DEBUG>(static_cast<intptr_t>(-1));
    section->LockCount = -1;
    section->RecursionCount = 0;
    section->OwningThread = nullptr;
    section->LockSemaphore = nullptr;
    section->SpinCount = spin;
    return TRUE;
}

extern "C" void WINAPI wit_native_section_enter(LPCRITICAL_SECTION section)
{
    const HANDLE thread = self();
    if (section->OwningThread == thread) {
        if (section->RecursionCount == LONG_MAX) {
            fatal();
        }
        ++section->RecursionCount;
        return;
    }
    wit_native_srw_acquire(sectionLock(section));
    section->OwningThread = thread;
    section->RecursionCount = 1;
}

extern "C" void WINAPI wit_native_section_leave(LPCRITICAL_SECTION section)
{
    if (section->OwningThread != self() || section->RecursionCount < 1) {
        fatal();
    }
    if (--section->RecursionCount == 0) {
        section->OwningThread = nullptr;
        wit_native_srw_release(sectionLock(section));
    }
}

extern "C" void WINAPI wit_native_section_delete(LPCRITICAL_SECTION section)
{
    if (section->OwningThread || section->RecursionCount || section->LockSemaphore) {
        fatal();
    }
    section->DebugInfo = nullptr;
    section->SpinCount = 0;
}

/* EncodePointer and DecodePointer with Windows' scheme, over a per-process secret from the kernel's random source:
 * the pointer XORed with the secret and rotated right by its low six bits. */
namespace {
volatile WitU32 secretGate;
volatile WitU64 secret;
volatile bool secretReady;

WitU64 pointerSecret()
{
    if (!secretReady) {
        wit_native_lock(&secretGate);
        if (!secretReady) {
            WitU64 value = 0, copied = 0;
            if (wit_native_call(WIT_CALL_RANDOM, (WitU64)&value, sizeof(value), 0, &copied) != WIT_STATUS_OK ||
                copied != sizeof(value)) {
                fatal();
            }
            secret = value;
            secretReady = true;
        }
        wit_native_unlock(&secretGate);
    }
    return secret;
}
} // namespace

extern "C" PVOID WINAPI wit_native_encode_pointer(PVOID pointer)
{
    const WitU64 key = pointerSecret();
    return reinterpret_cast<PVOID>(_rotr64(reinterpret_cast<uintptr_t>(pointer) ^ key, int(key & 0x3F)));
}

extern "C" PVOID WINAPI wit_native_decode_pointer(PVOID pointer)
{
    const WitU64 key = pointerSecret();
    return reinterpret_cast<PVOID>(_rotl64(reinterpret_cast<uintptr_t>(pointer), int(key & 0x3F)) ^ key);
}

/* GetStringTypeW for CT_CTYPE1: the Latin-1 classes of native_ctype.witos.h. A length of -1 includes the terminator,
 * as on Windows. The other information types are not implemented, and a character beyond U+00FF ends the process:
 * the guest has no Unicode character database, and no class is better than a wrong one. */
extern "C" BOOL WINAPI wit_native_string_type(DWORD type, LPCWCH source, int count, LPWORD output)
{
    if (type != CT_CTYPE1 && type != CT_CTYPE2 && type != CT_CTYPE3) {
        return (BOOL)fail(ERROR_INVALID_FLAGS);
    }
    if (!source || !output || !count || count < -1) {
        return (BOOL)fail(ERROR_INVALID_PARAMETER);
    }
    if (type != CT_CTYPE1) {
        return (BOOL)fail(ERROR_NOT_SUPPORTED);
    }
    size_t length = size_t(count);
    if (count == -1) {
        for (length = 0; source[length]; ++length) {
        }
        ++length;
    }
    for (size_t i = 0; i < length; ++i) {
        const unsigned short classes = wit_native_ctype1(source[i]);
        if (classes == 0xFFFF) {
            fatal();
        }
        output[i] = classes;
    }
    return TRUE;
}

/* The code pages of the guest's conversions, which are all UTF-8. */
extern "C" BOOL WINAPI wit_native_code_page_info(UINT page, LPCPINFO info)
{
    if (!info || (page != CP_UTF8 && page != CP_ACP && page != CP_THREAD_ACP)) {
        return (BOOL)fail(ERROR_INVALID_PARAMETER);
    }
    info->MaxCharSize = 4;
    info->DefaultChar[0] = '?';
    info->DefaultChar[1] = 0;
    for (BYTE &lead : info->LeadByte) {
        lead = 0;
    }
    return TRUE;
}

/* The STL compares and maps strings with these only for a named locale, which the guest does not have. */
extern "C" int WINAPI wit_native_compare_string(
    LPCWSTR, DWORD, LPCWCH, int, LPCWCH, int, LPNLSVERSIONINFO, LPVOID, LPARAM)
{
    return (int)fail(ERROR_NOT_SUPPORTED);
}

extern "C" int WINAPI wit_native_map_string(LPCWSTR, DWORD, LPCWSTR, int, LPWSTR, int, LPNLSVERSIONINFO, LPVOID, LPARAM)
{
    return (int)fail(ERROR_NOT_SUPPORTED);
}

/* The guest has no UTC clock, and this Windows function cannot fail. */
extern "C" void WINAPI wit_native_precise_system_time(LPFILETIME)
{
    fatal();
}
