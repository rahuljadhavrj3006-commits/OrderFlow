using Microsoft.Extensions.Options;

namespace OrderFlow.Shared;

public enum CircuitState { Closed, Open, HalfOpen }

/// <summary>
/// A small, thread-safe circuit breaker.
///   Closed   : calls flow; consecutive failures are counted.
///   Open     : calls are rejected immediately (fail fast) so a sick dependency isn't hammered.
///   HalfOpen : after the break duration exactly ONE probe call is let through;
///              success closes the circuit, failure re-opens it.
/// It is registered as a singleton because IHttpClientFactory recycles handler instances,
/// and the breaker's state must outlive them.
/// </summary>
public sealed class CircuitBreaker(IOptions<ResilienceOptions> options, TimeProvider time)
{
    private readonly object _gate = new();
    private readonly ResilienceOptions _options = options.Value;
    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _openedAt;
    private bool _probeInFlight;

    public CircuitState State
    {
        get { lock (_gate) return _state; }
    }

    public bool AllowRequest()
    {
        lock (_gate)
        {
            switch (_state)
            {
                case CircuitState.Closed:
                    return true;

                case CircuitState.Open:
                    if (time.GetUtcNow() - _openedAt < TimeSpan.FromSeconds(_options.BreakDurationSeconds))
                    {
                        return false;
                    }
                    _state = CircuitState.HalfOpen;
                    _probeInFlight = true;
                    return true;

                default: // HalfOpen
                    if (_probeInFlight)
                    {
                        return false; // someone else is already probing
                    }
                    _probeInFlight = true;
                    return true;
            }
        }
    }

    public void OnSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _probeInFlight = false;
            _state = CircuitState.Closed;
        }
    }

    public void OnFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            if (_state == CircuitState.HalfOpen || _consecutiveFailures >= _options.FailureThreshold)
            {
                _state = CircuitState.Open;
                _openedAt = time.GetUtcNow();
                _probeInFlight = false;
            }
        }
    }
}
