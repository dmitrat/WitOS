# M1 — Physical memory and exception foundation

Status: first M1 slice implemented and locally verified on 2026-09-16.
Version: WitOS 0.0.2. The complete M1 milestone is **not** finished.

## New behavior

The UEFI adapter exits boot services and enters an x64 assembly trampoline. The trampoline switches to a kernel-owned 64 KiB stack before calling the common C kernel.

The kernel validates the handoff and memory map, installs its own GDT, TSS and 256-entry IDT, initializes a physical-page allocator and runs memory checks before completing the normal boot demonstration.

Maskable interrupts remain disabled. The system still uses firmware-created page tables and runs one CPU.

## Architecture boundaries

- `src/Boot.Uefi/`: firmware map translation and handoff.
- `src/Kernel.Arch.X64/entry.asm`: stack switch, descriptor loading, exception entry and test fault triggers.
- `src/Kernel.Arch.X64/exceptions.c`: x64 descriptor construction and fatal CPU diagnostics.
- `src/Kernel/memory.c`: architecture-independent physical-page bookkeeping.
- `src/Kernel/memory_tests.c`: in-guest checks of real memory and synthetic bookkeeping fixtures.

MSVC's x64 compiler and MASM are host tools only. The resulting EFI executable has no Windows or CRT imports.

## Stack and table ownership

The 64 KiB main stack, 32 KiB double-fault stack, GDT, IDT, TSS, allocator metadata and boot metadata all reside in the loaded native image. The UEFI adapter reserves all non-conventional memory, including the image and firmware-owned allocations.

The stack trampoline preserves the boot argument, aligns RSP to 16 bytes and allocates the Microsoft x64 ABI's 32-byte caller shadow space before entering C.

The GDT supplies ring-0 code and data selectors plus a 64-bit TSS. IDT vector 8 uses TSS IST1 to deliver a double fault on the separate emergency stack.

Changing stacks does **not** authorize reclaiming the old firmware memory: active page tables and other inherited state still depend on it.

There are no stack guard pages yet. The emergency-stack test proves diagnostic delivery for a deliberately invalid stack, not general detection of arbitrary stack overflow.

## Exception behavior

All exception handlers are currently fatal. They do not attempt instruction recovery, unwind managed code, return with IRETQ or implement context switching.

Assembly stubs normalize vectors with and without a CPU-supplied error code. The diagnostic handler reports:

```text
vector, error, RIP, CS, RFLAGS, interrupted RSP, SS, CR2, handler stack class
```

The handler ends through the existing panic/QEMU failure path. The independent host test checks the expected exception number, error code, segment selectors and stack information rather than accepting any failure as success.

The temporary page-fault probe uses a canonical address in a PML4 slot verified to be absent before triggering the access. This is a test against the inherited mappings, not a kernel virtual-memory manager.

Double-fault injection sets RSP to 1 and triggers a page fault. Failure to deliver the page-fault frame causes an actual hardware double fault. The host requires vector 8, error 0, interrupted RSP 1 and `stack=emergency`.

NMI handling, machine-check recovery, resumable faults and SMP are outside this slice's guarantees.

## Physical-page allocator

The allocator tracks 4 KiB physical pages using separate eligibility and allocation bitmaps. Free pages contain no linked-list metadata.

Only normalized usable regions become eligible; physical page zero is always excluded. Reserved holes, image data, stacks and firmware pages never become eligible. Initialization rejects malformed, overflowing or overlapping descriptors, regardless of their order.

The initial bookkeeping limit is **physical addresses below 4 GiB**. A usable region beyond this limit fails initialization explicitly. Reserved/MMIO ranges may extend above it. This is a temporary implementation bound, not a hardware-independence claim.

Operations:

- Allocate one page: returns a physical address and decreases the free count.
- Free one page: succeeds only for an aligned, eligible, currently allocated page.
- Reject duplicate, reserved, zero, unaligned and out-of-range frees.
- Report exhaustion without changing allocator state.
- Reuse a freed page.

Initialization invalidates the previous allocator state even if the new map is rejected. The API is privileged, single-CPU bookkeeping with interrupts disabled; it is not synchronized for concurrent callers and is not a security capability API.

## Validation

Run:

```powershell
dotnet build WitOS.slnx --configuration Release
dotnet run --project tools/WitOS.Dev --configuration Release -- test
```

The suite boots eleven separate VM scenarios:

| Scenario | Required outcome |
| --- | --- |
| 128 MiB boot | Kernel-owned stack, exception tables, memory checks, normal exit |
| 512 MiB boot | Same checks with a different usable-memory size |
| Bad boot version | Contract panic before normal initialization |
| Overlapping boot map | Map rejection before physical allocation |
| Breakpoint | Vector 3, error 0 |
| Divide error | Vector 0, error 0 |
| Invalid opcode | Vector 6, error 0 |
| Invalid data selector | Vector 13, error 0xFFF8 |
| Unmapped read | Vector 14, error 0, expected CR2 |
| Invalid stack plus page fault | Vector 8 on the emergency stack |
| Deliberate hang | Full initialization followed by host timeout |

Normal boots allocate two real physical pages, write and verify every word while both are live, reject invalid frees and restore the initial free count. A small synthetic pool tests exhaustion, reserved holes and reuse without exhausting the whole VM. Additional malformed-map fixtures cover overlap, zero length, alignment, overflow and the explicit address limit.

All eleven scenarios passed locally. GitHub Actions runs the same suite on its independent Windows host.

## Remaining M1 work

1. Kernel-owned page tables and explicit mappings/protection, including guard pages.
2. Fault diagnostics under the kernel-owned mappings.
3. Timer interrupt setup and controlled interrupt enabling.
4. Two execution contexts with tested switching.
5. Clear ownership rules before reclaiming firmware memory.

The .NET runtime dependency inventory remains required before stabilizing the user/kernel ABI.

## References

- [Intel architecture manuals: system programming and exception delivery](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html)
- [Microsoft x64 calling convention](https://learn.microsoft.com/en-us/cpp/build/x64-calling-convention)
- [MASM for x64](https://learn.microsoft.com/en-us/cpp/assembler/masm/masm-for-x64-ml64-exe)
