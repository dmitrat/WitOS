/* A mechanism fixture (plan step K8.3) in the component's code window: wit_user_create maps it at WIT_USER_CODE
 * (witos/user_layout.h) as executable pages up to the startup block, and the first thread starts at its first byte.
 * Code and constants form one segment with the entry first; a fixture has no writable data, since its state lives in
 * the data page at WIT_USER_DATA, and the build refuses an image with a second segment. */
ENTRY(wit_user_start)
SECTIONS
{
    . = 0x0000008000001000;
    .text : { *(.text.entry) *(.text .text.*) *(.rodata .rodata.*) }
    /DISCARD/ : { *(.comment .note.* .eh_frame .eh_frame_hdr .gcc_except_table .interp .dynamic .dynsym .dynstr .hash .gnu.hash) }
}
