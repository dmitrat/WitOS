; Secondary processors of ARM64 (plan step K7.2): the PSCI call that starts one, its entry with the MMU off, and the
; reads of the boot processor's translation registers it will copy.
    AREA |.text|, CODE, READONLY
    EXPORT wit_a64_psci_cpu_on
    EXPORT wit_a64_secondary_entry
    EXPORT wit_a64_translation_control
    EXPORT wit_a64_memory_attributes
    EXPORT wit_a64_translation_base_high
    IMPORT wit_a64_boot_mair
    IMPORT wit_a64_boot_tcr
    IMPORT wit_a64_boot_ttbr0
    IMPORT wit_a64_boot_ttbr1
    IMPORT wit_a64_boot_sctlr
    IMPORT wit_a64_boot_cpacr
    IMPORT wit_a64_secondary_stack_tops
    IMPORT wit_a64_vectors
    IMPORT wit_a64_secondary_main

PSCI_CPU_ON EQU 0xC4000003

; x0 = target MPIDR, x1 = entry (physical), x2 = context -> x0 the PSCI result (0 success). The conduit of the
; QEMU virt profile is HVC.
wit_a64_psci_cpu_on PROC
    mov x3, x2
    mov x2, x1
    mov x1, x0
    ldr x0, =PSCI_CPU_ON
    hvc #0
    ret
    ENDP

; The entry of a started processor: EL1, MMU off, x0 = the kernel's number of the processor (the PSCI context). It
; copies the boot processor's MAIR, TCR and both table bases, turns translation on with the boot processor's SCTLR
; (the kernel is identity mapped, so the next instruction is where it was), opens FP/SIMD access as the boot
; processor did (the vectors save the q registers), installs the vectors, takes its own stack and enters C; the main
; function never returns.
wit_a64_secondary_entry PROC
    msr daifset, #0xf
    mov x19, x0
    ldr x1, =wit_a64_boot_mair
    ldr x1, [x1]
    msr mair_el1, x1
    ldr x1, =wit_a64_boot_tcr
    ldr x1, [x1]
    msr tcr_el1, x1
    ldr x1, =wit_a64_boot_ttbr0
    ldr x1, [x1]
    msr ttbr0_el1, x1
    ldr x1, =wit_a64_boot_ttbr1
    ldr x1, [x1]
    msr ttbr1_el1, x1
    isb
    tlbi vmalle1
    dsb ish
    isb
    ldr x1, =wit_a64_boot_sctlr
    ldr x1, [x1]
    msr sctlr_el1, x1
    isb
    ldr x1, =wit_a64_boot_cpacr
    ldr x1, [x1]
    msr cpacr_el1, x1
    isb
    ldr x1, =wit_a64_vectors
    msr vbar_el1, x1
    isb
    msr tpidrro_el0, xzr
    msr tpidr_el0, xzr
    ldr x1, =wit_a64_secondary_stack_tops
    ldr x1, [x1, x19, lsl #3]
    mov sp, x1
    mov x29, #0
    mov x30, #0
    mov x0, x19
    bl wit_a64_secondary_main
secondary_halt
    wfi
    b secondary_halt
    ENDP

wit_a64_translation_control PROC
    mrs x0, tcr_el1
    ret
    ENDP

wit_a64_memory_attributes PROC
    mrs x0, mair_el1
    ret
    ENDP

wit_a64_translation_base_high PROC
    mrs x0, ttbr1_el1
    ret
    ENDP
    LTORG
    END
