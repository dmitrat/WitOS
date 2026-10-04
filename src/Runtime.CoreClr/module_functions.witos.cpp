#include "function_tables_guest.witos.h"
#include "unwind_checked.witos.h"
extern "C" {
#include "library.h"
}

namespace {
PRUNTIME_FUNCTION lookup(const WitLibraryInfo &image, DWORD64 pc)
{
    if (image.Version != WIT_LIBRARY_VERSION ||
        image.Size != sizeof(image) ||
        !image.Base ||
        !image.ImageBytes ||
        image.Base > UINT64_MAX - image.ImageBytes ||
        image.UnwindBytes % sizeof(RUNTIME_FUNCTION) ||
        image.UnwindRva > image.ImageBytes ||
        image.UnwindBytes > image.ImageBytes - image.UnwindRva) {
        wit_unwind_access_failure(WitUnwindFailureReason::Contract);
    }
    if (pc < image.Base || pc - image.Base >= image.ImageBytes) {
        return nullptr;
    }
    auto table = reinterpret_cast<PRUNTIME_FUNCTION>(image.Base + image.UnwindRva);
    DWORD low = 0, high = image.UnwindBytes / sizeof(RUNTIME_FUNCTION);
    while (low < high) {
        const DWORD mid = low + (high - low) / 2;
        const auto &entry = table[mid];
        if (pc - image.Base < entry.BeginAddress) {
            high = mid;
        } else if (pc - image.Base >= entry.EndAddress) {
            low = mid + 1;
        } else {
            return table + mid;
        }
    }
    return nullptr;
}
}

bool wit_coreclr_acquire_module(DWORD64 pc, WitFunctionLease *lease)
{
    if (!lease) {
        return false;
    }
    WitLibraryInfo image;
    WitU64 reader = 0;
    const auto status = wit_native_library_acquire_reader(pc, &image, &reader);
    if (status == WIT_STATUS_NOT_FOUND) {
        return false;
    }
    if (status != WIT_STATUS_OK) {
        wit_unwind_access_failure(
            status == WIT_STATUS_NO_MEMORY ? WitUnwindFailureReason::Budget : WitUnwindFailureReason::Contract);
    }
    auto entry = lookup(image, pc);
    if (!entry) {
        if (wit_native_library_release_reader(reader) != WIT_STATUS_OK) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        return false;
    }
    *lease = {0, 0, image.Base, image.ImageBytes, reinterpret_cast<WitRuntimeFunction *>(entry), reader};
    return true;
}

PRUNTIME_FUNCTION wit_coreclr_leased_module(const WitFunctionLease &lease, DWORD64 pc)
{
    // The caller's writable lease view is not module identity/metadata authority.
    // Re-query the generation-bearing kernel reader before selecting a record.
    WitLibraryInfo image;
    if (wit_native_library_query_reader(lease.ModuleReader, &image) != WIT_STATUS_OK ||
        image.Base != lease.Base ||
        image.ImageBytes != lease.Length) {
        return nullptr;
    }
    return lookup(image, pc);
}

void wit_coreclr_release_module(WitFunctionLease *lease)
{
    if (wit_native_library_release_reader(lease->ModuleReader) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    *lease = {};
}
