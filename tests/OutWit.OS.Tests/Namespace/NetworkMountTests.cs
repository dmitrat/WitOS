using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using OutWit.OS.FileSystems;
using OutWit.OS.FileSystems.Smb;
using OutWit.OS.Namespace.Server;
using OutWit.OS.Namespace.Volumes;
using OutWit.OS.Namespaces;
using OutWit.OS.Tests.FileSystems.Smb;

namespace OutWit.OS.Tests.Namespace;

/// <summary>
/// Network volumes in the namespace (plan step E2.2): mount(2)'s request read as mount.cifs's options, a share of Samba
/// mounted through the session below /media and in the catalog with the network's origin, its files used through the
/// session, an unmount refused while a file is open and done once it closes, and each refusal's errno.
/// </summary>
[TestFixture]
public sealed class NetworkMountTests
{
    #region Tests

    [Test]
    public void ReadsAMountRequestTest()
    {
        var mount = NetworkMount.Parse(@"\\10.0.2.100\witos", "/media/Projects", "smb3",
            "username=dev,password=a,,b,domain=WORK,port=4445,vers=3.1.1,ro,noserverino", readOnly: false);
        Assert.That((mount.Server, mount.Share, mount.Address, mount.Port, mount.ReadOnly, mount.Alias),
            Is.EqualTo(("10.0.2.100", "witos", "10.0.2.100", (ushort)4445, true, "Projects")));
        Assert.That(mount.Credentials, Is.EqualTo(new SmbCredentials("dev", "a,b", "WORK")));
        Assert.That(mount.ToString(), Does.Not.Contain("a,b"));

        var named = NetworkMount.Parse("//pc/share", "", "cifs", "user=u,pass=p,ip=192.168.1.5", readOnly: true);
        Assert.That((named.Server, named.Address, named.Port, named.ReadOnly, named.Alias), Is.EqualTo(("pc", "192.168.1.5", (ushort)445, true, (string?)null)));
    }

    [TestCase("//10.0.2.100/s", "", "nfs", "username=u", typeof(NotSupportedException))]
    [TestCase("//10.0.2.100", "", "smb3", "username=u", typeof(ArgumentException))]
    [TestCase("//10.0.2.100/s/sub", "", "smb3", "username=u", typeof(ArgumentException))]
    [TestCase("//pc/s", "", "smb3", "username=u", typeof(ArgumentException))]
    [TestCase("//10.0.2.100/s", "", "smb3", "password=p", typeof(ArgumentException))]
    [TestCase("//10.0.2.100/s", "/data/s", "smb3", "username=u", typeof(ArgumentException))]
    [TestCase("//10.0.2.100/s", "/media/two words", "smb3", "username=u", typeof(ArgumentException))]
    [TestCase("//10.0.2.100/s", "", "smb3", "username=u,vers=1.0", typeof(ArgumentException))]
    public void RefusesAMalformedRequestTest(string source, string target, string type, string options, Type refusal) =>
        Assert.That(() => NetworkMount.Parse(source, target, type, options, false), Throws.TypeOf(refusal));

    [Test]
    public async Task MountsUsesAndUnmountsAShareTest()
    {
        await using var server = await SambaServer.StartAsync();
        await File.WriteAllTextAsync(server.HostPath("/from-host.txt"), "hello");
        var mounts = new MountTable { Connector = (address, port) => Connect(address, server.Port) };
        mounts.Add(MountTable.MEDIA, new MediaDirectory(mounts));
        var window = new ByteWindow(65536);
        await using var session = new NamespaceSession(mounts, window);

        var mounted = await MountAsync(session, window, "//127.0.0.1/" + SambaServer.SHARE, "", "smb3",
            $"username={server.Credentials.User},password={SambaServer.PASSWORD}");
        Assert.That(mounted.Error, Is.Zero);
        var volume = NamespaceVolume.Read(window.Read(0, (int)mounted.Length));
        Assert.That((volume.Alias, volume.Point, volume.Format, volume.Origin, volume.Label),
            Is.EqualTo((SambaServer.SHARE, "/media/" + SambaServer.SHARE, SmbFileSystem.FORMAT, NamespaceMessages.ORIGIN_NETWORK, SambaServer.SHARE)));
        Assert.That(volume.Flags & NamespaceMessages.VOLUME_SPACE, Is.Not.Zero);
        Assert.That(mounts.Catalog.Volumes.Select(item => item.Point), Is.EqualTo(new[] { "/media/" + SambaServer.SHARE }));

        // The share's files through the session, as the libc reaches them.
        var stat = await Path(session, window, NamespaceMessages.STAT, $"/media/{SambaServer.SHARE}/from-host.txt");
        Assert.That((stat.Error, stat.Stat!.Size), Is.EqualTo((0, 5L)));
        var opened = await Path(session, window, NamespaceMessages.OPEN, $"/media/{SambaServer.SHARE}/from-witos.txt",
            NamespaceMessages.WRITE_ACCESS | NamespaceMessages.CREATE);
        Assert.That(opened.Error, Is.Zero);
        window.Write(0, "written in WitOS"u8);
        Assert.That((await session.HandleAsync(new NamespaceRequest(NamespaceMessages.WRITE, Handle: opened.Handle, Length: 16))).Error, Is.Zero);

        // An unmount waits for the open file, unless detached; the system's volumes never unmount by a request.
        Assert.That((await Path(session, window, NamespaceMessages.UNMOUNT, "/media/" + SambaServer.SHARE)).Error, Is.EqualTo(Errno.BUSY));
        Assert.That((await session.HandleAsync(new NamespaceRequest(NamespaceMessages.CLOSE, Handle: opened.Handle))).Error, Is.Zero);
        Assert.That(await File.ReadAllTextAsync(server.HostPath("/from-witos.txt")), Is.EqualTo("written in WitOS"));
        Assert.That((await Path(session, window, NamespaceMessages.UNMOUNT, "/media")).Error, Is.EqualTo(Errno.BUSY));
        Assert.That((await Path(session, window, NamespaceMessages.UNMOUNT, "/media/nothing")).Error, Is.EqualTo(Errno.INVALID));
        Assert.That((await Path(session, window, NamespaceMessages.UNMOUNT, "/media/" + SambaServer.SHARE)).Error, Is.Zero);
        Assert.That(mounts.Catalog.Volumes, Is.Empty);
        Assert.That((await Path(session, window, NamespaceMessages.STAT, $"/media/{SambaServer.SHARE}/from-host.txt")).Error, Is.EqualTo(Errno.NOT_FOUND));

        // Mounted again under a name of its own, and detached with a file open, which then fails as EIO.
        Assert.That((await MountAsync(session, window, "//127.0.0.1/" + SambaServer.SHARE, "/media/Share-2", "cifs",
            $"username={server.Credentials.User},password={SambaServer.PASSWORD},ro")).Error, Is.Zero);
        Assert.That(mounts.Catalog.Volumes.Single().Mount.Volume.Volume.IsReadOnly, Is.True);
        var reading = await Path(session, window, NamespaceMessages.OPEN, "/media/Share-2/from-host.txt", NamespaceMessages.READ_ACCESS);
        Assert.That(reading.Error, Is.Zero);
        Assert.That((await Path(session, window, NamespaceMessages.UNMOUNT, "/media/Share-2", NamespaceMessages.UNMOUNT_DETACH)).Error, Is.Zero);
        Assert.That((await session.HandleAsync(new NamespaceRequest(NamespaceMessages.READ, Handle: reading.Handle, Length: 5))).Error, Is.EqualTo(Errno.IO));
    }

