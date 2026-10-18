namespace WitOS.Dev.Runtime;

/// <summary>
/// What a person asks of the shell command (C1.4, C2.4).
/// </summary>
/// <param name="Published">A directory published on the host, laid on the data volume under its name; none by default.</param>
/// <param name="MemoryMiB">The guest's memory.</param>
/// <param name="Disk">The data disk's image to keep between sessions: made with the volume's files when missing, booted
/// as it is when there (what the previous session left on it stays; a published directory is laid only when the disk is
/// made); the scenario's own, made anew, when null.</param>
/// <param name="Window">The display and the keyboard in QEMU's window, the console mirroring the terminal (C2.4).</param>
/// <param name="Prepare">A directory to write the session's image and data disk to instead of booting them.</param>
/// <param name="Image">A prepared image to boot with its <paramref name="Disk"/> instead of building one.</param>
internal sealed record ShellOptions(string? Published, int MemoryMiB, string? Disk, bool Window, string? Prepare, string? Image);
