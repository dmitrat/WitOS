#include "user.h"
#include "witos/pe_imports.h"
#include "witos/storage.h"
#include "witos/platform.h"
/* Serialized bootstrap/UP calls run with IF disabled. Keeping the large
 * validation plan here avoids nesting it above commit's bounded rollback arrays. */
static WitPeImage plan;
WIT_STATIC_ASSERT(WIT_LIBRARY_PATH_BYTES == WIT_PACKAGE_MAX_NAME, "Library path covers complete package names");

void wit_user_library_initialize(WitUserProcess *process)
{
    process->LibraryLifecycle = (WitUserLibraryLifecycle){0};
    process->NextLibraryAttach = 0;
    process->LibraryShutdown = 0;
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        process->Libraries[i] = (WitUserLibrary){0};
        process->LibraryTls[i] = (WitUserLibraryTls){0};
    }
    for (WitU32 t = 0; t < WIT_USER_THREAD_CAPACITY; ++t) {
        for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
            process->Threads[t].LibraryTls[i] = 0;
        }
    }
    for (WitU32 i = 0; i < WIT_LIBRARY_READER_CAPACITY; ++i) {
        process->LibraryReaders[i] = (WitUserLibraryReader){0};
    }
}

static WitU32 u32(const WitU8 *p)
{
    return p[0] | ((WitU32)p[1] << 8) | ((WitU32)p[2] << 16) | ((WitU32)p[3] << 24);
}

static WitU64 u64(const WitU8 *p)
{
    return u32(p) | ((WitU64)u32(p + 4) << 32);
}

static WitU64 pe_status(WitPeStatus status)
{
    return status == WitPeTooLarge        ? WIT_STATUS_TOO_LARGE
        : status == WitPeUnsupportedImage ? WIT_STATUS_UNSUPPORTED
                                          : WIT_STATUS_INVALID_ARGUMENT;
}

static void rollback(WitUserSpace *space, WitU64 base)
{
    if (base && wit_user_memory_release(space, base) != WIT_STATUS_OK) {
        wit_panic("Unpublished library rollback failed");
    }
}

static WitU64 map(WitUserSpace *space, const WitU8 *file, const WitPeImage *image, WitU64 *output)
{
    WitU64 base = 0, status = wit_user_code_reserve(space, image->ImageSize, 65536, 0, ~0ULL, &base);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!image->RelocSize && base != image->PreferredBase) {
        rollback(space, base);
        return WIT_STATUS_UNSUPPORTED;
    }
    status = wit_user_memory_commit(space, base, 4096, 3);
    if (status == WIT_STATUS_OK && !wit_user_copy_to(space, base, file, image->HeadersSize)) {
        status = WIT_STATUS_BAD_ADDRESS;
    }
    for (WitU32 i = 0; status == WIT_STATUS_OK && i < image->SectionCount; ++i) {
        const WitPeSection *section = &image->Sections[i];
        status = wit_user_memory_commit(space, base + section->Rva, section->MapSize, 3);
        if (status == WIT_STATUS_OK &&
            section->RawSize &&
            !wit_user_copy_to(space, base + section->Rva, file + section->RawOffset, section->RawSize)) {
            status = WIT_STATUS_BAD_ADDRESS;
        }
    }
    WitU32 raw = 0, at = 0;
    if (status == WIT_STATUS_OK &&
        image->RelocSize &&
        !wit_pe_file_range(image, image->RelocRva, image->RelocSize, &raw)) {
        status = WIT_STATUS_BAD_ADDRESS;
    }
    while (status == WIT_STATUS_OK && at < image->RelocSize) {
        const WitU8 *block = file + raw + at;
        const WitU32 page = u32(block), bytes = u32(block + 4);
        for (WitU32 index = 8; index < bytes; index += 2) {
            const WitU32 relocation = block[index] | ((WitU32)block[index + 1] << 8);
            if (!(relocation >> 12)) {
                continue;
            }
            WitU32 source;
            const WitU32 rva = page + (relocation & 4095);
            if (!wit_pe_file_range(image, rva, 8, &source)) {
                status = WIT_STATUS_BAD_ADDRESS;
                break;
            }
            const WitU64 relocated = base + (u64(file + source) - image->PreferredBase);
            if (!wit_user_copy_to(space, base + rva, (const WitU8 *)&relocated, 8)) {
                status = WIT_STATUS_BAD_ADDRESS;
                break;
            }
        }
        at += bytes;
    }
    if (status != WIT_STATUS_OK) {
        rollback(space, base);
        return status;
    }
    *output = base;
    return WIT_STATUS_OK;
}

