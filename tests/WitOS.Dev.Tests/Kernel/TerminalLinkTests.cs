using System.Net;
using System.Net.Sockets;
using System.Text;
using WitOS.Dev.Host;

namespace WitOS.Dev.Tests.Kernel;

/// <summary>
/// The runner's side of the guest's terminal (plan step N4.3a): a client that plays QEMU's character device connects to
/// the listener, writes what the guest would, and must receive each scripted input once its line appeared, in order,
/// while the transcript and the terminal log keep what was written.
/// </summary>
[TestFixture]
public sealed class TerminalLinkTests
{
    #region Tests

    [Test]
    public async Task TypesEachInputAfterItsLineTest()
    {
        var log = Path.Combine(Path.GetTempPath(), $"witos-terminal-{Guid.NewGuid():N}.log");
        try
        {
            using var link = new TerminalLink([new TerminalInput("[A] type", "one\n"), new TerminalInput("[B] type", "two\n")], log);
            var port = int.Parse(link.Argument.Split("port=")[1].Split(',')[0]);
            Assert.That(link.Argument, Does.StartWith("socket,id=term0,host=127.0.0.1,port="));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var stream = client.GetStream();
            await stream.WriteAsync("booting\r\n[A] type a line\r\n"u8.ToArray());
            Assert.That(await ReadAsync(stream, 4), Is.EqualTo("one\n"));
            await stream.WriteAsync("[A] type again\r\n[B] type\r\n"u8.ToArray());
            Assert.That(await ReadAsync(stream, 4), Is.EqualTo("two\n"), "the second input follows the second line, not the first's repeat");
            await stream.WriteAsync("done\r\n"u8.ToArray());
            await Task.Delay(100);
            Assert.That(link.Typed, Is.EqualTo(2));
            Assert.That(link.Transcript, Does.Contain("[B] type\r\ndone\r\n"));
            client.Close();
            link.Dispose();
            Assert.That(await File.ReadAllTextAsync(log), Does.Contain("[A] type a line"));
        }
        finally
        {
            File.Delete(log);
        }
    }

    [Test]
    public async Task AnswersQueriesUnlessAMirrorTest()
    {
        var log = Path.Combine(Path.GetTempPath(), $"witos-terminal-{Guid.NewGuid():N}.log");
        try
        {
            using var answering = new TerminalLink([new TerminalInput("[A]", "one\n")], log);
            using var client = await ConnectAsync(answering);
            using var stream = client.GetStream();
            await stream.WriteAsync("\x1b[6n[A]\r\n"u8.ToArray());
            Assert.That(await ReadAsync(stream, 10), Is.EqualTo("\x1b[1;1Rone\n"), "the cursor position report precedes the script's input");
            client.Close();
        }
        finally
        {
            File.Delete(log);
        }
        try
        {
            using var mirror = new TerminalLink([new TerminalInput("[A]", "one\n")], log, answersQueries: false);
            using var client = await ConnectAsync(mirror);
            using var stream = client.GetStream();
            await stream.WriteAsync("\x1b[6n[A]\r\n"u8.ToArray());
            Assert.That(await ReadAsync(stream, 4), Is.EqualTo("one\n"), "a mirror of the display types the script alone");
            Assert.That(mirror.Screen, Does.StartWith("[A]"), "the mirror still paints its screen");
            client.Close();
        }
        finally
        {
            File.Delete(log);
        }
    }

    #endregion

    #region Tools

    private static async Task<TcpClient> ConnectAsync(TerminalLink link)
    {
        var port = int.Parse(link.Argument.Split("port=")[1].Split(',')[0]);
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    private static async Task<string> ReadAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (read < count)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
            if (got == 0)
                break;
            read += got;
        }
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    #endregion
}
