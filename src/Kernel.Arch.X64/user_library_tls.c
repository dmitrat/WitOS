#include "user.h"
#include "witos/platform.h"
/* Serialized bootstrap/UP operations. Slots and backing addresses come from
 * kernel records, never writable GS vectors or the module's _tls_index word. */
WIT_STATIC_ASSERT(0x80 + (WIT_LIBRARY_CAPACITY + 1) * 8 <= WIT_COMPILER_TLS_DATA_OFFSET,
    "Compiler slot vector fits before main TLS data");
WIT_STATIC_ASSERT(
    WIT_USER_TLS + (WIT_LIBRARY_CAPACITY + 3) * 4096 <= WIT_USER_PEER_PAGE, "DLL TLS blocks avoid fixed peer mapping");
WIT_STATIC_ASSERT(
    WIT_USER_TLS + (WIT_USER_THREAD_CAPACITY - 1) * WIT_USER_THREAD_STRIDE + (WIT_LIBRARY_CAPACITY + 3) * 4096 <=
        WIT_USER_IMAGE_BASE,
    "DLL TLS blocks avoid image window");

static void require(int value, const char *text)
{
    if (!value) {
        wit_panic(text);
    }
}

static WitU64 *header(WitUserProcess *p, WitUserThread *thread)
{
    const WitU64 physical = wit_user_space_physical(&p->Space, thread->CompilerTls, 1, 0);
    require(physical != 0, "DLL compiler TLS header missing");
    return (WitU64 *)physical;
}

static void trim_header(WitUserProcess *p, WitUserThread *thread)
{
    if (p->TlsBytes || !thread->CompilerTls) {
        return;
    }
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        if (thread->LibraryTls[i]) {
            return;
        }
    }
    require(wit_user_space_unmap_fixed(&p->Space, thread->CompilerTls), "DLL TLS header ownership lost");
    thread->CompilerTls = 0;
}

static void remove_block(WitUserProcess *p, WitUserThread *thread, WitU32 slot)
{
    if (!thread->LibraryTls[slot]) {
        return;
    }
    {
        header(p, thread)[0x80 / 8 + slot + 1] = 0;
        require(wit_user_space_unmap_fixed(&p->Space, thread->LibraryTls[slot]), "DLL TLS block ownership lost");
        thread->LibraryTls[slot] = 0;
    }
    trim_header(p, thread);
}

static int add_block(WitUserProcess *p, WitU32 threadIndex, WitU32 slot)
{
    WitUserThread *thread = &p->Threads[threadIndex];
    const WitUserLibraryTls *seed = &p->LibraryTls[slot];
    if (!seed->Bytes) {
        return 1;
    }
    require(!thread->LibraryTls[slot], "DLL TLS slot already owned");
    if (!thread->CompilerTls) {
        const WitU64 address = WIT_USER_TLS + threadIndex * WIT_USER_THREAD_STRIDE + 4096;
        if (!wit_user_space_map(&p->Space, address, 1, 0)) {
            return 0;
        }
        thread->CompilerTls = address;
    }
    const WitU64 block = WIT_USER_TLS + threadIndex * WIT_USER_THREAD_STRIDE + (slot + 2) * 4096;
    if (!wit_user_space_map(&p->Space, block, 1, 0)) {
        trim_header(p, thread);
        return 0;
    }
    const WitU64 physical = wit_user_space_physical(&p->Space, block, 1, 0);
    require(physical != 0, "DLL TLS backing missing");
    for (WitU32 i = 0; i < seed->Bytes; ++i) {
        ((WitU8 *)physical)[i] = seed->Data[i];
    }
    thread->LibraryTls[slot] = block;
    WitU64 *table = header(p, thread);
    table[0x58 / 8] = thread->CompilerTls + 0x80;
    table[0x80 / 8] = p->TlsBytes ? thread->CompilerTls + WIT_COMPILER_TLS_DATA_OFFSET : 0;
    table[0x80 / 8 + slot + 1] = block;
    return 1;
}

void wit_user_library_tls_remove(WitUserProcess *p, WitU32 slot)
{
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        remove_block(p, &p->Threads[i], slot);
    }
    p->LibraryTls[slot] = (WitUserLibraryTls){0};
}

int wit_user_library_tls_install(WitUserProcess *p, WitU32 slot, const WitPeImage *image, WitU64 base)
{
    if (!image->TlsSize) {
        return 1;
    }
    require(!p->LibraryTls[slot].Bytes, "DLL TLS seed already owned");
    WitUserLibraryTls *seed = &p->LibraryTls[slot];
    *seed = (WitUserLibraryTls){0};
    if (!wit_user_copy_from(&p->Space, base + image->TlsTemplateRva, seed->Data, image->TlsInitialized)) {
        return 0;
    }
    seed->Bytes = image->TlsInitialized + image->TlsZeroFill;
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        if (p->Threads[i].State != WitThreadEmpty && p->Threads[i].State != WitThreadExited) {
            if (!add_block(p, i, slot)) {
                wit_user_library_tls_remove(p, slot);
                return 0;
            }
        }
    }
    const WitU32 index = slot + 1;
    if (!wit_user_copy_to(&p->Space, base + image->TlsIndexRva, (const WitU8 *)&index, sizeof(index))) {
        wit_user_library_tls_remove(p, slot);
        return 0;
    }
    return 1;
}

int wit_user_library_tls_create_thread(WitUserProcess *p, WitU32 index)
{
    for (WitU32 slot = 0; slot < WIT_LIBRARY_CAPACITY; ++slot) {
        if (!add_block(p, index, slot)) {
            for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
                remove_block(p, &p->Threads[index], i);
            }
            return 0;
        }
    }
    return 1;
}

void wit_user_library_tls_reap_thread(WitUserProcess *p, WitU32 index)
{
    for (WitU32 slot = 0; slot < WIT_LIBRARY_CAPACITY; ++slot) {
        remove_block(p, &p->Threads[index], slot);
    }
}

void wit_user_library_tls_refresh(WitUserProcess *p)
{
    WitUserThread *thread = &p->Threads[p->CurrentThread];
    wit_arch_set_user_tls(thread->Tls, thread->CompilerTls);
}
