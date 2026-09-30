# P1.5 native CloseHandle and Sleep bindings

Direct and readonly import bindings for CloseHandle/Sleep delegate to the existing real PAL/kernel paths. Close preserves generation-bearing ownership checks and existing join/event lifetimes. It does not fabricate thread references or make pseudo handles into capabilities. Sleep(0) performs a real yield; finite sleeps use the existing monotonic deadlines and preserve native last-error/errno. No mandatory parking assumption was added.

The full source archive's PAL event object and the new binding objects are verified byte-for-byte and used in the guest fixture. The PAL event source retains /GS and uses /Od to keep this probe in the accepted plain-unwind profile. Tests check close/reuse/stale rejection, live event state after stale close, direct/import calls, elapsed finite sleep, worker ownership, before-compiler-TLS use and complete teardown.

Release build, 20 boot scenarios, source audit, hosted probe, target/source/readiness, formatting/compiler-GS references and four runtime-config boots passed. Each runtime boot passed 224 user groups and 54 expected contained faults. The minimal/broad strict boundaries are 51/57 symbols. Full source archive: 103 members. This is native service evidence, not guest managed execution.

P1.5 remains open. CoreLib's actual GetOSHandleForCurrentThread path needs a real duplicated thread reference. Its WaitHandle path requests alertable waits; silently forwarding to the existing non-alertable PAL would not satisfy that behavior. See P1-Thread-Capabilities-Next.md for the next implementation invariants.
