#pragma warning(push)
#pragma warning(disable : 4100)
#include "pal.h"
#pragma warning(pop)
extern "C" {
#include "path.h"
}

// These are real standard-library objects, evaluated by the C++ compiler.
// This leaf probe does not claim the full host's throwing heap/CRT closure.
extern "C" unsigned long long wit_host_pal_file_probe()
{
    constexpr pal::string_t root = L"/";
    constexpr pal::string_t directory = L"/app";
    constexpr pal::string_t file = L"p";
    constexpr pal::string_t missing = L"/absent";
    SetLastError(0x1234);
    if (!pal::file_exists(root) || !pal::file_exists(directory) || GetLastError() != 0x1234) {
        return 2301;
    }
    size_t bytes = 0;
    auto read = pal::mmap_read(file, &bytes);
    if (!read || !bytes || ((const char *)read)[0] != '{' || GetLastError() != 0x1234) {
        return 2302;
    }
    size_t copyBytes = 0;
    auto copy = pal::mmap_copy_on_write(file, &copyBytes);
    if (!copy || copyBytes != bytes) {
        return 2303;
    }
    ((char *)copy)[0] = 'X';
    if (((const char *)read)[0] != '{') {
        return 2304;
    }
    if (pal::munmap((void *)read, bytes + 1) || GetLastError() != ERROR_INVALID_PARAMETER) {
        return 2305;
    }
    SetLastError(0x4321);
    if (!pal::munmap((void *)read, bytes) || !pal::munmap(copy, bytes) || GetLastError() != 0x4321) {
        return 2306;
    }
    if (wit_native_cwd_set("/app", 4) != WIT_STATUS_OK) {
        return 2308;
    }
    constexpr pal::string_t parent = L"../p";
    if (!pal::file_exists(parent) || wit_native_cwd_set("/", 1) != WIT_STATUS_OK) {
        return 2309;
    }
    if (pal::file_exists(missing) || GetLastError() != ERROR_FILE_NOT_FOUND) {
        return 2307;
    }
    if (wit_native_cwd_set("/native", 7) != WIT_STATUS_OK) {
        return 2310;
    }
    constexpr pal::string_t dllPath = L"lib.dll";
    pal::dll_t library = nullptr;
    SetLastError(0x4567);
    if (!pal::load_library(&dllPath, &library) || !library || GetLastError() != 0x4567) {
        return 2311;
    }
    const auto symbol = pal::get_symbol(library, "LibraryAdd");
    if (!symbol || reinterpret_cast<int (*)(int, int)>(symbol)(731, 11) != 742 || GetLastError() != 0x4567) {
        return 2312;
    }
    if (pal::get_symbol(library, "missing") || GetLastError() != ERROR_PROC_NOT_FOUND) {
        return 2313;
    }
    pal::dll_t second = nullptr;
    if (!pal::load_library(&dllPath, &second) || second != library) {
        return 2314;
    }
    SetLastError(0x6789);
    pal::unload_library(second);
    if (GetLastError() != 0x6789 || reinterpret_cast<int (*)(int, int)>(symbol)(3, 4) != 7) {
        return 2315;
    }
    pal::unload_library(library);
    if (GetLastError() != 0x6789 || pal::get_symbol(library, "LibraryAdd") || GetLastError() != ERROR_INVALID_HANDLE) {
        return 2316;
    }
    library = reinterpret_cast<pal::dll_t>(99);
    if (pal::load_library(&missing, &library) || library || GetLastError() != ERROR_FILE_NOT_FOUND) {
        return 2317;
    }
    if (wit_native_cwd_set("/", 1) != WIT_STATUS_OK) {
        return 2318;
    }
    return 42;
}
