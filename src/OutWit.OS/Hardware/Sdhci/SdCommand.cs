using System;

namespace OutWit.OS.Hardware.Sdhci;

/// <summary>
/// The kind of answer an SD command expects (SD Physical Layer Specification 3.01, section 4.9): its length and whether the
/// host checks its CRC and index, and whether the card holds the data line busy after it.
/// </summary>
public enum SdResponse
{
    /// <summary>
    /// No response (CMD0).
    /// </summary>
    None,

    /// <summary>
    /// R1: 48 bits, the card's status, CRC and index checked.
    /// </summary>
    R1,

    /// <summary>
    /// R1b: R1 with the card busy on DAT0 after it.
    /// </summary>
    R1b,

    /// <summary>
    /// R2: 136 bits, the CID or the CSD, CRC checked.
    /// </summary>
    R2,

    /// <summary>
    /// R3: 48 bits, the OCR, neither CRC nor index checked.
    /// </summary>
    R3,

    /// <summary>
    /// R6: 48 bits, the relative card address and status bits, CRC and index checked.
    /// </summary>
    R6,

    /// <summary>
    /// R7: 48 bits, the interface condition's echo, CRC and index checked.
    /// </summary>
    R7
}

/// <summary>
/// The SD memory card's commands the host uses (plan step H8.2) and the SD Host Controller's encoding of a command
/// (SD Host Controller Simplified Specification 3.00, section 2.2.6).
/// </summary>
public static class SdCommand
{
    #region Constants

    /// <summary>GO_IDLE_STATE.</summary>
    public const int GO_IDLE_STATE = 0;

    /// <summary>ALL_SEND_CID.</summary>
    public const int ALL_SEND_CID = 2;

    /// <summary>SEND_RELATIVE_ADDR.</summary>
    public const int SEND_RELATIVE_ADDR = 3;

    /// <summary>SET_BUS_WIDTH, an application command.</summary>
    public const int SET_BUS_WIDTH = 6;

    /// <summary>SELECT_CARD.</summary>
    public const int SELECT_CARD = 7;

    /// <summary>SEND_IF_COND.</summary>
    public const int SEND_IF_COND = 8;

    /// <summary>SEND_CSD.</summary>
    public const int SEND_CSD = 9;

    /// <summary>STOP_TRANSMISSION.</summary>
    public const int STOP_TRANSMISSION = 12;

    /// <summary>SEND_STATUS.</summary>
    public const int SEND_STATUS = 13;

    /// <summary>SET_BLOCKLEN.</summary>
    public const int SET_BLOCKLEN = 16;

    /// <summary>READ_SINGLE_BLOCK.</summary>
    public const int READ_SINGLE_BLOCK = 17;

    /// <summary>READ_MULTIPLE_BLOCK.</summary>
    public const int READ_MULTIPLE_BLOCK = 18;

    /// <summary>WRITE_BLOCK.</summary>
    public const int WRITE_BLOCK = 24;

    /// <summary>WRITE_MULTIPLE_BLOCK.</summary>
    public const int WRITE_MULTIPLE_BLOCK = 25;

    /// <summary>SD_SEND_OP_COND, an application command.</summary>
    public const int SD_SEND_OP_COND = 41;

    /// <summary>APP_CMD: the next command is an application command.</summary>
    public const int APP_CMD = 55;

    /// <summary>
    /// SEND_IF_COND's argument: 2.7–3.6 V and the check pattern 0xAA, which the card echoes.
    /// </summary>
    public const uint INTERFACE_CONDITION = 0x1AA;

    /// <summary>
    /// The card status bits of an R1 that name an error: out of range, address, block length, erase sequence and
    /// parameter, write protection, lock and unlock, CRC, illegal command, ECC, controller and general errors.
    /// </summary>
    public const uint STATUS_ERRORS = 0xFDF8_0000;

    /// <summary>
    /// The card status's current state, bits 12–9: transfer.
    /// </summary>
    public const uint STATE_TRANSFER = 4;

