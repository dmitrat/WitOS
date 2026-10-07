; ARM64 bring-up, memory-management, user-mode and cache instructions, AAPCS64. Interrupts stay masked except in
; the idle wait and at EL0.

HOST_SIZE EQU 176

    AREA |.data|, DATA, READWRITE
host_stack DCQ 0 ; SP of wit_a64_run_user while a component runs

    AREA |.text|, CODE, READONLY

    IMPORT wit_a64_resume_frame

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
    EXPORT wit_a64_system_control
    EXPORT wit_a64_set_system_control
    EXPORT wit_a64_fp_access
    EXPORT wit_a64_set_fp_access
    EXPORT wit_a64_float_control
    EXPORT wit_a64_set_float_control
    EXPORT wit_a64_debug_control
    EXPORT wit_a64_set_thread_pointer
    EXPORT wit_a64_thread_pointer
    EXPORT wit_a64_run_user
    EXPORT wit_a64_leave_user
    EXPORT wit_arch_idle_once
    EXPORT wit_a64_idle_resume
    EXPORT wit_a64_clean_data
    EXPORT wit_a64_invalidate_instructions
    EXPORT wit_a64_data_barrier
    EXPORT wit_arch_process_write_barrier

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

wit_a64_system_control PROC
    mrs x0, sctlr_el1
    ret
    ENDP

wit_a64_set_system_control PROC
    msr sctlr_el1, x0
    isb
    ret
    ENDP

wit_a64_fp_access PROC
    mrs x0, cpacr_el1
    ret
    ENDP

wit_a64_set_fp_access PROC
    msr cpacr_el1, x0
    isb
    ret
    ENDP

wit_a64_float_control PROC
    mrs x0, fpcr
    ret
    ENDP

wit_a64_set_float_control PROC
    msr fpcr, x0
    ret
    ENDP

wit_a64_debug_control PROC
    mrs x0, mdscr_el1
    ret
    ENDP

wit_a64_set_thread_pointer PROC
    msr tpidrro_el0, x0
    ret
    ENDP

wit_a64_thread_pointer PROC
    mrs x0, tpidrro_el0
    ret
    ENDP

; x0 = EL0 frame at the top of its kernel stack, x1 = address-space root. Serialized launch with IRQ masked:
; saves the callee-saved host state, switches TTBR0 and resumes the frame. wit_a64_leave_user returns from here.
wit_a64_run_user PROC
    sub sp, sp, #HOST_SIZE
    stp x19, x20, [sp]
    stp x21, x22, [sp, #16]
    stp x23, x24, [sp, #32]
    stp x25, x26, [sp, #48]
    stp x27, x28, [sp, #64]
    stp x29, x30, [sp, #80]
    stp d8, d9, [sp, #96]
    stp d10, d11, [sp, #112]
    stp d12, d13, [sp, #128]
    stp d14, d15, [sp, #144]
    mrs x2, fpcr
    mrs x3, ttbr0_el1
    stp x2, x3, [sp, #160]
    ldr x4, =host_stack
    mov x2, sp
    str x2, [x4]
    dsb ishst
    msr ttbr0_el1, x1
    isb
    tlbi vmalle1
    dsb ish
    isb
    b wit_a64_resume_frame
    ENDP

; Masks IRQ, restores the kernel root and the host state saved by wit_a64_run_user and returns from it. The
; user thread registers are cleared; the abandoned kernel stack belongs to the finished component.
wit_a64_leave_user PROC
    msr daifset, #2
    ldr x4, =host_stack
    ldr x2, [x4]
    mov sp, x2
    msr tpidr_el0, xzr
    msr sp_el0, xzr
    ldp x2, x3, [sp, #160]
    msr fpcr, x2
    dsb ishst
    msr ttbr0_el1, x3
    isb
    tlbi vmalle1
    dsb ish
    isb
    ldp d14, d15, [sp, #144]
    ldp d12, d13, [sp, #128]
    ldp d10, d11, [sp, #112]
    ldp d8, d9, [sp, #96]
    ldp x29, x30, [sp, #80]
    ldp x27, x28, [sp, #64]
    ldp x25, x26, [sp, #48]
    ldp x23, x24, [sp, #32]
    ldp x21, x22, [sp, #16]
    ldp x19, x20, [sp]
    add sp, sp, #HOST_SIZE
    ret
    ENDP

; IRQ is masked at entry. WFI wakes for a pending interrupt even while it is masked; unmasking takes it at
; wit_a64_idle_resume, so no interrupt is lost between the caller's check and the wait.
wit_arch_idle_once PROC
    wfi
    msr daifclr, #2
wit_a64_idle_resume
    msr daifset, #2
    ret
    ENDP

; x0 = address, x1 = size. Cleans by the smallest data cache line (CTR_EL0.DminLine) to the point of unification.
wit_a64_clean_data PROC
    cbz x1, clean_done
    mrs x2, ctr_el0
    ubfx x2, x2, #16, #4
    mov x3, #4
    lsl x3, x3, x2
    add x1, x0, x1
    sub x4, x3, #1
    bic x0, x0, x4
clean_loop
    dc cvau, x0
    add x0, x0, x3
    cmp x0, x1
    b.lo clean_loop
clean_done
    dsb ish
    ret
    ENDP

wit_a64_invalidate_instructions PROC
    dsb ish
    ic ialluis
    dsb ish
    isb
    ret
    ENDP

wit_a64_data_barrier PROC
    dsb ish
    ret
    ENDP

; All guest threads execute on the sole online processor. This is a full data-memory barrier, not instruction
; cache maintenance or a GC stop.
wit_arch_process_write_barrier PROC
    dsb ish
    ret
    ENDP

    END
