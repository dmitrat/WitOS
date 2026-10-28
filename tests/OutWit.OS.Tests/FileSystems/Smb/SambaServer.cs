using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OutWit.OS.FileSystems.Smb;

namespace OutWit.OS.Tests.FileSystems.Smb;

/// <summary>
/// Samba as the reference server the SMB client is judged against (plan step E2), as e2fsprogs judges the ext4 provider:
/// an smbd of the Linux host, started without privileges on a free loopback port with a configuration of its own — one
/// share, SMB 3 required, signing mandatory, guests refused — and an smbpasswd file it writes itself with the NT hash of
/// the current user's password, since smbpasswd adds an account as root alone. A host without Samba skips the tests.
/// </summary>
internal sealed class SambaServer : IAsyncDisposable
{
    #region Constants

    /// <summary>
    /// The share's name.
    /// </summary>
    public const string SHARE = "witos";

    /// <summary>
    /// The account's password.
    /// </summary>
    public const string PASSWORD = "Wit-OS secret 1";

    private const string SMBD = "/usr/sbin/smbd";

    private static readonly TimeSpan START = TimeSpan.FromSeconds(20);

    // smbd's output and error lines arrive on two threads.
    private static readonly Lock LOG_LOCK = new();

    #endregion

    #region Fields

    private readonly Process m_process;
    private readonly string m_directory;

    #endregion

    #region Constructors

    private SambaServer(Process process, string directory, int port, string user)
    {
        m_process = process;
        m_directory = directory;
        Port = port;
        Credentials = new SmbCredentials(user, PASSWORD);
    }

    #endregion

    #region Functions

    /// <summary>
    /// Starts a server, or skips the test on a host without Samba.
    /// </summary>
    /// <param name="maxProtocol">smb.conf's server max protocol: SMB3_11, SMB3_02 or SMB3_00.</param>
    /// <returns>The server, listening.</returns>
    public static async Task<SambaServer> StartAsync(string maxProtocol = "SMB3_11")
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(SMBD))
            Assert.Ignore("The host has no Samba (smbd).");
        var directory = Path.Combine(Path.GetTempPath(), $"witos-samba-{Guid.NewGuid():N}");
        foreach (var name in new[] { "share", "private", "state", "cache", "lock", "pid", "ncalrpc" })
            Directory.CreateDirectory(Path.Combine(directory, name));
        var port = FreePort();
        var user = Environment.UserName;
        var configuration = Path.Combine(directory, "smb.conf");
        await File.WriteAllTextAsync(configuration, $"""
            [global]
                smb ports = {port}
                interfaces = 127.0.0.1
                bind interfaces only = yes
                private dir = {directory}/private
                state directory = {directory}/state
                cache directory = {directory}/cache
                lock directory = {directory}/lock
                pid directory = {directory}/pid
                ncalrpc dir = {directory}/ncalrpc
                passdb backend = smbpasswd:{directory}/private/smbpasswd
                server min protocol = SMB3_00
                server max protocol = {maxProtocol}
                server signing = mandatory
                map to guest = never
                restrict anonymous = 2
                disable netbios = yes
                load printers = no
                printing = bsd
                printcap name = /dev/null
                server role = standalone server
                log level = 1
            [{SHARE}]
                path = {directory}/share
                read only = no
            """);
        // An smbpasswd line: the user, its uid, no LM hash, the NT hash, a normal account changed now.
        var hash = Convert.ToHexString(NtlmClient.NtHash(PASSWORD));
        await File.WriteAllTextAsync(Path.Combine(directory, "private", "smbpasswd"),
            $"{user}:{Run("id", "-u").Trim()}:{new string('X', 32)}:{hash}:[U          ]:LCT-{DateTimeOffset.UtcNow.ToUnixTimeSeconds():X8}:\n");
        if (OperatingSystem.IsLinux()) // always, past the skip above; the analyzer reads the guard alone
            File.SetUnixFileMode(Path.Combine(directory, "private", "smbpasswd"), UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var start = new ProcessStartInfo(SMBD, ["--foreground", "--no-process-group", "--debug-stdout", "-s", configuration])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var process = Process.Start(start) ?? throw new InvalidOperationException("smbd did not start.");
        var log = Path.Combine(directory, "smbd.log");
        process.OutputDataReceived += (_, line) => Append(log, line.Data);
        process.ErrorDataReceived += (_, line) => Append(log, line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var server = new SambaServer(process, directory, port, user);
        try
        {
            await server.WaitListeningAsync();
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A stream connected to the server's port.
    /// </summary>
    /// <returns>The stream.</returns>
    public async Task<Stream> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port);
        client.NoDelay = true;
        return client.GetStream(); // owns the socket: GetStream's stream closes it
    }

    /// <summary>
    /// Mounts the share as the client would.
    /// </summary>
    /// <param name="readOnly">Whether the mount refuses changes.</param>
    /// <returns>The filesystem.</returns>
    public async Task<SmbFileSystem> MountAsync(bool readOnly = false) =>
        await SmbFileSystem.MountAsync(await ConnectAsync(), "127.0.0.1", SHARE, Credentials, readOnly);

    /// <summary>
    /// The path in the host's share directory of a path of the volume.
    /// </summary>
    /// <param name="path">The path, absolute within the volume.</param>
    /// <returns>The host's path.</returns>
    public string HostPath(string path) => Path.Combine(m_directory, "share", path.TrimStart('/'));

    /// <summary>
    /// What smbd logged, for a failure's message.
    /// </summary>
    /// <returns>The log.</returns>
    public string Log()
    {
        var log = Path.Combine(m_directory, "smbd.log");
        return File.Exists(log) ? File.ReadAllText(log) : "";
    }

    /// <summary>
    /// Stops smbd and removes its directory.
    /// </summary>
    /// <returns>When it is stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!m_process.HasExited)
                m_process.Kill(entireProcessTree: true);
            await m_process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
        }
        m_process.Dispose();
        try
        {
            Directory.Delete(m_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task WaitListeningAsync()
    {
        var deadline = DateTime.UtcNow + START;
        while (DateTime.UtcNow < deadline)
        {
            if (m_process.HasExited)
                throw new InvalidOperationException($"smbd exited with {m_process.ExitCode}: {Log()}");
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, Port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }
        throw new TimeoutException($"smbd did not listen on {Port} within {START.TotalSeconds} s: {Log()}");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Run(string program, params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(program, arguments) { RedirectStandardOutput = true })
            ?? throw new InvalidOperationException($"{program} did not start.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static void Append(string log, string? line)
    {
        if (line is null)
            return;
        lock (LOG_LOCK)
            File.AppendAllText(log, line + "\n");
    }

    #endregion

    #region Properties

    /// <summary>
    /// The loopback port smbd listens on.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// The account the share takes: the current user with <see cref="PASSWORD"/>.
    /// </summary>
    public SmbCredentials Credentials { get; }

    #endregion
}
