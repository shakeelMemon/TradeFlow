namespace TradeFlow.Messaging;

/// <summary>A message on the modern bus, independent of the cloud provider.</summary>
public sealed record BusMessage(string EventType, string Body, string MessageId, string CorrelationId);

/// <summary>Publishes events to the trade topic (Azure Service Bus topic or AWS SNS topic).</summary>
public interface IEventPublisher
{
    Task PublishAsync(BusMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Receives events from the subscriber's queue (Service Bus subscription or SQS queue).
/// If the handler throws, the message is not completed and the provider redelivers it,
/// moving it to the dead-letter queue after the configured number of attempts.
/// </summary>
public interface IEventConsumer
{
    Task RunAsync(Func<BusMessage, CancellationToken, Task> handler, CancellationToken cancellationToken);
}
