// Arm semihosting on AArch64: operation in w0, parameter block in x1, result in x0.

    .text

    .globl wit_virt_semihosting

wit_virt_semihosting:
    hlt #0xf000
    ret
