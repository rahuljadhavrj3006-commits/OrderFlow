using System.Threading.Channels;

namespace Orders.Api;

public enum OrderEventType { Confirmed, Cancelled }

public sealed record OrderEvent(Guid OrderId, OrderEventType Type, DateTimeOffset At);

public interface IEventPublisher
{
    ValueTask PublishAsync(OrderEvent orderEvent, CancellationToken ct = default);
}

/// <summary>
/// In-process "message bus" built on a bounded Channel. The bound gives BACKPRESSURE:
/// if consumers fall behind, producers wait instead of memory growing without limit.
/// (In a real system this would be Azure Service Bus / RabbitMQ + the outbox pattern.)
/// </summary>
public sealed class ChannelEventPublisher(Channel<OrderEvent> channel) : IEventPublisher
{
    public ValueTask PublishAsync(OrderEvent orderEvent, CancellationToken ct = default) =>
        channel.Writer.WriteAsync(orderEvent, ct);
}

public sealed class OrderStats
{
    private long _processed;
    public long Processed => Interlocked.Read(ref _processed);
    public void Increment() => Interlocked.Increment(ref _processed);
}

/// <summary>
/// Hosted background worker that drains the channel with bounded parallelism (4 at a time).
/// Parallel.ForEachAsync over an IAsyncEnumerable = async streaming + throttled concurrency in a few lines.
/// </summary>
public sealed class OrderEventProcessor(
    Channel<OrderEvent> channel,
    OrderStats stats,
    ILogger<OrderEventProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Parallel.ForEachAsync(
                channel.Reader.ReadAllAsync(stoppingToken),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken },
                async (orderEvent, ct) =>
                {
                    await Task.Delay(200, ct); // pretend to send an email / call a notification service
                    logger.LogInformation("Processed {Type} event for order {OrderId}", orderEvent.Type, orderEvent.OrderId);
                    stats.Increment();
                });
        }
        catch (OperationCanceledException)
        {
            // Normal during application shutdown.
        }
    }
}
