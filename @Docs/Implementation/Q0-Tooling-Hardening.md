# Q0: tooling and automated acceptance hardening

Date: 2026-09-29. **Q0 complete.** Implementation, host regressions and the full final boot/runtime validation pass. No guest ABI or managed-runtime behavior was changed. The kernel runtime fixture adds an explicit image-base diagnostic.

## Process deadline and ownership

Processes.RunAsync now owns a Windows Job Object for each command. The job is attached in CreateProcessW through PROC_THREAD_ATTRIBUTE_JOB_LIST, before the initial thread can spawn descendants. Only the three intended standard handles are inherited. Children cannot request job breakaway. See the [Microsoft attribute contract](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute).

Asynchronous named pipes permit cancellation of pending reads. One budget covers launch/setup, root exit and both stream drains; expiry sets TimedOut even if the root already exited. Cleanup terminates the owned job and waits for zero active processes, with a separate five-second maximum grace. Cleanup failure raises an error, never a successful result. Partial stdout/stderr is retained on ordinary timeout. Normal completion also terminates remaining owned descendants; this API runs finite commands, not persistent services. Job kill-on-close also applies if the host process dies. It never walks or terminates unrelated process trees.

WindowsCommandLine provides argument quoting and uses CommandLineToArgvW for captured compiler commands. Include paths and defines retain spaces, escaped quotes and trailing backslashes. RuntimeGuestDriver extracts definitions/includes/warning settings as whole parsed arguments, then verifies the required runtime feature defines. The tests include a real MSVC compile using a quoted include directory and string-valued define.

## Current attempt versus last success

Both runtime-boot and runtime-boot-run enter RuntimeBootAttempt before discovery/build/source work. Guest commands acquire an exclusive output lock. Each attempt has a unique runId, command, timestamps and running/succeeded/failed status.

- current-run.json describes the current attempt. Running or failed never means guest acceptance.
- acceptance.json exists only for a fully validated successful current attempt.
- last-success.json retains the last successful record; it is explicitly historical when the current attempt fails.
- runs/<runId>/ contains status, captured runtime-input.json/runtime-image.pe, per-profile logs and the successful acceptance record.

Current acceptance is invalidated before build. Legacy global logs are copied when migrating a previous success; schema-v2 successes retain their original bytes and immutable per-run log paths. Publication uses temporary files and same-directory atomic replacement. Success status is written last. A killed process can leave a running record, but cannot leave a prior acceptance as success for that attempt. The next attempt can acquire the OS-released lock.

The lock serializes guest boot commands using this output directory. Other workspace builds should still be run sequentially: this is not a general repository-wide concurrent-build scheduler. History is not automatically pruned.

## Evidence for both image bases

The kernel prints the selected base before each load. RuntimeBootProtocol requires the two distinct supported bases in order, with exactly one complete load/startup/hijack/workload/wmain/state sequence inside each block. It checks actual redirection-or-hijack and service-refusal counters, exit 42, no fault RIP, and final rejection/teardown markers. Missing, duplicate, reordered and corrupted phases, wrong/duplicate bases, panic and timeout fail acceptance.

The full boot verifier still checks kernel/scheduler/user foundations, fault counts and process outcome. The new validator replaces only the formerly global managed-marker sequence. Acceptance counts use the versioned thirteen-worker fixture contract for each validated execution; they are not inferred from a lone marker anywhere in the log.

## Regressions and CI

Versioned tests live in tests/WitOS.Dev.Tests and are included in WitOS.slnx. Baseline: five groups failed for the known deadline, stale acceptance, second-workload, quoted-path and CI-trigger defects. Current host suite: 17 groups pass, including publication failures/history/exclusion, root and descendant cleanup, large concurrent streams, argv/environment and per-base protocol mutations. Native corpus: 527 cases.

Both workflows run the host suite. NativeAOT CI additionally includes Boot.Uefi, tooling tests and solution changes in both push/PR path filters, runs the hosted PE corpus after runtime-source, and uploads run-specific evidence. Remote workflows were not triggered or published by this task.

