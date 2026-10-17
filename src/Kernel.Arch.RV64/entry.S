// RISC-V bring-up, idle and trap entry, the psABI (plan step V1.2). Interrupts stay disabled except in the idle wait,
// in the self-test kernel threads and in U-mode (V1.3).

    .equ FRAME_SIZE, 560
    .equ SSTATUS_SPP, 0x100

    .text

    .globl wit_rv64_call_on_stack
    .globl wit_rv64_stack_pointer
    .globl wit_arch_idle_once
    .globl wit_rv64_idle_resume
    .globl wit_rv64_trap_vector
    .globl wit_rv64_resume_frame

// a0 = argument, a1 = stack top, a2 = function that never returns.
wit_rv64_call_on_stack:
    csrci sstatus, 2
    mv sp, a1
    mv s0, zero
    mv ra, zero
    jalr a2
halt_forever:
    wfi
    j halt_forever

wit_rv64_stack_pointer:
    mv a0, sp
    ret

// Interrupts are disabled at entry. WFI wakes for a pending interrupt even while they are; enabling them takes it at
// wit_rv64_idle_resume, so no interrupt is lost between the caller's check and the wait.
wit_arch_idle_once:
    wfi
    csrsi sstatus, 2
wit_rv64_idle_resume:
    csrci sstatus, 2
    ret

// The trap vector (stvec, direct mode). sscratch is zero while the kernel runs and the kernel stack top of the running
// user thread while U-mode runs (V1.3): a trap swaps it with sp, so a trap from U-mode lands on that kernel stack and
// a trap from S-mode, which swapped a zero, stays on the interrupted stack. The frame holds x1–x31 (x2 the interrupted
// sp), the trap CSRs, the floating-point registers and fcsr; wit_rv64_trap returns the frame to resume, possibly on
// another thread's kernel stack.
    .p2align 2
wit_rv64_trap_vector:
    csrrw sp, sscratch, sp
    bnez sp, 1f
    csrr sp, sscratch
1:
    addi sp, sp, -FRAME_SIZE
    sd x1, 8(sp)
    sd x3, 24(sp)
    sd x4, 32(sp)
    sd x5, 40(sp)
    sd x6, 48(sp)
    sd x7, 56(sp)
    sd x8, 64(sp)
    sd x9, 72(sp)
    sd x10, 80(sp)
    sd x11, 88(sp)
    sd x12, 96(sp)
    sd x13, 104(sp)
    sd x14, 112(sp)
    sd x15, 120(sp)
    sd x16, 128(sp)
    sd x17, 136(sp)
    sd x18, 144(sp)
    sd x19, 152(sp)
    sd x20, 160(sp)
    sd x21, 168(sp)
    sd x22, 176(sp)
    sd x23, 184(sp)
    sd x24, 192(sp)
    sd x25, 200(sp)
    sd x26, 208(sp)
    sd x27, 216(sp)
    sd x28, 224(sp)
    sd x29, 232(sp)
    sd x30, 240(sp)
    sd x31, 248(sp)
    sd zero, 0(sp)
    csrr t0, sscratch
    sd t0, 16(sp)
    csrw sscratch, zero
    csrr t0, sepc
    sd t0, 256(sp)
    csrr t0, sstatus
    sd t0, 264(sp)
    csrr t0, scause
    sd t0, 272(sp)
    csrr t0, stval
    sd t0, 280(sp)
    fsd f0, 288(sp)
    fsd f1, 296(sp)
    fsd f2, 304(sp)
    fsd f3, 312(sp)
    fsd f4, 320(sp)
    fsd f5, 328(sp)
    fsd f6, 336(sp)
    fsd f7, 344(sp)
    fsd f8, 352(sp)
    fsd f9, 360(sp)
    fsd f10, 368(sp)
    fsd f11, 376(sp)
    fsd f12, 384(sp)
    fsd f13, 392(sp)
    fsd f14, 400(sp)
    fsd f15, 408(sp)
    fsd f16, 416(sp)
    fsd f17, 424(sp)
    fsd f18, 432(sp)
    fsd f19, 440(sp)
    fsd f20, 448(sp)
    fsd f21, 456(sp)
    fsd f22, 464(sp)
    fsd f23, 472(sp)
    fsd f24, 480(sp)
    fsd f25, 488(sp)
    fsd f26, 496(sp)
    fsd f27, 504(sp)
    fsd f28, 512(sp)
    fsd f29, 520(sp)
    fsd f30, 528(sp)
    fsd f31, 536(sp)
    frcsr t0
    sd t0, 544(sp)
    sd zero, 552(sp)
    mv a0, sp
    call wit_rv64_trap

// a0 = frame to resume. The stack pointer after SRET is the frame's X[2]; a frame that returns to U-mode leaves its
// kernel stack top in sscratch for the next trap, so it must sit at the top of its thread's kernel stack.
wit_rv64_resume_frame:
    mv sp, a0
    ld t0, 544(sp)
    fscsr t0
    fld f0, 288(sp)
    fld f1, 296(sp)
    fld f2, 304(sp)
    fld f3, 312(sp)
    fld f4, 320(sp)
    fld f5, 328(sp)
    fld f6, 336(sp)
    fld f7, 344(sp)
    fld f8, 352(sp)
    fld f9, 360(sp)
    fld f10, 368(sp)
    fld f11, 376(sp)
    fld f12, 384(sp)
    fld f13, 392(sp)
    fld f14, 400(sp)
    fld f15, 408(sp)
    fld f16, 416(sp)
    fld f17, 424(sp)
    fld f18, 432(sp)
    fld f19, 440(sp)
    fld f20, 448(sp)
    fld f21, 456(sp)
    fld f22, 464(sp)
    fld f23, 472(sp)
    fld f24, 480(sp)
    fld f25, 488(sp)
    fld f26, 496(sp)
    fld f27, 504(sp)
    fld f28, 512(sp)
    fld f29, 520(sp)
    fld f30, 528(sp)
    fld f31, 536(sp)
    ld t0, 256(sp)
    csrw sepc, t0
    ld t0, 264(sp)
    csrw sstatus, t0
    andi t1, t0, SSTATUS_SPP
    bnez t1, 2f
    addi t1, sp, FRAME_SIZE
    csrw sscratch, t1
2:
    ld x1, 8(sp)
    ld x3, 24(sp)
    ld x4, 32(sp)
    ld x5, 40(sp)
    ld x6, 48(sp)
    ld x7, 56(sp)
    ld x8, 64(sp)
    ld x9, 72(sp)
    ld x10, 80(sp)
    ld x11, 88(sp)
    ld x12, 96(sp)
    ld x13, 104(sp)
    ld x14, 112(sp)
    ld x15, 120(sp)
    ld x16, 128(sp)
    ld x17, 136(sp)
    ld x18, 144(sp)
    ld x19, 152(sp)
    ld x20, 160(sp)
    ld x21, 168(sp)
    ld x22, 176(sp)
    ld x23, 184(sp)
    ld x24, 192(sp)
    ld x25, 200(sp)
    ld x26, 208(sp)
    ld x27, 216(sp)
    ld x28, 224(sp)
    ld x29, 232(sp)
    ld x30, 240(sp)
    ld x31, 248(sp)
    ld sp, 16(sp)
    sret
