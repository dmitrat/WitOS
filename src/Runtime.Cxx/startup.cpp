#include <stdint.h>

/* The static initializers of a module (P6.4.i3b), as vcruntime's startup runs them: the C initializers between
 * .CRT$XIA and .CRT$XIZ, which may fail, then the C++ ones between .CRT$XCA and .CRT$XCZ, among them the STL's
 * init_seg(compiler) and init_seg(lib) objects. The linker sorts the sections by name and may pad between their
 * contributions with zeros, which are skipped. A module's startup calls wit_cxx_run_initializers once, after its
 * compiler TLS and before its own code; the destructors these initializers register run through atexit. The markers
 * are writable data, as vcruntime's are, so the linker never folds them, and the tables are walked by address. */
using CInitializer = int(__cdecl *)();
using CppInitializer = void(__cdecl *)();

#pragma section(".CRT$XIA", long, read)
#pragma section(".CRT$XIZ", long, read)
#pragma section(".CRT$XCA", long, read)
#pragma section(".CRT$XCZ", long, read)

extern "C" {
__declspec(allocate(".CRT$XIA")) CInitializer __xi_a[] = {nullptr};
__declspec(allocate(".CRT$XIZ")) CInitializer __xi_z[] = {nullptr};
__declspec(allocate(".CRT$XCA")) CppInitializer __xc_a[] = {nullptr};
__declspec(allocate(".CRT$XCZ")) CppInitializer __xc_z[] = {nullptr};
}

namespace {
template <typename Initializer> const Initializer *At(uintptr_t address)
{
    return reinterpret_cast<const Initializer *>(address);
}
} // namespace

/* Returns 0, or the first failure of a C initializer, after which nothing else runs. */
extern "C" int wit_cxx_run_initializers(void)
{
    const uintptr_t cLast = reinterpret_cast<uintptr_t>(__xi_z);
    for (uintptr_t next = reinterpret_cast<uintptr_t>(__xi_a); next < cLast; next += sizeof(CInitializer)) {
        if (const CInitializer initializer = *At<CInitializer>(next)) {
            if (const int failure = initializer()) {
                return failure;
            }
        }
    }
    const uintptr_t cppLast = reinterpret_cast<uintptr_t>(__xc_z);
    for (uintptr_t next = reinterpret_cast<uintptr_t>(__xc_a); next < cppLast; next += sizeof(CppInitializer)) {
        if (const CppInitializer initializer = *At<CppInitializer>(next)) {
            initializer();
        }
    }
    return 0;
}