Final local logs are artifacts/q0-release-build.log, q0-host-tests.log, q0-kernel-test.log and q0-runtime-{audit,probe,target,source,config,boot}.log. Root PLAN.md now marks Q0.1-Q0.6 complete. artifacts/q0-validation.json records the verified gate-log hashes, schema-v2 runId and unchanged managed-image SHA-256. Release had zero warnings/errors; all 20 kernel scenarios, 4 native profiles (284 groups / 66 expected faults each), and 8 managed executions / 104 lifecycles passed. Current, immutable per-run and last-success records match; the previous success and its serial hashes remain intact. These tests do not claim full line/branch coverage, return-address fallback stress coverage, or completion of the remaining P3/P5/P6 work.


## P3 follow-up: forced termination is not pipe EOF

During P3.5's complete kernel suite, the timeout case twice reported that cleanup exceeded five seconds although native accounting showed active=0 and the root exit code was already -1. Awaited exit/stdout/stderr tasks were still pending. Targeted repeats alone did not reproduce the full-suite failure.

A versioned regression now duplicates only the test child's output handles into its consenting test coordinator, outside the child's job. The old cleanup reproducibly failed with active=0 and pending pipe reads. The corrected path confirms process termination directly through the native process wait handle and job accounting, without awaiting delivery of a managed completion callback, then cancels/disposes outstanding reads on timeout while retaining a synchronized snapshot of captured text. EOF is not treated as a process-liveness oracle. The deadline and five-second cleanup budget are unchanged; normal successful completion still requires complete stream draining. Exceptional cleanup does not add an unbounded final await.

The pipe-independent descendant fixture now clears output-handle inheritance before spawning its child and asserts prompt cleanup, rather than accidentally waiting for an inherited unnamed writer to close naturally. Host suite: 18 passing groups including the new negative reproduction, plus 527 PE inputs. Full P3.5 regression status is tracked in PLAN.md and artifacts/p3-fault-kernel-final.log.

## P5 final-validation cleanup follow-up

A later full regression reached the QEMU timeout scenario but exceeded the native termination grace. The failing log is retained as artifacts/p5-host-cleanup-baseline.log. Timeout cleanup now releases pending capture I/O before waiting for process termination and polls the authoritative native process/job state asynchronously, allowing I/O continuations to run. The five-second cleanup grace, original command deadline, kill-on-close ownership and requirement for both process signaling and zero active owned processes are retained. No success is inferred from EOF or a log marker. The added burst-output case and the existing process/QEMU stress harness are part of P5's final validation; see [P5 completion audit](P5-Completion-Audit.md).

The added native-state diagnostics showed that cancellation/polling alone did not resolve QEMU's forced-exit delay: original and freshly opened process handles remained unsignaled even with exit -1 and zero active job processes. Closing the already empty job did not resolve that observation. No success was accepted from those states.

QEMU timeout shutdown now uses its [QMP protocol](https://www.qemu.org/docs/master/interop/qmp-spec.html): after the original deadline expires, negotiate capabilities, wait for the matching acknowledgement, then request quit. Guest serial evidence is kept in a separate file and monitor traffic in a separate log. The cooperative phase shares the existing five-second cleanup budget with forced root/job termination fallback. Native process signaling and empty owned-job accounting remain mandatory; TimedOut remains true even when QEMU exits normally in response to quit. This follows the distinction between an exit request and [completed Windows process termination](https://learn.microsoft.com/en-us/windows/win32/procthread/terminating-a-process).

The first pipelined capability/quit attempt produced QMP parse errors; explicit acknowledgement sequencing fixed it. Host tests now cover cooperative timeout status, acknowledged control exchange, uncooperative forced fallback, output bursts, detached descendants and external pipe writers. All 23 host groups/529 PE inputs pass. The 300-process plus three-QEMU-timeout stress passes with exact native completion; artifacts/p5-cleanup-qmp-handshake.log and the corresponding monitor logs record acknowledgements and host-qmp-quit events. Failed intermediate experiments remain diagnostic evidence, not acceptance.
