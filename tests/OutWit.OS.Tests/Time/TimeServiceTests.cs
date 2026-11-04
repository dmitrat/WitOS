using System;
using System.IO;
using OutWit.OS.Time;

namespace OutWit.OS.Tests.Time;

/// <summary>
/// The time service's parts (plan step H7.4): the package's list of time servers and the time it saves on /data.
/// </summary>
[TestFixture]
public sealed class TimeServiceTests
{
    #region Tests

    [Test]
    public void TimeServersTest()
    {
        var servers = TimeServers.Parse(
        [
            "# the runner's server", "", "10.0.2.2:12345", "  pool.ntp.org  ", "[fec0::3]:1234", "fec0::4", "[::1]", "time.example:123"
        ]);
        Assert.That(servers, Is.EqualTo(new[]
        {
            new TimeServer("10.0.2.2", 12345), new TimeServer("pool.ntp.org", 123), new TimeServer("fec0::3", 1234),
            new TimeServer("fec0::4", 123), new TimeServer("::1", 123), new TimeServer("time.example", 123)
        }));
        Assert.That(servers[0].ToString(), Is.EqualTo("10.0.2.2:12345"));
        Assert.That(servers[2].ToString(), Is.EqualTo("[fec0::3]:1234"));
        Assert.Throws<FormatException>(() => TimeServers.Parse([":123"]), "no host");
        Assert.Throws<FormatException>(() => TimeServers.Parse(["host:0"]), "port 0");
        Assert.Throws<FormatException>(() => TimeServers.Parse(["host:70000"]), "a port beyond 16 bits");
        Assert.Throws<FormatException>(() => TimeServers.Parse(["host:+12"]), "a sign");
        Assert.Throws<FormatException>(() => TimeServers.Parse(["[fec0::3"]), "an open bracket");
        Assert.Throws<FormatException>(() => TimeServers.Parse(["[fec0::3]123"]), "a port without its colon");
    }

    [Test]
    public void SavedTimeTest()
    {
        const ulong time = 1_767_225_600_123_456_700; // 2026-01-01T00:00:00.1234567Z
        Assert.That(SavedTime.Format(time), Is.EqualTo("2026-01-01T00:00:00.1234567Z"));
        Assert.That(SavedTime.TryParse("2026-01-01T00:00:00.1234567Z\n", out var parsed), Is.True);
        Assert.That(parsed, Is.EqualTo(time));
        Assert.That(SavedTime.Utc(time), Is.EqualTo(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567)));
        Assert.That(SavedTime.TryParse("", out _), Is.False);
        Assert.That(SavedTime.TryParse("2026-01-01 00:00:00", out _), Is.False, "fake-hwclock's form is not this file's");
        Assert.That(SavedTime.TryParse("yesterday", out _), Is.False);
        var directory = Path.Combine(Path.GetTempPath(), "witos-time-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "system", "time");
            SavedTime.Save(path, time);
            SavedTime.Save(path, time + 1_000_000_000);
            Assert.That(File.ReadAllText(path), Is.EqualTo("2026-01-01T00:00:01.1234567Z\n"), "the later time replaced the earlier");
            Assert.That(File.Exists(path + ".next"), Is.False, "the file beside it was renamed over it");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    #endregion
}
