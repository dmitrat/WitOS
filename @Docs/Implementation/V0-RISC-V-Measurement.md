# V0 — RISC-V: the measurement before the port

Plan step V0 measures what the third ISA costs before a line of its kernel is written: the toolchain, the board, the
common kernel's assumptions, the system layer's and the runtime's RISC-V support, and the tool and CI sites that assume
two ISAs. Everything below was measured on 2026-10-16 with the pinned tools (clang 20.1.8, QEMU 11.1.0, musl 1.2.5,
dotnet/runtime `release/10.0` at 10.0.8 and 10.0.12) unless a line says it is not yet measured. The step's result is the
decisions at the end, which V1 inherits.

## 1. The board

The stand is QEMU's `virt` machine for riscv64 with EDK II as the UEFI firmware, as `virt` for ARM64 is.

**It boots as pinned.** The Windows package of QEMU 11.1.0 carries `qemu-system-riscv64.exe` and EDK II's RISC-V
images `share/edk2-riscv-code.fd` and `share/edk2-riscv-vars.fd`; the Linux host's build of the same release limits
`--target-list` to x86_64 and aarch64 and must add `riscv64-softmmu` (a change of `Toolchain.QEMU_CONFIGURE`, so a
`setup` rebuilds). The command

```
qemu-system-riscv64 -M virt -m 512 -smp 1 -bios default
    -drive if=pflash,unit=0,format=raw,file=edk2-riscv-code.fd,readonly=on
    -drive if=pflash,unit=1,format=raw,file=<copy of edk2-riscv-vars.fd>
    -serial file:serial.txt -display none -icount shift=2,sleep=off
```

reaches the UEFI Interactive Shell v2.2 (EDK II, UEFI 2.70) over OpenSBI v1.8.1 (`riscv-virtio,qemu`, one hart, PMP),
with the firmware's console on the board's UART. `-bios default` is QEMU's OpenSBI, which runs in M-mode and starts the
pflash firmware in S-mode; the kernel will run in S-mode over SBI, where the ARM64 kernel runs at EL1 over PSCI.
`-icount` works, so guest gates stay in virtual time.

**The device tree** (`-M virt,dumpdtb=`, with a virtio-blk, a virtio-serial and a virtio-keyboard on the PCI bus)
describes:

| Node | Compatible | Address | Use |
| --- | --- | --- | --- |
| `cpus` | `riscv`, ISA `rv64imafdch_zicsr_zifencei_zicbom_zicbop_zicboz_…_sstc_…_zba_zbb_zbc_zbs`, `riscv,sv57` | | the ISA string names the extensions the default `rv64` CPU has; `sstc` gives a supervisor timer compare register, `zicbom`/`zicboz` cache-block management, `sv57` means Sv39 and Sv48 are there too |
| `interrupt-controller@c000000` | `sifive,plic-1.0.0`, `riscv,plic0` | 0xC000000 | the interrupt controller: claim and complete per context, as `wit_platform_interrupt_claim` already shapes it for the GIC |
| `clint@2000000` | `sifive,clint0`, `riscv,clint0` | 0x2000000 | timer and software interrupts; the supervisor uses SBI TIME or `sstc`, and the `time` CSR for the monotonic count |
| `serial@10000000` | `ns16550a` | 0x10000000 | the console UART, as the PL011 is on virt ARM64 |
| `pci@30000000` | `pci-host-ecam-generic` | 0x30000000 | the PCI host: virtio-pci devices as on both other boards |
| `test@100000` | `sifive,test1`, `sifive,test0` | 0x100000 | the test finisher: a write ends QEMU with a code, the board's `isa-debug-exit` |
| `rtc@101000` | `google,goldfish-rtc` | 0x101000 | a real-time clock for `wit_platform_realtime_seconds` |
| `flash@20000000` | | 0x20000000 | the pflash the firmware runs from |
| `fw-cfg@10100000` | `qemu,fw-cfg-mmio` | 0x10100000 | not needed |
| `virtio_mmio@1000{1..8}000` | `virtio,mmio` | | eight MMIO slots the kernel ignores; the drivers use PCI |
| `poweroff`, `reboot` | `syscon-poweroff`, `syscon-reboot` | | over the test device |
| `platform-bus@4000000` | `qemu,platform`, `simple-bus` | | empty |

