# Upstream native runtime thread records

**Status:** WitOS 0.0.36; experimental ABI v17 unchanged.

This slice runs actual upstream Thread::SetGCSpecial, Thread::Construct, Thread::IsInitialized, Thread::IsGCSpecial, Thread::SetState and Thread::GetPalThreadIdForLogging in the guest. It initializes native runtime records. It does not attach them to ThreadStore, initialize the collector or execute managed code.

## OS-handle policy

The pinned .NET 10.0.8 Thread::Construct first initializes transition frames, obtains a PAL thread ID, attempts Windows DuplicateHandle for the current thread, then queries stack bounds. Upstream explicitly allows that duplication to fail and requires consumers to handle INVALID_HANDLE_VALUE.

WitOS currently supplies real generation-bearing kernel identity and stack bounds, but no duplicated thread/context capability. The source overlay retains m_hOSThread = INVALID_HANDLE_VALUE and removes the Windows pseudo-handle/duplication attempt from this method. If stack discovery fails, its fatal branch calls the existing native component fail-fast instead of importing Windows RaiseFailFastException. Other runtime fatal/exception paths remain unresolved where unsupported. It neither implements a successful DuplicateHandle stub nor stores a kernel identity in a handle field. Context capture, hijacking and GC suspension remain unsupported requirements.

RuntimeStartupSources requires one exact occurrence of each pinned source block, writes the complete thread.witos.cpp, and records the generated hash. The CMake overlay replaces precisely one original thread.cpp only in the WitOS archive. The Windows reference retains the original source. Full Thread::Detach, Destroy, transition/GC/exception methods remain present with their real dependencies.

The startup probe extracts the same adapted Construct body and unchanged state/GC-special/logging methods. SetGCSpecial is the real public entry to the private Construct method: it marks a GC-special record but does not publish a ThreadStore list entry. The probe does not call PalAttachThread, install a pretend RuntimeThreadShutdown or provide a fake collector.

The initial WitOS archive and configuration probe consistently define upstream NO_STRESS_LOG. This disables the runtime diagnostic stress log; it does not disable or replace GC, change managed semantics, or claim EventPipe support. The Windows reference keeps its original diagnostics. Both compile profiles are checked from actual compiler commands. The pinned DebugHeader.cpp unconditionally described StressLog types even with this switch disabled; the overlay wraps exactly that type block and g_stressLog publication in STRESS_LOG guards. It does not publish invented layouts or globals for the unavailable feature and does not claim DAC/debugger compatibility. DebugHeader.cpp is pinned as 1bcec8e4bcb190da174dff05b261c2b6c805815ef01180c5903abd2ccf130f05. The stressLog.h switch is pinned from canonical bytes (SHA-256 47d3bc193daf4be76b55d1d67e6de176da177dfde5bf5d6e3cb05fc61a5cba52).

The same hash-verified stressLog.h supplies an include overlay with two recorded corrections for the disabled profile: its existing no-op diagnostic macros remain available after gcenv.base.h, and STRESS_LOG_VA takes the same two arguments as the enabled macro. These changes disable diagnostic output only; they do not replace GC/runtime methods. The original checkout header is preserved.

The unchanged inline methods in thread.inl now have their own canonical-byte SHA-256 pin: 66b9f935d0e697d843bd48f96469c0789f79b48bb2b1d8b69f49e6147fe7f88e. It was calculated from upstream download bytes, not a CRLF-transformed checkout. Runtime/VMR revisions and the previous 58 pins are unchanged; thread.inl, stressLog.h and DebugHeader.cpp bring the audit to 61.

## Guest probe

RuntimeThreadRecord runs after the existing RuntimeInstance/empty ThreadStore workload. It uses the actual compiler-TLS RuntimeThreadLocals record and actual headers, without private-field access or locally duplicated layouts.

- A cold record must be uninitialized and not GC-special. Its location must match kernel-confirmed compiler TLS and the previously validated DAC offset.
- Writable raw FS self/ID hints are cleared while Construct executes. The resulting runtime ID and stack bounds must still match the kernel snapshot; native last-error must be preserved.
- The real record must become initialized and GC-special, remain in preemptive mode, expose correct low-inclusive/high-exclusive stack membership, and retain INVALID_HANDLE_VALUE for the absent OS handle.
- The entire real ee_alloc_context remains zero. No managed allocation context is simulated or allocated.
- Repeated calls and native thread yields preserve those fields.
- Four batches of three concurrent workers validate fresh compiler TLS, unique kernel generations across slot reuse and isolation from the main record. Worker failure fails the component immediately rather than stranding the rendezvous.
- The existing three RuntimeInstance workers plus these twelve workers require exactly 16 thread creations including main, 15 joins and 15 reaps. Component commitment/accounting must return to its baseline between batches. Final component destruction must recover all owned physical pages.

The native probe executes at both normal and relocated image bases, in 128 and 512 MiB VMs. Compiler TLS/native worker cleanup reclaims these unlisted, allocation-free records; the workload intentionally does not claim managed detach or Thread::Destroy execution.

## Validation

Local results:

- Release solution build passed with no warnings or errors.
- All 19 ordinary QEMU scenarios passed; successful boots require 178 user groups and 51 contained hardware faults.
- runtime-audit verified 61 canonical-byte pins; runtime-probe passed all eight hosted Windows groups.
- runtime-target passed its four hosted native-bootstrap groups and retained both strict package link failures.
- runtime-source verified the full 83-member runtime and 11-member minipal archives, exact adapter object bytes and both diagnostic compile profiles. Its unchanged Windows reference passed all four groups.
- runtime-config passed 194 user groups and 51 contained hardware faults in both 128/512 MiB boots, including RuntimeThreadRecord at both image bases. The import-free native fixture is 38,912 bytes on the local compiler.

The strict WitOS source link retains 89 unresolved symbols, including five GC environment and thirteen PAL requirements. The reduction from 93 reflects three removed Windows duplication dependencies and a StressLog-only PAL time dependency removed by the explicit diagnostics profile; it is not four newly implemented OS services. Full collector/ThreadStore dependencies remain real and incomplete.

## Next boundary

Connect PalAttachThread to the native exit notification while preserving the actual RuntimeThreadShutdown dependency. That shutdown path must reach the real ThreadStore and collector allocation-context cleanup. Select explicit startup/diagnostic/COM behavior and implement genuine thread/context capabilities before claiming GC rendezvous or managed thread support. Standard upstream CoreLib and unchanged portable IL compatibility remain the project direction.
