#include "witos/arch.h"
#include "witos/platform.h"
#include "a64.h"

/* The ARM64 context profile (plan step K1.4). The floating-point control bits the hardware implements are probed
 * once at boot, as x64 probes the MXCSR mask: FPCR stores the implemented bits and reads zero elsewhere.
 * CONTEXT_PROFILE reports the mask, a captured context carries FPCR within it and the kernel refuses a context
 * outside it. Hardware debug must be off for EL0: the kernel never enables monitor debug events, and a context
 * reports WIT_CPU_DEBUG_DISABLED only because this was checked. */

/* FPCR bits AArch64 defines: AHP, DN, FZ, RMode, FZ16, the exception trap enables and EBF, NEP, AH and FIZ. */
#define FPCR_ARCHITECTURAL 0x07C8BF07ULL
/* Always implemented: AHP, DN, FZ and the rounding mode. */
#define FPCR_BASE 0x07C00000ULL
#define MDSCR_KDE (1ULL << 13)
#define MDSCR_MDE (1ULL << 15)

static WitU64 float_control_mask;

WitU64 wit_a64_float_control_mask(void)
{
    return float_control_mask;
}

void wit_a64_context_initialize(void)
{
    const WitU64 saved = wit_a64_float_control();
    wit_a64_set_float_control(FPCR_ARCHITECTURAL);
    const WitU64 mask = wit_a64_float_control();
    wit_a64_set_float_control(saved);
    if ((mask & ~FPCR_ARCHITECTURAL) || (mask & FPCR_BASE) != FPCR_BASE || wit_a64_float_control() != saved) {
        wit_panic("Unsupported FPCR profile");
    }
    if (wit_a64_debug_control() & (MDSCR_MDE | MDSCR_KDE)) {
        wit_panic("Inherited hardware debug control is unsupported");
    }
    float_control_mask = mask;
}
