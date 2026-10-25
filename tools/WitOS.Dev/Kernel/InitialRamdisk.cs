using System.Buffers.Binary;

namespace WitOS.Dev.Kernel;

/// <summary>
/// The initial ramdisk of the Linux arm64 boot path (plan step H6, ADR 0030): the root task's flat image and the boot
/// package in one file, which a bootloader loads beside the kernel image and names in the device tree's /chosen, and
/// which the boot layer turns into the contract's extents (src/Boot.Linux/boot_linux.h, WitInitrdHeader). A page of
/// header, then the root task and the package, each at a page boundary. Little endian.
/// </summary>
internal static class InitialRamdisk
{
    #region Constants

    /// <summary>
    /// "WITSRD1\0".
    /// </summary>
    public const ulong MAGIC = 0x0031445253544957;

    /// <summary>
    /// The format's version.
    /// </summary>
    public const uint VERSION = 1;

    /// <summary>
    /// The header's bytes, a page.
    /// </summary>
    public const int HEADER_BYTES = 4096;

    #endregion

    #region Functions

    /// <summary>
    /// Writes the ramdisk.
    /// </summary>
    /// <param name="rootTask">The root task's flat image.</param>
    /// <param name="package">The boot package.</param>
    /// <returns>The ramdisk's bytes.</returns>
    public static byte[] Create(byte[] rootTask, byte[] package)
    {
        ArgumentOutOfRangeException.ThrowIfZero(rootTask.Length, nameof(rootTask));
        ArgumentOutOfRangeException.ThrowIfZero(package.Length, nameof(package));
        long rootOffset = HEADER_BYTES;
        var packageOffset = Align(rootOffset + rootTask.Length);
        var ramdisk = new byte[Align(packageOffset + package.Length)];
        BinaryPrimitives.WriteUInt64LittleEndian(ramdisk, MAGIC);
        BinaryPrimitives.WriteUInt32LittleEndian(ramdisk.AsSpan(8), VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(ramdisk.AsSpan(12), HEADER_BYTES);
        BinaryPrimitives.WriteInt64LittleEndian(ramdisk.AsSpan(16), rootOffset);
        BinaryPrimitives.WriteInt64LittleEndian(ramdisk.AsSpan(24), rootTask.Length);
        BinaryPrimitives.WriteInt64LittleEndian(ramdisk.AsSpan(32), packageOffset);
        BinaryPrimitives.WriteInt64LittleEndian(ramdisk.AsSpan(40), package.Length);
        rootTask.CopyTo(ramdisk, rootOffset);
        package.CopyTo(ramdisk, packageOffset);
        return ramdisk;
    }

    private static long Align(long value) => (value + HEADER_BYTES - 1) & ~(long)(HEADER_BYTES - 1);

    #endregion
}
