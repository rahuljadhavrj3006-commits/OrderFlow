namespace OrderFlow.Shared;

public sealed class ResilienceOptions
{
    /// <summary>How many times a transient failure is retried (so total attempts = MaxRetries + 1).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay for exponential backoff: 200ms, 400ms, 800ms ... (with jitter).</summary>
    public int BaseDelayMs { get; set; } = 200;

    /// <summary>Per-attempt timeout.</summary>
    public int TimeoutSeconds { get; set; } = 2;

    /// <summary>Consecutive failures before the circuit opens.</summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>How long the circuit stays open before one probe request is allowed.</summary>
    public int BreakDurationSeconds { get; set; } = 15;
}
