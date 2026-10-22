using Microsoft.Extensions.Options;
using OrderFlow.Shared;

namespace OrderFlow.Shared.Tests;

public class CircuitBreakerTests
{
    /// <summary>Controllable clock: tests never sleep, they just move time forward.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static CircuitBreaker Create(ManualTime time) => new(
        Options.Create(new ResilienceOptions { FailureThreshold = 3, BreakDurationSeconds = 10 }),
        time);

    [Fact]
    public void Opens_after_consecutive_failures_reach_threshold()
    {
        var breaker = Create(new ManualTime());

        breaker.OnFailure();
        breaker.OnFailure();
        Assert.Equal(CircuitState.Closed, breaker.State);

        breaker.OnFailure();
        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.False(breaker.AllowRequest());
    }

    [Fact]
    public void A_success_resets_the_failure_count()
    {
        var breaker = Create(new ManualTime());

        breaker.OnFailure();
        breaker.OnFailure();
        breaker.OnSuccess();
        breaker.OnFailure();
        breaker.OnFailure();

        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public void After_break_duration_only_one_probe_is_allowed()
    {
        var time = new ManualTime();
        var breaker = Create(time);
        for (var i = 0; i < 3; i++) breaker.OnFailure();

        time.Advance(TimeSpan.FromSeconds(11));

        Assert.True(breaker.AllowRequest());   // the probe
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        Assert.False(breaker.AllowRequest());  // everyone else still waits
    }

    [Fact]
    public void Successful_probe_closes_the_circuit()
    {
        var time = new ManualTime();
        var breaker = Create(time);
        for (var i = 0; i < 3; i++) breaker.OnFailure();
        time.Advance(TimeSpan.FromSeconds(11));
        breaker.AllowRequest();

        breaker.OnSuccess();

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.True(breaker.AllowRequest());
    }

    [Fact]
    public void Failed_probe_reopens_the_circuit()
    {
        var time = new ManualTime();
        var breaker = Create(time);
        for (var i = 0; i < 3; i++) breaker.OnFailure();
        time.Advance(TimeSpan.FromSeconds(11));
        breaker.AllowRequest();

        breaker.OnFailure();

        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.False(breaker.AllowRequest());
    }
}
