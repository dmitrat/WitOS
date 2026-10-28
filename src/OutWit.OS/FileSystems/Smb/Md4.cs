using System;
using System.Buffers.Binary;
using System.Numerics;

namespace OutWit.OS.FileSystems.Smb;

/// <summary>
/// MD4 (RFC 1320), which NTLM needs for the NT hash of a password (MS-NLMP 3.3.1) and .NET does not provide. It is no
/// digest of anything else: NTLM's own definition fixes it.
/// </summary>
internal static class Md4
{
    #region Constants

    /// <summary>
    /// The digest's bytes.
    /// </summary>
    public const int SIZE = 16;

    private const uint ROUND_2 = 0x5A827999;
    private const uint ROUND_3 = 0x6ED9EBA1;

    // Round 3 takes the words in this order.
    private static readonly int[] ORDER_3 = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15];

    #endregion

    #region Functions

    /// <summary>
    /// The digest of the bytes.
    /// </summary>
    /// <param name="data">The bytes.</param>
    /// <returns>Their 16-byte digest.</returns>
    public static byte[] Hash(ReadOnlySpan<byte> data)
    {
        // The message, a one bit, zeros to 56 bytes modulo 64, then its length in bits, little-endian.
        var padded = new byte[(data.Length + 8) / 64 * 64 + 64];
        data.CopyTo(padded);
        padded[data.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(padded.Length - 8), (ulong)data.Length * 8);

        uint a = 0x67452301, b = 0xEFCDAB89, c = 0x98BADCFE, d = 0x10325476;
        Span<uint> x = stackalloc uint[16];
        for (var block = 0; block < padded.Length; block += 64)
        {
            for (var i = 0; i < 16; i++)
                x[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(block + i * 4));
            uint aa = a, bb = b, cc = c, dd = d;
            for (var i = 0; i < 16; i += 4)
            {
                a = BitOperations.RotateLeft(a + F(b, c, d) + x[i], 3);
                d = BitOperations.RotateLeft(d + F(a, b, c) + x[i + 1], 7);
                c = BitOperations.RotateLeft(c + F(d, a, b) + x[i + 2], 11);
                b = BitOperations.RotateLeft(b + F(c, d, a) + x[i + 3], 19);
            }
            for (var i = 0; i < 4; i++)
            {
                a = BitOperations.RotateLeft(a + G(b, c, d) + x[i] + ROUND_2, 3);
                d = BitOperations.RotateLeft(d + G(a, b, c) + x[i + 4] + ROUND_2, 5);
                c = BitOperations.RotateLeft(c + G(d, a, b) + x[i + 8] + ROUND_2, 9);
                b = BitOperations.RotateLeft(b + G(c, d, a) + x[i + 12] + ROUND_2, 13);
            }
            for (var i = 0; i < 16; i += 4)
            {
                a = BitOperations.RotateLeft(a + H(b, c, d) + x[ORDER_3[i]] + ROUND_3, 3);
                d = BitOperations.RotateLeft(d + H(a, b, c) + x[ORDER_3[i + 1]] + ROUND_3, 9);
                c = BitOperations.RotateLeft(c + H(d, a, b) + x[ORDER_3[i + 2]] + ROUND_3, 11);
                b = BitOperations.RotateLeft(b + H(c, d, a) + x[ORDER_3[i + 3]] + ROUND_3, 15);
            }
            a += aa;
            b += bb;
            c += cc;
            d += dd;
        }

        var digest = new byte[SIZE];
        BinaryPrimitives.WriteUInt32LittleEndian(digest, a);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(4), b);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(8), c);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(12), d);
        return digest;
    }

    private static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);

    private static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);

    private static uint H(uint x, uint y, uint z) => x ^ y ^ z;

    #endregion
}
