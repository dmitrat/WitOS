using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OutWit.OS.FileSystems.Smb;

/// <summary>
/// One SMB 3 connection and its session (MS-SMB2; plan step E2, the author's decision of 2026-10-28): over a byte stream
/// to the server's port 445 in Direct TCP framing, the dialects 3.0, 3.0.2 and 3.1.1 negotiated with signing required
/// (Windows 11 24H2 requires it both ways and refuses guests), a session set up with NTLMv2 under SPNEGO, its messages
/// signed with AES-128-CMAC under the key SP800-108 derives from the session key — for 3.1.1 over the hash of the
/// negotiation and the session setup (the pre-authentication integrity of MS-SMB2 3.2.5.2) — and every response's
/// signature checked. Requests go one at a time; each asks credits enough for the next, and a large read or write is
/// charged one credit per 64 KiB when the server offers large MTUs. No oplocks or leases are asked, so the server sends
/// no break; encryption, multichannel, DFS and durable handles are not offered.
/// </summary>
internal sealed class SmbConnection : IAsyncDisposable
{
    #region Constants

    public const ushort NEGOTIATE = 0x0000;
    public const ushort SESSION_SETUP = 0x0001;
    public const ushort LOGOFF = 0x0002;
    public const ushort TREE_CONNECT = 0x0003;
    public const ushort TREE_DISCONNECT = 0x0004;
    public const ushort CREATE = 0x0005;
    public const ushort CLOSE = 0x0006;
    public const ushort FLUSH = 0x0007;
    public const ushort READ = 0x0008;
    public const ushort WRITE = 0x0009;
    public const ushort QUERY_DIRECTORY = 0x000E;
    public const ushort QUERY_INFO = 0x0010;
    public const ushort SET_INFO = 0x0011;

    public const ushort DIALECT_300 = 0x0300;
    public const ushort DIALECT_302 = 0x0302;
    public const ushort DIALECT_311 = 0x0311;

    /// <summary>
    /// The header's bytes; every offset in a message counts from its start.
    /// </summary>
    public const int HEADER = 64;

    /// <summary>
    /// The bytes one credit pays for in a large read or write.
    /// </summary>
    public const int CREDIT_BYTES = 65536;

    private const uint FLAGS_RESPONSE = 0x00000001;
    private const uint FLAGS_ASYNC = 0x00000002;
    private const uint FLAGS_SIGNED = 0x00000008;

    private const ushort SIGNING_ENABLED = 0x0001;
    private const ushort SIGNING_REQUIRED = 0x0002;
    private const uint CAPABILITY_LARGE_MTU = 0x00000004;

    private const ushort SESSION_GUEST = 0x0001;
    private const ushort SESSION_NULL = 0x0002;
    private const ushort SESSION_ENCRYPT = 0x0004;
    private const uint SHARE_ENCRYPT = 0x00008000;

    private const ushort CONTEXT_PREAUTH = 0x0001;
    private const ushort CONTEXT_SIGNING = 0x0008;
    private const ushort HASH_SHA512 = 0x0001;
    private const ushort SIGNING_AES_CMAC = 0x0001;

    // The workstation NTLM names: the server records it and checks nothing of it.
    private const string WORKSTATION = "WITOS";

    // The credits a request asks for: enough that a read or a write of a MiB never waits for the next grant.
    private const ushort CREDITS_ASKED = 64;

    // The unsolicited message id of an oplock or lease break, which a client that asks for neither never sees.
    private const ulong UNSOLICITED = ulong.MaxValue;

    private static readonly byte[] PROTOCOL = [0xFE, (byte)'S', (byte)'M', (byte)'B'];
    private static readonly byte[] SIGNING_LABEL_311 = "SMBSigningKey\0"u8.ToArray();
    private static readonly byte[] SIGNING_LABEL_300 = "SMB2AESCMAC\0"u8.ToArray();
    private static readonly byte[] SIGNING_CONTEXT_300 = "SmbSign\0"u8.ToArray();

    #endregion

    #region Fields

    private readonly Stream m_stream;
    private readonly SemaphoreSlim m_turn = new(1, 1);
    private readonly byte[] m_frame = new byte[4];
    private byte[] m_preauth = new byte[64];
    private AesCmac? m_signing;
    private ulong m_messageId;
    private ulong m_sessionId;
    private int m_credits = 1;
    private bool m_largeMtu;
    private int m_disposed;

