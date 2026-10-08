; Identification registers of the running processor (plan step K7.1), read for the processor table.
    AREA |.text|, CODE, READONLY
    EXPORT wit_a64_processor_id
    EXPORT wit_a64_isa_features
    EXPORT wit_a64_processor_features

wit_a64_processor_id PROC
    mrs x0, mpidr_el1
    ret
    ENDP

wit_a64_isa_features PROC
    mrs x0, id_aa64isar0_el1
    ret
    ENDP

wit_a64_processor_features PROC
    mrs x0, id_aa64pfr0_el1
    ret
    ENDP
    END
