using System;

namespace OutWit.OS.Networking;

/// <summary>
/// The stack's DHCP client (RFC 2131 §4.4, plan step W1.2a): it discovers, takes the first offer, requests it and
/// configures the interface with the acknowledged address, mask, router and DNS servers; it renews with the server at
/// half the lease, rebinds by broadcast at seven eighths and starts over when the lease ends or a server refuses.
/// Retransmissions wait RETRY_MILLISECONDS; a lease of all ones never ends.
/// </summary>
public sealed class DhcpClient
{
    #region Constants

    /// <summary>
    /// The wait before a message is sent again.
    /// </summary>
    public const long RETRY_MILLISECONDS = 2000;

    private const int ATTEMPTS = 8;

    #endregion

    #region Fields

    private readonly NetworkStack m_stack;
    private readonly Random m_random;
    private DhcpState m_state;
    private uint m_transactionId;
    private long m_deadline = long.MaxValue;
    private int m_attempts;
    private Ipv4Address m_offered;
    private Ipv4Address m_server;
    private long m_renewAt, m_rebindAt, m_expiresAt;

    #endregion

    #region Constructors

    internal DhcpClient(NetworkStack stack, Random random)
    {
        m_stack = stack;
        m_random = random;
    }

    #endregion

    #region Functions

    /// <summary>
    /// Starts over: forgets any lease and discovers.
    /// </summary>
    /// <param name="now">The stack's time.</param>
    public void Start(long now)
    {
        m_stack.Unconfigure();
        m_transactionId = (uint)m_random.Next() | ((uint)m_random.Next(2) << 31);
        m_state = DhcpState.Selecting;
        m_attempts = 0;
        SendCurrent(now);
    }

    internal void Receive(DhcpMessage message, long now)
    {
        if (message.Operation != DhcpMessage.BOOT_REPLY || message.TransactionId != m_transactionId ||
            message.ClientMac != m_stack.LinkAddress)
            return;
        switch (m_state, message.MessageType)
        {
            case (DhcpState.Selecting, DhcpMessage.OFFER) when message.YourAddress != Ipv4Address.Any:
                m_offered = message.YourAddress;
                m_server = message.ServerId;
                m_state = DhcpState.Requesting;
                m_attempts = 0;
                SendCurrent(now);
                break;
            case (DhcpState.Requesting or DhcpState.Renewing or DhcpState.Rebinding, DhcpMessage.ACK):
                Bind(message, now);
                break;
            case (DhcpState.Requesting or DhcpState.Renewing or DhcpState.Rebinding, DhcpMessage.NAK):
                Start(now);
                break;
        }
    }

    internal void Advance(long now)
    {
        if (now < m_deadline)
            return;
        switch (m_state)
        {
            case DhcpState.Selecting or DhcpState.Requesting:
                if (++m_attempts >= ATTEMPTS)
                {
                    Start(now);
                    return;
                }
                SendCurrent(now);
                break;
            case DhcpState.Bound:
                m_state = DhcpState.Renewing;
                SendCurrent(now);
                break;
            case DhcpState.Renewing when now >= m_rebindAt:
                m_state = DhcpState.Rebinding;
                SendCurrent(now);
                break;
            case DhcpState.Renewing or DhcpState.Rebinding when now >= m_expiresAt:
                Start(now);
                break;
            case DhcpState.Renewing or DhcpState.Rebinding:
                SendCurrent(now);
                break;
        }
    }

    private void SendCurrent(long now)
    {
        var mac = m_stack.LinkAddress;
        switch (m_state)
        {
            case DhcpState.Selecting:
                m_stack.SendDhcpBroadcast(DhcpMessage.Discover(mac, m_transactionId));
                break;
            case DhcpState.Requesting:
                m_stack.SendDhcpBroadcast(DhcpMessage.Request(mac, m_transactionId, Ipv4Address.Any, m_offered, m_server));
                break;
            case DhcpState.Renewing:
                m_stack.SendDhcpUnicast(m_server, DhcpMessage.Request(mac, m_transactionId, m_stack.Address, Ipv4Address.Any, Ipv4Address.Any));
                break;
            case DhcpState.Rebinding:
                m_stack.SendDhcpBroadcast(DhcpMessage.Request(mac, m_transactionId, m_stack.Address, Ipv4Address.Any, Ipv4Address.Any));
                break;
        }
        m_deadline = m_state is DhcpState.Renewing or DhcpState.Rebinding
            ? Math.Min(now + RETRY_MILLISECONDS * 4, m_state == DhcpState.Renewing ? m_rebindAt : m_expiresAt)
            : now + RETRY_MILLISECONDS;
    }

    private void Bind(DhcpMessage ack, long now)
    {
        var address = ack.YourAddress != Ipv4Address.Any ? ack.YourAddress : m_stack.Address;
        var mask = ack.SubnetMask != Ipv4Address.Any ? ack.SubnetMask : new Ipv4Address(0xFFFF_FF00);
        m_server = ack.ServerId != Ipv4Address.Any ? ack.ServerId : m_server;
        m_stack.Configure(address, mask, ack.Router, ack.DnsServers);
        Lease = ack;
        m_state = DhcpState.Bound;
        if (ack.LeaseSeconds == uint.MaxValue)
        {
            m_renewAt = m_rebindAt = m_expiresAt = m_deadline = long.MaxValue;
            return;
        }
        var lease = ack.LeaseSeconds * 1000L;
        m_renewAt = now + lease / 2;
        m_rebindAt = now + lease / 8 * 7;
        m_expiresAt = now + lease;
        m_deadline = m_renewAt;
    }

    #endregion

    #region Properties

    /// <summary>
    /// When the client next needs the stack's attention; long.MaxValue for never.
    /// </summary>
    public long NextDeadline => m_deadline;

    /// <summary>
    /// Whether a lease configures the interface.
    /// </summary>
    public bool IsBound => m_state is DhcpState.Bound or DhcpState.Renewing or DhcpState.Rebinding;

    /// <summary>
    /// The last acknowledgment, which named the lease.
    /// </summary>
    public DhcpMessage? Lease { get; private set; }

    #endregion

    private enum DhcpState
    {
        Idle,
        Selecting,
        Requesting,
        Bound,
        Renewing,
        Rebinding
    }
}
