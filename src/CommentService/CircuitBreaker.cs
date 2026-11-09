// A minimal, hand-rolled circuit breaker guarding the call to the
// ProfanityService, so a failure in that swimlane cannot cascade into the
// CommentService.
//
//   Closed ──(>= threshold failures)──> Open ──(cooldown elapsed)──> HalfOpen
//     ▲                                                                 │
//     └────────────────────────(success)────────────────────────────────┘
//
// While Open, callers skip the remote call entirely (fail fast). Once the
// cooldown has elapsed the breaker enters HalfOpen and lets calls through; a
// success closes it again, another failure reopens it.
public sealed class CircuitBreaker
{
    private readonly object _gate = new();
    private readonly int _threshold;
    private readonly TimeSpan _openDuration;

    private int _failures;
    private DateTimeOffset _openedAt;

    public CircuitBreaker(int threshold = 3, TimeSpan? openDuration = null)
    {
        _threshold = threshold;
        _openDuration = openDuration ?? TimeSpan.FromSeconds(15);
    }

    public string State
    {
        get
        {
            lock (_gate)
            {
                if (_failures < _threshold) return "closed";
                return DateTimeOffset.UtcNow - _openedAt >= _openDuration ? "half-open" : "open";
            }
        }
    }

    // True while the breaker is Open — the caller must skip the remote call.
    public bool IsOpen => State == "open";

    public void RecordSuccess()
    {
        lock (_gate) _failures = 0;
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _failures++;
            if (_failures >= _threshold) _openedAt = DateTimeOffset.UtcNow;
        }
    }
}
