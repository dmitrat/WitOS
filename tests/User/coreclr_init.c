/* The CoreCLR load check (plan step R3.1), /bin/init of the runtime-coreclr scenario: musl's dynamic linker loads
 * CoreCLR's runtime and JIT built for witos from the boot package, with their C++ runtime, their TLS and their
 * initializers, and the entry points a host and the runtime call resolve. Nothing of the runtime runs yet: the host's
 * coreclr_initialize is step R3.2. */
#include <dlfcn.h>
#include <stdio.h>

static int failures;

static void *load(const char *path)
{
    void *library = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    if (!library) {
        printf("[CORECLR] FAILED: dlopen %s: %s\n", path, dlerror());
        ++failures;
    }
    return library;
}

static void require(void *library, const char *name)
{
    if (library && !dlsym(library, name)) {
        printf("[CORECLR] FAILED: %s: %s\n", name, dlerror());
        ++failures;
    }
}

int main(void)
{
    void *runtime = load("/coreclr/libcoreclr.so");
    require(runtime, "coreclr_initialize");
    require(runtime, "coreclr_execute_assembly");
    require(runtime, "coreclr_create_delegate");
    require(runtime, "coreclr_shutdown_2");
    void *jit = load("/coreclr/libclrjit.so");
    require(jit, "jitStartup");
    require(jit, "getJit");
    if (failures) {
        printf("[CORECLR] %d failures\n", failures);
        return 1;
    }
    printf("[CORECLR] libcoreclr.so and libclrjit.so loaded with their entry points\n");
    return 0;
}