static WitU64 finish(WitUserSpace *space, const WitPeImage *image, WitU64 base)
{
    WitU64 status = WIT_STATUS_OK;
    if (status == WIT_STATUS_OK) {
        status = wit_user_memory_protect(space, base, 4096, 1);
    }
    for (WitU32 i = 0; status == WIT_STATUS_OK && i < image->SectionCount; ++i) {
        const WitPeSection *section = &image->Sections[i];
        const WitU64 protection = section->Flags;
        status = wit_user_code_protect(space, base + section->Rva, section->MapSize, protection);
        if (status == WIT_STATUS_OK && (protection & WIT_PE_EXECUTE)) {
            status = wit_user_code_publish(space, base + section->Rva, section->MapSize);
        }
    }
    return status;
}

#define LIBRARY_PROFILE (WIT_PE_LIBRARY | WIT_PE_UNWIND_RUNTIME | WIT_PE_LIBRARY_IMPORTS | WIT_PE_LIBRARY_TLS)

typedef struct LibraryTransaction {
    WitUserLibrary Modules[WIT_LIBRARY_CAPACITY];
    WitPeImage Images[WIT_LIBRARY_CAPACITY];
    WitPeImports Imports[WIT_LIBRARY_CAPACITY];
    WitU32 Dependency[WIT_LIBRARY_CAPACITY][WIT_PE_IMPORT_MODULES];
    WitU32 Active, Added, AllowEntry;
} LibraryTransaction;

static LibraryTransaction transaction;

/* Discovery reserves graph slots before following edges, so cycles terminate.
 * No physical allocation, handle grant or live reference changes occur here. */
static WitU64 discover(const WitPackage *package, const WitPackageFile *file, WitU32 *selected)
{
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        if ((transaction.Active & (1U << i)) &&
            transaction.Modules[i].FileOffset == file->Offset &&
            transaction.Modules[i].FileBytes == file->Length) {
            *selected = i;
            return WIT_STATUS_OK;
        }
    }
    WitU32 slot = 0;
    while (slot < WIT_LIBRARY_CAPACITY && (transaction.Active & (1U << slot))) {
        ++slot;
    }
    if (slot == WIT_LIBRARY_CAPACITY) {
        return WIT_STATUS_NO_MEMORY;
    }
    if (file->Length > WIT_PE_MAX_FILE_SIZE) {
        return WIT_STATUS_TOO_LARGE;
    }
    WitPeImage *image = &transaction.Images[slot];
    WitPeImports *imports = &transaction.Imports[slot];
    const WitU8 *bytes = package->Data + file->Offset;
    WitPeStatus valid = wit_pe_validate_profile(bytes, (WitU32)file->Length, image, LIBRARY_PROFILE);
    if (valid != WitPeOk) {
        return pe_status(valid);
    }
    if (image->EntryRva && !transaction.AllowEntry) {
        return WIT_STATUS_UNSUPPORTED;
    }
    valid = wit_pe_imports_validate(bytes, (WitU32)file->Length, image, image->ImportRva, image->ImportSize, imports);
    if (valid != WitPeOk) {
        return pe_status(valid);
    }
    transaction.Active |= 1U << slot;
    transaction.Added |= 1U << slot;
    WitUserLibrary *module = &transaction.Modules[slot];
    *module = (WitUserLibrary){0, 0, image->ImageSize, file->Offset, file->Length, image->EntryRva, image->UnwindRva,
        image->UnwindSize, 0, (WitU64)(file->Name - package->Data), file->NameLength, 0, 0, 0};
    WitU32 prefix = 0;
    for (WitU32 i = 0; i < file->NameLength; ++i) {
        if (file->Name[i] == '/') {
            prefix = i + 1;
        }
    }
    for (WitU32 i = 0; i < imports->ModuleCount; ++i) {
        const WitPeImportModule *dependency = &imports->Modules[i];
        WitU32 raw;
        if (dependency->NameBytes > WIT_PACKAGE_MAX_NAME - prefix) {
            return WIT_STATUS_TOO_LARGE;
        }
        if (!wit_pe_file_range(image, dependency->NameRva, dependency->NameBytes, &raw)) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        WitU8 name[WIT_PACKAGE_MAX_NAME];
        for (WitU32 j = 0; j < prefix; ++j) {
            name[j] = file->Name[j];
        }
        for (WitU32 j = 0; j < dependency->NameBytes; ++j) {
            name[prefix + j] = bytes[raw + j];
        }
        WitPackageFile target;
        const WitPackageStatus found = wit_package_find(package, name, prefix + dependency->NameBytes, &target);
        if (found != WitPackageOk) {
            return found == WitPackageMissing ? WIT_STATUS_NOT_FOUND : WIT_STATUS_INVALID_ARGUMENT;
        }
        WitU32 targetSlot;
        const WitU64 status = discover(package, &target, &targetSlot);
        if (status != WIT_STATUS_OK) {
            return status;
        }
        module->Dependencies |= 1U << targetSlot;
        transaction.Dependency[slot][i] = targetSlot;
    }
    *selected = slot;
    return WIT_STATUS_OK;
}

