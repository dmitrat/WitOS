// The kernel's compiler helpers, the RISC-V psABI (plan step V1.1). clang calls memcpy and memset for structure
// copies and initialization. They are assembly so that no C body can be lowered back into a call to itself; the psABI
// has no stack probing, so there is no __chkstk.

    .text

    .globl memcpy
    .globl memset

// a0 = destination, a1 = source, a2 = count; returns the destination. The ranges must not overlap.
memcpy:
    mv a3, a0
    beqz a2, copy_done
copy_byte:
    lbu a4, 0(a1)
    sb a4, 0(a3)
    addi a1, a1, 1
    addi a3, a3, 1
    addi a2, a2, -1
    bnez a2, copy_byte
copy_done:
    ret

// a0 = destination, a1 = byte value, a2 = count; returns the destination.
memset:
    mv a3, a0
    beqz a2, set_done
set_byte:
    sb a1, 0(a3)
    addi a3, a3, 1
    addi a2, a2, -1
    bnez a2, set_byte
set_done:
    ret