So the platform layer `Kernel.Platform.QemuVirtRv` has the same shape as `Kernel.Platform.QemuVirt`: a device tree to
enumerate, an ECAM PCI bus, one interrupt controller, a UART, a clock and an exit device; the differences are the
controller's programming model (PLIC against GICv3), the timer (SBI or `sstc` against the generic timer) and the
processor start (SBI HSM against PSCI).

## 2. The toolchain

**clang compiles riscv64; lld-link does not link it.** The pinned clang 20.1.8 compiles `riscv64-unknown-elf` and
`riscv64-unknown-linux-musl` (`elf64-littleriscv`, `EM_RISCV`), and `ld.lld -m elf64lriscv` links them. Asked for
`--target=riscv64-unknown-windows`, clang emits an ELF object all the same: LLVM has no COFF backend for RISC-V, and
`lld-link /machine:riscv64` answers `unknown /machine argument: riscv64`. The kernel rule of T3 — the kernel and its EFI
loader are PE/COFF with the Microsoft conventions, linked by `lld-link` — therefore cannot be followed literally for
the third ISA.

**What UEFI needs on RISC-V.** The UEFI specification defines the RISC-V calling convention as the standard psABI (the
one ELF uses), and the image format as PE32+ with machine type 0x5064. EDK II itself builds its RISC-V firmware as ELF
with GCC or clang and converts each module to PE32+ with its `GenFw` tool. So the loader's code needs no special
convention, only its container does. Our loader (`src/Boot.Uefi`, 579 lines) has no `EFIAPI` macro and no
architecture conditional: it compiles for `riscv64-unknown-elf` as it is (§3), and its one architecture check is the
kernel image's PE machine against `wit_arch_identity()->PeMachine`.

**The two ways, and the default.**

1. *ELF, converted.* The loader and the kernel are linked by `ld.lld` as position-independent ELF with relative
   relocations alone (`-pie`, which `StartedProgram.Validate` already requires of layer-2 programs) and converted by
   the tool into PE32+ with a base relocation table, as `GenFw` does: sections from the segments, the entry from
   `e_entry`, `IMAGE_REL_BASED_DIR64` from `R_RISCV_RELATIVE`. The loader stays a PE loader (its `image.c` reads the
   kernel's PE sections and checks the machine), `KernelArchitecture.KernelTriple` becomes the ELF triple for this
   ISA, and the T3 rule is restated: PE/COFF is the firmware's image format, not a property of the compiler. The
   converter is a host tool of the order of `FlatImage.FromElfAsync` (an ELF reader the tool already has) plus a PE
   writer; CodeView debug records do not survive, so the kernel's symbols for the runner's hang samples come from the
   ELF beside the image.
2. *ELF, loaded.* The loader learns to load an ELF kernel and the kernel image stays ELF; the loader itself still needs
   conversion to PE32+ to be started by the firmware, so the converter is needed in both ways.

The default is way 1, since the converter is needed anyway and the loader's and the runner's PE handling stays one for
three ISAs; V1 measures the converter's size before V1.1 and records it.

## 3. The common kernel

**It compiles for riscv64 as it is.** Every C source of the layers `kernel-boot`, `kernel-foundation`, `kernel-common`
and `boot-uefi` (37 files) passes `clang --target=riscv64-unknown-elf -march=rv64gc -mabi=lp64d -fsyntax-only` with the
kernel's options (`-ffreestanding -nostdinc -fno-stack-protector -Wall -Wextra -Werror`) and the ARM64 architecture's
include directory, whose `witos/arch_types.h` is 18 lines. The only architecture conditional in the shared headers is
the register block of `thread_context.h` (`_M_ARM64 || __aarch64__`), which RFC 0011 §7 names as the one thing that
differs between ISAs. Nothing in `src/Kernel` names x64 or ARM64 otherwise.

**The port's contract.** `witos/arch.h` declares 74 `wit_arch_*` functions and `witos/platform.h` 21 `wit_platform_*`
functions; a third ISA implements these and nothing else (`Kernel.Arch.RV64` and `Kernel.Platform.QemuVirtRv`), and a
change to `src/Kernel` during V1 is a finding to record, not a port.

**The reference cost.** The ARM64 port today is 3,097 lines of `Kernel.Arch.A64` and `Kernel.Platform.QemuVirt`
(paging 553, the virt devices 485, frames 394, `entry.S` 273, the virt interrupts 229, page tables 204, scheduler 172,
exceptions 151, `vectors.S` 147, secondary start 159, clock 70, bring-up 56, console 53, `compiler.S` 52), beside
196 lines of ARM64 self-tests (`tests/Kernel.A64`) and the expectations `arm64-foundation.json` and `arm64-users.json`.
The plan's ~2,300 of A2 grew with phases K and R (SMP groundwork, the user-mode cache maintenance, x18 as an ordinary
register). The RISC-V port is budgeted at the same order.

