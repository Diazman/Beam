using System.Net;
using System.Security.Cryptography;

namespace Beam.Core.Direct;

/// <summary>
/// Where one sender-hosted direct session stands (sender side, one per receiver). Setup goes forward only:
/// <see cref="Negotiating"/> → <see cref="Hosting"/> → <see cref="OfferingCredentials"/> → <see cref="Joining"/> →
/// <see cref="Verifying"/> → <see cref="Verified"/> → <see cref="Transferring"/>. A verified link that drops is
/// <see cref="Interrupted"/> and may set up again (back to <see cref="Negotiating"/>). The last three are final.
/// </summary>
public enum DirectSessionState
{
    /// <summary>Authenticating the receiver over the control channel and asking whether it accepts a direct session.</summary>
    Negotiating,

    /// <summary>This device (the sender) is starting its direct network.</summary>
    Hosting,

    /// <summary>Handing the receiver this session's network name and passphrase over the control channel.</summary>
    OfferingCredentials,

    /// <summary>The receiver is joining (it may be waiting for its user to approve the system's Wi-Fi prompt).</summary>
    Joining,

    /// <summary>The receiver said it joined; checking peer, session and route over the direct network itself.</summary>
    Verifying,

    /// <summary>Peer, session, route and transfer endpoint are verified on the direct network: files may go.</summary>
    Verified,

    /// <summary>File data is moving over the verified direct endpoint.</summary>
    Transferring,

    /// <summary>The verified direct link dropped; a bounded reconnect may follow.</summary>
    Interrupted,

    Completed,
    Cancelled,
    Failed,
}

/// <summary>How the two devices talk while setting up the session (never what the files travel over).</summary>
public enum DirectControlChannel
{
    None,

    /// <summary>Beam's mutually authenticated TLS connection over the shared network.</summary>
    Lan,

    /// <summary>The encrypted Bluetooth LE handshake (no shared network).</summary>
    Bluetooth,
}

