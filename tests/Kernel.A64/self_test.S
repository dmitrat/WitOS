// ARM64 kernel self-test code, AAPCS64: fault triggers that each raise one synchronous exception at EL1, and the
// kernel workers of the preemption test.

    .text

    .globl wit_a64_trigger_breakpoint
    .globl wit_a64_trigger_undefined
    .globl wit_a64_trigger_data_abort
    .globl wit_a64_worker

wit_a64_trigger_breakpoint:
    brk #0
    ret

wit_a64_trigger_undefined:
    udf #0
    ret

// x0 = address that the translation tables leave unmapped.
wit_a64_trigger_data_abort:
    ldr x0, [x0]
    ret

// x0 = worker index. The worker never yields or returns. Each worker keeps its own sentinels in x21, x22, v8 and
// v16 and its own FPCR rounding mode, which every preemption must preserve.
wit_a64_worker:
    mov x19, x0
    ldr x20, =wit_worker_iterations
    ldr x23, =wit_worker_slices
    ldr x24, =wit_worker_done
    ldr x25, =wit_worker_errors
    ldr x21, =0x55AA001100220033
    add x21, x21, x19
    ldr x22, =0xAA55003300220011
    add x22, x22, x19
    fmov d8, x21
    fmov d16, x22
    mov x26, #0x00400000
    lsl x26, x26, x19 // round towards plus infinity for worker 0, minus infinity for worker 1
    msr fpcr, x26
worker_loop:
    ldr x9, =0x55AA001100220033
    add x9, x9, x19
    cmp x21, x9
    b.ne worker_corrupt
    ldr x9, =0xAA55003300220011
    add x9, x9, x19
    cmp x22, x9
    b.ne worker_corrupt
    fmov x9, d8
    cmp x9, x21
    b.ne worker_corrupt
    fmov x9, d16
    cmp x9, x22
    b.ne worker_corrupt
    mrs x9, fpcr
    cmp x9, x26
    b.ne worker_corrupt
    ldr x9, [x20, x19, lsl #3]
    add x9, x9, #1
    str x9, [x20, x19, lsl #3]
    ldr x9, [x23, x19, lsl #3]
    cmp x9, #3
    b.lo worker_loop
    b worker_finished
worker_corrupt:
    mov x9, #1
    str x9, [x25, x19, lsl #3]
worker_finished:
    mov x9, #1
    str x9, [x24, x19, lsl #3]
worker_wait:
    wfi
    b worker_wait
