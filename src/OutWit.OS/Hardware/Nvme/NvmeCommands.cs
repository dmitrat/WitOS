using System;
using System.Buffers.Binary;

namespace OutWit.OS.Hardware.Nvme;

/// <summary>
/// What an NVMe controller's queues hold (NVM Express Base 1.4 §4.2, §4.6, plan step H3.2): a submission queue entry of
/// 64 bytes — the opcode and the command's identifier, the namespace, the data pointer as PRP entries and the command's
/// own dwords — and a completion queue entry of 16 bytes with its phase tag. A transfer's data are Physical Region Page
/// entries: the first with any offset within its page, every other page-aligned, and a list of pages where more than two
/// pages are reached.
/// </summary>
public static class NvmeCommands
{
    #region Constants

    /// <summary>
    /// Bytes of a submission queue entry.
    /// </summary>
    public const int SUBMISSION_BYTES = 64;

    /// <summary>
    /// Bytes of a completion queue entry.
    /// </summary>
    public const int COMPLETION_BYTES = 16;

    /// <summary>
    /// The memory page the driver gives the controller (CC.MPS of 0): what every PRP entry but the first is aligned to.
    /// </summary>
    public const int PAGE = 4096;

    // Admin commands.
    /// <summary>
    /// Create I/O Submission Queue.
    /// </summary>
    public const byte CREATE_SUBMISSION_QUEUE = 0x01;

    /// <summary>
    /// Create I/O Completion Queue.
    /// </summary>
    public const byte CREATE_COMPLETION_QUEUE = 0x05;

    /// <summary>
    /// Identify, whose CNS names what.
    /// </summary>
    public const byte IDENTIFY = 0x06;

    // NVM commands.
    /// <summary>
    /// Flush: the volatile write cache written to the medium.
    /// </summary>
    public const byte FLUSH = 0x00;

    /// <summary>
    /// Write.
    /// </summary>
    public const byte WRITE = 0x01;

    /// <summary>
    /// Read.
    /// </summary>
    public const byte READ = 0x02;

    // Identify's CNS values.
    /// <summary>
    /// Identify Namespace.
    /// </summary>
    public const uint IDENTIFY_NAMESPACE = 0;

    /// <summary>
    /// Identify Controller.
    /// </summary>
    public const uint IDENTIFY_CONTROLLER = 1;

    /// <summary>
    /// The list of active namespaces.
    /// </summary>
    public const uint ACTIVE_NAMESPACES = 2;

    #endregion

    #region Functions

