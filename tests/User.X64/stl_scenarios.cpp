#define _SILENCE_CXX17_UNCAUGHT_EXCEPTION_DEPRECATION_WARNING
#include <algorithm>
#include <atomic>
#include <bitset>
#include <cerrno>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <exception>
#include <functional>
#include <initializer_list>
#include <mutex>
#include <new>
#include <stdexcept>
#include <string>
#include <system_error>
#include <thread>
#include <vector>
#include <xthreads.h>

/* Scenarios of the separately compiled STL parts the host uses, through the STL's headers as the host compiles them:
 * the exceptions of the throw helpers and their messages, generic error messages and Windows error mapping,
 * std::uncaught_exception and the vectorized algorithms, compared with plain loops over random data for elements of
 * one, two, four and eight bytes (P6.4.i1); threads, mutexes and condition variables (P6.4.i2). The same source runs
 * on the toolset's msvcp140 (hosted reference), on the pinned microsoft/STL sources with the WitOS C++ and C runtimes
 * (hosted) and in the guest; every run must trace the same tokens. The guest component has four threads, so no
 * scenario runs more than three besides its own. */
extern "C" void stl_trace(const char *text);

// The legacy C thread functions, exported for binary compatibility and no longer declared by the headers.
extern "C" _CRTIMP2_PURE _Thrd_result __cdecl _Thrd_create(_Thrd_t *, int (*)(void *), void *) noexcept;
extern "C" [[noreturn]] _CRTIMP2_PURE void __cdecl _Thrd_exit(int) noexcept;

namespace {

char token[256];
unsigned length;

void Begin(const char *name)
{
    length = 0;
    while (*name && length < sizeof(token) - 2) {
        token[length++] = *name++;
    }
    token[length++] = ':';
    token[length] = 0;
}

void Add(char value)
{
    if (length < sizeof(token) - 1) {
        token[length++] = value;
        token[length] = 0;
    }
}

void AddText(const char *text)
{
    for (; *text; ++text) {
        Add(*text == ' ' ? '_' : *text);
    }
}

void AddNumber(unsigned long long value)
{
    char digits[24];
    int count = 0;
    do {
        digits[count++] = char('0' + value % 10);
        value /= 10;
    } while (value);
    while (count) {
        Add(digits[--count]);
    }
}

void Text(const char *name, const char *text)
{
    Begin(name);
    AddText(text);
    stl_trace(token);
}

template <typename Exception, typename Action> void Throws(const char *name, Action action)
{
    try {
        action();
        Text(name, "none");
    } catch (const Exception &exception) {
        Text(name, exception.what());
    } catch (...) {
        Text(name, "other");
    }
}

void Exceptions()
{
    Throws<std::length_error>("x1", [] {
        std::string text;
        text.resize(text.max_size() + 1);
    });
    Throws<std::length_error>("x2", [] {
        std::vector<int> values;
        values.reserve(values.max_size() + 1);
    });
    Throws<std::out_of_range>("x3", [] { (void)std::string("abc").at(5); });
    Throws<std::out_of_range>("x4", [] { (void)std::vector<int>(2).at(2); });
    Throws<std::invalid_argument>("x5", [] { (void)std::bitset<4>(std::string("12")); });
    Throws<std::bad_function_call>("x6", [] {
        std::function<void()> empty;
        empty();
    });
    Throws<std::runtime_error>("x7", [] { std::_Xruntime_error("runtime helper"); });
    Throws<std::bad_alloc>("x8", [] { std::_Xbad_alloc(); });
    Throws<std::overflow_error>("x9", [] { (void)std::bitset<80>().set().to_ulong(); });
    Throws<std::system_error>("x10", [] { std::_Throw_Cpp_error(std::_INVALID_ARGUMENT); });
    Throws<std::system_error>("x11", [] { std::_Throw_Cpp_error(std::_RESOURCE_DEADLOCK_WOULD_OCCUR); });
    try {
        std::_Throw_Cpp_error(std::_DEVICE_OR_RESOURCE_BUSY);
    } catch (const std::system_error &error) {
        Begin("x12");
        AddNumber((unsigned)error.code().value());
        Add(',');
        AddText(error.code().category().name());
        stl_trace(token);
    }
}

void Errors()
{
    for (const int code :
        {0, EPERM, ENOENT, EINTR, EACCES, EEXIST, EINVAL, ERANGE, EILSEQ, EADDRINUSE, ETIMEDOUT, EWOULDBLOCK, 9999}) {
        Begin("g");
        AddNumber((unsigned)code);
        Add('=');
        AddText(std::generic_category().message(code).c_str());
        stl_trace(token);
    }
    Text("g2", std::make_error_code(std::errc::no_such_file_or_directory).message().c_str());
    Text("g3", std::make_error_condition(std::errc::not_enough_memory).message().c_str());
    // Windows errors map to generic conditions without a message (the guest has its own message catalogue).
    for (const int code : {2, 5, 8, 87, 183, 1460, 12345}) {
        const auto condition = std::system_category().default_error_condition(code);
        Begin("w");
        AddNumber((unsigned)code);
        Add('=');
        AddNumber((unsigned)condition.value());
        Add(',');
        AddText(condition.category().name());
        stl_trace(token);
    }
    Text("w2", std::system_category().name());
}

struct Watcher {
    bool *During;

