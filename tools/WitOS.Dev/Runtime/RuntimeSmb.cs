using System.Security.Cryptography;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The network share scenario (plan step E2.2, ADR 0032): beside the device services, the .NET install and the adapter,
/// the runner serves a directory of its host over SMB 3 with Samba's smbd at 10.0.2.100:445; /bin/init mounts it with
/// witos-volumes at /media/witos as the host's user with a password of this boot, lists the volumes, runs an ordinary
/// program published without a RID that uses the share through System.IO, unmounts it and checks it is gone. After the
/// boot the host finds what the program left in its directory.
/// </summary>
internal static class RuntimeSmb
{
    #region Constants

    /// <summary>
    /// The check's project.
    /// </summary>
    public const string PROJECT = "tests/Runtime.Witos/Share";

    /// <summary>
    /// The share's name, and its alias below /media.
    /// </summary>
    public const string SHARE = "witos";

    private const string APPLICATION = "share";

    private const string PASSWORD_FILE = "etc/witos/share-password";

    private const string FROM_HOST = "from-host.txt";

    // The check's own text: the program compares what it reads with it.
    private const string FROM_HOST_TEXT = "Laid in the share by the runner's host, read by a program in WitOS.";

    private const int WRITTEN_BYTES = 300_000;

    private const int CHECKS = 9;

    private const int TIMEOUT_SECONDS = 1800;

    #endregion

    #region Functions

    /// <summary>
    /// Boots the scenario on the services and the install earlier runs built.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>When the boot passed and the share holds what the program left.</returns>
    /// <exception cref="InvalidOperationException">The share does not hold what the program left.</exception>
    public static async Task RunAsync(string root, KernelArchitecture architecture)
    {
        var scenario = KernelImageBuilder.RUNTIME_SMB_SCENARIO;
        var output = Path.Combine(root, "artifacts", architecture.Name, scenario);
        Directory.CreateDirectory(output);
        var directory = Path.Combine(output, "share");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, FROM_HOST), FROM_HOST_TEXT);
        var share = new NetworkShare(SHARE, directory, Environment.UserName, Convert.ToHexString(RandomNumberGenerator.GetBytes(12)));
        var passwordFile = Path.Combine(output, "share-password");
        await File.WriteAllTextAsync(passwordFile, share.Password + "\n");

        var published = await RuntimeApps.PublishAsync(root, output, PROJECT, APPLICATION);
        var package = (await RuntimeApps.PackageAsync(root, output, architecture, [(APPLICATION, published)], Commands(share))).ToList();
        package.Add((PASSWORD_FILE, passwordFile));
        await RuntimeApps.BootAsync(root, architecture, scenario, RequiredLines(architecture, share), TIMEOUT_SECONDS, package: package, share: share);

        var written = Path.Combine(directory, "from-witos.bin");
        var moved = Path.Combine(directory, "made-in-witos", "inner", "note.txt");
        if (!File.Exists(written) || !(await File.ReadAllBytesAsync(written)).SequenceEqual(Enumerable.Range(0, WRITTEN_BYTES).Select(i => (byte)(i * 7))))
            throw new InvalidOperationException($"The share does not hold the {WRITTEN_BYTES} bytes the program wrote to from-witos.bin.");
        if (!File.Exists(moved) || await File.ReadAllTextAsync(moved) != "moved by WitOS" || File.Exists(Path.Combine(directory, "doomed.txt")))
            throw new InvalidOperationException("The share does not hold the program's moved note, or holds the file it deleted.");
        Console.WriteLine($"A network share on witos-{architecture.Name}: witos-volumes mounted the runner's Samba share at /media/{SHARE} over SMB 3 " +
            "with signing, a program published without a RID read, wrote, copied, moved and deleted through System.IO, the share was " +
            "unmounted, and the host's directory holds what the program left.");
    }

    /// <summary>
    /// What /bin/init runs: the mount, the volumes, the program, the unmount, the program again to see the share gone.
    /// </summary>
    /// <param name="share">The share.</param>
    /// <returns>The lines of /etc/apps.</returns>
    public static string[] Commands(NetworkShare share) =>
    [
        $"{RuntimeDevices.VOLUMES_COMMAND} mount //{NetworkPeer.ECHO_ADDRESS}/{share.Name} --user {share.User} --password-file /{PASSWORD_FILE}",
        RuntimeDevices.VOLUMES_COMMAND,
        $"/dotnet/dotnet /app/{APPLICATION}/Share.dll",
        $"{RuntimeDevices.VOLUMES_COMMAND} unmount {share.Name}",
        $"/dotnet/dotnet /app/{APPLICATION}/Share.dll unmounted"
    ];

    /// <summary>
    /// The lines the scenario requires: the network service's endpoint at the namespace, the mount, the volumes' list, every
    /// check of the program, the unmount and each command's exit.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="share">The share.</param>
    /// <returns>The lines.</returns>
    public static string[] RequiredLines(KernelArchitecture architecture, NetworkShare share) =>
    [
        "[NAMESPACE] the network service's endpoint: shares mount below /media",
        $"[NAMESPACE] mounted /media/{share.Name}: SMB3 volume \"{share.Name}\"",
        $"from //{NetworkPeer.ECHO_ADDRESS}/{share.Name} (SMB 3.1.1 at {NetworkPeer.ECHO_ADDRESS}:{SambaPeer.GUEST_PORT} as {share.User})",
        $"Mounted //{NetworkPeer.ECHO_ADDRESS}/{share.Name} at /media/{share.Name}.",
        // witos-volumes' list: the share's row, its point and its type in their columns.
        $"{$"/media/{share.Name}",-24} smb3",
        $"[SHARE] on {architecture.OsArchitecture}: {CHECKS} of {CHECKS} checks passed",
        $"[NAMESPACE] unmounted /media/{share.Name}",
        $"Unmounted /media/{share.Name}.",
        "[SHARE] unmounted: /media/witos is gone",
        // apps_init names a command by its program and first argument.
        $"[APPS-INIT] {RuntimeDevices.VOLUMES_COMMAND} mount exited with 0",
        $"[APPS-INIT] {RuntimeDevices.VOLUMES_COMMAND}  exited with 0",
        $"[APPS-INIT] /dotnet/dotnet /app/{APPLICATION}/Share.dll exited with 0",
        $"[APPS-INIT] {RuntimeDevices.VOLUMES_COMMAND} unmount exited with 0"
    ];

    #endregion
}
