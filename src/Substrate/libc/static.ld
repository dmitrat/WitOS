/* A static libc program as the root task (plan step S1.1): a static ELF executable at the image window of a
 * component (witos/user_layout.h, WIT_USER_IMAGE_BASE). The ELF header and the program headers are mapped with the
 * read-only data, since musl's static TLS setup reads PT_TLS through AT_PHDR; that read-only segment, the code and
 * the writable data (with the TLS template and the initializer arrays) each start a page, so that the ELF-to-flat
 * conversion maps them as three segments with their own protection. No interpreter, no dynamic section, no
 * relocations. */
ENTRY(_start)
SECTIONS
{
    . = 0x0000008000100000 + SIZEOF_HEADERS;
    .rodata : { *(.rodata .rodata.*) }
    .text : ALIGN(4096) { *(.text .text.*) }
    . = ALIGN(4096);
    .tdata : { *(.tdata .tdata.*) }
    .tbss : { *(.tbss .tbss.*) }
    .init_array : { *(.init_array .init_array.*) }
    .fini_array : { *(.fini_array .fini_array.*) }
    .data : { *(.data .data.*) }
    .bss : { *(.bss .bss.* COMMON) }
    /DISCARD/ : { *(.comment .note.* .eh_frame .eh_frame_hdr .gcc_except_table .interp .dynamic .dynsym .dynstr .hash .gnu.hash) }
}
