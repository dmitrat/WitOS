using System.Text.Json;
using WitOS.Dev.Pe;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Pe;

/// <summary>
/// Named native platform objects: semantic groups, manifest round trip and hash checks.
/// </summary>
[TestFixture]
public sealed class NativePlatformObjectsTests
{
    #region Functions

    [Test]
    public void NamedNativeObjectsTest()
    {
        var scratch = TestEnvironment.Scratch();
        var input = Path.Combine(scratch, "platform-input");
        var output = Path.Combine(scratch, "platform-output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        var entries = NativePlatformObjects.SOURCES.Select(name =>
        {
            var file = Path.Combine(input, name + ".obj");
            File.WriteAllText(file, name);
            return KeyValuePair.Create(name, file);
        }).Reverse().ToArray(); // Enumeration order must not alter semantic link groups.
        var objects = new NativePlatformObjects(entries);
        Assert.That(objects.Record.Select(Path.GetFileName).SequenceEqual(new[] { "native_services.witos.cpp.obj", "native_services.asm.obj", "pal_events.witos.cpp.obj", "native_thread_handles.witos.cpp.obj", "native_thread_handles.asm.obj" }), Is.True, "Record group depends on manifest order");
        Assert.That(objects.Thread.Length == 31 && objects.Cpu.Length == 19 && objects.Com.Length == 35, Is.True, "Native group membership changed");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(objects.CopyTo(output)));
        var loaded = NativePlatformObjects.Read(output, json.RootElement);
        Assert.That(loaded.All.Select(Path.GetFileName).SequenceEqual(objects.All.Select(Path.GetFileName)), Is.True, "Named manifest roundtrip changed objects");
        var rejected = false;
        try
        { _ = new NativePlatformObjects(entries.Skip(1)); }
        catch (InvalidDataException) { rejected = true; }
        Assert.That(rejected, Is.True, "Missing native source accepted");
        File.AppendAllText(loaded.All[0], "changed");
        rejected = false;
        try
        { NativePlatformObjects.Read(output, json.RootElement); }
        catch (InvalidDataException) { rejected = true; }
        Assert.That(rejected, Is.True, "Changed object hash accepted");
    }

    #endregion
}
