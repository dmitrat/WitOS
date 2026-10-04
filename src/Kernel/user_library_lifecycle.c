#include "user.h"
#include "witos/platform.h"

/* As on Windows, only a library with an entry point takes thread and detach notifications; one without runs its TLS
 * callbacks for the process attach alone. */
int wit_user_library_participates(const WitUserLibrary *library)
{
    return library->EntryRva != 0;
}

int wit_user_library_attaches(const WitUserLibrary *library)
{
    return library->EntryRva || library->TlsCallbackCount;
}

WitU64 wit_user_library_thread_admission(const WitUserProcess *p, WitU64 flags)
{
    if (p->LibraryLifecycle.Token) {
        return p->LibraryLifecycle.Owner == p->Threads[p->CurrentThread].Handle ? WIT_STATUS_DEADLOCK : WIT_STATUS_BUSY;
    }
    if (!(flags & WIT_THREAD_LIBRARY_NOTIFICATIONS)) {
        for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
            if (p->Libraries[i].Token && wit_user_library_participates(&p->Libraries[i])) {
                return WIT_STATUS_UNSUPPORTED;
            }
        }
    }
    return WIT_STATUS_OK;
}

static void order_attach(WitUserProcess *p, WitU32 slot, WitU32 mask, WitU32 *visited, WitUserLibraryLifecycle *life)
{
    if ((*visited & (1U << slot)) || !(mask & (1U << slot))) {
        return;
    }
    *visited |= 1U << slot;
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        if (p->Libraries[slot].Dependencies & (1U << i)) {
            order_attach(p, i, mask, visited, life);
        }
    }
    if (wit_user_library_attaches(&p->Libraries[slot])) {
        life->Order[life->Count++] = slot;
    }
}

/* Attach follows dependency order; detach, shutdown and thread notification follow attach order, newest first
 * except for thread attach. */
static void order_lifecycle(WitUserProcess *p, WitU32 mask, WitUserLibraryLifecycle *life)
{
    if (life->Attach) {
        WitU32 visited = 0;
        for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
            order_attach(p, i, mask, &visited, life);
        }
        return;
    }
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        if ((mask & (1U << i)) && p->Libraries[i].AttachOrder) {
            life->Order[life->Count++] = i;
        }
    }
    for (WitU32 i = 0; i < life->Count; ++i) {
        for (WitU32 j = i + 1; j < life->Count; ++j) {
            if (life->ThreadNotify == 2
                    ? p->Libraries[life->Order[j]].AttachOrder < p->Libraries[life->Order[i]].AttachOrder
                    : p->Libraries[life->Order[j]].AttachOrder > p->Libraries[life->Order[i]].AttachOrder) {
                WitU32 v = life->Order[i];
                life->Order[i] = life->Order[j];
                life->Order[j] = v;
            }
        }
    }
}

/* The user-space plan: the reason, the ordered libraries, their entry points and TLS callback lists. */
static WitLibraryLifecycle lifecycle_plan(
    const WitUserProcess *p, const WitUserLibraryLifecycle *life, WitU32 reason, WitU64 root)
{
    WitLibraryLifecycle plan = {WIT_LIBRARY_VERSION, sizeof(plan), reason, life->Count, life->Token, root, {{0}}};
    for (WitU32 i = 0; i < life->Count; ++i) {
        const WitUserLibrary *m = &p->Libraries[life->Order[i]];
        plan.Entries[i] = (WitLibraryLifecycleEntry){m->Token, m->Base, m->EntryRva ? m->Base + m->EntryRva : 0,
            m->TlsCallbackCount ? m->Base + m->TlsCallbacksRva : 0, m->TlsCallbackCount, 0};
    }
    return plan;
}

