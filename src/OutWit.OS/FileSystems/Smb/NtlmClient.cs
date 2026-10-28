using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OutWit.OS.FileSystems.Smb;

/// <summary>
/// The client of NTLM version 2 (MS-NLMP), as SMB's session setup needs it under SPNEGO: the NEGOTIATE message, then
/// from the server's CHALLENGE the AUTHENTICATE message with the NTLMv2 response, the session key exchanged under RC4
/// and the MIC over the three messages, which Windows requires once its challenge carries a timestamp; then the
/// signature SPNEGO's mechListMIC needs. Extended session security, 128-bit keys and key exchange are always asked;
/// LM, NTLMv1 and anonymous logons never.
/// </summary>
internal sealed class NtlmClient
{
    #region Constants

    private const uint NEGOTIATE_UNICODE = 0x00000001;
    private const uint REQUEST_TARGET = 0x00000004;
    private const uint NEGOTIATE_SIGN = 0x00000010;
    private const uint NEGOTIATE_NTLM = 0x00000200;
    private const uint NEGOTIATE_ALWAYS_SIGN = 0x00008000;
    private const uint NEGOTIATE_EXTENDED_SESSIONSECURITY = 0x00080000;
    private const uint NEGOTIATE_TARGET_INFO = 0x00800000;
    private const uint NEGOTIATE_VERSION = 0x02000000;
    private const uint NEGOTIATE_128 = 0x20000000;
    private const uint NEGOTIATE_KEY_EXCH = 0x40000000;
    private const uint NEGOTIATE_56 = 0x80000000;

    private const uint FLAGS = NEGOTIATE_UNICODE | REQUEST_TARGET | NEGOTIATE_SIGN | NEGOTIATE_NTLM | NEGOTIATE_ALWAYS_SIGN |
        NEGOTIATE_EXTENDED_SESSIONSECURITY | NEGOTIATE_TARGET_INFO | NEGOTIATE_VERSION | NEGOTIATE_128 | NEGOTIATE_KEY_EXCH | NEGOTIATE_56;

    private static readonly byte[] SIGNATURE = "NTLMSSP\0"u8.ToArray();

    // Windows 10's version as the message's Version field gives it, with NTLM's current revision (15).
    private static readonly byte[] VERSION = [10, 0, 0x61, 0x4A, 0, 0, 0, 15];

    private const int NEGOTIATE_BYTES = 40;
    private const int AUTHENTICATE_HEADER = 88;
    private const int MIC_AT = 72;

    // The AV pairs of a challenge's target information (MS-NLMP 2.2.2.1).
    private const ushort AV_EOL = 0;
    private const ushort AV_FLAGS = 6;
    private const ushort AV_TIMESTAMP = 7;
    private const ushort AV_TARGET_NAME = 9;
    private const uint AV_FLAG_MIC = 0x2;

    private static readonly byte[] CLIENT_SIGNING = "session key to client-to-server signing key magic constant\0"u8.ToArray();
    private static readonly byte[] CLIENT_SEALING = "session key to client-to-server sealing key magic constant\0"u8.ToArray();

    #endregion

    #region Fields

    private readonly string m_user;
    private readonly string m_password;
    private readonly string m_domain;
    private readonly string m_workstation;
    private readonly string? m_targetName;
    private byte[]? m_negotiate;
    private byte[]? m_signingKey;
    private Rc4? m_sealing;
    private uint m_flags;
    private uint m_sequence;

    #endregion

    #region Constructors

    /// <summary>
    /// A client for one logon.
    /// </summary>
    /// <param name="user">The account's name.</param>
    /// <param name="password">Its password.</param>
    /// <param name="domain">Its domain, empty for an account of the server itself.</param>
    /// <param name="workstation">This machine's name.</param>
    /// <param name="targetName">The service's principal name the client names in the response ("cifs/server"), or null.</param>
    public NtlmClient(string user, string password, string domain, string workstation, string? targetName)
    {
        m_user = user;
        m_password = password;
        m_domain = domain;
        m_workstation = workstation;
        m_targetName = targetName;
    }

    #endregion

    #region Functions

