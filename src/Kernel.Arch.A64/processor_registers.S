// Identification registers of the running processor (plan step K7.1), read for the processor table.
    .text
    .globl wit_a64_processor_id
    .globl wit_a64_isa_features
    .globl wit_a64_processor_features

wit_a64_processor_id:
    mrs x0, mpidr_el1
    ret

wit_a64_isa_features:
    mrs x0, id_aa64isar0_el1
    ret

wit_a64_processor_features:
    mrs x0, id_aa64pfr0_el1
    ret

// The processor's number in the kernel's table (P1.1): TPIDR_EL1, which EL0 cannot reach, written once at its start.
    .globl wit_arch_processor_set_index
    .globl wit_arch_processor_index
wit_arch_processor_set_index:
    mov w0, w0
    msr tpidr_el1, x0
    ret

wit_arch_processor_index:
    mrs x0, tpidr_el1
    ret