**What `Kernel.Arch.RV64` needs**, from the ISA and the board:

- *Address spaces.* Sv48 (four levels, 48-bit, as both ISAs have) is the default so that the common kernel's page-table
  record (K9: `OWNED` and `ALIAS` leaves, `wit_arch_page_next`) keeps its depth; Sv39 if a measurement shows the common
  kernel does not depend on the number of levels. `satp` for the root, `sfence.vma` for invalidation (by address and
  address space), `sstatus.SUM` off except where the kernel reads user pages through its own view (it copies through
  `wit_virtual_view`, so the flag may stay off). User pages are U-bit leaves; the kernel's are not.
- *Traps.* One vector `stvec` in direct mode with the cause in `scause` and the address in `stval`: `ecall` from U-mode
  is ABI-1's entry, with the call number and arguments in `a0`–`a7` (the SysV registers, as the x64 `SYSCALL` and the
  ARM64 `SVC` entries take them), the environment call exception code 8; page faults 12, 13 and 15; illegal
  instruction 2; the supervisor timer and external interrupts with the interrupt bit set. One frame layout serves calls
  and interrupts, as the x64 entry builds an interrupt gate's frame for `SYSCALL`.
- *Contexts.* A thread context carries `x1`–`x31`, `pc`, `f0`–`f31` and `fcsr`; `tp` (x4) is the thread pointer the
  psABI gives TLS (musl's `TLS_ABOVE_TP`), read and written in U-mode like TPIDR_EL0, so `THREAD_SET_TLS` is
  `UNSUPPORTED` as on ARM64 and the kernel preserves `tp` as frame state. `gp` (x3) is the global pointer, an
  ordinary register for layer 2 (no `-mrelax` global-pointer relaxation across the kernel boundary). The vector
  extension is not in the default CPU's ISA string and stays out of the context.
- *Code publication.* `fence.i` orders the local hart's instruction fetch after stores, and the ISA string offers
  `zicbom`; compiler-rt's `__clear_cache` for `riscv64-linux` issues the `riscv_flush_icache` system call (`a7` =
  `__NR_riscv_flush_icache`, 259 in musl's table), so `__wit_syscall` answers it with `CODE_PUBLISH` (or a `fence.i`
  where the range is the caller's own, phase P deciding how other harts are reached), and `wit_arch_publish_code_page`
  is `fence.i` on the one scheduling processor.
- *Time and processors.* The monotonic count is the `time` CSR (`rdtime`) at the device tree's `timebase-frequency`;
  the timer is `sstc`'s `stimecmp` when the ISA has it (it does), SBI TIME otherwise; inter-processor interrupts and
  the start of the other harts go through SBI IPI and HSM (`hart_start`), as PSCI `CPU_ON` does on virt;
  `wit_cpus_fence_all` becomes SBI IPI with a `fence` on arrival, and `fence.i` for code when phase P schedules on
  several harts.
- *Boot.* UEFI hands the loader the boot hart's id and the device tree through the configuration table
  (`EFI_DTB_TABLE_GUID`), as on virt ARM64; the boot contract already carries the firmware tables as physical
  addresses, so `WitBootInfo` gains the hart id or the kernel reads it from `mhartid`'s SBI mirror in the device tree's
  `boot-hartid`. The loader's `wit_arch_identity()->PeMachine` is 0x5064.
- *Assembly.* `entry.S` (trap vector, the user return, `compiler.S`'s `memcpy`/`memset`; `__chkstk` is not needed,
  since the psABI has no stack probing) in GNU syntax through clang's integrated assembler, as the other two.

## 4. The system layer and the runtime

**musl** 1.2.5 has `arch/riscv64` (`atomic_arch.h`, `crt_arch.h`, `pthread_arch.h`, `reloc.h`, `syscall_arch.h`,
`bits/`) and the thread and ldso assembly (`clone.s`, `__set_thread_area.s`, `__unmapself.s`, `syscall_cp.s`,
`dlsym.s`, `tlsdesc.s`); the libc build's rules leave out the same files it leaves out on the other ISAs (`clone.c`,
`__unmapself.c`, the syscall assembly) in favour of `__wit_syscall`, whose number table is the asm-generic one riscv64
shares with aarch64 (`fstat` 80, `newfstatat` 79, `futex` 98, `clone` 220, `membarrier` 283, `statx` 291) plus
`riscv_flush_icache` (259). compiler-rt's builtins and LLVM's runtimes have riscv64; the triple is
`riscv64-unknown-linux-musl` with `-march=rv64gc -mabi=lp64d`, `ELF e_machine` 243; libc-test's math suite will need
its own `mathExcluded`, measured as on the other two.