    #endregion

    #region Constructors

    private SmbConnection(Stream stream, string server)
    {
        m_stream = stream;
        Server = server;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Negotiates over a connected stream and logs on.
    /// </summary>
    /// <param name="stream">The stream to the server's SMB port, which the connection owns from now on.</param>
    /// <param name="server">The server's name or address, as the service's principal name and the share paths name it.</param>
    /// <param name="credentials">The account.</param>
    /// <param name="cancellationToken">Cancels the logon.</param>
    /// <returns>The connection with its session.</returns>
    /// <exception cref="FileSystemException">The server refused the account (AccessDenied).</exception>
    /// <exception cref="IOException">The server or the stream failed, or the server does not speak SMB 3 with signing.</exception>
    public static async Task<SmbConnection> ConnectAsync(Stream stream, string server, SmbCredentials credentials, CancellationToken cancellationToken = default)
    {
        var connection = new SmbConnection(stream, server);
        try
        {
            await connection.NegotiateAsync(cancellationToken).ConfigureAwait(false);
            await connection.SessionSetupAsync(credentials, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Connects a share of the server (TREE_CONNECT).
    /// </summary>
    /// <param name="share">The share's name.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The tree's id and the share's maximal access for this account.</returns>
    /// <exception cref="FileSystemException">No such share (NotFound), or the account may not reach it.</exception>
    /// <exception cref="IOException">The share requires encryption, which the client does not offer.</exception>
    public async Task<(uint TreeId, uint MaximalAccess)> TreeConnectAsync(string share, CancellationToken cancellationToken = default)
    {
        var path = Encoding.Unicode.GetBytes($@"\\{Server}\{share}");
        var body = new byte[8 + path.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 9);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), HEADER + 8);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), (ushort)path.Length);
        path.CopyTo(body, 8);
        var response = await RequestAsync(TREE_CONNECT, body, 0, 1, cancellationToken, SmbStatus.BAD_NETWORK_NAME).ConfigureAwait(false);
        if (response.Status == SmbStatus.BAD_NETWORK_NAME)
            throw new FileSystemException(FileSystemError.NotFound, $"//{Server} has no share {share}.");
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(response.Body[4..]);
        if ((flags & SHARE_ENCRYPT) != 0)
            throw new SmbException(0, $"//{Server}/{share} requires encryption, which the client does not offer yet.");
        return (response.TreeId, BinaryPrimitives.ReadUInt32LittleEndian(response.Body[12..]));
    }

