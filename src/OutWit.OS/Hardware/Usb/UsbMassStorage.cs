using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace OutWit.OS.Hardware.Usb;

/// <summary>
/// The result of one bulk transfer: done (perhaps short), stalled by the device, or failed — a transaction error or a
/// device gone, after which only a reset helps.
/// </summary>
public enum BulkResult
{
    /// <summary>The transfer completed, perhaps short.</summary>
    Done,

    /// <summary>The device stalled the endpoint.</summary>
    Stalled,

    /// <summary>The transfer failed otherwise.</summary>
    Failed
}

/// <summary>
/// The bulk pipes of a USB mass storage interface as Bulk-Only Transport uses them (USB MSC BOT 1.0; plan step E3): the
/// command block out and the status block in through the pipes' own buffer, the data phase through physical segments the
/// device reaches or through that buffer for a small answer, and the recovery requests. The xHCI driver serves them
/// over its bulk endpoints; the host tests over a device they simulate.
/// </summary>
public interface IBulkOnlyPipes
{
    /// <summary>
    /// Sends bytes to the bulk OUT endpoint through the pipes' buffer: a command block, or a small data phase.
    /// </summary>
    /// <param name="bytes">The bytes, at most a page.</param>
    /// <param name="transferred">The bytes the device took.</param>
    /// <returns>The transfer's result.</returns>
    BulkResult SendBytes(ReadOnlySpan<byte> bytes, out int transferred);

    /// <summary>
    /// Receives bytes from the bulk IN endpoint through the pipes' buffer: a status block, or a small data phase.
    /// </summary>
    /// <param name="bytes">Where the bytes go, at most a page.</param>
    /// <param name="received">The bytes the device sent.</param>
    /// <returns>The transfer's result.</returns>
    BulkResult ReceiveBytes(Span<byte> bytes, out int received);

    /// <summary>
    /// A data phase through physical segments, in one transfer.
    /// </summary>
    /// <param name="deviceToHost">Whether the data goes from the device (IN) or to it (OUT).</param>
    /// <param name="segments">The physical segments.</param>
    /// <param name="transferred">The bytes moved.</param>
    /// <returns>The transfer's result.</returns>
    BulkResult Transfer(bool deviceToHost, ReadOnlySpan<(ulong Address, int Length)> segments, out int transferred);

    /// <summary>
    /// Clears an endpoint's halt (CLEAR_FEATURE(ENDPOINT_HALT) and the controller's endpoint reset).
    /// </summary>
    /// <param name="deviceToHost">The IN endpoint, or the OUT one.</param>
    void ClearHalt(bool deviceToHost);

    /// <summary>
    /// The Bulk-Only Mass Storage Reset class request to the interface.
    /// </summary>
    void Reset();
}

/// <summary>
/// A drive's bulk pipes through a host controller (E3): the command, status and small data blocks through the bulk
/// endpoints' own buffers, the data phase straight through the client's physical segments, a halt cleared on the
/// controller's side and then at the device by CLEAR_FEATURE(ENDPOINT_HALT), the reset as the interface's class request.
/// </summary>
/// <param name="host">The controller.</param>
/// <param name="storage">The drive as the bus configured it.</param>
public sealed class UsbBulkPipes(IUsbHost host, UsbStorage storage) : IBulkOnlyPipes
{
    #region Functions

    /// <inheritdoc />
    public BulkResult SendBytes(ReadOnlySpan<byte> bytes, out int transferred)
    {
        var buffer = host.BulkBuffer(storage.Slot, storage.OutIndex, out var physical);
        if (bytes.Length > buffer.Length)
            throw new ArgumentException($"{bytes.Length} bytes; the endpoint's buffer holds {buffer.Length}.", nameof(bytes));
        bytes.CopyTo(buffer);
        return host.Bulk(storage.Slot, storage.OutIndex, [(physical, bytes.Length)], out transferred);
    }

    /// <inheritdoc />
    public BulkResult ReceiveBytes(Span<byte> bytes, out int received)
    {
        var buffer = host.BulkBuffer(storage.Slot, storage.InIndex, out var physical);
        if (bytes.Length > buffer.Length)
            throw new ArgumentException($"{bytes.Length} bytes; the endpoint's buffer holds {buffer.Length}.", nameof(bytes));
        var result = host.Bulk(storage.Slot, storage.InIndex, [(physical, bytes.Length)], out received);
        buffer[..Math.Min(received, bytes.Length)].CopyTo(bytes);
        return result;
    }

