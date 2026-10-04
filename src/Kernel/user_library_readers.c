#include "user.h"
#include "witos/platform.h"

static WitLibraryInfo info(const WitUserLibrary *module)
{
    return (WitLibraryInfo){WIT_LIBRARY_VERSION, sizeof(WitLibraryInfo), module->Base, module->ImageBytes,
        module->EntryRva, module->UnwindRva, module->UnwindBytes, module->References};
}

/* Only called on a copied request under the serialized IF-disabled syscall path.
 * Reader handles are separate authority from external load/unload references. */
/* The slot of the loaded library whose image holds the executable address, or WIT_LIBRARY_CAPACITY. */
static WitU32 library_at(const WitUserProcess *process, WitU64 address)
{
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        const WitUserLibrary *module = &process->Libraries[i];
        if (module->Token &&
            address >= module->Base &&
            address - module->Base < module->ImageBytes &&
            wit_user_space_physical(&process->Space, address, 0, 1)) {
            return i;
        }
    }
    return WIT_LIBRARY_CAPACITY;
}

static WitU64 acquire_reader(WitUserProcess *process, const WitLibraryRequest *request, WitU64 *result)
{
    if (request->Handle ||
        request->Flags ||
        request->Name ||
        request->NameBytes ||
        request->BufferBytes != sizeof(WitLibraryInfo)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_buffer_writable(&process->Space, request->Buffer, sizeof(WitLibraryInfo))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    const WitU32 slot = library_at(process, request->Ordinal);
    if (slot == WIT_LIBRARY_CAPACITY) {
        return WIT_STATUS_NOT_FOUND;
    }
    WitU32 readerSlot = 0;
    while (readerSlot < WIT_LIBRARY_READER_CAPACITY && process->LibraryReaders[readerSlot].Token) {
        ++readerSlot;
    }
    if (readerSlot == WIT_LIBRARY_READER_CAPACITY) {
        return WIT_STATUS_NO_MEMORY;
    }
    WitUserLibrary *module = &process->Libraries[slot];
    const WitU64 token = wit_handle_grant(&process->Handles, WIT_HANDLE_LIBRARY_READER, WIT_RIGHT_READ);
    if (!token) {
        return WIT_STATUS_NO_MEMORY;
    }
    const WitLibraryInfo output = info(module);
    if (!wit_user_copy_to(&process->Space, request->Buffer, (const WitU8 *)&output, sizeof(output))) {
        if (wit_handle_close(&process->Handles, token) != WIT_STATUS_OK) {
            wit_panic("Library reader rollback failed");
        }
        return WIT_STATUS_BAD_ADDRESS;
    }
    process->LibraryReaders[readerSlot] = (WitUserLibraryReader){token, module->Token, slot};
    ++module->Readers;
    *result = token;
    return WIT_STATUS_OK;
}

/* Arguments of query and release: no name or ordinal, a library information buffer to query, an optional
 * lifecycle pointer to release. */
static WitU64 check_reader_request(WitUserProcess *process, const WitLibraryRequest *request)
{
    const int release = request->Operation == WIT_LIBRARY_RELEASE_READER;
    if ((release ? request->Flags > WIT_LIBRARY_USER_LIFECYCLE : request->Flags) ||
        request->Name ||
        request->NameBytes ||
        request->Ordinal) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (release ? (request->Flags ? request->BufferBytes != 8 : (request->Buffer || request->BufferBytes))
                : (request->BufferBytes != sizeof(WitLibraryInfo))) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (release && request->Flags && !wit_user_buffer_writable(&process->Space, request->Buffer, 8)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    return WIT_STATUS_OK;
}

/* The live reader record of a reader handle and its library, or 0 with the failure status. */
static WitUserLibraryReader *reader_of(WitUserProcess *process, WitU64 handle, WitUserLibrary **module, WitU64 *status)
{
    *status = wit_handle_check(&process->Handles, handle, WIT_HANDLE_LIBRARY_READER, WIT_RIGHT_READ);
    if (*status != WIT_STATUS_OK) {
        return 0;
    }
    WitUserLibraryReader *reader = 0;
    for (WitU32 i = 0; i < WIT_LIBRARY_READER_CAPACITY; ++i) {
        if (process->LibraryReaders[i].Token == handle) {
            reader = &process->LibraryReaders[i];
            break;
        }
    }
    *status = WIT_STATUS_BAD_HANDLE;
    if (!reader || reader->Slot >= WIT_LIBRARY_CAPACITY) {
        return 0;
    }
    *module = &process->Libraries[reader->Slot];
    if ((*module)->Token != reader->ModuleToken || !(*module)->Readers) {
        return 0;
    }
    *status = WIT_STATUS_OK;
    return reader;
}

static WitU64 release_reader(
    WitUserProcess *process, WitUserLibraryReader *reader, WitUserLibrary *module, const WitLibraryRequest *request)
{
    WitU64 address = 0;
    const WitU64 status = wit_user_library_release_plan(process, module, reader->Token, 1, request->Flags, &address);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (request->Flags && !wit_user_copy_to(&process->Space, request->Buffer, (const WitU8 *)&address, 8)) {
        wit_panic("Reader lifecycle pointer lost validation");
    }
    if (address) {
        return WIT_STATUS_OK;
    }
    if (wit_handle_close(&process->Handles, reader->Token) != WIT_STATUS_OK) {
        wit_panic("Library reader handle lost");
    }
    --module->Readers;
    *reader = (WitUserLibraryReader){0};
    wit_user_library_collect(process);
    return WIT_STATUS_OK;
}

WitU64 wit_user_library_reader_call(WitUserProcess *process, const WitLibraryRequest *request, WitU64 *result)
{
    if (request->Operation < WIT_LIBRARY_ACQUIRE_READER || request->Operation > WIT_LIBRARY_QUERY_READER) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (request->Operation == WIT_LIBRARY_ACQUIRE_READER) {
        return acquire_reader(process, request, result);
    }
    WitU64 status = check_reader_request(process, request);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    WitUserLibrary *module = 0;
    WitUserLibraryReader *reader = reader_of(process, request->Handle, &module, &status);
    if (!reader) {
        return status;
    }
    if (request->Operation == WIT_LIBRARY_QUERY_READER) {
        const WitLibraryInfo output = info(module);
        if (!wit_user_copy_to(&process->Space, request->Buffer, (const WitU8 *)&output, sizeof(output))) {
            return WIT_STATUS_BAD_ADDRESS;
        }
        *result = sizeof(output);
        return WIT_STATUS_OK;
    }
    return release_reader(process, reader, module, request);
}