static void discard(WitUserProcess *process)
{
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        if (transaction.Added & (1U << i)) {
            WitUserLibrary *module = &transaction.Modules[i];
            if (module->Token && wit_handle_close(&process->Handles, module->Token) != WIT_STATUS_OK) {
                wit_panic("Unpublished library handle rollback failed");
            }
            wit_user_library_tls_remove(process, i);
            rollback(&process->Space, module->Base);
        }
    }
}

static WitU64 load(WitUserProcess *process, const WitPackage *package, const WitPackageFile *file, WitU32 flags,
    WitU64 *lifecycleAddress, WitU64 *result)
{
    transaction.Active = transaction.Added = 0;
    transaction.AllowEntry = (flags & WIT_LIBRARY_USER_LIFECYCLE) != 0;
    *lifecycleAddress = 0;
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        transaction.Modules[i] = process->Libraries[i];
        if (transaction.Modules[i].Token) {
            transaction.Active |= 1U << i;
        }
    }
    WitU32 root;
    WitU64 status = discover(package, file, &root);
    if (status != WIT_STATUS_OK) {
        return status; // Discovery has not acquired resources.
    }
    if (transaction.Modules[root].References == ~0U) {
        return WIT_STATUS_NO_MEMORY;
    }
    int callbacks = 0;
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        if ((transaction.Added & (1U << i)) && transaction.Modules[i].EntryRva) {
            callbacks = 1;
        }
    }
    if (callbacks) {
        WitU32 live = 0;
        for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
            if (process->Threads[i].State != WitThreadEmpty && process->Threads[i].State != WitThreadExited) {
                ++live;
            }
        }
        if (live != 1) {
            return WIT_STATUS_UNSUPPORTED;
        }
    }
    // Existing immutable images provide exports, but their maps/references stay unchanged.
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        if ((transaction.Active & (1U << i)) && !(transaction.Added & (1U << i))) {
            const WitUserLibrary *module = &transaction.Modules[i];
            if (wit_pe_validate_profile(package->Data + module->FileOffset, (WitU32)module->FileBytes,
                    &transaction.Images[i], LIBRARY_PROFILE) != WitPeOk) {
                return WIT_STATUS_INVALID_ARGUMENT;
            }
        }
    }
    for (WitU32 i = 0; status == WIT_STATUS_OK && i < WIT_LIBRARY_CAPACITY; ++i) {
        if (transaction.Added & (1U << i)) {
            WitUserLibrary *module = &transaction.Modules[i];
            status = map(&process->Space, package->Data + module->FileOffset, &transaction.Images[i], &module->Base);
        }
    }
    for (WitU32 i = 0; status == WIT_STATUS_OK && i < WIT_LIBRARY_CAPACITY; ++i) {
        if (transaction.Added & (1U << i)) {
            WitUserLibrary *module = &transaction.Modules[i];
            const WitPeImports *imports = &transaction.Imports[i];
            const WitU8 *bytes = package->Data + module->FileOffset;
            for (WitU32 j = 0; status == WIT_STATUS_OK && j < imports->ModuleCount; ++j) {
                const WitPeImportModule *dependency = &imports->Modules[j];
                const WitU32 target = transaction.Dependency[i][j];
                const WitUserLibrary *provider = &transaction.Modules[target];
                for (WitU32 k = 0; k < dependency->SymbolCount; ++k) {
                    const WitPeImportSymbol *symbol = &imports->Symbols[dependency->FirstSymbol + k];
                    const char *name = 0;
                    WitU32 raw, rva = 0;
                    if (symbol->NameRva) {
                        if (!wit_pe_file_range(&transaction.Images[i], symbol->NameRva + 2, symbol->NameBytes, &raw)) {
                            status = WIT_STATUS_INVALID_ARGUMENT;
                            break;
                        }
                        name = (const char *)(bytes + raw);
                    }
                    const WitPeStatus found = wit_pe_export_find(package->Data + provider->FileOffset,
                        &transaction.Images[target], name, symbol->NameBytes, symbol->Ordinal, &rva);
                    if (found != WitPeOk || !rva) {
                        status = found == WitPeOk ? WIT_STATUS_NOT_FOUND : pe_status(found);
                        break;
                    }
                    const WitU64 address = provider->Base + rva;
                    if (!wit_user_copy_to(&process->Space, module->Base + symbol->IatRva, (const WitU8 *)&address, 8)) {
                        status = WIT_STATUS_BAD_ADDRESS;
                        break;
                    }
                }
            }
        }
    }
    for (WitU32 i = 0; status == WIT_STATUS_OK && i < WIT_LIBRARY_CAPACITY; ++i) {
        if (transaction.Added & (1U << i)) {
            if (!wit_user_library_tls_install(process, i, &transaction.Images[i], transaction.Modules[i].Base)) {
                status = WIT_STATUS_NO_MEMORY;
            }
        }
    }
    for (WitU32 i = 0; status == WIT_STATUS_OK && i < WIT_LIBRARY_CAPACITY; ++i) {
        if (transaction.Added & (1U << i)) {
            status = finish(&process->Space, &transaction.Images[i], transaction.Modules[i].Base);
        }
    }
    for (WitU32 i = 0; status == WIT_STATUS_OK && i < WIT_LIBRARY_CAPACITY; ++i) {
        if (transaction.Added & (1U << i)) {
            transaction.Modules[i].Token = wit_handle_grant(&process->Handles, WIT_HANDLE_LIBRARY, WIT_RIGHT_READ);
            if (!transaction.Modules[i].Token) {
                status = WIT_STATUS_NO_MEMORY;
            }
        }
    }
    if (status != WIT_STATUS_OK) {
        discard(process);
        return status;
    }
    WitUserLibrary previous[WIT_LIBRARY_CAPACITY];
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        previous[i] = process->Libraries[i];
    }
    ++transaction.Modules[root].References;
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        process->Libraries[i] = transaction.Modules[i];
        if (transaction.Added & (1U << i)) {
            process->Space.LibraryRanges[i] =
                (WitVirtualRange){transaction.Modules[i].Base, transaction.Modules[i].ImageBytes};
        }
    }
    if (callbacks) {
        status = wit_user_library_begin_lifecycle(
            process, transaction.Added, 1, 0, 0, transaction.Modules[root].Token, lifecycleAddress);
        if (status != WIT_STATUS_OK) {
            for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
                if (transaction.Added & (1U << i)) {
                    wit_user_library_tls_remove(process, i);
                    if (wit_user_library_release(&process->Space, process->Libraries[i].Base) != WIT_STATUS_OK ||
                        wit_handle_close(&process->Handles, process->Libraries[i].Token) != WIT_STATUS_OK) {
                        wit_panic("Lifecycle prepare rollback failed");
                    }
                    process->Space.LibraryRanges[i] = (WitVirtualRange){0};
                }
            }
            for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
                process->Libraries[i] = previous[i];
            }
            return status;
        }
    }
    wit_user_library_tls_refresh(process);
    *result = transaction.Modules[root].Token;
    return WIT_STATUS_OK;
}