    /// <inheritdoc />
    public BulkResult Transfer(bool deviceToHost, ReadOnlySpan<(ulong Address, int Length)> segments, out int transferred) =>
        host.Bulk(storage.Slot, deviceToHost ? storage.InIndex : storage.OutIndex, segments, out transferred);

    /// <inheritdoc />
    public void ClearHalt(bool deviceToHost)
    {
        host.RecoverEndpoint(storage.Slot, deviceToHost ? storage.InIndex : storage.OutIndex);
        host.Control(storage.Slot, UsbSetup.ClearEndpointHalt((deviceToHost ? storage.Input : storage.Output).Address));
    }

    /// <inheritdoc />
    public void Reset() => host.Control(storage.Slot, UsbSetup.BulkOnlyReset(storage.Interface));

    #endregion
}

/// <summary>
/// Bulk-Only Transport (USB MSC BOT 1.0): each SCSI command as a command block wrapper, its data phase and its command
/// status wrapper, checked by signature, tag and length; a stalled data phase cleared and the status read, a stalled
/// status read once more, and every phase error or wrapper out of order answered by the reset recovery of BOT 5.3.4
/// before the command fails.
/// </summary>
public sealed class BulkOnlyTransport
{
    #region Constants

    /// <summary>
    /// A command block wrapper's bytes.
    /// </summary>
    public const int COMMAND_BYTES = 31;

    /// <summary>
    /// A command status wrapper's bytes.
    /// </summary>
    public const int STATUS_BYTES = 13;

    private const uint COMMAND_SIGNATURE = 0x43425355; // "USBC"
    private const uint STATUS_SIGNATURE = 0x53425355; // "USBS"
    private const byte DIRECTION_IN = 0x80;

    #endregion

    #region Fields

    private readonly IBulkOnlyPipes m_pipes;
    private readonly byte m_lun;
    private uint m_tag;

    #endregion

    #region Constructors