    ~Watcher()
    {
        *During = std::uncaught_exception();
    }
};

void Uncaught()
{
    bool during = false;
    try {
        Watcher watcher{&during};
        throw 1;
    } catch (int) {
        Begin("u");
        AddNumber(during);
        Add(',');
        AddNumber(std::uncaught_exception());
        stl_trace(token);
    }
}

uint32_t seed = 0x9E3779B9u;

uint32_t Random()
{
    seed ^= seed << 13;
    seed ^= seed >> 17;
    seed ^= seed << 5;
    return seed;
}

struct Tally {
    unsigned long long Checks = 0, Wrong = 0, Mix = 0;

    void Check(bool same, size_t value)
    {
        ++Checks;
        Wrong += !same;
        Mix = Mix * 1099511628211ULL + value + 1;
    }
};

/* The algorithms the STL vectorizes, each against a plain loop. */
template <typename T> void Algorithms(const char *name)
{
    Tally tally;
    for (int round = 0; round < 400; ++round) {
        const size_t n = Random() % 300;
        std::vector<T> a(n), b(n);
        const unsigned spread = round % 3 == 0 ? 3 : 200;
        for (size_t i = 0; i < n; ++i) {
            a[i] = T(Random() % spread);
            b[i] = Random() % 8 ? a[i] : T(Random() % spread);
        }
        const T value = T(Random() % spread);

        size_t plain = 0;
        while (plain < n && a[plain] != value) {
            ++plain;
        }
        const size_t found = size_t(std::find(a.begin(), a.end(), value) - a.begin());
        tally.Check(found == plain, found);

        size_t counted = 0;
        for (const T &item : a) {
            counted += item == value;
        }
        const size_t stlCount = size_t(std::count(a.begin(), a.end(), value));
        tally.Check(stlCount == counted, stlCount);

        size_t differ = 0;
        while (differ < n && a[differ] == b[differ]) {
            ++differ;
        }
        const size_t mismatch = size_t(std::mismatch(a.begin(), a.end(), b.begin()).first - a.begin());
        tally.Check(mismatch == differ, mismatch);

        if (n) {
            size_t low = 0, high = 0;
            for (size_t i = 1; i < n; ++i) {
                low = a[i] < a[low] ? i : low;
                high = a[i] > a[high] ? i : high;
            }
            const size_t minimum = size_t(std::min_element(a.begin(), a.end()) - a.begin());
            const size_t maximum = size_t(std::max_element(a.begin(), a.end()) - a.begin());
            tally.Check(minimum == low && maximum == high, minimum * 1000 + maximum);
        }

        const size_t m = n ? Random() % (n < 5 ? n + 1 : 5) : 0;
        const size_t start = n ? Random() % (n - m + 1) : 0;
        std::vector<T> needle(a.begin() + start, a.begin() + start + m);
        if (Random() % 4 == 0 && m) {
            needle[Random() % m] = T(Random() % spread);
        }
        size_t first = n + 1, last = n + 1;
        for (size_t i = 0; i + needle.size() <= n; ++i) {
            if (std::equal(needle.begin(), needle.end(), a.begin() + i, [](T x, T y) { return x == y; })) {
                first = first == n + 1 ? i : first;
                last = i;
            }
        }
        if (needle.empty()) {
            first = 0;
            last = n;
        }
        const size_t searched = size_t(std::search(a.begin(), a.end(), needle.begin(), needle.end()) - a.begin());
        const size_t ended = size_t(std::find_end(a.begin(), a.end(), needle.begin(), needle.end()) - a.begin());
        tally.Check(
            searched == (first == n + 1 ? n : first) && ended == (last == n + 1 ? n : last), searched * 1000 + ended);

        size_t anyOf = n;
        for (size_t i = 0; i < n && anyOf == n; ++i) {
            for (const T &item : needle) {
                if (a[i] == item) {
                    anyOf = i;
                    break;
                }
            }
        }
        const size_t firstOf = size_t(std::find_first_of(a.begin(), a.end(), needle.begin(), needle.end()) - a.begin());
        tally.Check(firstOf == anyOf, firstOf);

        size_t adjacent = n;
        for (size_t i = 0; i + 1 < n; ++i) {
            if (a[i] == a[i + 1]) {
                adjacent = i;
                break;
            }
        }
        const size_t pair = size_t(std::adjacent_find(a.begin(), a.end()) - a.begin());
        tally.Check(pair == adjacent, pair);

        std::vector<T> reversed(a.rbegin(), a.rend()), copy = a;
        std::reverse(copy.begin(), copy.end());
        tally.Check(copy == reversed, n);
    }
    Begin(name);
    AddNumber(tally.Checks);
    Add(',');
    AddNumber(tally.Wrong);
    Add(',');
    AddNumber(tally.Mix % 1000000007ULL);
    stl_trace(token);
}

/* The string searches the STL vectorizes, each against a plain loop. */
template <typename Char> void Strings(const char *name)
{
    Tally tally;
    using String = std::basic_string<Char>;
    for (int round = 0; round < 400; ++round) {
        const size_t n = Random() % 200;
        String text(n, Char('a'));
        const unsigned spread = round % 2 ? 4 : 40;
        for (auto &c : text) {
            c = Char('a' + Random() % spread);
        }
        String set(Random() % 6, Char('a'));
        for (auto &c : set) {
            c = Char('a' + Random() % spread);
        }
        const auto in = [&set](Char c) {
            return set.find(c) != String::npos;
        };
        size_t plain = String::npos;
        for (size_t i = n; i-- > 0;) {
            if (in(text[i])) {
                plain = i;
                break;
            }
        }
        const size_t lastOf = text.find_last_of(set);
        tally.Check(lastOf == plain, lastOf);
        plain = String::npos;
        for (size_t i = 0; i < n; ++i) {
            if (!in(text[i])) {
                plain = i;
                break;
            }
        }
        const size_t firstNot = text.find_first_not_of(set);
        tally.Check(firstNot == plain, firstNot);
        plain = String::npos;
        for (size_t i = 0; i < n; ++i) {
            if (in(text[i])) {
                plain = i;
                break;
            }
        }
        const size_t firstOf = text.find_first_of(set);
        tally.Check(firstOf == plain, firstOf);
        const Char c = Char('a' + Random() % spread);
        plain = String::npos;
        for (size_t i = n; i-- > 0;) {
            if (text[i] == c) {
                plain = i;
                break;
            }
        }
        const size_t reverse = text.rfind(c);
        tally.Check(reverse == plain, reverse);
        const size_t sub = text.find(set);
        plain = set.empty() ? 0 : String::npos;
        for (size_t i = 0; !set.empty() && i + set.size() <= n; ++i) {
            if (text.compare(i, set.size(), set) == 0) {
                plain = i;
                break;
            }
        }
        tally.Check(sub == plain, sub);
    }
    Begin(name);
    AddNumber(tally.Checks);
    Add(',');
    AddNumber(tally.Wrong);
    Add(',');
    AddNumber(tally.Mix % 1000000007ULL);
    stl_trace(token);
}

void Flags(const char *name, std::initializer_list<unsigned long long> values)
{
    Begin(name);
    bool first = true;
    for (const unsigned long long value : values) {
        if (!first) {
            Add(',');
        }
        first = false;
        AddNumber(value);
    }
    stl_trace(token);
}

/* A thread's sum, joinable states and identities. */
void Started()
{
    unsigned long long sum = 0;
    std::thread::id inside;
    std::thread worker([&] {
        for (unsigned i = 1; i <= 1000; ++i) {
            sum += i;
        }
        inside = std::this_thread::get_id();
    });
    const bool before = worker.joinable();
    const std::thread::id id = worker.get_id();
    worker.join();
    Flags("t1",
        {sum, before, worker.joinable(), id == inside, id != std::this_thread::get_id(),
            worker.get_id() == std::thread::id()});
}

/* Three threads increment a counter under a mutex and yield while they hold it, so the others block on it. */
void Contended()
{
    std::mutex mutex;
    unsigned long long counter = 0;
    const auto work = [&] {
        for (int i = 0; i < 300; ++i) {
            std::lock_guard<std::mutex> hold(mutex);
            const unsigned long long seen = counter;
            if (i % 10 == 0) {
                std::this_thread::yield();
            }
            counter = seen + 1;
        }
    };
    std::thread a(work), b(work), c(work);
    a.join();
    b.join();
    c.join();
    Flags("t2", {counter});
}

/* One producer and two consumers through a bounded queue and one condition variable. */
void Queue()
{
    std::mutex mutex;
    std::condition_variable changed;
    unsigned items[16];
    unsigned head = 0, tail = 0;
    bool done = false;
    unsigned long long sums[2] = {};
    unsigned counts[2] = {};
    const auto consume = [&](int index) {
        for (;;) {
            std::unique_lock<std::mutex> hold(mutex);
            changed.wait(hold, [&] { return head != tail || done; });
            if (head == tail) {
                return;
            }
            sums[index] += items[head++ % 16];
            ++counts[index];
            changed.notify_all(); // room for the producer
        }
    };
    std::thread first(consume, 0), second(consume, 1);
    for (unsigned value = 1; value <= 500; ++value) {
        std::unique_lock<std::mutex> hold(mutex);
        changed.wait(hold, [&] { return tail - head < 16; });
        items[tail++ % 16] = value;
        changed.notify_one();
    }
    {
        std::lock_guard<std::mutex> hold(mutex);
        done = true;
    }
    changed.notify_all();
    first.join();
    second.join();
    Flags("t3", {sums[0] + sums[1], counts[0] + counts[1]});
}

/* notify_all releases three threads waiting on one condition variable. */
void Broadcast()
{
    std::mutex mutex;
    std::condition_variable go;
    bool open = false;
    unsigned waiting = 0, released = 0;
    const auto wait = [&] {
        std::unique_lock<std::mutex> hold(mutex);
        ++waiting;
        go.notify_all();
        go.wait(hold, [&] { return open; });
        ++released;
    };
    std::thread a(wait), b(wait), c(wait);
    {
        // The third thread holds the mutex until its wait releases it, so all three are waiting here.
        std::unique_lock<std::mutex> hold(mutex);
        go.wait(hold, [&] { return waiting == 3; });
        open = true;
    }
    go.notify_all();
    a.join();
    b.join();
    c.join();
    Flags("t4", {released});
}

/* try_lock fails while another thread holds the mutex and succeeds after it released it; a recursive mutex counts
 * its owner's locks; locking a mutex its owner holds throws. */
void Ownership()
{
    std::mutex mutex, stageMutex;
    std::condition_variable changed;
    int stage = 0;
    std::thread holder([&] {
        std::lock_guard<std::mutex> hold(mutex);
        {
            std::lock_guard<std::mutex> step(stageMutex);
            stage = 1;
        }
        changed.notify_all();
        std::unique_lock<std::mutex> step(stageMutex);
        changed.wait(step, [&] { return stage == 2; });
    });
    bool held;
    {
        std::unique_lock<std::mutex> step(stageMutex);
        changed.wait(step, [&] { return stage == 1; });
        held = !mutex.try_lock();
        stage = 2;
    }
    changed.notify_all();
    holder.join();
    const bool free = mutex.try_lock();
    if (free) {
        mutex.unlock();
    }
    Flags("t5", {held, free});

    std::recursive_mutex recursive;
    recursive.lock();
    recursive.lock();
    const bool again = recursive.try_lock();
    bool other = true;
    std::thread probe([&] {
        other = recursive.try_lock();
        if (other) {
            recursive.unlock();
        }
    });
    probe.join();
    recursive.unlock();
    recursive.unlock();
    recursive.unlock();
    bool released = false;
    std::thread second([&] {
        released = recursive.try_lock();
        if (released) {
            recursive.unlock();
        }
    });
    second.join();
    Flags("t6", {again, other, released});

    mutex.lock();
    Throws<std::system_error>("t7", [&] { mutex.lock(); });
    mutex.unlock();
}

/* The legacy C functions: an exit code through _Thrd_join, and _Thrd_exit. */
void Legacy()
{
    _Thrd_t thread;
    int code = 0, exited = 0;
    const _Thrd_result created = _Thrd_create(&thread, [](void *) { return 42; }, nullptr);
    const _Thrd_result joined = _Thrd_join(thread, &code);
    _Thrd_create(&thread, [](void *) -> int { _Thrd_exit(7); }, nullptr);
    _Thrd_join(thread, &exited);
    Flags("t8", {(unsigned)created, (unsigned)joined, (unsigned)code, (unsigned)exited});
}

/* The steady clock and the processors, and yielding until another thread ran. */
void Scheduling()
{
    const auto first = std::chrono::steady_clock::now();
    std::atomic<bool> ran{false};
    std::thread setter([&] { ran = true; });
    while (!ran) {
        std::this_thread::yield();
    }
    setter.join();
    const auto second = std::chrono::steady_clock::now();
    Flags("t9", {std::thread::hardware_concurrency() >= 1, second >= first, std::chrono::steady_clock::is_steady});
}

std::mutex finishedMutex;
std::condition_variable finished;
bool ready;

/* A detached thread that notifies at its exit, which keeps the mutex locked until then. Static objects outlive the
 * thread's last notification. It runs last: the thread may still be ending when the scenarios end. */
void Detached()
{
    std::thread([] {
        std::unique_lock<std::mutex> hold(finishedMutex);
        ready = true;
        std::notify_all_at_thread_exit(finished, std::move(hold));
    }).detach();
    std::unique_lock<std::mutex> hold(finishedMutex);
    finished.wait(hold, [] { return ready; });
    Flags("t10", {ready});
}

void Threads()
{
    Started();
    Contended();
    Queue();
    Broadcast();
    Ownership();
    Legacy();
    Scheduling();
    Detached();
}

} // namespace

extern "C" void stl_scenarios_run()
{
    Exceptions();
    Errors();
    Uncaught();
    Algorithms<uint8_t>("v1");
    Algorithms<uint16_t>("v2");
    Algorithms<uint32_t>("v4");
    Algorithms<uint64_t>("v8");
    Strings<char>("s1");
    Strings<wchar_t>("s2");
    Threads();
}
