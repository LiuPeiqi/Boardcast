namespace Boardcast.Core;

/// <summary>Admits measured, useful peer sources without repeatedly probing slow or unavailable devices.</summary>
public sealed class ChunkPeerPolicy
{
    // Conservative starting points; these are tuning values, not protocol requirements.
    public const long MinimumPeerTransferBytes = 64L * 1024 * 1024;
    public const long SlowOriginBytesPerSecond = 32L * 1024 * 1024;
    private const double MinimumPeerSpeedRatio = 0.8;
    private const double SampleWeight = 0.25;
    private static readonly TimeSpan InitialCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumCooldown = TimeSpan.FromMinutes(5);

    private readonly object _sync = new();
    private readonly Dictionary<string, PeerState> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _totalBytes;
    private readonly long _minimumVerifiedBytes;
    private readonly int _chunkSize;
    private readonly TimeProvider _timeProvider;
    private int _originSamples;
    private double _originBytesPerSecond;
    private bool _probeInFlight;

    public ChunkPeerPolicy(long totalBytes, int chunkSize = ChunkTransfer.DefaultChunkSize, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        _totalBytes = totalBytes;
        _chunkSize = chunkSize;
        _minimumVerifiedBytes = 2L * chunkSize;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>A bounded probe timeout based on the measured origin speed.</summary>
    public TimeSpan PeerTimeout
    {
        get
        {
            lock (_sync)
            {
                var seconds = _originBytesPerSecond > 0 ? 0.5 + 4 * _chunkSize / _originBytesPerSecond : 2;
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 2, 10));
            }
        }
    }

    /// <summary>Use peers when the sender is serving more than one recipient.</summary>
    public bool ShouldTryPeer(string fingerprint, int activeRecipients, long peerVerifiedBytes) =>
        ShouldTryPeer(fingerprint, activeRecipients >= 2, peerVerifiedBytes);

    /// <summary>Use when the caller has evidence that the original source is contended.</summary>
    public bool ShouldTryPeer(string fingerprint, bool sourceContended, long peerVerifiedBytes)
    {
        lock (_sync)
            return TryReservePeer(fingerprint, sourceContended, peerVerifiedBytes);
    }

    /// <summary>Without known contention, probe peers only after the origin measures below the low-speed threshold.</summary>
    public bool ShouldTryPeer(string fingerprint, long peerVerifiedBytes)
    {
        lock (_sync)
            return TryReservePeer(fingerprint, _originBytesPerSecond < SlowOriginBytesPerSecond, peerVerifiedBytes);
    }

    public void RecordOrigin(int bytes, TimeSpan duration)
    {
        if (bytes <= 0 || duration <= TimeSpan.Zero) return;
        var speed = bytes / duration.TotalSeconds;
        lock (_sync)
        {
            _originBytesPerSecond = _originSamples == 0 ? speed : Smooth(_originBytesPerSecond, speed);
            // Only the existence of two samples matters; avoid a counter that can overflow on long transfers.
            _originSamples = Math.Min(2, _originSamples + 1);
        }
    }

    /// <summary>
    /// Complete every successful reservation, including failures and cancellations. The caller must verify
    /// the chunk before reporting success. One source has at most one request in flight at a time.
    /// </summary>
    public void RecordPeer(string fingerprint, int bytes, TimeSpan duration, bool successful)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return;
        lock (_sync)
        {
            if (!_peers.TryGetValue(fingerprint, out var peer) || !peer.InFlight) return;
            peer.InFlight = false;
            if (peer.Probing)
            {
                peer.Probing = false;
                _probeInFlight = false;
            }

            if (successful && bytes > 0 && duration > TimeSpan.Zero)
            {
                var speed = bytes / duration.TotalSeconds;
                peer.BytesPerSecond = peer.Proven ? Smooth(peer.BytesPerSecond, speed) : speed;
                // Reject a newly slow request immediately rather than hiding it in the moving average.
                if (speed >= _originBytesPerSecond * MinimumPeerSpeedRatio &&
                    peer.BytesPerSecond >= _originBytesPerSecond * MinimumPeerSpeedRatio)
                {
                    peer.Proven = true;
                    peer.Failures = 0;
                    peer.Cooldown = TimeSpan.Zero;
                    return;
                }
            }

            peer.Proven = false;
            peer.Failures = Math.Min(5, peer.Failures + 1);
            peer.Cooldown = TimeSpan.FromSeconds(Math.Min(MaximumCooldown.TotalSeconds,
                InitialCooldown.TotalSeconds * (1 << (peer.Failures - 1))));
            peer.CooldownStarted = _timeProvider.GetTimestamp();
        }
    }

    // All callers hold _sync. Returning true reserves a request, so parallel workers cannot probe the same source.
    private bool TryReservePeer(string fingerprint, bool useful, long peerVerifiedBytes)
    {
        if (!useful || _totalBytes < MinimumPeerTransferBytes || peerVerifiedBytes < _minimumVerifiedBytes ||
            _originSamples < 2 || string.IsNullOrWhiteSpace(fingerprint))
            return false;

        if (!_peers.TryGetValue(fingerprint, out var peer))
        {
            peer = new PeerState();
            _peers.Add(fingerprint, peer);
        }
        if (peer.InFlight || (peer.Cooldown > TimeSpan.Zero &&
            _timeProvider.GetElapsedTime(peer.CooldownStarted) < peer.Cooldown))
            return false;
        if (!peer.Proven && _probeInFlight) return false;

        peer.InFlight = true;
        peer.Probing = !peer.Proven;
        if (peer.Probing) _probeInFlight = true;
        return true;
    }

    private static double Smooth(double previous, double current) =>
        previous * (1 - SampleWeight) + current * SampleWeight;

    private sealed class PeerState
    {
        public bool InFlight { get; set; }
        public bool Probing { get; set; }
        public bool Proven { get; set; }
        public double BytesPerSecond { get; set; }
        public int Failures { get; set; }
        public long CooldownStarted { get; set; }
        public TimeSpan Cooldown { get; set; }
    }
}