**dotnet/runtime `release/10.0`** knows riscv64 as a community architecture: `eng/build.sh` accepts `-arch riscv64`;
`eng/native/tryrun.cmake` has riscv64 in its Linux arch list (so the witos answers of R1 extend as they are, measured
again in the guest); CoreCLR has `pal/src/arch/riscv64`, `vm/riscv64` and the JIT's riscv64 code generator; NativeAOT
has `nativeaot/Runtime/riscv64` and `_NativeAotSupportedArch` includes riscv64, so ILC targets it and the services of
ADR 0025 need no other form; `System.Native`'s `pal_runtimeinformation.c` reports the architecture. Not yet measured:
whether `build.sh -os witos -arch riscv64` configures and builds CoreCLR, the native libraries and NativeAOT against
the riscv64 sysroot (the Linux x64 host cross-compiles, as it does for arm64), whether the community JIT passes the M3
acceptance and the PAL suite, and the RID graph's `linux-riscv64` entries, which the patched graph of R2.3b extends
with `witos-riscv64`. This is V2's first measurement, made in the container on the tree an earlier `runtime-witos`
built; it decides whether the services run on RISC-V at V2 or wait.

## 5. The tool and CI

A third ISA touches: `KernelArchitecture` (a third record: `riscv64`, the QEMU machine `virt` with `-bios default`
and the pflash drives, the exit through `sifive,test`, the firmware images, the triple and the kernel triple),
`build/kernel-riscv64.json` and the layer manifests `kernel-arch-rv64.json`, `kernel-platform-qemu-virt-rv.json`,
`kernel-selftest-rv64.json`; `tests/Kernel.RV64` with `tests/Expectations/riscv64-*.json`; the 16 tool files and 4
test files that name `x64` or `arm64`; `Toolchain.QEMU_CONFIGURE` (the Linux build's target list); the sysroot and
substrate builds by architecture (`artifacts/sysroot/riscv64`, compiler-rt's riscv64 builtins in the pin);
`KernelImageBuilder` (the PE32+ conversion for this ISA); the CI jobs `boot-riscv64`, `linux-selftest` and
`linux-host`'s matrix; the hardware matrix of `build/compatibility-matrix.json`.

## 6. Decisions for V1

1. The RISC-V kernel and loader are ELF (`riscv64-unknown-elf`, PIE, relative relocations alone) converted by the tool
   to PE32+ (machine 0x5064) with a base relocation table; the T3 rule becomes "the firmware's image is PE/COFF", and
   `KernelTriple` names the compiler's target, ELF for this ISA. V1 measures and records the converter's size first.
2. Sv48 by default; Sv39 only if the common kernel is shown to be independent of the table depth.
3. `ecall` with `a0`–`a7`; the context `x1`–`x31`, `pc`, `f0`–`f31`, `fcsr`; `tp` as TLS preserved like TPIDR_EL0;
   `THREAD_SET_TLS` `UNSUPPORTED`.
4. The timer by `sstc`, the monotonic count by `rdtime`, the other harts by SBI HSM and IPI, UTC by the goldfish RTC,
   the exit by `sifive,test`, the console by the 16550 at 0x10000000 until on-demand mapping.
5. `riscv_flush_icache` in `__wit_syscall` is `CODE_PUBLISH`.
6. The port's budget is the order of ARM64's 3,097 lines of arch and platform; a change in `src/Kernel` is a finding.
7. V2 opens with the CoreCLR and NativeAOT build for `witos-riscv64` in the container, which decides the services'
   fate on this ISA.
