namespace MultiSeat.Protocol;

/// <summary>
/// Rejects replayed or stale packets on the receiving side.
/// </summary>
/// <remarks>
/// A packet is accepted when its timestamp is within <see cref="MaxClockSkew"/> of the local clock
/// (Hyper-V time synchronisation keeps host and guest clocks aligned) and, for the current session,
/// its sequence number is higher than any seen before. A new session id (router restart) is accepted
/// only with a fresh timestamp that is not older than the newest packet already accepted, so captured
/// traffic from an earlier session cannot be replayed to hijack the current one.
/// </remarks>
public sealed class ReplayGuard
{
    private bool _hasSession;
    private uint _session;
    private ulong _lastSequence;
    private long _lastTimestampMs = long.MinValue;

    public ReplayGuard(TimeSpan? maxClockSkew = null)
    {
        MaxClockSkew = maxClockSkew ?? TimeSpan.FromSeconds(10);
    }

    public TimeSpan MaxClockSkew { get; }

    /// <summary>True when the last accepted packet started a new session.</summary>
    public bool SessionChanged { get; private set; }

    public bool Accept(in PacketHeader header, long nowMs)
    {
        SessionChanged = false;
        if (Math.Abs(nowMs - header.TimestampMs) > (long)MaxClockSkew.TotalMilliseconds)
        {
            return false;
        }

        if (!_hasSession || header.Session != _session)
        {
            if (header.TimestampMs < _lastTimestampMs)
            {
                return false;
            }

            _hasSession = true;
            _session = header.Session;
            _lastSequence = header.Sequence;
            _lastTimestampMs = header.TimestampMs;
            SessionChanged = true;
            return true;
        }

        if (header.Sequence <= _lastSequence)
        {
            return false;
        }

        _lastSequence = header.Sequence;
        _lastTimestampMs = Math.Max(_lastTimestampMs, header.TimestampMs);
        return true;
    }
}
