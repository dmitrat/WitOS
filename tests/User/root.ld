/* The root task fixture as a static ELF executable at the image window of a component (witos/user_layout.h,
 * WIT_USER_IMAGE_BASE; plan step T1): code, read-only data and writable data each start a page, so that the
 * ELF-to-flat conversion (tools/WitOS.Dev, FlatImage) maps them as three segments with their own protection. The
 * image has no interpreter, no dynamic section and no relocations: the kernel's flat format is a boot format. */
ENTRY(wit_user_start)
SECTIONS
{
    . = 0x0000008000100000;
    .text : ALIGN(4096) { *(.text .text.*) }
    .rodata : ALIGN(4096) { *(.rodata .rodata.*) }
    . = ALIGN(4096);
    .data : { *(.data .data.*) }
    .bss : { *(.bss .bss.* COMMON) }
    /DISCARD/ : { *(.comment .note.* .eh_frame .eh_frame_hdr .gcc_except_table .interp .dynamic .dynsym .dynstr .hash .gnu.hash) }
}