    /// <summary>
    /// The transport of one logical unit.
    /// </summary>
    /// <param name="pipes">The interface's pipes.</param>
    /// <param name="lun">The logical unit.</param>
    public BulkOnlyTransport(IBulkOnlyPipes pipes, byte lun = 0)
    {
        m_pipes = pipes;
        m_lun = lun;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Executes a command whose data phase moves through physical segments.
    /// </summary>
    /// <param name="command">The command descriptor block, 6 to 16 bytes.</param>
    /// <param name="deviceToHost">Whether the data phase reads from the device.</param>
    /// <param name="segments">The data phase's segments; none for a command without data.</param>
    /// <returns>The command's status and the residue the device reported.</returns>
    /// <exception cref="IOException">A transport failure, after the reset recovery.</exception>
    public (ScsiStatus Status, int Residue) Execute(ReadOnlySpan<byte> command, bool deviceToHost, ReadOnlySpan<(ulong Address, int Length)> segments)
    {
        var length = 0;
        foreach (var (_, bytes) in segments)
            length += bytes;
        var tag = SendCommand(command, deviceToHost, length);
        if (length > 0)
        {
            var result = m_pipes.Transfer(deviceToHost, segments, out _);
            DataPhaseEnded(result, deviceToHost);
        }
        return ReadStatus(tag);
    }

    /// <summary>
    /// Executes a command whose data phase, at most a page, moves through the pipes' buffer: INQUIRY, READ CAPACITY,
    /// REQUEST SENSE, MODE SENSE.
    /// </summary>
    /// <param name="command">The command descriptor block.</param>
    /// <param name="answer">Where an IN data phase goes; empty for no data phase.</param>
    /// <param name="received">The bytes the device sent.</param>
    /// <returns>The command's status and its residue.</returns>
    /// <exception cref="IOException">A transport failure, after the reset recovery.</exception>
    public (ScsiStatus Status, int Residue) Execute(ReadOnlySpan<byte> command, Span<byte> answer, out int received)
    {
        received = 0;
        var tag = SendCommand(command, deviceToHost: true, answer.Length);
        if (!answer.IsEmpty)
        {
            var result = m_pipes.ReceiveBytes(answer, out received);
            DataPhaseEnded(result, deviceToHost: true);
        }
        return ReadStatus(tag);
    }

    /// <summary>
    /// The reset recovery of BOT 5.3.4: the class reset, then each endpoint's halt cleared.
    /// </summary>
    public void ResetRecovery()
    {
        m_pipes.Reset();
        m_pipes.ClearHalt(deviceToHost: true);
        m_pipes.ClearHalt(deviceToHost: false);
    }

    private uint SendCommand(ReadOnlySpan<byte> command, bool deviceToHost, int length)
    {
        if (command.Length is < 1 or > 16)
            throw new ArgumentException("A command descriptor block holds 1 to 16 bytes.", nameof(command));
        Span<byte> block = stackalloc byte[COMMAND_BYTES];
        block.Clear();
        var tag = ++m_tag;
        BinaryPrimitives.WriteUInt32LittleEndian(block, COMMAND_SIGNATURE);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], tag);
        BinaryPrimitives.WriteUInt32LittleEndian(block[8..], (uint)length);
        block[12] = length > 0 && deviceToHost ? DIRECTION_IN : (byte)0;
        block[13] = m_lun;
        block[14] = (byte)command.Length;
        command.CopyTo(block[15..]);
        if (m_pipes.SendBytes(block, out var sent) != BulkResult.Done || sent != COMMAND_BYTES)
            Fail($"the device refused command 0x{command[0]:X2}'s block");
        return tag;
    }

    // A data phase the device stalled ends there: its halt cleared, the status follows (BOT 6.7.2, 6.7.3).
    private void DataPhaseEnded(BulkResult result, bool deviceToHost)
    {
        if (result == BulkResult.Stalled)
            m_pipes.ClearHalt(deviceToHost);
        else if (result == BulkResult.Failed)
            Fail("the data phase failed");
    }

    private (ScsiStatus Status, int Residue) ReadStatus(uint tag)
    {
        Span<byte> block = stackalloc byte[STATUS_BYTES];
        var result = m_pipes.ReceiveBytes(block, out var received);
        if (result == BulkResult.Stalled)
        {
            // BOT 6.7.2: a stalled status is cleared and read once more.
            m_pipes.ClearHalt(deviceToHost: true);
            result = m_pipes.ReceiveBytes(block, out received);
        }
        if (result != BulkResult.Done || received != STATUS_BYTES || BinaryPrimitives.ReadUInt32LittleEndian(block) != STATUS_SIGNATURE ||
            BinaryPrimitives.ReadUInt32LittleEndian(block[4..]) != tag)
            Fail($"the command status wrapper is {(result != BulkResult.Done ? result.ToString().ToLowerInvariant() : "not valid")}");
        var status = block[12];
        if (status == 2)
            Fail("the device reported a phase error");
        if (status > 2)
            Fail($"the device reported status {status}");
        return (status == 0 ? ScsiStatus.Good : ScsiStatus.CheckCondition, (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(block[8..]), int.MaxValue));
    }

    // A transport failure: the reset recovery, then the command fails.
    private void Fail(string what)
    {
        try
        {
            ResetRecovery();
        }
        catch (IOException)
        {
            // A device that does not answer the reset either has gone; the command fails all the same.
        }
        throw new IOException($"USB mass storage: {what}.");
    }

    #endregion
}

/// <summary>
/// A command's status as Bulk-Only Transport reports it.
/// </summary>
public enum ScsiStatus
{
    /// <summary>The command passed.</summary>
    Good,

    /// <summary>The command failed; REQUEST SENSE says why.</summary>
    CheckCondition
}

/// <summary>
/// A USB drive as a disk (plan step E3): a direct-access SCSI device behind Bulk-Only Transport, opened by INQUIRY, TEST
/// UNIT READY until its unit attention passes, READ CAPACITY (10, or 16 beyond 2 TiB) and MODE SENSE's write protect,
/// then read and written by READ and WRITE (10, or 16 beyond 2^32 blocks or 65535 blocks a command) straight from and to
/// the client's physical segments, flushed by SYNCHRONIZE CACHE; a failed command's REQUEST SENSE names it in the
/// error.
/// </summary>
public sealed class ScsiDisk : IBlockDevice
{
    #region Constants