    [Test]
    public async Task RefusesWhatCannotMountTest()
    {
        await using var server = await SambaServer.StartAsync();
        var mounts = new MountTable();
        mounts.Add(MountTable.MEDIA, new MediaDirectory(mounts));
        var window = new ByteWindow(65536);
        await using var session = new NamespaceSession(mounts, window);
        var options = $"username={server.Credentials.User},password={SambaServer.PASSWORD}";

        Assert.That((await MountAsync(session, window, "//127.0.0.1/witos", "", "smb3", options)).Error, Is.EqualTo(Errno.NETWORK_DOWN), "no network yet");
        mounts.Connector = (address, port) => Connect(address, server.Port);
        Assert.That((await MountAsync(session, window, "//127.0.0.1/witos", "", "nfs", options)).Error, Is.EqualTo(Errno.NO_FILE_SYSTEM));
        Assert.That((await MountAsync(session, window, "//pc/witos", "", "smb3", options)).Error, Is.EqualTo(Errno.INVALID), "a name with no ip");
        Assert.That((await MountAsync(session, window, "//127.0.0.1/witos", "", "smb3", $"username={server.Credentials.User},password=wrong")).Error,
            Is.EqualTo(Errno.ACCESS));
        Assert.That((await MountAsync(session, window, "//127.0.0.1/nothing", "", "smb3", options)).Error, Is.EqualTo(Errno.NOT_FOUND));
        Assert.That((await MountAsync(session, window, "//127.0.0.1/witos", "/media/Data", "smb3", options)).Error, Is.EqualTo(Errno.BUSY),
            "the system's alias");
        Assert.That((await MountAsync(session, window, "//127.0.0.1/witos", "", "smb3", options)).Error, Is.Zero);
        Assert.That((await MountAsync(session, window, "//127.0.0.1/witos", "/media/witos", "smb3", options)).Error, Is.EqualTo(Errno.BUSY), "taken");
        Assert.That((await MountAsync(session, window, "//127.0.0.1/witos", "", "smb3", options)).Error, Is.Zero, "the same share again");
        Assert.That(mounts.Catalog.Volumes.Select(volume => (volume.Alias, volume.Collision)),
            Is.EqualTo(new[] { ("witos", false), ("witos-2", true) }), "a second mount of one share has its identity: a collision");
        mounts.Connector = (address, port) => throw new SocketException((int)SocketError.ConnectionRefused);
        Assert.That((await MountAsync(session, window, "//127.0.0.1/witos", "", "smb3", options)).Error, Is.EqualTo(Errno.CONNECTION_REFUSED));
    }

    #endregion

    #region Functions

    private static Stream Connect(string address, int port)
    {
        var client = new TcpClient();
        client.Connect(address, port);
        return client.GetStream();
    }

    private static ValueTask<NamespaceReply> MountAsync(NamespaceSession session, ByteWindow window, string source, string target, string type,
        string options)
    {
        var bytes = Encoding.UTF8.GetBytes($"{source}\0{target}\0{type}\0{options}\0");
        window.Write(0, bytes);
        return session.HandleAsync(new NamespaceRequest(NamespaceMessages.MOUNT, Length: bytes.Length));
    }

    private static ValueTask<NamespaceReply> Path(NamespaceSession session, ByteWindow window, uint operation, string path, uint flags = 0)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        window.Write(0, bytes);
        return session.HandleAsync(new NamespaceRequest(operation, flags, Length: bytes.Length));
    }

    #endregion

    #region Types

    private sealed class ByteWindow(int bytes) : INamespaceWindow
    {
        private readonly byte[] m_bytes = new byte[bytes];

        public int Length => m_bytes.Length;

        public byte[] Read(int offset, int count) => m_bytes.AsSpan(offset, count).ToArray();

        public void Write(int offset, ReadOnlySpan<byte> bytes) => bytes.CopyTo(m_bytes.AsSpan(offset));
    }

    #endregion
}