WitU32 wit_user_library_reachable(const WitUserProcess *process)
{
    WitU32 live = process->LibraryLifecycle.Token ? process->LibraryLifecycle.Mask : 0;
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        if (process->Libraries[i].Token && (process->Libraries[i].References || process->Libraries[i].Readers)) {
            live |= 1U << i;
        }
    }
    for (WitU32 pass = 0; pass < WIT_LIBRARY_CAPACITY; ++pass) {
        for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
            if (live & (1U << i)) {
                live |= process->Libraries[i].Dependencies;
            }
        }
    }
    return live;
}

void wit_user_library_collect(WitUserProcess *process)
{
    const WitU32 live = wit_user_library_reachable(process);
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        WitUserLibrary *module = &process->Libraries[i];
        if (!module->Token || (live & (1U << i))) {
            continue;
        }
        if (module->AttachOrder) {
            wit_panic("Initialized library lacks detach completion");
        }
        wit_user_library_tls_remove(process, i);
        if (wit_user_library_release(&process->Space, module->Base) != WIT_STATUS_OK) {
            wit_panic("Library graph release failed");
        }
        if (wit_handle_close(&process->Handles, module->Token) != WIT_STATUS_OK) {
            wit_panic("Library graph handle lost");
        }
        process->Space.LibraryRanges[i] = (WitVirtualRange){0};
        *module = (WitUserLibrary){0};
    }
    wit_user_library_tls_refresh(process);
}