    /// <summary>The USB class of mass storage.</summary>
    public const byte CLASS_MASS_STORAGE = 8;

    /// <summary>The subclass of SCSI's transparent command set.</summary>
    public const byte SUBCLASS_SCSI = 6;

    /// <summary>The protocol of Bulk-Only Transport.</summary>
    public const byte PROTOCOL_BULK_ONLY = 0x50;

    private const byte TEST_UNIT_READY = 0x00;
    private const byte REQUEST_SENSE = 0x03;
    private const byte INQUIRY = 0x12;
    private const byte MODE_SENSE_6 = 0x1A;
    private const byte READ_CAPACITY_10 = 0x25;
    private const byte READ_10 = 0x28;
    private const byte WRITE_10 = 0x2A;
    private const byte SYNCHRONIZE_CACHE_10 = 0x35;
    private const byte READ_16 = 0x88;
    private const byte WRITE_16 = 0x8A;
    private const byte SERVICE_ACTION_IN_16 = 0x9E;
    private const byte READ_CAPACITY_16 = 0x10;

    private const int SENSE_NOT_READY = 2;
    private const int SENSE_ILLEGAL_REQUEST = 5;
    private const int SENSE_UNIT_ATTENTION = 6;
    private const byte ASC_MEDIUM_NOT_PRESENT = 0x3A;

    // A unit's attention after a reset or a medium change passes within a few commands; a medium spinning up within more.
    private const int READY_TRIES = 20;

    private const int READY_DELAY_MILLISECONDS = 100;

    #endregion

    #region Fields

    private readonly BulkOnlyTransport m_transport;
    private readonly Action<int> m_delay;

    #endregion

    #region Constructors

