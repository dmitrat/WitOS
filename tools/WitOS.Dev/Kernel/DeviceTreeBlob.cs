using System.Buffers.Binary;
using System.Text;

namespace WitOS.Dev.Kernel;

/// <summary>
/// A flattened device tree as the host changes it before a board takes it (plan step H7.2): QEMU's raspi4b hands the
/// Linux arm64 entry the tree it was given, with no entropy seed in /chosen, where its virt board writes one; the runner
/// adds the seed. The blob is read whole (header, memory reservations, structure, strings, version 17) and written anew
/// with the structure after the reservations and the strings after the structure.
/// </summary>
internal static class DeviceTreeBlob
{
    #region Constants

    private const uint MAGIC = 0xD00DFEED;
    private const uint BEGIN_NODE = 1;
    private const uint END_NODE = 2;
    private const uint PROP = 3;
    private const uint NOP = 4;
    private const uint END = 9;
    private const int HEADER_BYTES = 40;
    private const uint VERSION = 17;
    private const uint LAST_COMPATIBLE_VERSION = 16;

    #endregion

    #region Functions

    /// <summary>
    /// Requires a blob this class reads: the magic, version 17 or later, and its blocks within its total size.
    /// </summary>
    /// <param name="blob">The blob.</param>
    /// <exception cref="InvalidDataException">The blob is not one.</exception>
    public static void Require(byte[] blob) => _ = Read(blob);

    /// <summary>
    /// The blob with a property of /chosen set: replaced where the node holds it, added before its other properties
    /// otherwise, and the node added to the root where the tree has none.
    /// </summary>
    /// <param name="blob">The tree.</param>
    /// <param name="name">The property's name.</param>
    /// <param name="value">Its value.</param>
    /// <returns>The new tree.</returns>
    /// <exception cref="InvalidDataException">The blob is not one, or its structure is malformed.</exception>
    public static byte[] WithChosenProperty(byte[] blob, string name, byte[] value)
    {
        var (reservations, structure, strings, bootProcessor) = Read(blob);
        var nameOffset = StringOffset(ref strings, name);
        var property = Property(nameOffset, value);
        var output = new List<byte>(structure.Length + property.Length + 16);
        var offset = 0;
        var depth = 0;
        var inChosen = false;
        var chosenFound = false;
        while (true)
        {
            var token = Word(structure, offset);
            var start = offset;
            offset += 4;
            switch (token)
            {
                case BEGIN_NODE:
                    {
                        var end = Array.IndexOf(structure, (byte)0, offset);
                        if (end < 0)
                            throw new InvalidDataException("Unterminated node name.");
                        var nodeName = Encoding.ASCII.GetString(structure, offset, end - offset);
                        offset = Align(end + 1);
                        ++depth;
                        output.AddRange(structure.AsSpan(start, offset - start).ToArray());
                        if (depth == 2 && nodeName == "chosen")
                        {
                            inChosen = chosenFound = true;
                            output.AddRange(property);
                        }
                        else
                        {
                            inChosen = false;
                        }
                        break;
                    }
                case END_NODE:
                    if (depth == 1 && !chosenFound)
                    {
                        output.AddRange(Token(BEGIN_NODE));
                        output.AddRange(Padded(Encoding.ASCII.GetBytes("chosen\0")));
                        output.AddRange(property);
                        output.AddRange(Token(END_NODE));
                    }
                    inChosen = false;
                    --depth;
                    output.AddRange(structure.AsSpan(start, 4).ToArray());
                    break;
                case PROP:
                    {
                        var length = (int)Word(structure, offset);
                        var propertyName = Word(structure, offset + 4);
                        offset = Align(offset + 8 + length);
                        // The property this call sets is dropped where the node held it; the new one leads the node.
                        if (!(inChosen && depth == 2 && propertyName == nameOffset))
                            output.AddRange(structure.AsSpan(start, offset - start).ToArray());
                        break;
                    }
                case NOP:
                    break;
                case END:
                    output.AddRange(Token(END));
                    return Write(reservations, output.ToArray(), strings, bootProcessor);
                default:
                    throw new InvalidDataException($"Unknown structure token {token} at {start}.");
            }
            if (depth < 0 || offset > structure.Length)
                throw new InvalidDataException("Malformed structure block.");
        }
    }

