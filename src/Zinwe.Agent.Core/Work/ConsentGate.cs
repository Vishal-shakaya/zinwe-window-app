namespace Zinwe.Agent.Core.Work;

/// <summary>
/// "Has the member agreed to monitoring on this PC?" Until they press "I agree" on the widget's consent
/// screen (for the current <see cref="ConsentReader.CurrentVersion"/>), the agent records nothing about
/// their activity: <see cref="Events.EventQueue"/> and <see cref="Metrics.MetricsStore"/> drop new rows.
/// Protection rules (USB blocking and the like) stay on — they're the company's device policy, not a record
/// of the person. The answer is cached briefly so the hot recording paths don't read the file every time.
/// Thread-safe.
/// </summary>
public sealed class ConsentGate
{
    /// <summary>How long an answer is reused before the consent file is read again.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(5);

    private readonly ConsentReader _reader;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private ConsentRecord? _record;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public ConsentGate(ConsentReader reader, TimeProvider? clock = null)
    {
        _reader = reader;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The member's recorded consent (any version), or null — what the heartbeat reports.</summary>
    public ConsentRecord? Current()
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (now - _readAt >= RecheckInterval || now < _readAt)
            {
                _record = _reader.Read();
                _readAt = now;
            }
            return _record;
        }
    }

    /// <summary>True when the member agreed to the current consent notice: recording may run.</summary>
    public bool IsGranted() => Current()?.Version == ConsentReader.CurrentVersion;

    /// <summary>Forget the cached answer (the next check reads the file).</summary>
    public void Refresh()
    {
        lock (_lock)
        {
            _readAt = DateTimeOffset.MinValue;
        }
    }
}
