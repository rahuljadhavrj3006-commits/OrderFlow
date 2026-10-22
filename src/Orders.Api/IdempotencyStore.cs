using System.Collections.Concurrent;
using OrderFlow.Shared;

namespace Orders.Api;

/// <summary>
/// Makes "create order" safe to retry. Two requests with the same Idempotency-Key (a double click, a client retry
/// after a timeout) share ONE execution and get the same result.
///
/// Key technique: ConcurrentDictionary&lt;string, Lazy&lt;Task&lt;T&gt;&gt;&gt;.
/// GetOrAdd alone can run its factory several times under contention (only one result is kept);
/// wrapping the work in Lazy with ExecutionAndPublication guarantees the work itself starts once.
/// </summary>
public sealed class IdempotencyStore
{
    private sealed record Entry(string Fingerprint, Lazy<Task<Order>> Result);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public async Task<Order> ExecuteAsync(string key, string fingerprint, Func<Task<Order>> factory, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry(
            fingerprint,
            new Lazy<Task<Order>>(factory, LazyThreadSafetyMode.ExecutionAndPublication)));

        // Same key, different payload = client bug (or an attack). Reject rather than silently returning the wrong order.
        if (entry.Fingerprint != fingerprint)
        {
            throw new ConflictException("This Idempotency-Key was already used with a different request payload.");
        }

        var task = entry.Result.Value;
        try
        {
            // WaitAsync lets THIS caller stop waiting when it is cancelled, without cancelling the shared work.
            return await task.WaitAsync(ct);
        }
        finally
        {
            // Don't cache failures: a retry with the same key must get a fresh attempt.
            if (task.IsFaulted)
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
        }
    }
}
