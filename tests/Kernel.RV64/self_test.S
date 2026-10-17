// RISC-V kernel self-test code, the psABI: fault triggers that each raise one synchronous exception in S-mode, and the
// kernel workers of the preemption test.

    .text

    .globl wit_rv64_trigger_breakpoint
    .globl wit_rv64_trigger_illegal
    .globl wit_rv64_trigger_load_fault
    .globl wit_rv64_worker

wit_rv64_trigger_breakpoint:
    ebreak
    ret

wit_rv64_trigger_illegal:
    unimp
    ret

// a0 = address that the translation tables leave unmapped.
wit_rv64_trigger_load_fault:
    ld a0, 0(a0)
    ret

// a0 = worker index. The worker never yields or returns. Each worker keeps its own sentinels in s2, s3, f8 and f16
// and its own rounding mode in frm, which every preemption must preserve.
wit_rv64_worker:
    mv s0, a0
    lla s1, wit_worker_iterations
    lla s5, wit_worker_slices
    lla s6, wit_worker_done
    lla s7, wit_worker_errors
    li s2, 0x55AA001100220033
    add s2, s2, s0
    li s3, 0xAA55003300220011
    add s3, s3, s0
    fmv.d.x f8, s2
    fmv.d.x f16, s3
    li s4, 3
    sub s4, s4, s0 // round up (3) for worker 0, round down (2) for worker 1
    fsrm s4
    slli s8, s0, 3 // the worker's slot in each table
worker_loop:
    li t0, 0x55AA001100220033
    add t0, t0, s0
    bne s2, t0, worker_corrupt
    li t0, 0xAA55003300220011
    add t0, t0, s0
    bne s3, t0, worker_corrupt
    fmv.x.d t0, f8
    bne t0, s2, worker_corrupt
    fmv.x.d t0, f16
    bne t0, s3, worker_corrupt
    frrm t0
    bne t0, s4, worker_corrupt
    add t1, s1, s8
    ld t0, 0(t1)
    addi t0, t0, 1
    sd t0, 0(t1)
    add t1, s5, s8
    ld t0, 0(t1)
    li t2, 3
    bltu t0, t2, worker_loop
    j worker_finished
worker_corrupt:
    li t0, 1
    add t1, s7, s8
    sd t0, 0(t1)
worker_finished:
    li t0, 1
    add t1, s6, s8
    sd t0, 0(t1)
worker_wait:
    wfi
    j worker_wait
