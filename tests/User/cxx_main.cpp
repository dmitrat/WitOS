// The C++ runtime of the system layer (plan step S4): the exception scenarios of tests/User/cxx_exceptions.cpp,
// compiled unchanged in their Itanium form, must print the trace Linux prints (cxx_itanium_trace.h, which the
// itanium-reference job of CI checks by building this same program natively on Linux), and libc++ must work for
// what the runtime port needs: strings and containers, algorithms, smart pointers, std::function, streams, threads
// with mutexes, condition variables and futures, exception_ptr across threads, thread_local destructors, the clock,
// the random device and the filesystem. The program is the root task of the cxx scenario on WitOS and an ordinary
// program on Linux; its last line names the result.
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <exception>
#include <filesystem>
#include <functional>
#include <future>
#include <map>
#include <memory>
#include <mutex>
#include <numeric>
#include <random>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

#include "cxx_itanium_trace.h"

#if defined(__x86_64__)
#define ISA_NAME "x86_64"
#elif defined(__aarch64__)
#define ISA_NAME "aarch64"
#else
#error "unsupported architecture"
#endif

extern "C" int cxx_exceptions_run();

namespace {

std::string trace_line;
int checks, failures;

void check(bool condition, const char *what)
{
    ++checks;
    if (!condition) {
        ++failures;
        std::printf("[CXX] FAILED: %s\n", what);
    }
}

struct Counted {
    static std::atomic<int> live;
    int value;

    explicit Counted(int v) : value(v)
    {
        ++live;
    }

    ~Counted()
    {
        --live;
    }
};

std::atomic<int> Counted::live{0};

std::atomic<int> thread_local_destroyed{0};

struct PerThread {
    int value = 7;

    ~PerThread()
    {
        ++thread_local_destroyed;
    }
};

thread_local PerThread per_thread;

void library_checks()
{
    // Strings, containers, algorithms.
    std::string text = "system";
    text += " layer";
    check(text == "system layer" && text.find("layer") == 7, "std::string");
    std::vector<int> numbers(100);
    std::iota(numbers.begin(), numbers.end(), 1);
    std::reverse(numbers.begin(), numbers.end());
    std::sort(numbers.begin(), numbers.end());
    check(numbers.front() == 1 && numbers.back() == 100 && std::accumulate(numbers.begin(), numbers.end(), 0) == 5050,
        "std::vector with sort and accumulate");
    std::map<std::string, int> ordered{{"b", 2}, {"a", 1}, {"c", 3}};
    check(ordered.begin()->first == "a" && ordered.size() == 3, "std::map");
    std::unordered_map<int, std::string> hashed;
    for (int i = 0; i < 1000; ++i) {
        hashed[i] = std::to_string(i * i);
    }
    check(hashed.size() == 1000 && hashed[31] == "961", "std::unordered_map and std::to_string");

    // Smart pointers and std::function.
    {
        auto shared = std::make_shared<Counted>(5);
        std::weak_ptr<Counted> weak = shared;
        auto unique = std::make_unique<Counted>(6);
        check(Counted::live == 2 && weak.lock()->value == 5 && unique->value == 6, "smart pointers");
    }
    check(Counted::live == 0, "smart pointers released their objects");
    std::function<int(int)> twice = [](int v) {
        return v * 2;
    };
    check(twice(21) == 42, "std::function");

    // Streams.
    std::ostringstream out;
    out << "pi=" << 3.25 << " hex=" << std::hex << 255;
    check(out.str() == "pi=3.25 hex=ff", "std::ostringstream");
    std::istringstream in("17 25");
    int a = 0, b = 0;
    in >> a >> b;
    check(a + b == 42, "std::istringstream");

    // Exceptions of the library: what() and a standard hierarchy.
    try {
        (void)std::vector<int>().at(3);
        check(false, "std::out_of_range thrown");
    } catch (const std::out_of_range &error) {
        check(std::strlen(error.what()) > 0, "std::out_of_range caught with a message");
    }

    // Threads, a mutex, a condition variable, a future and exception_ptr across threads.
    std::mutex lock;
    std::condition_variable ready;
    int total = 0, arrived = 0;
    std::vector<std::thread> workers;
    for (int t = 0; t < 2; ++t) {
        workers.emplace_back([&, t] {
            check(per_thread.value == 7, "thread_local in a worker");
            per_thread.value = t;
            std::lock_guard<std::mutex> guard(lock);
            total += 1000 * (t + 1);
            ++arrived;
            ready.notify_one();
        });
    }
    {
        std::unique_lock<std::mutex> guard(lock);
        ready.wait(guard, [&] { return arrived == 2; });
    }
    for (auto &worker : workers) {
        worker.join();
    }
    check(total == 3000, "std::thread, std::mutex and std::condition_variable");
    check(thread_local_destroyed == 2, "thread_local destructors ran at the threads' exit");
    auto answer = std::async(std::launch::async, [] { return 6 * 7; });
    check(answer.get() == 42, "std::async and std::future");
    std::exception_ptr carried;
    std::thread thrower([&] {
        try {
            throw std::runtime_error("from a worker");
        } catch (...) {
            carried = std::current_exception();
        }
    });
    thrower.join();
    try {
        std::rethrow_exception(carried);
    } catch (const std::runtime_error &error) {
        check(std::string(error.what()) == "from a worker", "exception_ptr rethrown in another thread");
    }

    // The clock, the random device and the filesystem.
    const auto start = std::chrono::steady_clock::now();
    std::this_thread::sleep_for(std::chrono::milliseconds(2));
    check(std::chrono::steady_clock::now() - start >= std::chrono::milliseconds(2), "steady_clock and sleep_for");
    std::random_device device;
    unsigned mixed = 0;
    for (int i = 0; i < 4; ++i) {
        mixed |= device();
    }
    check(mixed != 0, "std::random_device");
    std::error_code error;
    check(std::filesystem::is_directory("/", error) && !error, "std::filesystem sees the root directory");
}

} // namespace

extern "C" void cxx_trace(const char *text)
{
    if (!trace_line.empty()) {
        trace_line += ' ';
    }
    trace_line += text;
}

// The scenario's declaration is vcruntime's; the Itanium runtime counts the same through std::uncaught_exceptions.
extern "C" int __uncaught_exceptions()
{
    return std::uncaught_exceptions();
}

int main()
{
#if defined(_LIBCPP_VERSION)
    std::printf("[CXX] libc++ %d on " ISA_NAME "\n", _LIBCPP_VERSION);
#else
    std::printf("[CXX] the host's C++ library on " ISA_NAME "\n");
#endif
    const int live = cxx_exceptions_run();
    std::printf("[CXX] trace: %s\n", trace_line.c_str());
    check(live == 0, "every exception object of the scenarios was destroyed");
    check(trace_line == WIT_CXX_ITANIUM_TRACE, "the scenarios' trace is Linux's");
    library_checks();
    std::printf("[CXX] C++ runtime on " ISA_NAME ": %d checks passed, %d failed\n", checks - failures, failures);
    return failures ? 1 : 0;
}
