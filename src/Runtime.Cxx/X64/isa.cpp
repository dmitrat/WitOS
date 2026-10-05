#include <intrin.h>
#include <isa_availability.h>

/* The processor levels vcruntime publishes to compiled code and to the STL's vectorized algorithms (P6.4.i):
 * __isa_available, the highest usable level, and __isa_enabled, one bit per level. Until a module's startup calls
 * wit_cxx_initialize_isa they hold the x64 baseline, SSE2. SSE4.2 needs its CPUID bit; AVX2 also needs AVX with the
 * operating system's YMM state (OSXSAVE and XCR0) and BMI1 and BMI2, whose instructions the STL uses on that path. The
 * WitOS kernel clears OSXSAVE, so the guest runs the SSE4.2 paths at most and never reads XCR0. */
extern "C" {
int __isa_available = __ISA_AVAILABLE_SSE2;
long __isa_enabled = (1 << __ISA_AVAILABLE_X86) | (1 << __ISA_AVAILABLE_SSE2);
}

extern "C" void wit_cxx_initialize_isa(void)
{
    int info[4];
    __cpuid(info, 0);
    const int leaves = info[0];
    __cpuid(info, 1);
    const int features = info[2];
    if (!(features & (1 << 20))) {
        return; // no SSE4.2
    }
    int available = __ISA_AVAILABLE_SSE42;
    long enabled = __isa_enabled | (1 << __ISA_AVAILABLE_SSE42);
    const bool avx = (features & (1 << 28)) && (features & (1 << 27)) && (_xgetbv(0) & 6) == 6;
    if (avx) {
        available = __ISA_AVAILABLE_AVX;
        enabled |= 1 << __ISA_AVAILABLE_AVX;
        constexpr int AVX2_BMI1_BMI2 = (1 << 3) | (1 << 5) | (1 << 8);
        if (leaves >= 7) {
            __cpuidex(info, 7, 0);
            if ((info[1] & AVX2_BMI1_BMI2) == AVX2_BMI1_BMI2) {
                available = __ISA_AVAILABLE_AVX2;
                enabled |= 1 << __ISA_AVAILABLE_AVX2;
            }
        }
    }
    __isa_available = available;
    __isa_enabled = enabled;
}
