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
