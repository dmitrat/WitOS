using System.Security.Cryptography;
using System.Text.Json;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.NativeAot.Acceptance;

/// <summary>
/// Boots the prepared NativeAOT runtime image in all four guest profiles and publishes the acceptance evidence.
/// </summary>
internal static class RuntimeBootMatrix
{
    #region Fields

    private static readonly string[] PROFILE_NAMES = ["runtime-boot-128", "runtime-boot-512", "runtime-boot-intel", "runtime-boot-avx"];

    #endregion

    #region Functions

    /// <summary>
    /// Verifies the hosted reference evidence, boots every profile and publishes the combined evidence.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="image">Kernel disk image that embeds the prepared runtime image.</param>
    /// <param name="attempt">Attempt that snapshots inputs and commits the evidence.</param>
    /// <exception cref="InvalidDataException">The hosted evidence or the shared managed object changed.</exception>
    public static async Task RunAsync(string root, string image, RuntimeBootAttempt attempt)
    {
        var directory = Path.GetDirectoryName(image)!;
        var capturedInput = attempt.Snapshot(Path.Combine(directory, "runtime-input.json"));
        attempt.Snapshot(Path.Combine(directory, "runtime-image.pe"));
        using var input = JsonDocument.Parse(await File.ReadAllTextAsync(capturedInput));
        var referenceReport = attempt.Snapshot(Path.Combine(root, "artifacts/runtime-readiness/readiness.json"));
        var referenceLog = attempt.Snapshot(Path.Combine(root, "artifacts/runtime-readiness/hosted.log"));
        var referenceImage = attempt.Snapshot(Path.Combine(root, "artifacts/runtime-readiness/reference/NativeAotBoot.exe"));
        using var reference = JsonDocument.Parse(await File.ReadAllTextAsync(referenceReport));
        var proof = reference.RootElement;
        var managedObjectHash = RuntimeBootProtocol.SharedManagedObjectHash(input.RootElement);
        if (!proof.TryGetProperty("hostedSemanticAcceptance", out var semantic) ||
            !semantic.GetBoolean() ||
            !proof.GetProperty("hostedPassed").GetBoolean() ||
            proof.GetProperty("managedObjectSha256").GetString() != managedObjectHash ||
            proof.GetProperty("referenceImageSha256").GetString() != Hash(referenceImage) ||
            proof.GetProperty("hostedLogSha256").GetString() != Hash(referenceLog) ||
            !RuntimeBootProtocol.ValidateHostedLog(await File.ReadAllTextAsync(referenceLog)))
        {
            throw new InvalidDataException("Hosted semantic evidence or shared managed object changed; rebuild runtime-source.");
        }
        var logs = Path.Combine(attempt.RunDirectory, "logs");
        await BootProfileAsync(root, image, new BootRequest(PROFILE_NAMES[0], 128, 120, ExpectedOutcome.Success), logs);
        await BootProfileAsync(root, image, new BootRequest(PROFILE_NAMES[1], 512, 120, ExpectedOutcome.Success), logs);
        await BootProfileAsync(root, image,
            new BootRequest(PROFILE_NAMES[2], 256, 120, ExpectedOutcome.Success) { CpuModel = "Nehalem" }, logs);
        await BootProfileAsync(root, image,
            new BootRequest(PROFILE_NAMES[3], 256, 120, ExpectedOutcome.Success) { CpuModel = "max" }, logs);

        attempt.Publish(new
        {
            guestManagedExecution = true,
            integrationCyclesPerExecution = RuntimeBootProtocol.INTEGRATION_CYCLES,
            executionsPerProfile = RuntimeBootProtocol.EXECUTION_BASES.Length,
            threadStoreAudit = true,
            collectorExecution = true,
            managedStackOverflowContained = true,
            managedThreadCapacityRecovery = true,
            managedThreadApis = true,
            managedThreadsPerExecution = 28,
            parkedManagedRoots = true,
            managedFinalization = true,
            probeFinalizersPerExecution = 56,
            managedExceptionUnwind = true,
            managedExceptionRoundsPerExecution = 88,
            hardwareFaultTranslation = true,
            hardwareFaultsPerExecution = 36,
            nativeFaultContainment = true,
            abruptWorkerContainment = true,
            abruptWorkerCasesPerProfile = 8,
            orderlyThreadCompletion = true,
            gcInitializationFailure = true,
            managedOomRecovery = true,
            managedOomFailuresPerExecution = 4,
            workerLifecycle = true,
            workerDrivenCollection = true,
            hijackObserved = true,
            activeServiceFrameRejected = true,
            collectionDuringThreadExit = true,
            workersPerExecution = RuntimeBootProtocol.WORKERS_PER_EXECUTION,
            imageRelocations = RuntimeBootProtocol.IMAGE_BASES.Length,
            profiles = 4,
            runtimeImageSha256 = input.RootElement.GetProperty("sha256").GetString(),
            kernelDiskSha256 = Hash(image),
            hostedReference = new
            {
                semanticPassed = true,
                sharedManagedObjectSha256 = managedObjectHash,
                report = referenceReport,
                reportSha256 = Hash(referenceReport),
                log = referenceLog,
                logSha256 = Hash(referenceLog),
                image = referenceImage,
                imageSha256 = Hash(referenceImage)
            },
            workload = "Standard CoreLib: four combined hardware/managed EH, compacting GC, finalization/resurrection and " +
                "Thread/Monitor/TLS/quota-recovery cycles; locked ThreadStore audit and stale observer checks; 43 orderly " +
                "workers; base A/B/A/B; actual failure components, exit 42 and full component resource reclamation. Hosted " +
                "reference uses the exact same managed object with an explicitly different OS thread quota.",
            logs = PROFILE_NAMES.Select(name =>
            {
                var file = Path.Combine(logs, name + ".serial.log");
                return new { file, sha256 = Hash(file) };
            })
        });
    }

    #endregion

    #region Tools

    private static Task BootProfileAsync(string root, string image, BootRequest request, string logs)
        => BootScenarioRunner.RunAsync(root, image, request with { Suite = BootSuite.RuntimeBoot, LogDirectory = logs });

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    #endregion
}
