using System;
using System.Buffers.Binary;
using System.Text;

namespace OutWit.OS.Namespaces;

/// <summary>
/// A volume of the namespace's catalog (plan step E1, ADR 0032; witos/namespace.h's WitNamespaceVolume): what VOLUMES
/// and STATFS write into the window.
/// </summary>
/// <param name="Flags">NamespaceMessages.VOLUME_*.</param>
/// <param name="Origin">NamespaceMessages.ORIGIN_*.</param>
/// <param name="TotalBytes">The bytes its files may take, with VOLUME_SPACE alone.</param>
/// <param name="FreeBytes">The bytes still free, with VOLUME_SPACE alone.</param>
/// <param name="AllocationUnit">The bytes it allocates at a time.</param>
/// <param name="Index">Its index in the mount list, which a stat's Volume names.</param>
/// <param name="Alias">Its unique name: Boot, Data, Fat, Tmp, or its name under /media.</param>
/// <param name="Point">Its mount point.</param>
/// <param name="Format">Its format.</param>
/// <param name="Identity">Its identity, empty for none.</param>
/// <param name="Label">Its label, empty for none.</param>
public sealed record NamespaceVolume(uint Flags, uint Origin, long TotalBytes, long FreeBytes, int AllocationUnit, uint Index,
    string Alias, string Point, string Format, string Identity, string Label)
{
    #region Constants

    /// <summary>The bytes of a record.</summary>
    public const int SIZE = 256;

    private const int ALIAS_AT = 40;
    private const int ALIAS_BYTES = 32;
    private const int POINT_AT = ALIAS_AT + ALIAS_BYTES;
    private const int POINT_BYTES = 64;
    private const int FORMAT_AT = POINT_AT + POINT_BYTES;
    private const int FORMAT_BYTES = 16;
    private const int IDENTITY_AT = FORMAT_AT + FORMAT_BYTES;
    private const int IDENTITY_BYTES = 40;
    private const int LABEL_AT = IDENTITY_AT + IDENTITY_BYTES;
    private const int LABEL_BYTES = 64;

    #endregion

    #region Functions

    /// <summary>
    /// Writes the record into a window.
    /// </summary>
    /// <param name="window">At least SIZE bytes.</param>
    /// <exception cref="ArgumentException">The window is too small, or a text does not fit its field.</exception>
    public void Write(Span<byte> window)
    {
        if (window.Length < SIZE)
            throw new ArgumentException($"A volume's record takes {SIZE} bytes.", nameof(window));
        var bytes = window[..SIZE];
        bytes.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, NamespaceMessages.VERSION);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], SIZE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], Origin);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], TotalBytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[24..], FreeBytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[32..], AllocationUnit);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[36..], Index);
        Text(bytes.Slice(ALIAS_AT, ALIAS_BYTES), Alias, nameof(Alias));
        Text(bytes.Slice(POINT_AT, POINT_BYTES), Point, nameof(Point));
        Text(bytes.Slice(FORMAT_AT, FORMAT_BYTES), Format, nameof(Format));
        Text(bytes.Slice(IDENTITY_AT, IDENTITY_BYTES), Identity, nameof(Identity));
        // A label longer than its field is cut at a character, never refused: a person names a volume.
        var label = Encoding.UTF8.GetBytes(Label);
        var length = Math.Min(label.Length, LABEL_BYTES - 1);
        while (length > 0 && length < label.Length && (label[length] & 0xC0) == 0x80)
            length--;
        label.AsSpan(0, length).CopyTo(bytes[LABEL_AT..]);
    }

    /// <summary>
    /// Reads a record.
    /// </summary>
    /// <param name="bytes">SIZE bytes.</param>
    /// <returns>The volume.</returns>
    /// <exception cref="FormatException">The bytes are no record of this version.</exception>
    public static NamespaceVolume Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < SIZE || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != NamespaceMessages.VERSION ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != SIZE)
            throw new FormatException($"A namespace volume of {bytes.Length} bytes is not of version {NamespaceMessages.VERSION}.");
        return new NamespaceVolume(BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]),
            Text(bytes.Slice(ALIAS_AT, ALIAS_BYTES)), Text(bytes.Slice(POINT_AT, POINT_BYTES)), Text(bytes.Slice(FORMAT_AT, FORMAT_BYTES)),
            Text(bytes.Slice(IDENTITY_AT, IDENTITY_BYTES)), Text(bytes.Slice(LABEL_AT, LABEL_BYTES)));
    }

    private static void Text(Span<byte> field, string text, string what)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length >= field.Length)
            throw new ArgumentException($"The volume's {what} '{text}' does not fit {field.Length - 1} bytes.", what);
        bytes.CopyTo(field);
    }

    private static string Text(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
    }

    #endregion
}
