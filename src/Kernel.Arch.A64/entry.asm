; ARM64 bring-up and memory-management instructions, AAPCS64. Interrupts stay masked until the timer arrives with
; A1.3.

    AREA |.text|, CODE, READONLY

    EXPORT wit_a64_call_on_stack
    EXPORT wit_a64_mask_interrupts
    EXPORT wit_a64_enable_interrupts
    EXPORT wit_a64_interrupt_mask
    EXPORT wit_a64_wait
    EXPORT wit_a64_exception_level
    EXPORT wit_a64_stack_pointer
    EXPORT wit_a64_set_vectors
    EXPORT wit_a64_vectors_base
    EXPORT wit_a64_memory_features
    EXPORT wit_a64_install_tables
    EXPORT wit_a64_translation_base
    EXPORT wit_a64_invalidate_page

; x0 = argument, x1 = stack top, x2 = function that never returns.
wit_a64_call_on_stack PROC
    msr daifset, #0xf
    mov sp, x1
    mov x29, #0
    mov x30, #0
    blr x2
halt_forever
    wfi
    b halt_forever
    ENDP

wit_a64_mask_interrupts PROC
    msr daifset, #0xf
    ret
    ENDP

wit_a64_enable_interrupts PROC
    msr daifclr, #2
    ret
    ENDP

wit_a64_interrupt_mask PROC
    mrs x0, daif
    ret
    ENDP

wit_a64_wait PROC
    wfi
    ret
    ENDP

wit_a64_exception_level PROC
    mrs x0, CurrentEL
    ubfx x0, x0, #2, #2
    ret
    ENDP

wit_a64_stack_pointer PROC
    mov x0, sp
    ret
    ENDP

; x0 = vector table, 2 KiB aligned.
wit_a64_set_vectors PROC
    msr vbar_el1, x0
    isb
    ret
    ENDP

wit_a64_vectors_base PROC
    mrs x0, vbar_el1
    ret
    ENDP

wit_a64_memory_features PROC
    mrs x0, id_aa64mmfr0_el1
    ret
    ENDP

; x0 = TTBR0, x1 = TTBR1, x2 = TCR, x3 = MAIR. The table stores must reach the walker before the switch; the
; caller's code page stays translated by the TLB and identity-mapped in both tables.
wit_a64_install_tables PROC
    dsb ishst
    msr mair_el1, x3
    msr tcr_el1, x2
    msr ttbr0_el1, x0
    msr ttbr1_el1, x1
    isb
    tlbi vmalle1
    dsb ish
    isb
    ret
    ENDP

wit_a64_translation_base PROC
    mrs x0, ttbr0_el1
    ret
    ENDP

; x0 = virtual address; TLBI VAAE1IS takes VA[55:12] in bits 43:0.
wit_a64_invalidate_page PROC
    dsb ishst
    ubfx x0, x0, #12, #44
    tlbi vaae1is, x0
    dsb ish
    isb
    ret
    ENDP

    END