WitU64 wit_user_library_call(
    WitUserProcess *process, WitU64 inputAddress, WitU64 bytes, WitU64 reserved, WitU64 *result)
{
    WitLibraryRequest request;
    *result = 0;
    if (reserved || bytes != sizeof(request)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_copy_from(&process->Space, inputAddress, (WitU8 *)&request, sizeof(request))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request.Version != WIT_LIBRARY_VERSION ||
        request.Size != sizeof(request) ||
        request.Operation > WIT_LIBRARY_THREAD_LEAVE) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (request.Operation == WIT_LIBRARY_THREAD_ENTER || request.Operation == WIT_LIBRARY_THREAD_LEAVE) {
        return wit_user_library_thread_notify(process, &request);
    }
    if (request.Operation == WIT_LIBRARY_SHUTDOWN) {
        return wit_user_library_shutdown(process, &request);
    }
    if (process->LibraryShutdown && (request.Operation == WIT_LIBRARY_LOAD || request.Operation == WIT_LIBRARY_FIND)) {
        return WIT_STATUS_CLOSED;
    }
    if (request.Operation == WIT_LIBRARY_FINISH_LIFECYCLE) {
        return wit_user_library_finish_lifecycle(process, &request);
    }
    if (process->LibraryLifecycle.Token &&
        ((process->LibraryLifecycle.Owner != process->Threads[process->CurrentThread].Handle &&
             !process->LibraryLifecycle.ThreadNotify) ||
            request.Operation == WIT_LIBRARY_LOAD ||
            request.Operation == WIT_LIBRARY_UNLOAD ||
            request.Operation == WIT_LIBRARY_FIND)) {
        return WIT_STATUS_BUSY;
    }
    if (request.Operation >= WIT_LIBRARY_ACQUIRE_READER) {
        return wit_user_library_reader_call(process, &request, result);
    }
    const WitPackage *package = wit_storage_package();
    if (!package) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (request.Operation == WIT_LIBRARY_FIND) {
        if (request.Handle ||
            request.Flags > WIT_LIBRARY_BY_BASENAME ||
            request.Ordinal ||
            request.Buffer ||
            request.BufferBytes ||
            !request.NameBytes) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        if (request.NameBytes > WIT_LIBRARY_PATH_BYTES) {
            return WIT_STATUS_TOO_LARGE;
        }
        WitU8 name[WIT_LIBRARY_PATH_BYTES];
        if (!wit_user_copy_from(&process->Space, request.Name, name, (WitU32)request.NameBytes)) {
            return WIT_STATUS_BAD_ADDRESS;
        }
        for (WitU32 i = 0; i < request.NameBytes; ++i) {
            if (!name[i] || name[i] == '\\' || (request.Flags && name[i] == '/')) {
                return WIT_STATUS_INVALID_ARGUMENT;
            }
        }
        WitUserLibrary *match = 0;
        for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
            WitUserLibrary *current = &process->Libraries[i];
            if (!current->Token) {
                continue;
            }
            const WitU8 *stored = package->Data + current->NameOffset;
            WitU32 start = 0;
            if (request.Flags) {
                for (WitU32 j = 0; j < current->NameBytes; ++j) {
                    if (stored[j] == '/') {
                        start = j + 1;
                    }
                }
            }
            if (current->NameBytes - start != request.NameBytes) {
                continue;
            }
            WitU32 j = 0;
            while (j < request.NameBytes && name[j] == stored[start + j]) {
                ++j;
            }
            if (j != request.NameBytes) {
                continue;
            }
            if (match) {
                return WIT_STATUS_BUSY;
            }
            match = current;
        }
        if (!match) {
            return WIT_STATUS_NOT_FOUND;
        }
        if (match->References == ~0U) {
            return WIT_STATUS_NO_MEMORY;
        }
        ++match->References;
        *result = match->Token;
        return WIT_STATUS_OK;
    }
    if (request.Operation == WIT_LIBRARY_LOAD) {
        if (request.Handle ||
            request.Flags > WIT_LIBRARY_USER_LIFECYCLE ||
            request.Ordinal ||
            !request.NameBytes ||
            (request.Flags ? request.BufferBytes != 8 : (request.Buffer || request.BufferBytes))) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        if (request.Flags && !wit_user_buffer_writable(&process->Space, request.Buffer, 8)) {
            return WIT_STATUS_BAD_ADDRESS;
        }
        if (request.NameBytes > WIT_PACKAGE_MAX_NAME) {
            return WIT_STATUS_TOO_LARGE;
        }
        WitU8 name[WIT_PACKAGE_MAX_NAME];
        if (!wit_user_copy_from(&process->Space, request.Name, name, (WitU32)request.NameBytes)) {
            return WIT_STATUS_BAD_ADDRESS;
        }
        WitPackageFile file;
        const WitPackageStatus found = wit_package_find(package, name, (WitU32)request.NameBytes, &file);
        if (found != WitPackageOk) {
            return found == WitPackageMissing ? WIT_STATUS_NOT_FOUND : WIT_STATUS_INVALID_ARGUMENT;
        }
        WitU64 address = 0;
        const WitU64 status = load(process, package, &file, request.Flags, &address, result);
        if (status == WIT_STATUS_OK &&
            request.Flags &&
            !wit_user_copy_to(&process->Space, request.Buffer, (const WitU8 *)&address, 8)) {
            wit_panic("Lifecycle pointer copy lost validation");
        }
        return status;
    }
    const WitU64 checked = wit_handle_check(&process->Handles, request.Handle, WIT_HANDLE_LIBRARY, WIT_RIGHT_READ);
    if (checked != WIT_STATUS_OK) {
        return checked;
    }
    WitU32 slot = WIT_LIBRARY_CAPACITY;
    for (WitU32 i = 0; i < WIT_LIBRARY_CAPACITY; ++i) {
        if (process->Libraries[i].Token == request.Handle) {
            slot = i;
            break;
        }
    }
    if (slot == WIT_LIBRARY_CAPACITY) {
        return WIT_STATUS_BAD_HANDLE;
    }
    WitUserLibrary *module = &process->Libraries[slot];
    if (request.Operation == WIT_LIBRARY_PATH) {
        if (request.Flags ||
            request.Name ||
            request.NameBytes ||
            request.Ordinal ||
            request.BufferBytes != sizeof(WitLibraryPath)) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        WitLibraryPath path = {WIT_LIBRARY_VERSION, sizeof(path), module->NameBytes, 0, {0}};
        for (WitU32 i = 0; i < module->NameBytes; ++i) {
            path.Name[i] = package->Data[module->NameOffset + i];
        }
        if (!wit_user_copy_to(&process->Space, request.Buffer, (const WitU8 *)&path, sizeof(path))) {
            return WIT_STATUS_BAD_ADDRESS;
        }
        *result = sizeof(path);
        return WIT_STATUS_OK;
    }
    if (request.Operation == WIT_LIBRARY_QUERY) {
        if (request.Flags ||
            request.Name ||
            request.NameBytes ||
            request.Ordinal ||
            request.BufferBytes != sizeof(WitLibraryInfo)) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        const WitLibraryInfo info = {WIT_LIBRARY_VERSION, sizeof(info), module->Base, module->ImageBytes,
            module->EntryRva, module->UnwindRva, module->UnwindBytes, module->References};
        if (!wit_user_copy_to(&process->Space, request.Buffer, (const WitU8 *)&info, sizeof(info))) {
            return WIT_STATUS_BAD_ADDRESS;
        }
        *result = sizeof(info);
        return WIT_STATUS_OK;
    }
    if (request.Operation == WIT_LIBRARY_UNLOAD) {
        if (request.Flags > WIT_LIBRARY_USER_LIFECYCLE ||
            request.Name ||
            request.NameBytes ||
            request.Ordinal ||
            (request.Flags ? request.BufferBytes != 8 : (request.Buffer || request.BufferBytes))) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        if (request.Flags && !wit_user_buffer_writable(&process->Space, request.Buffer, 8)) {
            return WIT_STATUS_BAD_ADDRESS;
        }
        if (!module->References) {
            return WIT_STATUS_DENIED;
        }
        WitU64 address = 0;
        const WitU64 status =
            wit_user_library_release_plan(process, module, request.Handle, 0, request.Flags, &address);
        if (status != WIT_STATUS_OK) {
            return status;
        }
        if (!address) {
            --module->References;
            wit_user_library_collect(process);
        }
        if (request.Flags && !wit_user_copy_to(&process->Space, request.Buffer, (const WitU8 *)&address, 8)) {
            wit_panic("Lifecycle release pointer lost validation");
        }
        return WIT_STATUS_OK;
    }
    if (request.Buffer || request.BufferBytes || request.Flags > WIT_LIBRARY_BY_ORDINAL) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    char name[WIT_PE_EXPORT_NAME_MAX];
    const char *query = 0;
    if (request.Flags) {
        if (request.Name || request.NameBytes || request.Ordinal > 0xFFFFFFFFULL) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
    } else {
        if (request.Ordinal || !request.NameBytes) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        if (request.NameBytes > sizeof(name)) {
            return WIT_STATUS_TOO_LARGE;
        }
        if (!wit_user_copy_from(&process->Space, request.Name, (WitU8 *)name, (WitU32)request.NameBytes)) {
            return WIT_STATUS_BAD_ADDRESS;
        }
        query = name;
    }
    const WitU8 *file = package->Data + module->FileOffset;
    if (wit_pe_validate_profile(file, (WitU32)module->FileBytes, &plan, LIBRARY_PROFILE) != WitPeOk) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitU32 rva = 0;
    const WitPeStatus status =
        wit_pe_export_find(file, &plan, query, (WitU32)request.NameBytes, (WitU32)request.Ordinal, &rva);
    if (status != WitPeOk) {
        return pe_status(status);
    }
    if (!rva) {
        return WIT_STATUS_NOT_FOUND;
    }
    *result = module->Base + rva;
    return WIT_STATUS_OK;
}
