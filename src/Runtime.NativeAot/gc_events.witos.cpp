#include "gcenv.witos.h"

/* Bounded bootstrap storage. No native heap, C++ static constructors or
 * destructor registration. Event objects must not be copied. Lifecycle calls
 * must be externally serialized against upstream's inline IsValid(). */
class GCEvent::Impl
{
public:
    GCEvent* Owner;
    WitU64 Handle;
    static Impl Slots[4];
    static volatile WitU32 Gate;

    static void Lock()
    {
        while (!wit_native_try_lock(&Gate)) GCToOSInterface::YieldThread(0);
    }
    static void Unlock() { wit_native_unlock(&Gate); }
    static Impl* Find(GCEvent* event)
    {
        for (size_t i = 0; i < 4; ++i)
            if (event->m_impl == &Slots[i] && Slots[i].Owner == event && Slots[i].Handle) return &Slots[i];
        return nullptr;
    }
    static bool Create(GCEvent* event, bool manual, bool signaled)
    {
        Lock();
        if (event->m_impl) { Unlock(); return false; }
        for (size_t i = 0; i < 4; ++i) {
            if (Slots[i].Owner) continue;
            WitU64 handle = 0;
            const WitU64 flags = (manual ? WIT_EVENT_MANUAL_RESET : 0) | (signaled ? WIT_EVENT_INITIAL_SIGNALED : 0);
            if (wit_native_call(WIT_CALL_EVENT_CREATE, flags, 0, 0, &handle) != WIT_STATUS_OK) {
                Unlock();
                return false;
            }
            Slots[i].Handle = handle;
            Slots[i].Owner = event;
            event->m_impl = &Slots[i]; // Publish only after kernel creation succeeds.
            Unlock();
            return true;
        }
        Unlock();
        return false;
    }
    static void Change(GCEvent* event, WitU64 operation)
    {
        Lock();
        Impl* entry = Find(event);
        if (!entry || wit_native_call(operation, entry->Handle, 0, 0, nullptr) != WIT_STATUS_OK)
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        if (operation == WIT_CALL_CLOSE) {
            event->m_impl = nullptr;
            entry->Handle = 0;
            entry->Owner = nullptr;
        }
        Unlock();
    }
};
GCEvent::Impl GCEvent::Impl::Slots[4];
volatile WitU32 GCEvent::Impl::Gate;

GCEvent::GCEvent() : m_impl(nullptr) { }
bool GCEvent::CreateAutoEventNoThrow(bool state) { return CreateOSAutoEventNoThrow(state); }
bool GCEvent::CreateManualEventNoThrow(bool state) { return CreateOSManualEventNoThrow(state); }
bool GCEvent::CreateOSAutoEventNoThrow(bool state) { return Impl::Create(this, false, state); }
bool GCEvent::CreateOSManualEventNoThrow(bool state) { return Impl::Create(this, true, state); }
void GCEvent::Set() { Impl::Change(this, WIT_CALL_EVENT_SET); }
// Follow the pinned Windows implementation: ResetEvent clears either kind.
void GCEvent::Reset() { Impl::Change(this, WIT_CALL_EVENT_RESET); }
void GCEvent::CloseEvent() { Impl::Change(this, WIT_CALL_CLOSE); }
uint32_t GCEvent::Wait(uint32_t timeout, bool alertable)
{
    (void)alertable; // Both pinned upstream GC backends perform non-alertable waits.
    const WitU64 deadline = wit_gc_deadline(timeout);
    Impl::Lock();
    Impl* entry = Impl::Find(this);
    const WitU64 handle = entry ? entry->Handle : 0;
    Impl::Unlock();
    if (!handle) return WAIT_FAILED;
    // The handle includes its generation. Never hold Gate while parked and
    // never touch Impl after returning: Close may already have reused its slot.
    const WitU64 status = wit_native_call(WIT_CALL_EVENT_WAIT_UNTIL, handle, deadline, 0, nullptr);
    if (status == WIT_STATUS_OK) return WAIT_OBJECT_0;
    if (status == WIT_STATUS_TIMED_OUT) return WAIT_TIMEOUT;
    return WAIT_FAILED;
}

void GCToOSInterface::YieldThread(uint32_t switchCount)
{
    (void)switchCount;
    if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK)
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}