/* Thread attach and detach use the page and handles the thread reserved at creation. */
static WitU64 begin_thread_notification(WitUserProcess *p, WitUserLibraryLifecycle *life, WitU64 root, WitU64 *address)
{
    WitUserThread *thread = &p->Threads[p->CurrentThread];
    const WitU32 part = life->ThreadNotify == 2 ? 0U : 1U;
    if (!thread->LibraryNotificationPage || !thread->LibraryNotificationHandles[part]) {
        return WIT_STATUS_NO_MEMORY;
    }
    life->Address = thread->LibraryNotificationPage;
    life->Token = thread->LibraryNotificationHandles[part];
    life->ThreadReserved = 1;
    const WitLibraryLifecycle plan = lifecycle_plan(p, life, life->ThreadNotify + 1, root);
    const WitU64 physical = wit_user_space_physical(&p->Space, life->Address, 0, 0);
    if (!physical) {
        wit_panic("Thread notification backing lost");
    }
    for (WitU32 i = 0; i < sizeof(plan); ++i) {
        ((WitU8 *)physical)[i] = ((const WitU8 *)&plan)[i];
    }
    p->LibraryLifecycle = *life;
    *address = life->Address;
    return WIT_STATUS_OK;
}

/* Other lifecycles publish their plan on a fresh read-only page behind a lifecycle handle. */
static WitU64 publish_plan(WitUserProcess *p, WitUserLibraryLifecycle *life, WitU64 root, WitU64 *address)
{
    WitU64 status = wit_user_memory_reserve(&p->Space, 4096, 4096, &life->Address);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    status = wit_user_memory_commit(&p->Space, life->Address, 4096, 3);
    if (status == WIT_STATUS_OK) {
        life->Token = wit_handle_grant(&p->Handles, WIT_HANDLE_LIBRARY_LIFECYCLE, WIT_RIGHT_READ);
        if (!life->Token) {
            status = WIT_STATUS_NO_MEMORY;
        }
    }
    if (status == WIT_STATUS_OK) {
        const WitU32 reason = life->ThreadNotify ? life->ThreadNotify + 1
            : life->Shutdown                     ? WIT_LIBRARY_PROCESS_SHUTDOWN
                                                 : life->Attach;
        const WitLibraryLifecycle plan = lifecycle_plan(p, life, reason, root);
        if (!wit_user_copy_to(&p->Space, life->Address, (const WitU8 *)&plan, sizeof(plan))) {
            status = WIT_STATUS_BAD_ADDRESS;
        }
    }
    if (status == WIT_STATUS_OK) {
        status = wit_user_memory_protect(&p->Space, life->Address, 4096, 1);
    }
    if (status != WIT_STATUS_OK) {
        if (life->Token && wit_handle_close(&p->Handles, life->Token) != WIT_STATUS_OK) {
            wit_panic("Lifecycle handle rollback failed");
        }
        if (wit_user_memory_release(&p->Space, life->Address) != WIT_STATUS_OK) {
            wit_panic("Lifecycle page rollback failed");
        }
        return status;
    }
    p->LibraryLifecycle = *life;
    p->Space.LibraryRanges[WIT_LIBRARY_CAPACITY] = (WitVirtualRange){life->Address, 4096};
    *address = life->Address;
    return WIT_STATUS_OK;
}

WitU64 wit_user_library_begin_lifecycle(
    WitUserProcess *p, WitU32 mask, int attach, WitU64 origin, int reader, WitU64 root, WitU64 *address)
{
    WitUserLibraryLifecycle life = {0};
    life.Owner = p->Threads[p->CurrentThread].Handle;
    life.Mask = mask;
    life.Attach = attach == 1 ? 1U : 0U;
    life.Shutdown = attach == WIT_LIBRARY_PROCESS_SHUTDOWN ? 1U : 0U;
    life.ThreadNotify = attach == WIT_LIBRARY_THREAD_ATTACH ? 2U : attach == WIT_LIBRARY_THREAD_DETACH ? 3U : 0U;
    life.Origin = origin;
    life.ReaderRelease = reader ? 1U : 0U;
    if (p->LibraryLifecycle.Token) {
        return WIT_STATUS_BUSY;
    }
    order_lifecycle(p, mask, &life);
    if (!life.Count) {
        *address = 0;
        return WIT_STATUS_OK;
    }
    if (life.Attach && p->NextLibraryAttach > ~0ULL - life.Count) {
        return WIT_STATUS_NO_MEMORY;
    }
    return life.ThreadNotify ? begin_thread_notification(p, &life, root, address)
                             : publish_plan(p, &life, root, address);
}

