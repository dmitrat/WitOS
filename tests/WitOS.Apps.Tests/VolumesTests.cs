using System;
using WitOS.Volumes;

namespace WitOS.Apps.Tests;

/// <summary>
/// witos-volumes' parsing (plan step E2.2): a share's arguments, mount(2)'s options as mount.cifs writes them, its
/// target, /etc/mtab's lines and sizes as people read them.
/// </summary>
[TestFixture]
public sealed class VolumesTests
{
    #region Tests

    [Test]
    public void ParsesAShareTest()
    {
        var share = ShareMount.Parse(@"\\host\Projects", ["--user", "dev", "--domain", "WORK", "--as", "Projects-2", "--port", "4445", "--read-only",
            "--password-file", "/etc/secret"]);
        Assert.That(share, Is.EqualTo(new ShareMount("//host/Projects", "host", "Projects-2", "dev", "WORK", 4445, true, "/etc/secret")));
        Assert.That(share.Target, Is.EqualTo("/media/Projects-2"));
        Assert.That(share.ToString(), Is.EqualTo(@"//host/Projects as WORK\dev, read-only"));

        var plain = ShareMount.Parse("//10.0.2.100/witos", ["--user", "witos"]);
        Assert.That((plain.Target, plain.Port, plain.ReadOnly, plain.Domain), Is.EqualTo(((string?)null, 0, false, "")));
    }

    [TestCase("//host", "a share")]
    [TestCase("//host/share/sub", "a directory below it")]
    [TestCase("host/share", "no //")]
    public void RefusesASourceThatIsNoShareTest(string source, string reason) =>
        Assert.Throws<ArgumentException>(() => ShareMount.Parse(source, ["--user", "u"]), reason);

    [Test]
    public void RefusesBadOptionsTest()
    {
        Assert.Throws<ArgumentException>(() => ShareMount.Parse("//h/s", []), "no user");
        Assert.Throws<ArgumentException>(() => ShareMount.Parse("//h/s", ["--user"]), "a value missing");
        Assert.Throws<ArgumentException>(() => ShareMount.Parse("//h/s", ["--user", "u", "--port", "0"]));
        Assert.Throws<ArgumentException>(() => ShareMount.Parse("//h/s", ["--user", "u", "--as", "two words"]));
        Assert.Throws<ArgumentException>(() => ShareMount.Parse("//h/s", ["--user", "u", "--guest"]));
    }

    // mount.cifs's form: a comma in a value doubled; the address the command resolved as ip.
    [Test]
    public void WritesMountOptionsTest()
    {
        var share = ShareMount.Parse("//pc/data", ["--user", "me,you", "--domain", "D", "--port", "445", "--read-only"]);
        Assert.That(share.Options("a,b=c", "192.168.1.5"), Is.EqualTo("username=me,,you,password=a,,b=c,domain=D,ip=192.168.1.5,port=445,ro"));
        Assert.That(ShareMount.Parse("//10.0.2.100/s", ["--user", "u"]).Options("p", null), Is.EqualTo("username=u,password=p"));
    }

    [Test]
    public void ReadsTheMountTableTest()
    {
        var volumes = WitOS.Volumes.Program.Mounts(["package / witos-package ro 0 0", "Data /data ext4 rw 0 0", "witos /media/witos smb3 rw 0 0", "short line"]);
        Assert.That(volumes, Is.EqualTo(new[]
        {
            new MountedVolume("package", "/", "witos-package", "ro"), new MountedVolume("Data", "/data", "ext4", "rw"),
            new MountedVolume("witos", "/media/witos", "smb3", "rw")
        }));
        Assert.That(WitOS.Volumes.Program.Bytes(512), Is.EqualTo("512 B"));
        Assert.That(WitOS.Volumes.Program.Bytes(1536L << 20), Is.EqualTo("1.5 GiB"));
    }

    #endregion
}