    /// <summary>
    /// Writes a submission queue entry.
    /// </summary>
    /// <param name="entry">The entry's 64 bytes.</param>
    /// <param name="opcode">The command.</param>
    /// <param name="identifier">The command's identifier, which its completion names.</param>
    /// <param name="namespaceId">The namespace, zero for none.</param>
    /// <param name="prp1">PRP entry 1.</param>
    /// <param name="prp2">PRP entry 2, or a PRP list's address.</param>
    /// <param name="dwords">Command dwords 10 to 15, as many as the command needs.</param>
    public static void WriteSubmission(Span<byte> entry, byte opcode, ushort identifier, uint namespaceId, ulong prp1, ulong prp2,
        ReadOnlySpan<uint> dwords)
    {
        if (dwords.Length > 6)
            throw new ArgumentOutOfRangeException(nameof(dwords), $"{dwords.Length} command dwords.");
        var at = entry[..SUBMISSION_BYTES];
        at.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(at, opcode | (uint)identifier << 16); // FUSE and PSDT zero: PRPs
        BinaryPrimitives.WriteUInt32LittleEndian(at[4..], namespaceId);
        BinaryPrimitives.WriteUInt64LittleEndian(at[24..], prp1);
        BinaryPrimitives.WriteUInt64LittleEndian(at[32..], prp2);
        for (var i = 0; i < dwords.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(at[(40 + i * 4)..], dwords[i]);
    }

    /// <summary>
    /// Reads a completion queue entry.
    /// </summary>
    /// <param name="entry">The entry's 16 bytes.</param>
    /// <returns>The completion.</returns>
    public static NvmeCompletion ReadCompletion(ReadOnlySpan<byte> entry)
    {
        var dw2 = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
        var dw3 = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
        return new NvmeCompletion(BinaryPrimitives.ReadUInt32LittleEndian(entry), (ushort)dw2, (ushort)(dw2 >> 16), (ushort)dw3,
            (dw3 & 1 << 16) != 0, (ushort)(dw3 >> 17));
    }

    /// <summary>
    /// The PRP entries of a transfer's physical segments: PRP 1 the first byte, PRP 2 the second page or, where the
    /// transfer reaches more than two pages, the address of a list of every page after the first.
    /// </summary>
    /// <param name="segments">The segments: the first may start anywhere in its page, every other at a page; every one but
    /// the last ends at a page.</param>
    /// <param name="list">The PRP list's entries, as many as the transfer's pages less one at most.</param>
    /// <param name="listAddress">The list's physical address.</param>
    /// <param name="prp1">PRP entry 1.</param>
    /// <param name="prp2">PRP entry 2: zero, the second page, or <paramref name="listAddress"/>.</param>
    /// <returns>The entries the list holds.</returns>
    /// <exception cref="ArgumentException">A segment breaks the PRP rules, or the list has no room.</exception>
    public static int BuildPrps(ReadOnlySpan<(ulong Address, int Length)> segments, Span<ulong> list, ulong listAddress, out ulong prp1, out ulong prp2)
    {
        if (segments.Length == 0)
            throw new ArgumentException("A transfer without a segment.", nameof(segments));
        prp1 = segments[0].Address;
        prp2 = 0;
        var pages = 0; // the pages after the first byte's
        for (var i = 0; i < segments.Length; i++)
        {
            var (address, length) = segments[i];
            if (length <= 0 || i > 0 && address % PAGE != 0 || i < segments.Length - 1 && (address + (ulong)length) % PAGE != 0)
                throw new ArgumentException($"Segment {i} of {length} bytes at {address:x} breaks the PRP rules.", nameof(segments));
            var page = i == 0 ? address - address % PAGE + PAGE : address;
            for (; page < address + (ulong)length; page += PAGE)
            {
                if (pages == list.Length)
                    throw new ArgumentException($"A transfer of more than {list.Length + 1} pages.", nameof(segments));
                list[pages++] = page;
            }
        }
        if (pages == 1)
            prp2 = list[0];
        else if (pages > 1)
            prp2 = listAddress;
        return pages > 1 ? pages : 0;
    }

    #endregion
}

/// <summary>
/// A completion queue entry.
/// </summary>
/// <param name="Result">Dword 0: the command's own result.</param>
/// <param name="SubmissionHead">The submission queue's head as the controller consumed it.</param>
/// <param name="SubmissionQueue">The submission queue of the command.</param>
/// <param name="Identifier">The command's identifier.</param>
/// <param name="Phase">The phase tag, which a new entry carries inverted from the last lap's.</param>
/// <param name="Status">The status field: the code in bits 7–0, its type in bits 10–8, More and Do Not Retry above.</param>
public readonly record struct NvmeCompletion(uint Result, ushort SubmissionHead, ushort SubmissionQueue, ushort Identifier, bool Phase, ushort Status)
{
    /// <summary>
    /// Whether the command succeeded: a status code of zero in the generic type.
    /// </summary>
    public bool Succeeded => (Status & 0x7FF) == 0;

    /// <summary>
    /// The status code.
    /// </summary>
    public int StatusCode => Status & 0xFF;

    /// <summary>
    /// The status code's type: 0 generic, 1 command specific, 2 media and data integrity.
    /// </summary>
    public int StatusType => Status >> 8 & 7;
}