WitU64 wit_user_library_release_plan(
    WitUserProcess *p, WitUserLibrary *module, WitU64 origin, int reader, WitU32 flags, WitU64 *address)
{
    *address = 0;
    if (p->LibraryLifecycle.Token) {
        if ((p->LibraryLifecycle.Owner != p->Threads[p->CurrentThread].Handle && !p->LibraryLifecycle.ThreadNotify) ||
            !reader ||
            p->LibraryLifecycle.Origin == origin) {
            return WIT_STATUS_BUSY;
        }
        --module->Readers;
        const WitU32 savedMask = p->LibraryLifecycle.Mask;
        if (p->LibraryLifecycle.ThreadNotify) {
            p->LibraryLifecycle.Mask = 0;
        }
        const WitU32 retained = wit_user_library_reachable(p);
        p->LibraryLifecycle.Mask = savedMask;
        ++module->Readers;
        for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
            if (p->Libraries[i].Token && p->Libraries[i].AttachOrder && !(retained & (1U << i))) {
                return WIT_STATUS_BUSY;
            }
        }
        return WIT_STATUS_OK; // A temporary reader that cannot require nested detach.
    }
    if (reader) {
        --module->Readers;
    } else {
        --module->References;
    }
    const WitU32 live = wit_user_library_reachable(p);
    if (reader) {
        ++module->Readers;
    } else {
        ++module->References;
    }
    WitU32 mask = 0;
    int callbacks = 0;
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        if (p->Libraries[i].Token && !(live & (1U << i))) {
            mask |= 1U << i;
            if (p->Libraries[i].AttachOrder) {
                callbacks = 1;
            }
        }
    }
    if (!callbacks) {
        return WIT_STATUS_OK;
    }
    if (!(flags & WIT_LIBRARY_USER_LIFECYCLE)) {
        return WIT_STATUS_UNSUPPORTED;
    }
    return wit_user_library_begin_lifecycle(p, mask, 0, origin, reader, 0, address);
}

/* Readers into a library whose lifecycle aborts or retires must have finished their user-space work; only the
 * reader whose release started a detach lifecycle is still allowed. */
static int readers_block(const WitUserProcess *p, const WitUserLibraryLifecycle *life, WitU64 completed)
{
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        if (life->ThreadNotify || !(life->Mask & (1U << i)) || !p->Libraries[i].Readers) {
            continue;
        }
        WitU32 allowed = 0;
        if (!life->Attach && life->ReaderRelease) {
            for (WitU32 j = 0; j < WIT_LIBRARY_READER_CAPACITY; ++j) {
                if (p->LibraryReaders[j].Token == life->Origin && p->LibraryReaders[j].Slot == i) {
                    allowed = 1;
                }
            }
        }
        if ((!life->Attach || !completed) && p->Libraries[i].Readers > allowed) {
            return 1;
        }
    }
    return 0;
}

static WitU64 check_finish(WitUserProcess *p, const WitLibraryRequest *request, const WitUserLibraryLifecycle *life)
{
    if (request->Flags ||
        request->Name ||
        request->NameBytes ||
        request->Buffer ||
        request->BufferBytes ||
        request->Ordinal > 1) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!life->Token ||
        life->Token != request->Handle ||
        wit_handle_check(&p->Handles, request->Handle, WIT_HANDLE_LIBRARY_LIFECYCLE, WIT_RIGHT_READ) != WIT_STATUS_OK) {
        return WIT_STATUS_BAD_HANDLE;
    }
    if (life->Owner != p->Threads[p->CurrentThread].Handle) {
        return WIT_STATUS_DENIED;
    }
    if (!life->Attach && !request->Ordinal) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    return readers_block(p, life, request->Ordinal) ? WIT_STATUS_BUSY : WIT_STATUS_OK;
}

