using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OutWit.OS.FileSystems.Smb;

namespace WitOS.Dev.Host;

/// <summary>
/// The runner's SMB server (plan step E2.2): the Linux host's smbd, started without privileges on a free loopback port
/// with a configuration of its own — one share over a directory of the host, SMB 3 required, signing mandatory, guests
/// refused — and an smbpasswd file of the current user's NT hash, as the SMB client's host tests start it (E2.1); QEMU's
/// guestfwd puts it at 10.0.2.100:445, the address of the runner's other servers (W1.2b), whatever its port.
/// </summary>
internal sealed class SambaPeer : IDisposable
{
    #region Constants

    /// <summary>
    /// The port the guest reaches the server at.
    /// </summary>
    public const int GUEST_PORT = 445;

    private const string SMBD = "/usr/sbin/smbd";

    private static readonly TimeSpan START = TimeSpan.FromSeconds(20);

    #endregion

    #region Fields

    private readonly Process m_process;
    private readonly StreamWriter m_log;
    private readonly string m_state;

    #endregion

    #region Constructors

    private SambaPeer(Process process, StreamWriter log, string state, int port)
    {
        m_process = process;
        m_log = log;
        m_state = state;
        Port = port;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Starts smbd for a share.
    /// </summary>
    /// <param name="share">The share.</param>
    /// <returns>The server, listening.</returns>
    /// <exception cref="InvalidOperationException">The host has no Samba, or smbd did not listen.</exception>
    public static SambaPeer Start(NetworkShare share)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(SMBD))
            throw new InvalidOperationException($"The share {share.Name} needs Samba's smbd on a Linux host ({SMBD}): apt-get install samba.");
        Directory.CreateDirectory(share.Directory);
        var state = Path.Combine(Path.GetTempPath(), $"witos-samba-{Guid.NewGuid():N}");
        foreach (var name in new[] { "private", "state", "cache", "lock", "pid", "ncalrpc" })
            Directory.CreateDirectory(Path.Combine(state, name));
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        var configuration = Path.Combine(state, "smb.conf");
        File.WriteAllText(configuration, $"""
            [global]
                smb ports = {port}
                interfaces = 127.0.0.1
                bind interfaces only = yes
                private dir = {state}/private
                state directory = {state}/state
                cache directory = {state}/cache
                lock directory = {state}/lock
                pid directory = {state}/pid
                ncalrpc dir = {state}/ncalrpc
                passdb backend = smbpasswd:{state}/private/smbpasswd
                server min protocol = SMB3_00
                server signing = mandatory
                map to guest = never
                restrict anonymous = 2
                disable netbios = yes
                load printers = no
                printing = bsd
                printcap name = /dev/null
                server role = standalone server
                log level = 1
            [{share.Name}]
                path = {Path.GetFullPath(share.Directory)}
                read only = no
            """);
        var uid = Processes.RunAsync("id", ["-u"], state, 30).GetAwaiter().GetResult().Output.Trim();
        var passwords = Path.Combine(state, "private", "smbpasswd");
        File.WriteAllText(passwords, $"{share.User}:{uid}:{new string('X', 32)}:{Convert.ToHexString(NtlmClient.NtHash(share.Password))}:[U          ]:" +
            $"LCT-{DateTimeOffset.UtcNow.ToUnixTimeSeconds():X8}:\n");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(passwords, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var log = File.CreateText(Path.Combine(state, "smbd.log"));
        var process = Process.Start(new ProcessStartInfo(SMBD, ["--foreground", "--no-process-group", "--debug-stdout", "-s", configuration])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("smbd did not start.");
        process.OutputDataReceived += (_, line) => Append(log, line.Data);
        process.ErrorDataReceived += (_, line) => Append(log, line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var peer = new SambaPeer(process, log, state, port);
        var deadline = DateTime.UtcNow + START;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, port);
                break;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline && !process.HasExited)
            {
                Thread.Sleep(100);
            }
            catch (SocketException)
            {
                var written = File.ReadAllText(Path.Combine(state, "smbd.log"));
                peer.Dispose();
                throw new InvalidOperationException($"smbd did not listen on {port}: {written}");
            }
        }
        Console.WriteLine($"The runner's SMB server shares {share.Directory} as {share.Name} for {share.User} at 127.0.0.1:{port}, " +
            $"the guest's {NetworkPeer.ECHO_ADDRESS}:{GUEST_PORT}.");
        return peer;
    }

    /// <summary>
    /// Stops smbd and removes its state.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (!m_process.HasExited)
                m_process.Kill(entireProcessTree: true);
            m_process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }
        m_process.Dispose();
        lock (m_log)
            m_log.Dispose();
        try
        {
            Directory.Delete(m_state, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void Append(StreamWriter log, string? line)
    {
        if (line is null)
            return;
        lock (log)
        {
            log.WriteLine(line);
            log.Flush();
        }
    }

    #endregion

    #region Properties

    /// <summary>
    /// The loopback port smbd listens on.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// QEMU's user networking option that puts the server at 10.0.2.100:445.
    /// </summary>
    public string NetdevOptions => $",guestfwd=tcp:{NetworkPeer.ECHO_ADDRESS}:{GUEST_PORT}-tcp:127.0.0.1:{Port}";

    #endregion
}

/// <summary>
/// A share the runner serves to the guest (E2.2).
/// </summary>
/// <param name="Name">The share's name.</param>
/// <param name="Directory">The host's directory it shares.</param>
/// <param name="User">The account, the host's current user, which smbd runs as.</param>
/// <param name="Password">The account's password.</param>
internal sealed record NetworkShare(string Name, string Directory, string User, string Password);
