using System;

namespace OutWit.OS.FileSystems.Smb;

/// <summary>
/// RC4, which NTLM's key exchange encrypts the session key with and its signatures seal their checksums with
/// (MS-NLMP 3.4.4.2); .NET does not provide it. A stream: each call continues where the last one ended.
/// </summary>
internal sealed class Rc4
{
    #region Fields

    private readonly byte[] m_state = new byte[256];
    private int m_i;
    private int m_j;

    #endregion

    #region Constructors

    /// <summary>
    /// A stream under a key.
    /// </summary>
    /// <param name="key">The key, 1–256 bytes.</param>
    public Rc4(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty || key.Length > 256)
            throw new ArgumentException("An RC4 key holds 1 to 256 bytes.", nameof(key));
        for (var i = 0; i < 256; i++)
            m_state[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + m_state[i] + key[i % key.Length]) & 0xFF;
            (m_state[i], m_state[j]) = (m_state[j], m_state[i]);
        }
    }

    #endregion

    #region Functions

    /// <summary>
    /// Encrypts or decrypts bytes in place.
    /// </summary>
    /// <param name="data">The bytes.</param>
    public void Transform(Span<byte> data)
    {
        for (var k = 0; k < data.Length; k++)
        {
            m_i = (m_i + 1) & 0xFF;
            m_j = (m_j + m_state[m_i]) & 0xFF;
            (m_state[m_i], m_state[m_j]) = (m_state[m_j], m_state[m_i]);
            data[k] ^= m_state[(m_state[m_i] + m_state[m_j]) & 0xFF];
        }
    }

    /// <summary>
    /// The bytes encrypted under a key with a stream of their own.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="data">The bytes.</param>
    /// <returns>The encrypted bytes.</returns>
    public static byte[] Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        var result = data.ToArray();
        new Rc4(key).Transform(result);
        return result;
    }

    #endregion
}