/* Releases the plan page and handle, or returns a thread's reserved notification handle. */
static void release_descriptor(WitUserProcess *p, const WitUserLibraryLifecycle *life)
{
    if (life->ThreadReserved) {
        WitUserThread *thread = &p->Threads[p->CurrentThread];
        const WitU32 part = life->ThreadNotify == 2 ? 0U : 1U;
        if (thread->LibraryNotificationHandles[part] != life->Token ||
            wit_handle_close(&p->Handles, life->Token) != WIT_STATUS_OK) {
            wit_panic("Thread notification descriptor release failed");
        }
        thread->LibraryNotificationHandles[part] = 0;
    } else if (wit_user_library_release(&p->Space, life->Address) != WIT_STATUS_OK ||
        wit_handle_close(&p->Handles, life->Token) != WIT_STATUS_OK) {
        wit_panic("Lifecycle descriptor release failed");
    }
    p->Space.LibraryRanges[WIT_LIBRARY_CAPACITY] = (WitVirtualRange){0};
    p->LibraryLifecycle = (WitUserLibraryLifecycle){0};
}

/* A completed attach records the attach order of the libraries that take later notifications; a failed attach drops
 * the new libraries. */
static WitU64 finish_attach(WitUserProcess *p, const WitUserLibraryLifecycle *life, WitU64 completed)
{
    if (completed) {
        for (WitU32 i = 0; i < life->Count; ++i) {
            if (wit_user_library_participates(&p->Libraries[life->Order[i]])) {
                p->Libraries[life->Order[i]].AttachOrder = ++p->NextLibraryAttach;
            }
        }
        return WIT_STATUS_OK;
    }
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        if (life->Mask & (1U << i)) {
            p->Libraries[i].References = 0;
            p->Libraries[i].AttachOrder = 0;
        }
    }
    wit_user_library_collect(p);
    return WIT_STATUS_OK;
}

/* After detach: shutdown retires every library, a reader release completes, an unload drops its reference. */
static WitU64 finish_detach(WitUserProcess *p, const WitUserLibraryLifecycle *life)
{
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        if (life->Mask & (1U << i)) {
            p->Libraries[i].AttachOrder = 0;
        }
    }
    if (life->Shutdown) {
        for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
            if (life->Mask & (1U << i)) {
                p->Libraries[i].References = 0;
            }
        }
        p->LibraryShutdown = 1;
        wit_user_library_collect(p);
        return WIT_STATUS_OK;
    }
    if (life->ReaderRelease) {
        WitLibraryRequest release = {
            WIT_LIBRARY_VERSION, sizeof(release), WIT_LIBRARY_RELEASE_READER, 0, life->Origin, 0, 0, 0, 0, 0};
        WitU64 ignored = 0;
        return wit_user_library_reader_call(p, &release, &ignored);
    }
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        if (p->Libraries[i].Token == life->Origin) {
            if (!p->Libraries[i].References) {
                wit_panic("Lifecycle external reference lost");
            }
            --p->Libraries[i].References;
            wit_user_library_collect(p);
            return WIT_STATUS_OK;
        }
    }
    wit_panic("Lifecycle origin lost");
}

WitU64 wit_user_library_finish_lifecycle(WitUserProcess *p, const WitLibraryRequest *request)
{
    const WitUserLibraryLifecycle life = p->LibraryLifecycle;
    const WitU64 status = check_finish(p, request, &life);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    release_descriptor(p, &life);
    if (life.ThreadNotify) {
        p->Threads[p->CurrentThread].LibraryPhase = life.ThreadNotify == 2 ? 2U : 4U;
        return WIT_STATUS_OK;
    }
    return life.Attach ? finish_attach(p, &life, request->Ordinal) : finish_detach(p, &life);
}

