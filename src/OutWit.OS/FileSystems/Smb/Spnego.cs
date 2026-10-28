using System;
using System.IO;

namespace OutWit.OS.FileSystems.Smb;

/// <summary>
/// SPNEGO (RFC 4178) as SMB's session setup carries NTLM in it: the client's initial token, wrapped as GSS-API asks,
/// offers NTLM alone with its NEGOTIATE message; each later token is a negTokenResp with NTLM's message and, with the
/// last, the mechListMIC over the offered list. The server's tokens are read for their responseToken alone. DER is
/// written and read here, for these few shapes.
/// </summary>
internal static class Spnego
{
    #region Constants

    // SPNEGO's object identifier 1.3.6.1.5.5.2 and NTLM's 1.3.6.1.4.1.311.2.2.10, each as DER writes it.
    private static readonly byte[] SPNEGO_OID = [0x06, 0x06, 0x2B, 0x06, 0x01, 0x05, 0x05, 0x02];
    private static readonly byte[] NTLM_OID = [0x06, 0x0A, 0x2B, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x02, 0x02, 0x0A];

    private const byte SEQUENCE = 0x30;
    private const byte OCTET_STRING = 0x04;
    private const byte APPLICATION_0 = 0x60;
    private const byte CONTEXT_0 = 0xA0;
    private const byte CONTEXT_1 = 0xA1;
    private const byte CONTEXT_2 = 0xA2;
    private const byte CONTEXT_3 = 0xA3;

    #endregion

    #region Functions

    /// <summary>
    /// The client's first token: a negTokenInit that offers NTLM with its message.
    /// </summary>
    /// <param name="mechanismToken">NTLM's NEGOTIATE message.</param>
    /// <returns>The token.</returns>
    public static byte[] InitialToken(byte[] mechanismToken)
    {
        var init = Element(SEQUENCE, Concat(Element(CONTEXT_0, MechanismList), Element(CONTEXT_2, Element(OCTET_STRING, mechanismToken))));
        return Element(APPLICATION_0, Concat(SPNEGO_OID, Element(CONTEXT_0, init)));
    }

    /// <summary>
    /// A later token of the client: a negTokenResp with NTLM's message and, when given, the mechListMIC.
    /// </summary>
    /// <param name="mechanismToken">NTLM's message.</param>
    /// <param name="mechanismListMic">The signature of <see cref="MechanismList"/>, or null.</param>
    /// <returns>The token.</returns>
    public static byte[] ResponseToken(byte[] mechanismToken, byte[]? mechanismListMic)
    {
        var fields = Element(CONTEXT_2, Element(OCTET_STRING, mechanismToken));
        if (mechanismListMic is not null)
            fields = Concat(fields, Element(CONTEXT_3, Element(OCTET_STRING, mechanismListMic)));
        return Element(CONTEXT_1, Element(SEQUENCE, fields));
    }

    /// <summary>
    /// The responseToken of a server's negTokenResp, or null when it carries none.
    /// </summary>
    /// <param name="token">The server's token.</param>
    /// <returns>NTLM's message.</returns>
    /// <exception cref="InvalidDataException">The token is no negTokenResp.</exception>
    public static byte[]? ReadResponseToken(ReadOnlySpan<byte> token)
    {
        var at = 0;
        var response = Read(token, ref at, CONTEXT_1);
        var inner = 0;
        var sequence = Read(response, ref inner, SEQUENCE);
        for (var field = 0; field < sequence.Length;)
        {
            var tag = sequence[field];
            var value = Read(sequence, ref field, tag);
            if (tag == CONTEXT_2)
            {
                var octets = 0;
                return Read(value, ref octets, OCTET_STRING).ToArray();
            }
        }
        return null;
    }

    // A DER element: its tag, its length in short or long form, its value.
    private static byte[] Element(byte tag, byte[] value)
    {
        var length = value.Length;
        byte[] header = length switch
        {
            < 0x80 => [tag, (byte)length],
            <= 0xFF => [tag, 0x81, (byte)length],
            <= 0xFFFF => [tag, 0x82, (byte)(length >> 8), (byte)length],
            _ => [tag, 0x83, (byte)(length >> 16), (byte)(length >> 8), (byte)length]
        };
        return Concat(header, value);
    }

    // The value of the element at an offset, which must carry the tag; the offset moves past it.
    private static ReadOnlySpan<byte> Read(ReadOnlySpan<byte> data, ref int at, byte tag)
    {
        if (at + 2 > data.Length || data[at] != tag)
            throw new InvalidDataException($"SPNEGO expected tag 0x{tag:X2} at {at}.");
        var length = (int)data[at + 1];
        var start = at + 2;
        if (length >= 0x80)
        {
            var bytes = length & 0x7F;
            if (bytes is 0 or > 3 || start + bytes > data.Length)
                throw new InvalidDataException("SPNEGO's length is malformed.");
            length = 0;
            for (var i = 0; i < bytes; i++)
                length = (length << 8) | data[start + i];
            start += bytes;
        }
        if (start + length > data.Length)
            throw new InvalidDataException("A SPNEGO element overruns its token.");
        at = start + length;
        return data.Slice(start, length);
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The DER of the mechanism list the initial token offers, NTLM alone, which the mechListMIC signs.
    /// </summary>
    public static byte[] MechanismList { get; } = Element(SEQUENCE, NTLM_OID);

    #endregion
}
