// The kernel's compiler helpers, Microsoft x64 convention (plan step T3.1). clang calls memcpy and memset for
// structure copies and initialization and __chkstk for frames larger than a page. They are assembly so that no C body
// can be lowered back into a call to itself.
    .intel_syntax noprefix

    .text
// RAX is the allocation size. Preserves RAX, RSP and every argument, nonvolatile and SIMD register; only R10, R11
// and the flags are scratch. Touches every page of the allocation below the caller's frame, one 4 KiB step at a
// time and then the final partial span, so a guard page faults before the caller moves RSP past it. Kernel stacks
// are fixed mappings: nothing grows.
    .globl __chkstk
__chkstk:
    lea r10, [rsp + 8]
    mov r11, rax
    test r11, r11
    jz .Lprobe_done
.Lprobe_page:
    cmp r11, 0x1000
    jb .Lprobe_tail
    sub r10, 0x1000
    test byte ptr [r10], 0
    sub r11, 0x1000
    jnz .Lprobe_page
    ret
.Lprobe_tail:
    sub r10, r11
    test byte ptr [r10], 0
.Lprobe_done:
    ret

// rcx = destination, rdx = source, r8 = count; returns the destination. The ranges must not overlap. RDI and RSI
// are nonvolatile in this convention and are restored.
    .globl memcpy
memcpy:
    mov rax, rcx
    mov r9, rdi
    mov r10, rsi
    mov rdi, rcx
    mov rsi, rdx
    mov rcx, r8
    rep movsb
    mov rdi, r9
    mov rsi, r10
    ret

// rcx = destination, edx = byte value, r8 = count; returns the destination.
    .globl memset
memset:
    mov r9, rdi
    mov r10, rcx
    mov rdi, rcx
    mov eax, edx
    mov rcx, r8
    rep stosb
    mov rdi, r9
    mov rax, r10
    ret