/// <summary>
/// One sender-hosted direct session with one receiver: its own id, nonce, deadline, control channel, state and
/// cancellation. Changing state is thread-safe; nothing moves a session out of a final state, so a late callback can't
/// bring back a session that was cancelled or failed.
/// </summary>
public sealed class DirectSession
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private DirectSessionState _state = DirectSessionState.Negotiating;
    private DirectSessionState? _failedAt;
    private string? _error;

    internal DirectSession(string peerId, string peerName, TimeSpan lifetime)
    {
        Id = NewToken();
        Nonce = NewToken();
        PeerId = peerId;
        PeerName = peerName;
        Lifetime = lifetime;
        ExpiresAt = Environment.TickCount64 + (long)lifetime.TotalMilliseconds;
    }

    /// <summary>128 random bits, hex: session ids and nonces.</summary>
    internal static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>This session's id on the wire (changes when it sets up again after the link dropped).</summary>
    public string Id { get; private set; }

    /// <summary>Stays the same for the session's whole life (what owns its share of the sender's network).</summary>
    internal string Key { get; } = NewToken();

    /// <summary>Proves later messages (ready, verify, end) belong to this session; changes when the session sets up again.</summary>
    public string Nonce { get; private set; }

    public string PeerId { get; }

    public string PeerName { get; }

    /// <summary>The receiver's certificate fingerprint as authenticated on the control channel (empty until then).</summary>
    public string PeerFingerprint { get; internal set; } = "";

    public DirectControlChannel Channel { get; internal set; }

    internal TimeSpan Lifetime { get; }

    /// <summary>Setup must be verified by then (<see cref="Environment.TickCount64"/>).</summary>
    internal long ExpiresAt { get; private set; }

    public bool IsExpired => Environment.TickCount64 > ExpiresAt;

    /// <summary>The receiver's Beam on the direct network, once verified (the only place this session's files go).</summary>
    public IPEndPoint? VerifiedEndpoint { get; private set; }

    /// <summary>How often this session was set up again after its direct link dropped.</summary>
    public int Reconnects { get; internal set; }

    public DirectSessionState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    /// <summary>The stage that failed (null unless <see cref="State"/> is <see cref="DirectSessionState.Failed"/>).</summary>
    public DirectSessionState? FailedAt
    {
        get
        {
            lock (_gate) return _failedAt;
        }
    }

    /// <summary>Why it failed, for the user.</summary>
    public string? Error
    {
        get
        {
            lock (_gate) return _error;
        }
    }

    public bool IsFinished => IsFinal(State);

    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>State changed (any thread).</summary>
    public event Action<DirectSession>? Changed;

    public static bool IsFinal(DirectSessionState state) => state is DirectSessionState.Completed or DirectSessionState.Cancelled or DirectSessionState.Failed;

    /// <summary>
    /// Moves to <paramref name="next"/> if that is a valid step from where the session is. False (nothing changes) when
    /// it isn't: a final state, a step backwards, or skipping verification.
    /// </summary>
    internal bool TryMove(DirectSessionState next)
    {
        lock (_gate)
        {
            if (!CanMove(_state, next)) return false;
            _state = next;
        }

        Raise();
        return true;
    }

    internal static bool CanMove(DirectSessionState from, DirectSessionState to)
    {
        if (IsFinal(from) || from == to) return false;
        return to switch
        {
            DirectSessionState.Failed or DirectSessionState.Cancelled => true,
            DirectSessionState.Completed => from is DirectSessionState.Verified or DirectSessionState.Transferring or DirectSessionState.Interrupted,
            DirectSessionState.Interrupted => from is DirectSessionState.Verified or DirectSessionState.Transferring,
            DirectSessionState.Transferring => from is DirectSessionState.Verified or DirectSessionState.Interrupted,
            DirectSessionState.Negotiating => from == DirectSessionState.Interrupted,
            // Setup only goes forward, one stage at a time (Verified only from Verifying).
            _ => from < DirectSessionState.Verified && to == from + 1,
        };
    }

    /// <summary>Verification passed: files may go to <paramref name="endpoint"/>, and only there.</summary>
    internal bool MarkVerified(IPEndPoint endpoint)
    {
        lock (_gate)
        {
            if (_state != DirectSessionState.Verifying) return false;
            VerifiedEndpoint = endpoint;
            _state = DirectSessionState.Verified;
        }

        Raise();
        return true;
    }

    /// <summary>Setting up again after the link dropped: a new id, nonce and deadline; the old endpoint no longer counts.</summary>
    internal bool Restart()
    {
        lock (_gate)
        {
            if (_state != DirectSessionState.Interrupted) return false;
            _state = DirectSessionState.Negotiating;
            VerifiedEndpoint = null;
            Id = NewToken();
            Nonce = NewToken();
            ExpiresAt = Environment.TickCount64 + (long)Lifetime.TotalMilliseconds;
            Reconnects++;
        }

        Raise();
        return true;
    }

    /// <summary>Fails the session at the stage it is in (first failure wins). False when it had already ended.</summary>
    internal bool Fail(string message)
    {
        lock (_gate)
        {
            if (IsFinal(_state)) return false;
            _failedAt = _state;
            _error = message;
            _state = DirectSessionState.Failed;
            VerifiedEndpoint = null;
        }

        _cts.Cancel();
        Raise();
        return true;
    }

    /// <summary>Cancels this session only (other receivers' sessions go on).</summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            if (IsFinal(_state)) return false;
            _state = DirectSessionState.Cancelled;
            VerifiedEndpoint = null;
        }

        _cts.Cancel();
        Raise();
        return true;
    }

    /// <summary>The stage, for the user ("Direct connection failed at receiver join: …").</summary>
    public static string StageName(DirectSessionState state) => state switch
    {
        DirectSessionState.Negotiating => Localization.L.T("negotiation"),
        DirectSessionState.Hosting => Localization.L.T("starting the direct network"),
        DirectSessionState.OfferingCredentials => Localization.L.T("sending the connection details"),
        DirectSessionState.Joining => Localization.L.T("receiver join"),
        DirectSessionState.Verifying => Localization.L.T("verifying the direct link"),
        _ => Localization.L.T("transfer over the direct link"),
    };

    private void Raise()
    {
        try
        {
            Changed?.Invoke(this);
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error("DirectSession.Changed handler threw", ex);
        }
    }

    public override string ToString() => $"direct session {Id[..8]} with {PeerName} ({State})";
}