    /// <summary>
    /// The NEGOTIATE message.
    /// </summary>
    /// <returns>Its bytes.</returns>
    public byte[] Negotiate()
    {
        var message = new byte[NEGOTIATE_BYTES];
        SIGNATURE.CopyTo(message, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(12), FLAGS);
        // The empty domain and workstation fields point at the payload's start.
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(20), NEGOTIATE_BYTES);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(28), NEGOTIATE_BYTES);
        VERSION.CopyTo(message, 32);
        m_negotiate = message;
        return message;
    }

    /// <summary>
    /// The AUTHENTICATE message that answers the server's CHALLENGE, with the keys it leaves.
    /// </summary>
    /// <param name="challenge">The CHALLENGE message.</param>
    /// <returns>Its bytes.</returns>
    /// <exception cref="InvalidDataException">The challenge is malformed or refuses what the client needs.</exception>
    public byte[] Authenticate(ReadOnlySpan<byte> challenge)
    {
        if (m_negotiate is null)
            throw new InvalidOperationException("NEGOTIATE comes first.");
        if (challenge.Length < 48 || !challenge[..8].SequenceEqual(SIGNATURE) || BinaryPrimitives.ReadUInt32LittleEndian(challenge[8..]) != 2)
            throw new InvalidDataException("The server's NTLM token is no CHALLENGE message.");
        var serverFlags = BinaryPrimitives.ReadUInt32LittleEndian(challenge[20..]);
        if ((serverFlags & NEGOTIATE_EXTENDED_SESSIONSECURITY) == 0 || (serverFlags & NEGOTIATE_UNICODE) == 0)
            throw new InvalidDataException("The server refuses NTLM's extended session security or Unicode.");
        m_flags = FLAGS & (serverFlags | NEGOTIATE_VERSION);
        var serverChallenge = challenge.Slice(24, 8).ToArray();
        var targetInfo = Field(challenge, 40);

        var (pairs, timestamp) = TargetInfo(targetInfo);
        var time = timestamp ?? DateTimeOffset.UtcNow.ToFileTime();
        var clientChallenge = RandomNumberGenerator.GetBytes(8);
        var responseKey = NtOwfV2(m_password, m_user, m_domain);
        var temp = Temp(time, clientChallenge, pairs);
        var proof = ProofString(responseKey, serverChallenge, temp);
        var ntResponse = Concat(proof, temp);
        // With a timestamp the LMv2 response is 24 zero bytes (MS-NLMP 3.1.5.1.2); without one it is LMv2.
        var lmResponse = timestamp is not null
            ? new byte[24]
            : Concat(HMACMD5.HashData(responseKey, Concat(serverChallenge, clientChallenge)), clientChallenge);
        var sessionBaseKey = HMACMD5.HashData(responseKey, proof);
        byte[] exportedKey, encryptedKey;
        if ((m_flags & NEGOTIATE_KEY_EXCH) != 0)
        {
            exportedKey = RandomNumberGenerator.GetBytes(16);
            encryptedKey = Rc4.Transform(sessionBaseKey, exportedKey);
        }
        else
        {
            exportedKey = sessionBaseKey;
            encryptedKey = [];
        }

        var domain = Encoding.Unicode.GetBytes(m_domain);
        var user = Encoding.Unicode.GetBytes(m_user);
        var workstation = Encoding.Unicode.GetBytes(m_workstation);
        var message = new byte[AUTHENTICATE_HEADER + lmResponse.Length + ntResponse.Length + domain.Length + user.Length + workstation.Length +
            encryptedKey.Length];
        SIGNATURE.CopyTo(message, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 3);
        var at = AUTHENTICATE_HEADER;
        foreach (var (field, bytes) in new[] { (12, lmResponse), (20, ntResponse), (28, domain), (36, user), (44, workstation), (52, encryptedKey) })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(field), (ushort)bytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(field + 2), (ushort)bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(field + 4), (uint)at);
            bytes.CopyTo(message, at);
            at += bytes.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(60), m_flags);
        VERSION.CopyTo(message, 64);
        // The MIC covers the three messages with its own field zero (MS-NLMP 3.1.5.1.2).
        var mic = HMACMD5.HashData(exportedKey, Concat(Concat(m_negotiate, challenge.ToArray()), message));
        mic.CopyTo(message, MIC_AT);

        SessionKey = exportedKey;
        m_signingKey = MD5.HashData(Concat(exportedKey, CLIENT_SIGNING));
        m_sealing = new Rc4(MD5.HashData(Concat(exportedKey, CLIENT_SEALING)));
        m_sequence = 0;
        return message;
    }

    /// <summary>
    /// The signature of a message under the client's signing key and the next sequence number (MS-NLMP 3.4.4.2), as
    /// SPNEGO's mechListMIC carries it.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The 16-byte signature.</returns>
    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        if (m_signingKey is null || m_sealing is null)
            throw new InvalidOperationException("The keys come with AUTHENTICATE.");
        var signature = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(signature, 1);
        var sequence = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sequence, m_sequence);
        var checksum = HMACMD5.HashData(m_signingKey, Concat(sequence, message.ToArray())).AsSpan(0, 8).ToArray();
        if ((m_flags & NEGOTIATE_KEY_EXCH) != 0)
            m_sealing.Transform(checksum);
        checksum.CopyTo(signature, 4);
        sequence.CopyTo(signature, 12);
        m_sequence++;
        return signature;
    }

    /// <summary>
    /// NTOWFv2: the key of the NTLMv2 response, HMAC-MD5 of the NT hash over the upper-case user and the domain.
    /// </summary>
    /// <param name="password">The password.</param>
    /// <param name="user">The user.</param>
    /// <param name="domain">The domain.</param>
    /// <returns>The 16-byte key.</returns>
    public static byte[] NtOwfV2(string password, string user, string domain) =>
        HMACMD5.HashData(NtHash(password), Encoding.Unicode.GetBytes(user.ToUpperInvariant() + domain));

    /// <summary>
    /// The NT hash of a password: MD4 of its UTF-16LE bytes, what an smbpasswd file keeps.
    /// </summary>
    /// <param name="password">The password.</param>
    /// <returns>The 16-byte hash.</returns>
    public static byte[] NtHash(string password) => Md4.Hash(Encoding.Unicode.GetBytes(password));

    /// <summary>
    /// The client's blob of the NTLMv2 response (MS-NLMP 3.3.2, "temp"): the versions, the time, the client's challenge
    /// and the target information.
    /// </summary>
    /// <param name="time">The time as a FILETIME.</param>
    /// <param name="clientChallenge">The client's 8-byte challenge.</param>
    /// <param name="targetInfo">The target information the client sends.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] Temp(long time, ReadOnlySpan<byte> clientChallenge, ReadOnlySpan<byte> targetInfo)
    {
        var temp = new byte[28 + targetInfo.Length + 4];
        temp[0] = 1;
        temp[1] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(temp.AsSpan(8), time);
        clientChallenge.CopyTo(temp.AsSpan(16));
        targetInfo.CopyTo(temp.AsSpan(28));
        return temp;
    }

    /// <summary>
    /// NTProofStr: HMAC-MD5 under the response key over the server's challenge and the client's blob.
    /// </summary>
    /// <param name="responseKey">NTOWFv2.</param>
    /// <param name="serverChallenge">The server's 8-byte challenge.</param>
    /// <param name="temp">The client's blob.</param>
    /// <returns>The 16-byte proof.</returns>
    public static byte[] ProofString(byte[] responseKey, ReadOnlySpan<byte> serverChallenge, ReadOnlySpan<byte> temp) =>
        HMACMD5.HashData(responseKey, Concat(serverChallenge.ToArray(), temp.ToArray()));

    // The server's target information as the client sends it back (MS-NLMP 3.1.5.1.2): its pairs with the MIC flag set,
    // the service's name when known, and the end; and the server's timestamp, when it gave one.
    private (byte[] Pairs, long? Timestamp) TargetInfo(ReadOnlySpan<byte> info)
    {
        var pairs = new List<(ushort Id, byte[] Value)>();
        long? timestamp = null;
        for (var at = 0; at + 4 <= info.Length;)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(info[at..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(info[(at + 2)..]);
            if (at + 4 + length > info.Length)
                throw new InvalidDataException("The challenge's target information overruns its field.");
            if (id == AV_EOL)
                break;
            var value = info.Slice(at + 4, length).ToArray();
            if (id == AV_TIMESTAMP && length == 8)
                timestamp = BinaryPrimitives.ReadInt64LittleEndian(value);
            if (id != AV_FLAGS && id != AV_TARGET_NAME)
                pairs.Add((id, value));
            at += 4 + length;
        }
        var flags = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(flags, AV_FLAG_MIC);
        pairs.Add((AV_FLAGS, flags));
        if (m_targetName is not null)
            pairs.Add((AV_TARGET_NAME, Encoding.Unicode.GetBytes(m_targetName)));
        using var stream = new MemoryStream();
        Span<byte> head = stackalloc byte[4];
        foreach (var (id, value) in pairs)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(head, id);
            BinaryPrimitives.WriteUInt16LittleEndian(head[2..], (ushort)value.Length);
            stream.Write(head);
            stream.Write(value);
        }
        stream.Write(new byte[4]); // MsvAvEOL
        return (stream.ToArray(), timestamp);
    }

    // A message's field: its length and its offset, within the message.
    private static ReadOnlySpan<byte> Field(ReadOnlySpan<byte> message, int at)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(message[at..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(message[(at + 4)..]);
        if (offset + (long)length > message.Length)
            throw new InvalidDataException("An NTLM field lies outside its message.");
        return message.Slice((int)offset, length);
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
    /// The session key the logon exported (MS-NLMP's ExportedSessionKey), from which SMB derives its keys; null before
    /// AUTHENTICATE.
    /// </summary>
    public byte[]? SessionKey { get; private set; }

    #endregion
}