    /// <summary>
    /// Disconnects a share's tree.
    /// </summary>
    /// <param name="treeId">The tree.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>When it is disconnected.</returns>
    public async Task TreeDisconnectAsync(uint treeId, CancellationToken cancellationToken = default)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 4);
        await RequestAsync(TREE_DISCONNECT, body, treeId, 1, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one request and waits for its final response, signed once the session is, checking the response's signature;
    /// an interim STATUS_PENDING is waited past.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="body">The request's body after the header.</param>
    /// <param name="treeId">The tree it acts in, or 0.</param>
    /// <param name="creditCharge">The credits it costs: 1, or one per 64 KiB of a large read or write.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <param name="accepted">Statuses besides success the caller handles itself.</param>
    /// <returns>The response.</returns>
    /// <exception cref="FileSystemException">The server failed it with a status the filesystem contract shares.</exception>
    /// <exception cref="IOException">The server failed it otherwise, its signature was wrong, or the stream failed.</exception>
    public async Task<SmbResponse> RequestAsync(ushort command, byte[] body, uint treeId, int creditCharge, CancellationToken cancellationToken = default,
        params uint[] accepted)
    {
        await m_turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (m_disposed != 0)
                throw new SmbException(0, $"//{Server} is unmounted: {Describe(command)} goes nowhere."); // EIO for a file still open
            var response = await ExchangeAsync(command, body, treeId, creditCharge, cancellationToken).ConfigureAwait(false);
            if (response.Status != SmbStatus.SUCCESS && Array.IndexOf(accepted, response.Status) < 0)
                throw SmbStatus.Failure(response.Status, Describe(command));
            return response;
        }
        finally
        {
            m_turn.Release();
        }
    }

    /// <summary>
    /// The credits a read or a write of a length costs, which the caller passes with it.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The charge.</returns>
    public int ChargeOf(int bytes) => m_largeMtu ? Math.Max(1, (bytes + CREDIT_BYTES - 1) / CREDIT_BYTES) : 1;

    /// <summary>
    /// Logs off and closes the stream.
    /// </summary>
    /// <returns>When it is closed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0)
            return;
        await m_turn.WaitAsync().ConfigureAwait(false);
        try
        {
            if (m_sessionId != 0 && m_signing is not null)
            {
                var body = new byte[4];
                BinaryPrimitives.WriteUInt16LittleEndian(body, 4);
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await ExchangeAsync(LOGOFF, body, 0, 1, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // A server gone already leaves nothing to log off from.
                }
            }
            await m_stream.DisposeAsync().ConfigureAwait(false);
            m_signing?.Dispose();
        }
        finally
        {
            m_turn.Release();
        }
    }

    // NEGOTIATE (MS-SMB2 2.2.3): the three dialects of SMB 3, signing required, large MTUs, and for 3.1.1 the
    // pre-authentication integrity context (SHA-512 with a salt) and the signing context (AES-CMAC).
    private async Task NegotiateAsync(CancellationToken cancellationToken)
    {
        ushort[] dialects = [DIALECT_300, DIALECT_302, DIALECT_311];
        const int contextsAt = 48; // 36 fixed bytes and 6 of dialects, padded to 8 from the header's start
        var preauth = new byte[38];
        BinaryPrimitives.WriteUInt16LittleEndian(preauth, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(preauth.AsSpan(2), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(preauth.AsSpan(4), HASH_SHA512);
        RandomNumberGenerator.Fill(preauth.AsSpan(6));
        var signing = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(signing, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(signing.AsSpan(2), SIGNING_AES_CMAC);
        var body = new byte[contextsAt + 8 + 40 + 8 + signing.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 36);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), (ushort)dialects.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), SIGNING_ENABLED | SIGNING_REQUIRED);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), CAPABILITY_LARGE_MTU);
        RandomNumberGenerator.Fill(body.AsSpan(12, 16)); // the client's GUID
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28), HEADER + contextsAt);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(32), 2);
        for (var i = 0; i < dialects.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(36 + i * 2), dialects[i]);
        WriteContext(body.AsSpan(contextsAt), CONTEXT_PREAUTH, preauth); // 46 bytes, padded to 48
        WriteContext(body.AsSpan(contextsAt + 48), CONTEXT_SIGNING, signing);

        var request = Message(NEGOTIATE, body, 0, 0);
        var response = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Status != SmbStatus.SUCCESS)
            throw SmbStatus.Failure(response.Status, "SMB negotiation");
        var answer = response.Body;
        if (answer.Length < 64)
            throw new SmbException(0, "The server's NEGOTIATE response is short.");
        Dialect = BinaryPrimitives.ReadUInt16LittleEndian(answer[4..]);
        if (Array.IndexOf(dialects, Dialect) < 0)
            throw new SmbException(0, $"The server chose dialect 0x{Dialect:X4}, which the client did not offer (SMB 3 is required).");
        ServerGuid = new Guid(answer.Slice(8, 16));
        m_largeMtu = (BinaryPrimitives.ReadUInt32LittleEndian(answer[24..]) & CAPABILITY_LARGE_MTU) != 0;
        MaxTransactSize = BinaryPrimitives.ReadUInt32LittleEndian(answer[28..]);
        MaxReadSize = BinaryPrimitives.ReadUInt32LittleEndian(answer[32..]);
        MaxWriteSize = BinaryPrimitives.ReadUInt32LittleEndian(answer[36..]);
        if (Dialect == DIALECT_311)
        {
            CheckContexts(response);
            m_preauth = SHA512.HashData(Concat(SHA512.HashData(Concat(m_preauth, request)), response.Message));
        }
    }

    // The contexts a 3.1.1 server answered: SHA-512 for the integrity hash, AES-CMAC for signing when it names one.
    private static void CheckContexts(SmbResponse response)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(response.Body[6..]);
        var at = (int)BinaryPrimitives.ReadUInt32LittleEndian(response.Body[60..]);
        var preauth = false;
        for (var i = 0; i < count; i++)
        {
            at = (at + 7) & ~7;
            if (at + 8 > response.Message.Length)
                throw new SmbException(0, "The server's negotiate contexts overrun its response.");
            var type = BinaryPrimitives.ReadUInt16LittleEndian(response.Message.AsSpan(at));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(response.Message.AsSpan(at + 2));
            var data = response.Message.AsSpan(at + 8, Math.Min(length, response.Message.Length - at - 8));
            if (type == CONTEXT_PREAUTH)
                preauth = data.Length >= 6 && BinaryPrimitives.ReadUInt16LittleEndian(data) == 1 && BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) == HASH_SHA512;
            if (type == CONTEXT_SIGNING && (data.Length < 4 || BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) != SIGNING_AES_CMAC))
                throw new SmbException(0, "The server chose a signing algorithm other than AES-CMAC.");
            at += 8 + length;
        }
        if (!preauth)
            throw new SmbException(0, "The server answered SMB 3.1.1 without SHA-512 pre-authentication integrity.");
    }

    // SESSION_SETUP (MS-SMB2 3.2.4.2.3) twice: SPNEGO's initial token with NTLM's NEGOTIATE, then its AUTHENTICATE with
    // the mechListMIC; the signing key is derived before the final response, which it must verify.
    private async Task SessionSetupAsync(SmbCredentials credentials, CancellationToken cancellationToken)
    {
        var ntlm = new NtlmClient(credentials.User, credentials.Password, credentials.Domain, WORKSTATION, $"cifs/{Server}");
        var sessionHash = m_preauth;
        var request = Message(SESSION_SETUP, SessionSetup(Spnego.InitialToken(ntlm.Negotiate())), 0, 1);
        var response = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Status != SmbStatus.MORE_PROCESSING_REQUIRED)
            throw Refused(response.Status, credentials);
        m_sessionId = response.SessionId;
        if (Dialect == DIALECT_311)
            sessionHash = SHA512.HashData(Concat(SHA512.HashData(Concat(sessionHash, request)), response.Message));
        var challenge = Spnego.ReadResponseToken(SecurityBuffer(response)) ?? throw new SmbException(0, "The server's SPNEGO answer carries no NTLM challenge.");
        var authenticate = ntlm.Authenticate(challenge);
        request = Message(SESSION_SETUP, SessionSetup(Spnego.ResponseToken(authenticate, ntlm.Sign(Spnego.MechanismList))), 0, 1);
        if (Dialect == DIALECT_311)
            sessionHash = SHA512.HashData(Concat(sessionHash, request));
        m_signing = new AesCmac(Dialect == DIALECT_311
            ? SP800108HmacCounterKdf.DeriveBytes(ntlm.SessionKey!, HashAlgorithmName.SHA256, SIGNING_LABEL_311, sessionHash, 16)
            : SP800108HmacCounterKdf.DeriveBytes(ntlm.SessionKey!, HashAlgorithmName.SHA256, SIGNING_LABEL_300, SIGNING_CONTEXT_300, 16));
        response = await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Status != SmbStatus.SUCCESS)
            throw Refused(response.Status, credentials);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(response.Body[2..]);
        if ((flags & (SESSION_GUEST | SESSION_NULL)) != 0)
            throw new FileSystemException(FileSystemError.AccessDenied, $"//{Server} logged {credentials.User} on as a guest, which cannot sign.");
        if ((flags & SESSION_ENCRYPT) != 0)
            throw new SmbException(0, $"//{Server} requires encryption for the session, which the client does not offer yet.");
    }

    private FileSystemException Refused(uint status, SmbCredentials credentials) =>
        new(FileSystemError.AccessDenied, $"//{Server} refused {(credentials.Domain.Length == 0 ? "" : credentials.Domain + @"\")}{credentials.User}: " +
            $"{SmbStatus.Name(status)} (0x{status:X8})");

    private static byte[] SessionSetup(byte[] token)
    {
        var body = new byte[24 + token.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 25);
        body[3] = (byte)SIGNING_REQUIRED | (byte)SIGNING_ENABLED;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(12), HEADER + 24);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14), (ushort)token.Length);
        token.CopyTo(body, 24);
        return body;
    }

    private static ReadOnlySpan<byte> SecurityBuffer(SmbResponse response) =>
        response.Buffer(BinaryPrimitives.ReadUInt16LittleEndian(response.Body[4..]), BinaryPrimitives.ReadUInt16LittleEndian(response.Body[6..]));

    // A request of the session: built, signed, sent, and its final response read and checked.
    private async Task<SmbResponse> ExchangeAsync(ushort command, byte[] body, uint treeId, int creditCharge, CancellationToken cancellationToken)
    {
        if (creditCharge > m_credits)
            throw new SmbException(0, $"{Describe(command)} needs {creditCharge} credits, the server granted {m_credits}.");
        var request = Message(command, body, treeId, creditCharge);
        return await ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SmbResponse> ExchangeAsync(byte[] request, CancellationToken cancellationToken)
    {
        var messageId = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(24));
        var charge = Math.Max((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(6)));
        m_credits -= charge;
        BinaryPrimitives.WriteUInt32BigEndian(m_frame, (uint)request.Length);
        await m_stream.WriteAsync(m_frame, cancellationToken).ConfigureAwait(false);
        await m_stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await m_stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        for (; ; )
        {
            var message = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
            var response = new SmbResponse(message);
            m_credits += response.Credits;
            if (response.MessageId == UNSOLICITED)
                continue; // a break the client never asked for
            if (response.MessageId != messageId || (response.Flags & FLAGS_RESPONSE) == 0)
                throw new SmbException(0, $"The server answered message {response.MessageId} while the client waited for {messageId}.");
            if ((response.Flags & FLAGS_ASYNC) != 0 && response.Status == SmbStatus.PENDING)
                continue; // the interim response; the final one follows with the same id
            // A session setup the server refused has no key to sign with; every other response of a signed session is signed.
            var refusedSetup = BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(12)) == SESSION_SETUP && response.Status != SmbStatus.SUCCESS;
            if (m_signing is not null && !refusedSetup)
                Verify(message, response.Flags);
            return response;
        }
    }

    // A whole message of Direct TCP: a zero byte, a 24-bit length, the message.
    private async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        await m_stream.ReadExactlyAsync(m_frame, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(m_frame);
        if (length is < HEADER or > 16 << 20)
            throw new SmbException(0, $"The server sent a frame of {length} bytes.");
        var message = new byte[length];
        await m_stream.ReadExactlyAsync(message, cancellationToken).ConfigureAwait(false);
        if (!message.AsSpan(0, 4).SequenceEqual(PROTOCOL))
            throw new SmbException(0, "The server sent a message that is no SMB 2 message (a transform or an SMB 1 header).");
        return message;
    }

    // A request's header and body; the next message id, the credits it asks, the session and tree, signed when the
    // session has its key.
    private byte[] Message(ushort command, byte[] body, uint treeId, int creditCharge)
    {
        var message = new byte[HEADER + body.Length];
        PROTOCOL.CopyTo(message, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(4), HEADER);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(6), (ushort)creditCharge);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(12), command);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(14), CREDITS_ASKED);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(24), m_messageId);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(36), treeId);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(40), m_sessionId);
        body.CopyTo(message, HEADER);
        m_messageId += (ulong)Math.Max(1, creditCharge);
        if (m_signing is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(16), FLAGS_SIGNED);
            m_signing.Compute(message).CopyTo(message, 48);
        }
        return message;
    }

    // A response of the signed session must carry the right signature: the CMAC of the message with its own zeroed.
    private void Verify(byte[] message, uint flags)
    {
        if ((flags & FLAGS_SIGNED) == 0)
            throw new SmbException(0, "The server sent an unsigned response in a session that requires signing.");
        var signature = message.AsSpan(48, 16).ToArray();
        message.AsSpan(48, 16).Clear();
        var expected = m_signing!.Compute(message);
        signature.CopyTo(message, 48);
        if (!CryptographicOperations.FixedTimeEquals(signature, expected))
            throw new SmbException(0, "A response's signature is wrong: the message was altered or the keys differ.");
    }

    private static void WriteContext(Span<byte> at, ushort type, ReadOnlySpan<byte> data)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(at, type);
        BinaryPrimitives.WriteUInt16LittleEndian(at[2..], (ushort)data.Length);
        data.CopyTo(at[8..]);
    }

    private static string Describe(ushort command) => command switch
    {
        NEGOTIATE => "NEGOTIATE",
        SESSION_SETUP => "SESSION_SETUP",
        LOGOFF => "LOGOFF",
        TREE_CONNECT => "TREE_CONNECT",
        TREE_DISCONNECT => "TREE_DISCONNECT",
        CREATE => "CREATE",
        CLOSE => "CLOSE",
        FLUSH => "FLUSH",
        READ => "READ",
        WRITE => "WRITE",
        QUERY_DIRECTORY => "QUERY_DIRECTORY",
        QUERY_INFO => "QUERY_INFO",
        SET_INFO => "SET_INFO",
        _ => $"command 0x{command:X4}"
    };

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
    /// The server's name or address.
    /// </summary>
    public string Server { get; }

    /// <summary>
    /// The dialect negotiated: 0x0300, 0x0302 or 0x0311.
    /// </summary>
    public ushort Dialect { get; private set; }

    /// <summary>
    /// The server's GUID, from its NEGOTIATE response.
    /// </summary>
    public Guid ServerGuid { get; private set; }

    /// <summary>
    /// The largest read the server takes.
    /// </summary>
    public uint MaxReadSize { get; private set; }

    /// <summary>
    /// The largest write the server takes.
    /// </summary>
    public uint MaxWriteSize { get; private set; }

    /// <summary>
    /// The largest query or listing the server answers.
    /// </summary>
    public uint MaxTransactSize { get; private set; }

    /// <summary>
    /// The credits the server granted that the client has not spent.
    /// </summary>
    public int Credits => m_credits;

    #endregion
}