    // The command register's fields (SDHCI 3.00 section 2.2.6): the index, the type, data present, the index and CRC
    // checks and the response's length.
    private const int DATA_PRESENT = 1 << 5;
    private const int INDEX_CHECK = 1 << 4;
    private const int CRC_CHECK = 1 << 3;
    private const int RESPONSE_136 = 1;
    private const int RESPONSE_48 = 2;
    private const int RESPONSE_48_BUSY = 3;
    private const int TYPE_ABORT = 3 << 6;

    #endregion

    #region Functions

    /// <summary>
    /// The command register's value for a command.
    /// </summary>
    /// <param name="index">The command's index, 0–63.</param>
    /// <param name="response">The response it expects.</param>
    /// <param name="data">Whether a data transfer follows it.</param>
    /// <returns>The 16-bit value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is beyond 63.</exception>
    public static ushort Register(int index, SdResponse response, bool data)
    {
        if (index is < 0 or > 63)
            throw new ArgumentOutOfRangeException(nameof(index));
        var value = index << 8 | (data ? DATA_PRESENT : 0) | (index == STOP_TRANSMISSION ? TYPE_ABORT : 0) | response switch
        {
            SdResponse.None => 0,
            SdResponse.R1 or SdResponse.R6 or SdResponse.R7 => RESPONSE_48 | CRC_CHECK | INDEX_CHECK,
            SdResponse.R1b => RESPONSE_48_BUSY | CRC_CHECK | INDEX_CHECK,
            SdResponse.R2 => RESPONSE_136 | CRC_CHECK,
            SdResponse.R3 => RESPONSE_48,
            _ => throw new ArgumentOutOfRangeException(nameof(response))
        };
        return (ushort)value;
    }

    /// <summary>
    /// The card's capacity in bytes from its CSD (SD 3.01 section 5.3): version 2.0's C_SIZE in units of 512 KiB, or version
    /// 1.0's C_SIZE, C_SIZE_MULT and READ_BL_LEN.
    /// </summary>
    /// <param name="csd">The 128-bit register, bit 0 its CRC's place.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="FormatException">The CSD's structure version is not one the card's specification names.</exception>
    public static long Capacity(UInt128 csd)
    {
        var structure = (int)Bits(csd, 127, 126);
        if (structure == 1)
            return ((long)Bits(csd, 69, 48) + 1) * 512 * 1024;
        if (structure == 0)
        {
            var size = (long)Bits(csd, 73, 62);
            var multiplier = (int)Bits(csd, 49, 47);
            var blockLength = (int)Bits(csd, 83, 80);
            return (size + 1) << (multiplier + 2 + blockLength);
        }
        throw new FormatException($"A CSD of structure version {structure}.");
    }

    /// <summary>
    /// The product's name and serial number from a CID (SD 3.01 section 5.2), for the driver's line.
    /// </summary>
    /// <param name="cid">The 128-bit register.</param>
    /// <returns>The five characters of the name and the serial number.</returns>
    public static (string Name, uint Serial) Identity(UInt128 cid)
    {
        Span<char> name = stackalloc char[5];
        for (var i = 0; i < 5; i++)
        {
            var c = (char)Bits(cid, 103 - i * 8, 96 - i * 8);
            name[i] = c is >= ' ' and < (char)0x7F ? c : '?';
        }
        return (new string(name).Trim(), (uint)Bits(cid, 55, 24));
    }

    /// <summary>
    /// The bits from high to low of a 128-bit register, inclusive.
    /// </summary>
    /// <param name="value">The register.</param>
    /// <param name="high">The highest bit.</param>
    /// <param name="low">The lowest bit.</param>
    /// <returns>The field.</returns>
    public static ulong Bits(UInt128 value, int high, int low) =>
        (ulong)((value >> low) & ((UInt128.One << (high - low + 1)) - 1));

    #endregion
}
