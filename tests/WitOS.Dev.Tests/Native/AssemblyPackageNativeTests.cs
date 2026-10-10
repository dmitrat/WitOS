using WitOS.Dev.Host;
using WitOS.Dev.Kernel;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Native;

/// <summary>
/// The reference reader of the boot package and the firmware transport on guarded and fault-injected inputs (hosted).
/// </summary>
[TestFixture]
public sealed class AssemblyPackageNativeTests
{
    #region Functions

    [Test]
    public Task AssemblyPackageNativeGuardedTest() => RunAsync(TestEnvironment.Root, TestEnvironment.Scratch());

    #endregion

    #region Tools

    private static async Task RunAsync(string root, string output)
    {
        var payload = Enumerable.Range(0, 4099).Select(i => (byte)i).ToArray();
        var files = new (string Name, ReadOnlyMemory<byte> Bytes)[] {
            ("app/Probe.dll",payload),("app/Probe.runtimeconfig.json","{\"runtimeOptions\":{}}"u8.ToArray()),
            ("empty",Array.Empty<byte>()),("resources/\u03bb.txt","payload"u8.ToArray()) };
        var package = Path.Combine(output, "writer.pak");
        var small = Path.Combine(output, "two-files.pak");
        await File.WriteAllBytesAsync(package, AssemblyPackage.Create(files));
        await File.WriteAllBytesAsync(small, AssemblyPackage.Create(new (string, ReadOnlyMemory<byte>)[] { ("a.bin", new byte[] { 1, 2, 3 }), ("b.bin", new byte[] { 4, 5, 6 }) }));
        var firmware = Path.Combine(output, "firmware.pak");
        var firmwareData = Enumerable.Range(0, 2 * 1024 * 1024 + 123).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(firmware, AssemblyPackage.Create(new (string, ReadOnlyMemory<byte>)[] { ("data", firmwareData) }));
        var hierarchy = Path.Combine(output, "hierarchy.pak");
        await File.WriteAllBytesAsync(hierarchy, AssemblyPackage.Create(new (string, ReadOnlyMemory<byte>)[]{
            ("a",Array.Empty<byte>()),("b.c",Array.Empty<byte>()),("b/d",Array.Empty<byte>())}));
        var exe = await NativeHarness.BuildAsync(root, output, "assembly-package",
            ["tests/WitOS.Dev.Tests/Native/PackageReader.c", "tests/WitOS.Dev.Tests/Native/AssemblyPackageNative.c",
                "tests/WitOS.Dev.Tests/Native/BootPackageFirmware.c"], ["src/Kernel/include"]);
        var run = await Processes.RunAsync(exe, [package, small, firmware, hierarchy], output, 30);
        await File.WriteAllTextAsync(Path.Combine(output, "assembly-package.log"), run.Output + run.Error);
        if (run.TimedOut || run.ExitCode != 0 || !run.Output.StartsWith("PASS: ", StringComparison.Ordinal))
            throw new InvalidDataException("Native package validation failed: " + run.Output + run.Error);
        for (var i = 0; i < files.Length; ++i)
        {
            var extracted = await File.ReadAllBytesAsync(Path.Combine(output, $"package-roundtrip-{i}.bin"));
            if (!extracted.AsSpan().SequenceEqual(files[i].Bytes.Span))
                throw new InvalidDataException("Native package payload changed: " + files[i].Name);
        }
        Console.Write(run.Output);
    }

    #endregion
}
