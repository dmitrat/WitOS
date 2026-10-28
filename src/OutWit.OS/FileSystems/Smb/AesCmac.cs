using System;
using System.Security.Cryptography;

namespace OutWit.OS.FileSystems.Smb;

/// <summary>
/// AES-128-CMAC (RFC 4493), with which SMB 3.x signs its messages (MS-SMB2 3.1.4.1); .NET provides AES, not CMAC.
/// </summary>
internal sealed class AesCmac : IDisposable
{
    #region Constants

    /// <summary>
    /// The block and the tag's bytes.
    /// </summary>
    public const int SIZE = 16;

    private const byte RB = 0x87;

    #endregion

    #region Fields

    private readonly Aes m_aes;
    private readonly byte[] m_k1 = new byte[SIZE];
    private readonly byte[] m_k2 = new byte[SIZE];

    #endregion

    #region Constructors

    /// <summary>
    /// A MAC under a 128-bit key.
    /// </summary>
    /// <param name="key">The key, 16 bytes.</param>
    public AesCmac(ReadOnlySpan<byte> key)
    {
        if (key.Length != SIZE)
            throw new ArgumentException("An AES-128-CMAC key holds 16 bytes.", nameof(key));
        m_aes = Aes.Create();
        m_aes.Key = key.ToArray();
        // The subkeys: L = AES(K, 0), K1 = L << 1 (xor Rb on a carry), K2 = K1 << 1 likewise.
        var l = m_aes.EncryptEcb(new byte[SIZE], PaddingMode.None);
        Double(l, m_k1);
        Double(m_k1, m_k2);
    }

    #endregion

    #region Functions

    /// <summary>
    /// The tag of a message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>Its 16-byte tag.</returns>
    public byte[] Compute(ReadOnlySpan<byte> message)
    {
        var blocks = Math.Max(1, (message.Length + SIZE - 1) / SIZE);
        var complete = message.Length > 0 && message.Length % SIZE == 0;
        Span<byte> x = stackalloc byte[SIZE];
        x.Clear();
        Span<byte> y = stackalloc byte[SIZE];
        for (var i = 0; i < blocks - 1; i++)
        {
            for (var k = 0; k < SIZE; k++)
                y[k] = (byte)(x[k] ^ message[i * SIZE + k]);
            m_aes.EncryptEcb(y, x, PaddingMode.None);
        }
        // The last block, xor K1 when complete, padded with 0x80 and zeros and xor K2 otherwise.
        var last = message[((blocks - 1) * SIZE)..];
        for (var k = 0; k < SIZE; k++)
        {
            var value = k < last.Length ? last[k] : k == last.Length ? (byte)0x80 : (byte)0;
            y[k] = (byte)(x[k] ^ value ^ (complete ? m_k1[k] : m_k2[k]));
        }
        var tag = new byte[SIZE];
        m_aes.EncryptEcb(y, tag, PaddingMode.None);
        return tag;
    }

    /// <inheritdoc />
    public void Dispose() => m_aes.Dispose();

    // A block shifted left by one bit, xor Rb when its top bit was set (RFC 4493 2.3).
    private static void Double(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var carry = 0;
        for (var k = SIZE - 1; k >= 0; k--)
        {
            var value = input[k];
            output[k] = (byte)((value << 1) | carry);
            carry = value >> 7;
        }
        if ((input[0] & 0x80) != 0)
            output[SIZE - 1] ^= RB;
    }

    #endregion
}
