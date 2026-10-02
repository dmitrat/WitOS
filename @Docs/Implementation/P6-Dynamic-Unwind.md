# P6.2 dynamic function tables and checked unwind

Status: P6.2 capability acceptance completed; final common regression gates passed. This is a P6.2 capability checkpoint, not guest CoreCLR/JIT execution.

`function_tables.witos.*` keeps component-private static and callback registrations with non-repeating generations and independently tracked reader tokens. A duplicated/stale lease cannot decrement another reader. BeginRemove blocks new readers; FinishRemove requires prior leases to leave. Callbacks execute without the registry gate. A current lease can continue lookup while its registration retires. The runtime must additionally quiesce executing code before freeing it.

Guest bindings implement RtlAddFunctionTable, RtlInstallFunctionTableCallback, RtlDeleteFunctionTable and RtlLookupFunctionEntry through real direct and readonly IAT x64 bindings. Dynamic lookup does not use an unwind history cache. Callback/DAC path input is validated as diagnostic metadata; Windows out-of-process debugger support is not claimed. Kernel thread/compiler-TLS prerequisites precede per-thread lookup state access. Mapper release refuses a range while its function table remains registered.

The checked upstream AMD64 unwinder now accepts a separate dynamic read/lookup source. A function entry may live outside its code heap, as CoreCLR RealCodeHeader permits. Selected entry and chain metadata are validated on every call; canonical entry identity, instruction/stack bounds, access budget and transactional output remain checked. The static kernel-published immutable-image cache is unchanged and is never reused for mutable JIT metadata.

Each dynamic guest unwind holds both a registry reader lease and a kernel-confirmed stack lease. Tests execute an x64 function copied into actual RX memory; its live frame calls a native inspector. Static-table and callback-table paths restore the real caller RIP/RSP/RBX. A worker repeats this with its live dynamic frame suspended; the parent holds a foreign stack lease, unwinds it, proves resume is rejected until the lease closes, then resumes and joins the worker. Forged entries and malformed metadata terminate only the component and reclaim its resources. The guest fixture still has no OS imports.

Evidence so far:

- artifacts/p6-dynamic-source-reference2.log: original 20 Windows/checked comparisons, 40 cached comparisons and 13 transactional failures remain passing; added 20 dynamic-source comparisons and 40 entry/metadata rejection checks pass.
- artifacts/p6-functions-rundown.log: static/callback lookup compared with actual Windows APIs; callback rundown, duplicate/stale reader release and 128-reader bound pass.
- artifacts/p6-bindings-guest.log: actual direct/IAT guest paths and registered-code release rejection pass at128/512MiB.
- artifacts/p6-foreign-guest.log: own/foreign live dynamic frame walks and failure containment pass at128/512MiB.

The code is linked into a native test component using the real pinned unwinder and real WitOS transport/TLS. It has not yet been integrated into the full source-built CoreCLR runtime image; ordinary IL execution under that runtime remains P6.5. No successful placeholder runtime/OS methods or replacement CoreLib were added.

