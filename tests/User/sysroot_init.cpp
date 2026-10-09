#include <algorithm>
#include <cstdio>
#include <filesystem>
#include <map>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#include <witos/syscall.h>
#include <witos/user_abi.h>

/* /bin/init of the sysroot scenario (plan step R1.2a): a C++ program that clang's own driver compiled and linked against
 * the system layer's sysroot, as a Linux musl program is built (--sysroot, compiler-rt, libunwind, libc++, lld), and
 * that runs as a dynamic program under musl's dynamic linker with the shared C++ runtime from the package. It checks
 * libc++'s strings, containers and algorithms, an exception across frames, a thread and a mutex, std::filesystem over
 * the boot package, and the sysroot's WitOS headers: the ABI-1 version they were compiled with is the kernel's. The last
 * line names the ISA and the checks; a failed check exits with 1, which the root task reports as a failed boot. */

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#else
#define ISA_NAME "aarch64"
#endif

namespace {
int checks = 0;
int failures = 0;

void check(bool condition, const char *what)
{
    ++checks;
    if (!condition) {
        ++failures;
        std::printf("[SYSROOT] FAILED: %s\n", what);
    }
}

int thrower(int depth)
{
    if (depth == 0) {
        throw std::runtime_error("thrown four frames down");
    }
    return thrower(depth - 1) + 1;
}
} // namespace

int main()
{
    std::vector<std::string> words{"sysroot", "clang", "witos", "libc++"};
    std::sort(words.begin(), words.end());
    check(words.front() == "clang" && words.back() == "witos", "std::sort of std::string");
    std::map<std::string, int> lengths;
    for (const auto &word : words) {
        lengths[word] = static_cast<int>(word.size());
    }
    check(lengths.at("sysroot") == 7 && std::to_string(lengths.size()) == "4", "std::map and std::to_string");

    std::string caught;
    try {
        thrower(4);
    } catch (const std::exception &error) {
        caught = error.what();
    }
    check(caught == "thrown four frames down", "an exception across frames");

    std::mutex lock;
    int total = 0;
    std::thread worker([&] {
        for (int i = 1; i <= 100; ++i) {
            std::lock_guard<std::mutex> guard(lock);
            total += i;
        }
    });
    worker.join();
    check(total == 5050, "a thread and a mutex");

    check(std::filesystem::exists("/bin/init") && std::filesystem::is_directory("/lib"),
        "std::filesystem over the package");

    WitU64 query = 0;
    const WitU64 status = wit_syscall(WIT_CALL_QUERY, 0, 0, 0, &query);
    check(status == WIT_STATUS_OK && (query & 0xFFFFFFFFULL) == WIT_ABI_VERSION,
        "the sysroot's ABI-1 headers are the kernel's");

    std::printf(
        "[SYSROOT] clang driver program on " ISA_NAME ": %d checks passed, %d failed\n", checks - failures, failures);
    return failures ? 1 : 0;
}