WitU64 wit_user_library_shutdown(WitUserProcess *p, const WitLibraryRequest *request)
{
    if (request->Flags != WIT_LIBRARY_USER_LIFECYCLE ||
        request->Handle ||
        request->Name ||
        request->NameBytes ||
        request->Ordinal ||
        request->BufferBytes != 8) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_buffer_writable(&p->Space, request->Buffer, 8)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (p->LibraryLifecycle.Token) {
        return WIT_STATUS_BUSY;
    }
    WitU64 address = 0;
    WitU32 mask = 0;
    if (!p->LibraryShutdown) {
        for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
            const WitUserLibrary *module = &p->Libraries[i];
            if (module->Readers) {
                return WIT_STATUS_BUSY;
            }
            if (module->Token) {
                mask |= 1U << i;
            }
        }
        const WitU64 status =
            wit_user_library_begin_lifecycle(p, mask, WIT_LIBRARY_PROCESS_SHUTDOWN, 0, 0, 0, &address);
        if (status != WIT_STATUS_OK) {
            return status;
        }
        if (!address) {
            for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
                p->Libraries[i].References = 0;
            }
            p->LibraryShutdown = 1;
            wit_user_library_collect(p);
        }
    }
    if (!wit_user_copy_to(&p->Space, request->Buffer, (const WitU8 *)&address, 8)) {
        wit_panic("Library shutdown copy lost validation");
    }
    return WIT_STATUS_OK;
}

WitU64 wit_user_library_thread_notify(WitUserProcess *p, const WitLibraryRequest *request)
{
    if (request->Flags != WIT_LIBRARY_USER_LIFECYCLE ||
        request->Handle ||
        request->Name ||
        request->NameBytes ||
        request->Ordinal ||
        request->BufferBytes != 8) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_buffer_writable(&p->Space, request->Buffer, 8)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    WitUserThread *thread = &p->Threads[p->CurrentThread];
    const int entering = request->Operation == WIT_LIBRARY_THREAD_ENTER;
    if (!thread->LibraryNotifications) {
        if (entering) {
            return WIT_STATUS_DENIED;
        }
        for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
            if (p->Libraries[i].Token && wit_user_library_participates(&p->Libraries[i])) {
                return WIT_STATUS_DENIED;
            }
        }
        const WitU64 none = 0; // Existing raw/lazy-TLS native path has no DLL work.
        if (!wit_user_copy_to(&p->Space, request->Buffer, (const WitU8 *)&none, 8)) {
            wit_panic("Empty thread leave lost validation");
        }
        return WIT_STATUS_OK;
    }
    if (thread->LibraryPhase != (entering ? 1U : 2U)) {
        return WIT_STATUS_CLOSED;
    }
    if (p->LibraryLifecycle.Token) {
        return p->LibraryLifecycle.Owner == thread->Handle ? WIT_STATUS_DEADLOCK : WIT_STATUS_BUSY;
    }
    WitU32 mask = 0;
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        if (p->Libraries[i].Token && p->Libraries[i].AttachOrder) {
            mask |= 1U << i;
        }
    }
    WitU64 address = 0;
    const WitU64 status = wit_user_library_begin_lifecycle(
        p, mask, entering ? WIT_LIBRARY_THREAD_ATTACH : WIT_LIBRARY_THREAD_DETACH, 0, 0, 0, &address);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (mask) {
        thread->LibraryRequired = 1;
    }
    if (!address) {
        thread->LibraryPhase = entering ? 2U : 4U;
    }
    if (!wit_user_copy_to(&p->Space, request->Buffer, (const WitU8 *)&address, 8)) {
        wit_panic("Thread notification pointer lost validation");
    }
    return WIT_STATUS_OK;
}