Upstream contracts consulted: [RtlAddFunctionTable](https://learn.microsoft.com/en-us/windows/win32/api/winnt/nf-winnt-rtladdfunctiontable), [RtlInstallFunctionTableCallback](https://learn.microsoft.com/en-us/windows/win32/api/winnt/nf-winnt-rtlinstallfunctiontablecallback), pinned `vm/rtlfunctions.cpp`, `vm/codeman.cpp` and `vm/codeman.h`.

## Dynamic exception-dispatch checkpoint

The P6.2 audit found that VirtualUnwind alone was insufficient: the native exception dispatcher still restricted frame lookup to the static image. Under the explicit WITOS_DYNAMIC_CODE profile it now resolves registered dynamic ranges and uses the selected frame's actual ImageBase for handler search and target unwind. The NativeAOT build without that define retains its static-image policy.

A real page fault in a copied x64 dynamic function now travels through kernel exception upcall, the native dispatcher, dynamic RUNTIME_FUNCTION/EH metadata and a registered handler. The handler changes the real fault context; the kernel continuation resumes code and produces the expected result. The kernel verifies one actual hardware null read and one continuation. Both128/512MiB profiles passed: artifacts/p6-dispatch-guest4.log.

Registration and mapper release now share a code-lifetime gate; a new registration cannot race between the release check and unmapping. Static registration validates actual executable ranges, and registration of freed code is refused. Registry callbacks remain outside the registry gate.

Target-unwind bindings are now implemented and tested as described below; CoreCLR-style collided-dispatch checks also pass; final shared regressions have passed. Dynamic native C-specific scope tables remain distinct from CoreCLR's managed EH handler contract; no general JIT managed exception support is claimed from this fixture.

## Target-unwind checkpoint

The x64 RtlUnwind/RtlUnwindEx bindings capture the actual caller registers, flags and x87/SSE context and enter the checked user-space dispatcher. They support a non-null target frame, optional exception record, return value and the RtlUnwindEx output context; direct and readonly IAT bindings are provided. Null-target exit-unwind remains unsupported and fails explicitly. History tables are not cached. This is a bounded x64/UP native capability, not managed CoreCLR EH acceptance.

The guest checks continuation after a dynamic page fault, both target-unwind entry points, standalone calls outside hardware exception dispatch, target flags, return RAX, restored RBX and removal of pending kernel exception scopes. The nested two-frame test exposed a real omission: the initial bridge restarted at the outer search handler and skipped inner cleanup (trace 124). Restarting from the original fault context produces the required trace 1234. The failed baseline is retained in artifacts/p6-target-nested-baseline2.log; the fixed nested test and standalone entry tests passed at 128/512 MiB in artifacts/p6-target-nested-fixed.log and artifacts/p6-target-direct.log.

## CoreCLR collided dispatcher context

Pinned CoreCLR vm/exceptionhandling.cpp uses FixupDispatcherContext from HijackHandler and FixRedirectContextHandler to return ExceptionCollidedUnwind with a supplied context and personality routine. This differs from the internal nested-handler bridge. The initial guest regression failed at trace 1 (artifacts/p6-collided-baseline.log).

The WITOS_DYNAMIC_CODE dispatcher now supports this restart in search and unwind phases. It validates and copies the complete context, checks stack ownership/bounds and forward progress, validates executable control/handler addresses, and revalidates the canonical function entry, image base and establisher frame under the reopened stack lease. HandlerData and ScopeIndex are retained. The supplied personality is used even when it differs from the metadata handler. A 128-iteration bound prevents unbounded collision loops. The static NativeAOT profile keeps its existing policy.

The guest test requires trace 1234, the alternate handler and ScopeIndex 7, restored RBX and result 742. Five negative cases reject null/truncated contexts, a forged entry, a non-executable handler and a mismatched establisher frame; each verifies component containment and exact page reclamation. The runner requires all markers and exactly nine post-isolation contained faults (four W^X, five malformed contexts).

The actual Windows RtlUnwindEx implementation independently passes the same two-frame ordinary/collided contract using the shared x64 fixture. Its alternate personality and data/cursor assertions establish that those fields cannot be discarded. Evidence: artifacts/p6-collided-windows.log and artifacts/p6-collided-final-coreclr-memory.log (both 128/512 MiB). This does not replace later tests with real JIT-generated prologs/epilogs and managed personality routines in P6.5.

CI now runs coreclr-functions and coreclr-memory and selects changes under src/Runtime.CoreClr. Final Release/host/kernel/runtime gates passed; artifacts/p6-stage2-evidence.json records the logs and source hashes. One host-suite repeat failed native process-signal confirmation on a simple timeout child (33/34); the unchanged isolated repeat passed (34/34). The failed log is retained and the intermittent host issue remains unresolved. No new publication has been made.