    // The header's blocks: the memory reservations with their terminating entry, the structure and the strings.
    private static (byte[] Reservations, byte[] Structure, byte[] Strings, uint BootProcessor) Read(byte[] blob)
    {
        if (blob.Length < HEADER_BYTES || Word(blob, 0) != MAGIC)
            throw new InvalidDataException("Not a flattened device tree.");
        var total = Word(blob, 4);
        var structureOffset = Word(blob, 8);
        var stringsOffset = Word(blob, 12);
        var reservationsOffset = Word(blob, 16);
        var version = Word(blob, 20);
        var stringsSize = Word(blob, 32);
        var structureSize = Word(blob, 36);
        if (version < VERSION || total > blob.Length || structureOffset + (ulong)structureSize > total ||
            stringsOffset + (ulong)stringsSize > total || reservationsOffset % 8 != 0 || reservationsOffset >= total)
            throw new InvalidDataException("Unsupported device tree header.");
        var end = (int)reservationsOffset;
        while (true)
        {
            if (end + 16 > total)
                throw new InvalidDataException("Unterminated memory reservation block.");
            var entry = BinaryPrimitives.ReadUInt64BigEndian(blob.AsSpan(end)) | BinaryPrimitives.ReadUInt64BigEndian(blob.AsSpan(end + 8));
            end += 16;
            if (entry == 0)
                break;
        }
        return (blob[(int)reservationsOffset..end], blob[(int)structureOffset..(int)(structureOffset + structureSize)],
            blob[(int)stringsOffset..(int)(stringsOffset + stringsSize)], Word(blob, 28));
    }

    private static byte[] Write(byte[] reservations, byte[] structure, byte[] strings, uint bootProcessor)
    {
        var reservationsOffset = HEADER_BYTES;
        var structureOffset = reservationsOffset + reservations.Length;
        var stringsOffset = structureOffset + structure.Length;
        var total = stringsOffset + strings.Length;
        var blob = new byte[total];
        uint[] header =
        [
            MAGIC, (uint)total, (uint)structureOffset, (uint)stringsOffset, (uint)reservationsOffset, VERSION,
            LAST_COMPATIBLE_VERSION, bootProcessor, (uint)strings.Length, (uint)structure.Length
        ];
        for (var i = 0; i < header.Length; ++i)
            BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(i * 4), header[i]);
        reservations.CopyTo(blob, reservationsOffset);
        structure.CopyTo(blob, structureOffset);
        strings.CopyTo(blob, stringsOffset);
        return blob;
    }

    // The offset of a name that starts a string of the block, appended where the block has none.
    private static uint StringOffset(ref byte[] strings, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        for (var at = 0; at < strings.Length; at = Array.IndexOf(strings, (byte)0, at) + 1)
        {
            if (at + bytes.Length <= strings.Length && strings.AsSpan(at, bytes.Length).SequenceEqual(bytes))
                return (uint)at;
            if (Array.IndexOf(strings, (byte)0, at) < 0)
                break;
        }
        var offset = (uint)strings.Length;
        strings = [.. strings, .. bytes];
        return offset;
    }

    private static byte[] Property(uint nameOffset, byte[] value)
    {
        var bytes = new byte[12 + Align(value.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, PROP);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)value.Length);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), nameOffset);
        value.CopyTo(bytes, 12);
        return bytes;
    }

    private static byte[] Token(uint token)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, token);
        return bytes;
    }

    private static byte[] Padded(byte[] bytes) => [.. bytes, .. new byte[Align(bytes.Length) - bytes.Length]];

    private static uint Word(byte[] data, int offset) =>
        offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)) : throw new InvalidDataException("Truncated device tree.");

    private static int Align(int value) => (value + 3) & ~3;

    #endregion
}
