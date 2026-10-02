#include "user.h"
#include "witos/platform.h"

static WitLibraryInfo info(const WitUserLibrary *module)
{
    return (WitLibraryInfo){WIT_LIBRARY_VERSION, sizeof(WitLibraryInfo), module->Base, module->ImageBytes,
        module->EntryRva, module->UnwindRva, module->UnwindBytes, module->References};
}

/* Only called on a copied request under the serialized IF-disabled syscall path.
 * Reader handles are separate authority from external load/unload references. */
WitU64 wit_user_library_reader_call(WitUserProcess *process, const WitLibraryRequest *request, WitU64 *result)
{
    if (request->Operation < WIT_LIBRARY_ACQUIRE_READER || request->Operation > WIT_LIBRARY_QUERY_READER) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (request->Operation == WIT_LIBRARY_ACQUIRE_READER) {
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
        WitU32 slot = WIT_LIBRARY_CAPACITY;
        for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
            const WitUserLibrary *module = &process->Libraries[i];
            if (module->Token &&
                request->Ordinal >= module->Base &&
                request->Ordinal - module->Base < module->ImageBytes &&
                wit_user_space_physical(&process->Space, request->Ordinal, 0, 1)) {
                slot = i;
                break;
            }
        }
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
    if ((request->Operation == WIT_LIBRARY_QUERY_READER ? request->Flags
                                                        : request->Flags > WIT_LIBRARY_USER_LIFECYCLE) ||
        request->Name ||
        request->NameBytes ||
        request->Ordinal) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (request->Operation == WIT_LIBRARY_RELEASE_READER
            ? (request->Flags ? request->BufferBytes != 8 : (request->Buffer || request->BufferBytes))
            : (request->BufferBytes != sizeof(WitLibraryInfo))) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (request->Operation == WIT_LIBRARY_RELEASE_READER &&
        request->Flags &&
        !wit_user_buffer_writable(&process->Space, request->Buffer, 8)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    const WitU64 valid =
        wit_handle_check(&process->Handles, request->Handle, WIT_HANDLE_LIBRARY_READER, WIT_RIGHT_READ);
    if (valid != WIT_STATUS_OK) {
        return valid;
    }
    WitUserLibraryReader *reader = 0;
    for (WitU32 i = 0; i < WIT_LIBRARY_READER_CAPACITY; ++i) {
        if (process->LibraryReaders[i].Token == request->Handle) {
            reader = &process->LibraryReaders[i];
            break;
        }
    }
    if (!reader || reader->Slot >= WIT_LIBRARY_CAPACITY) {
        return WIT_STATUS_BAD_HANDLE;
    }
    WitUserLibrary *module = &process->Libraries[reader->Slot];
    if (module->Token != reader->ModuleToken || !module->Readers) {
        return WIT_STATUS_BAD_HANDLE;
    }
    if (request->Operation == WIT_LIBRARY_QUERY_READER) {
        const WitLibraryInfo output = info(module);
        if (!wit_user_copy_to(&process->Space, request->Buffer, (const WitU8 *)&output, sizeof(output))) {
            return WIT_STATUS_BAD_ADDRESS;
        }
        *result = sizeof(output);
        return WIT_STATUS_OK;
    }
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