    private ScsiDisk(BulkOnlyTransport transport, Action<int> delay, int maxSegments, long maxSegmentBytes, long maxTransferBytes)
    {
        m_transport = transport;
        m_delay = delay;
        MaxSegments = maxSegments;
        MaxSegmentBytes = maxSegmentBytes;
        MaxTransferBytes = maxTransferBytes;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Opens the logical unit: what it is, its readiness, its size and block, whether it is write-protected.
    /// </summary>
    /// <param name="transport">The unit's transport.</param>
    /// <param name="delay">Waits a number of milliseconds, between the tries of a unit not yet ready.</param>
    /// <param name="maxSegments">The physical segments one data phase may carry.</param>
    /// <param name="maxSegmentBytes">The bytes of one segment.</param>
    /// <param name="maxTransferBytes">The bytes of one command's data phase.</param>
    /// <returns>The disk.</returns>
    /// <exception cref="IOException">The unit is no direct-access device, has no medium, or failed a command.</exception>
    public static ScsiDisk Open(BulkOnlyTransport transport, Action<int> delay, int maxSegments, long maxSegmentBytes, long maxTransferBytes)
    {
        var disk = new ScsiDisk(transport, delay, maxSegments, maxSegmentBytes, maxTransferBytes);
        disk.Inquire();
        disk.WaitReady();
        disk.ReadCapacity();
        disk.ReadProtection();
        return disk;
    }

    /// <inheritdoc />
    public void Read(long offset, ReadOnlySpan<(ulong Address, int Length)> segments) => Move(offset, segments, write: false);

    /// <inheritdoc />
    public void Write(long offset, ReadOnlySpan<(ulong Address, int Length)> segments)
    {
        if (IsReadOnly)
            throw new IOException("USB mass storage: the medium is write-protected.");
        Move(offset, segments, write: true);
    }

    /// <inheritdoc />
    public void Flush()
    {
        Span<byte> command = stackalloc byte[10];
        command.Clear();
        command[0] = SYNCHRONIZE_CACHE_10;
        var (status, _) = m_transport.Execute(command, deviceToHost: false, []);
        if (status == ScsiStatus.Good)
            return;
        var (key, asc, ascq) = Sense();
        if (key == SENSE_ILLEGAL_REQUEST)
            return; // a drive without a cache to synchronize: what it took is on its medium
        throw Failure("SYNCHRONIZE CACHE", key, asc, ascq);
    }

    // READ or WRITE of the segments' blocks in one command: (10) while the blocks fit its fields, (16) beyond.
    private void Move(long offset, ReadOnlySpan<(ulong Address, int Length)> segments, bool write)
    {
        long bytes = 0;
        foreach (var (_, segment) in segments)
            bytes += segment;
        if (offset % BlockSize != 0 || bytes % BlockSize != 0 || offset < 0 || offset + bytes > Capacity)
            throw new ArgumentException($"A request of {bytes} bytes at {offset} is not of the disk's blocks.");
        if (bytes == 0)
            return;
        var block = offset / BlockSize;
        var count = bytes / BlockSize;
        Span<byte> command = stackalloc byte[16];
        command.Clear();
        int length;
        if (block + count <= uint.MaxValue && count <= ushort.MaxValue)
        {
            command[0] = write ? WRITE_10 : READ_10;
            BinaryPrimitives.WriteUInt32BigEndian(command[2..], (uint)block);
            BinaryPrimitives.WriteUInt16BigEndian(command[7..], (ushort)count);
            length = 10;
        }
        else
        {
            command[0] = write ? WRITE_16 : READ_16;
            BinaryPrimitives.WriteUInt64BigEndian(command[2..], (ulong)block);
            BinaryPrimitives.WriteUInt32BigEndian(command[10..], (uint)count);
            length = 16;
        }
        var (status, residue) = m_transport.Execute(command[..length], deviceToHost: !write, segments);
        var name = write ? "WRITE" : "READ";
        if (status != ScsiStatus.Good)
        {
            var (key, asc, ascq) = Sense();
            throw Failure($"{name} of {count} blocks at {block}", key, asc, ascq);
        }
        if (residue != 0)
            throw new IOException($"USB mass storage: {name} of {count} blocks at {block} left {residue} bytes unmoved.");
    }

    private void Inquire()
    {
        Span<byte> command = [INQUIRY, 0, 0, 0, 36, 0];
        Span<byte> answer = stackalloc byte[36];
        answer.Clear();
        var (status, _) = m_transport.Execute(command, answer, out var received);
        if (status != ScsiStatus.Good || received < 5)
            throw new IOException("USB mass storage: the unit failed INQUIRY.");
        if ((answer[0] & 0x1F) != 0 || (answer[0] & 0xE0) != 0)
            throw new IOException($"USB mass storage: the unit is no direct-access device (peripheral 0x{answer[0]:X2}).");
        IsRemovable = (answer[1] & 0x80) != 0;
        Vendor = Text(answer.Slice(8, 8), received - 8);
        Product = Text(answer.Slice(16, 16), received - 16);
        Revision = Text(answer.Slice(32, 4), received - 32);
    }

    // TEST UNIT READY until it passes: a unit attention (a reset, a medium changed) is asked again at once, a unit becoming
    // ready after a pause; a medium absent fails.
    private void WaitReady()
    {
        Span<byte> command = stackalloc byte[6];
        command.Clear();
        for (var attempt = 1; ; attempt++)
        {
            var (status, _) = m_transport.Execute(command, deviceToHost: false, []);
            if (status == ScsiStatus.Good)
                return;
            var (key, asc, ascq) = Sense();
            if (key == SENSE_NOT_READY && asc == ASC_MEDIUM_NOT_PRESENT)
                throw new IOException("USB mass storage: no medium is present.");
            if (attempt == READY_TRIES)
                throw Failure("TEST UNIT READY", key, asc, ascq);
            if (key != SENSE_UNIT_ATTENTION)
                m_delay(READY_DELAY_MILLISECONDS);
        }
    }

    private void ReadCapacity()
    {
        Span<byte> command = stackalloc byte[10];
        command.Clear();
        command[0] = READ_CAPACITY_10;
        Span<byte> answer = stackalloc byte[8];
        var (status, _) = m_transport.Execute(command, answer, out var received);
        if (status != ScsiStatus.Good || received < 8)
        {
            var (key, asc, ascq) = Sense();
            throw Failure("READ CAPACITY", key, asc, ascq);
        }
        var last = (long)BinaryPrimitives.ReadUInt32BigEndian(answer);
        var block = BinaryPrimitives.ReadUInt32BigEndian(answer[4..]);
        if (last == uint.MaxValue)
        {
            // Beyond 2 TiB of 512-byte blocks: READ CAPACITY (16), SERVICE ACTION IN's.
            Span<byte> large = stackalloc byte[16];
            large.Clear();
            large[0] = SERVICE_ACTION_IN_16;
            large[1] = READ_CAPACITY_16;
            BinaryPrimitives.WriteUInt32BigEndian(large[10..], 32);
            Span<byte> answer16 = stackalloc byte[32];
            (status, _) = m_transport.Execute(large, answer16, out received);
            if (status != ScsiStatus.Good || received < 12)
            {
                var (key, asc, ascq) = Sense();
                throw Failure("READ CAPACITY (16)", key, asc, ascq);
            }
            last = (long)BinaryPrimitives.ReadUInt64BigEndian(answer16);
            block = BinaryPrimitives.ReadUInt32BigEndian(answer16[8..]);
        }
        if (block is < 512 or > 65536 || (block & (block - 1)) != 0)
            throw new IOException($"USB mass storage: the unit reports a block of {block} bytes.");
        BlockSize = (int)block;
        Capacity = (last + 1) * block;
    }

    // MODE SENSE (6) of every page, whose header's device-specific byte holds the write protect; a unit that refuses it is
    // taken as writable, as Linux's sd takes it.
    private void ReadProtection()
    {
        Span<byte> command = [MODE_SENSE_6, 0, 0x3F, 0, 192, 0];
        Span<byte> answer = stackalloc byte[192];
        var (status, _) = m_transport.Execute(command, answer, out var received);
        if (status != ScsiStatus.Good)
        {
            Sense();
            return;
        }
        IsReadOnly = received >= 4 && (answer[2] & 0x80) != 0;
    }

    // REQUEST SENSE's fixed format: the key, the additional code and its qualifier.
    private (int Key, byte Asc, byte Ascq) Sense()
    {
        Span<byte> command = [REQUEST_SENSE, 0, 0, 0, 18, 0];
        Span<byte> answer = stackalloc byte[18];
        answer.Clear();
        var (status, _) = m_transport.Execute(command, answer, out var received);
        if (status != ScsiStatus.Good || received < 14)
            return (0, 0, 0);
        return (answer[2] & 0x0F, answer[12], answer[13]);
    }

    private static IOException Failure(string what, int key, byte asc, byte ascq) =>
        new($"USB mass storage: {what} failed: sense key {key:X}, additional sense 0x{asc:X2}/0x{ascq:X2}{key switch
        {
            SENSE_NOT_READY => " (not ready)",
            3 => " (medium error)",
            4 => " (hardware error)",
            SENSE_ILLEGAL_REQUEST => " (illegal request)",
            SENSE_UNIT_ATTENTION => " (unit attention)",
            7 => " (data protect)",
            _ => ""
        }}.");

    private static string Text(ReadOnlySpan<byte> field, int available) =>
        available <= 0 ? "" : Encoding.ASCII.GetString(field[..Math.Min(field.Length, available)]).Trim();

    #endregion

    #region Properties

    /// <inheritdoc />
    public long Capacity { get; private set; }

    /// <inheritdoc />
    public int BlockSize { get; private set; }

    /// <inheritdoc />
    public bool IsReadOnly { get; private set; }

    /// <inheritdoc />
    public bool Flushes => true;

    /// <inheritdoc />
    public int MaxSegments { get; }

    /// <inheritdoc />
    public long MaxSegmentBytes { get; }

    /// <inheritdoc />
    public long MaxTransferBytes { get; }

    /// <summary>
    /// Whether INQUIRY reports a removable medium.
    /// </summary>
    public bool IsRemovable { get; private set; }

    /// <summary>
    /// The vendor INQUIRY names.
    /// </summary>
    public string Vendor { get; private set; } = "";

    /// <summary>
    /// The product INQUIRY names.
    /// </summary>
    public string Product { get; private set; } = "";

    /// <summary>
    /// The product's revision.
    /// </summary>
    public string Revision { get; private set; } = "";

    #endregion
}
