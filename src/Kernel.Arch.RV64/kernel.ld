/* The RISC-V kernel image (plan step V1.1): one position-independent ELF, linked by ld.lld with relative relocations
 * alone, which the tool converts into the PE32+ the firmware loads (V0: the pinned LLVM links no RISC-V PE/COFF). The
 * first page of the image holds the ELF's own headers in the file and the PE headers in the converted image, so every
 * section starts at 0x1000; __ImageBase is the image's start, section-relative so that it relocates with the image
 * (the loader reads its own PE headers through it). Segments are page-aligned and never writable and executable. */
ENTRY(efi_main)

SECTIONS
{
    . = 0x1000;
    .text : {
        __ImageBase = . - 0x1000;
        *(.text .text.*)
    }
    . = ALIGN(0x1000);
    .rodata : {
        *(.rodata .rodata.*)
        *(.srodata .srodata.*)
    }
    .rela.dyn : { *(.rela.dyn) }
    .dynsym : { *(.dynsym) }
    .dynstr : { *(.dynstr) }
    .gnu.hash : { *(.gnu.hash) }
    .hash : { *(.hash) }
    . = ALIGN(0x1000);
    .data : {
        *(.data .data.*)
        *(.sdata .sdata.*)
    }
    .dynamic : { *(.dynamic) }
    .got : {
        *(.got .got.plt)
    }
    .bss : {
        *(.bss .bss.*)
        *(.sbss .sbss.*)
        *(COMMON)
    }
    /DISCARD/ : {
        *(.comment)
        *(.note.*)
        *(.eh_frame)
        *(.eh_frame_hdr)
        *(.riscv.attributes)
    }
}
