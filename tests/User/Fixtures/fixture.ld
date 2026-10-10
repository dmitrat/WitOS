/* A mechanism fixture (plan steps K8.3 and K8.4c) at the image window (WIT_USER_IMAGE_BASE, witos/user_layout.h),
 * converted to a flat image the kernel self-test validates and maps as it maps the root task's; the first thread
 * starts at its first byte. Code and constants form one segment with the entry first; a fixture has no writable data,
 * since its state lives in the data page at WIT_USER_DATA, and the build refuses an image with a second segment. */
ENTRY(wit_user_start)
SECTIONS
{
    . = 0x0000008000100000;
    .text : { *(.text.entry) *(.text .text.*) *(.rodata .rodata.*) }
    /DISCARD/ : { *(.comment .note.* .eh_frame .eh_frame_hdr .gcc_except_table .interp .dynamic .dynsym .dynstr .hash .gnu.hash) }
}