/// <summary>
/// An account on an SMB server.
/// </summary>
/// <param name="User">Its name.</param>
/// <param name="Password">Its password.</param>
/// <param name="Domain">Its domain, empty for an account of the server itself.</param>
public sealed record SmbCredentials(string User, string Password, string Domain = "")
{
    #region Functions

    /// <inheritdoc />
    public override string ToString() => $"{(Domain.Length == 0 ? "" : Domain + @"\")}{User}"; // never the password

    #endregion
}

/// <summary>
/// A response: its header's fields and the whole message, whose offsets count from the header's start.
/// </summary>
internal sealed class SmbResponse
{
    #region Constructors

    /// <summary>
    /// A response read from the stream.
    /// </summary>
    /// <param name="message">The whole message.</param>
    public SmbResponse(byte[] message)
    {
        Message = message;
        Credits = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(14));
        Status = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8));
        Flags = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(16));
        MessageId = BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(24));
        TreeId = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(36));
        SessionId = BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(40));
    }

    #endregion

    #region Functions

    /// <summary>
    /// A buffer the body names by its offset from the header's start and its length.
    /// </summary>
    /// <param name="offset">The offset.</param>
    /// <param name="length">The length.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="SmbException">The buffer lies outside the message.</exception>
    public ReadOnlySpan<byte> Buffer(int offset, int length)
    {
        if (length == 0)
            return [];
        if (offset < SmbConnection.HEADER || offset + (long)length > Message.Length)
            throw new SmbException(0, $"A response's buffer of {length} bytes at {offset} lies outside its {Message.Length} bytes.");
        return Message.AsSpan(offset, length);
    }

    #endregion

    #region Properties

    /// <summary>
    /// The whole message.
    /// </summary>
    public byte[] Message { get; }

    /// <summary>
    /// The body after the header.
    /// </summary>
    public ReadOnlySpan<byte> Body => Message.AsSpan(SmbConnection.HEADER);

    /// <summary>
    /// The credits it granted.
    /// </summary>
    public ushort Credits { get; }

    /// <summary>
    /// Its NTSTATUS.
    /// </summary>
    public uint Status { get; }

    /// <summary>
    /// Its header's flags.
    /// </summary>
    public uint Flags { get; }

    /// <summary>
    /// The request's message id it answers.
    /// </summary>
    public ulong MessageId { get; }

    /// <summary>
    /// The tree it names.
    /// </summary>
    public uint TreeId { get; }

    /// <summary>
    /// The session it names.
    /// </summary>
    public ulong SessionId { get; }

    #endregion
}
