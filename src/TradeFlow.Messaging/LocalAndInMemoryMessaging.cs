using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TradeFlow.Messaging;

/// <summary>
/// Cross-process bus backed by folders, so the whole system runs locally with no cloud account.
/// Works like a topic: each subscription has its own folder and gets its own copy of every event.
/// </summary>
public sealed class LocalFileBus(MessagingOptions options, ILogger<LocalFileBus> logger) : IEventPublisher, IEventConsumer
{
    private string SubscriptionDir(string subscription)
    {
        var dir = Path.Combine(options.LocalFile.Root, options.LocalFile.Topic, subscription);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string MySubscription =>
        string.IsNullOrWhiteSpace(options.Subscription) ? "simcorp" : options.Subscription;

    public Task PublishAsync(BusMessage message, CancellationToken cancellationToken)
    {
        var name = $"{DateTime.UtcNow.Ticks:D20}_{message.MessageId}";
        var json = JsonSerializer.Serialize(message);
        foreach (var subscription in options.LocalFile.Subscriptions.Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = SubscriptionDir(subscription);
            var tmp = Path.Combine(dir, name + ".tmp");
            File.WriteAllText(tmp, json);
            File.Move(tmp, Path.Combine(dir, name + ".json"));
        }
        return Task.CompletedTask;
    }

    public async Task RunAsync(Func<BusMessage, CancellationToken, Task> handler, CancellationToken cancellationToken)
    {
        var dir = SubscriptionDir(MySubscription);
        var deadLetterDir = SubscriptionDir(MySubscription + "-deadletter");
        var attempts = new Dictionary<string, int>();
        while (!cancellationToken.IsCancellationRequested)
        {
            var processed = false;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order().ToList())
            {
                var message = JsonSerializer.Deserialize<BusMessage>(await File.ReadAllTextAsync(file, cancellationToken))!;
                try
                {
                    await handler(message, cancellationToken);
                    File.Delete(file);
                    attempts.Remove(file);
                    processed = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var count = attempts.GetValueOrDefault(file) + 1;
                    attempts[file] = count;
                    logger.LogWarning(ex, "Handler failed for {MessageId} (attempt {Count})", message.MessageId, count);
                    if (count >= 5)
                    {
                        File.Move(file, Path.Combine(deadLetterDir, Path.GetFileName(file)));
                        attempts.Remove(file);
                        logger.LogError("Moved {MessageId} to dead-letter after {Count} attempts", message.MessageId, count);
                    }
                    break; // back off before retrying
                }
            }

            if (!processed)
            {
                try
                {
                    await Task.Delay(500, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}

/// <summary>Single-process bus for automated tests.</summary>
public sealed class InMemoryBus : IEventPublisher, IEventConsumer
{
    private readonly Channel<BusMessage> _channel = Channel.CreateUnbounded<BusMessage>();

    public ConcurrentQueue<BusMessage> Published { get; } = new();

    public async Task PublishAsync(BusMessage message, CancellationToken cancellationToken)
    {
        Published.Enqueue(message);
        await _channel.Writer.WriteAsync(message, cancellationToken);
    }

    public async Task RunAsync(Func<BusMessage, CancellationToken, Task> handler, CancellationToken cancellationToken)
    {
        await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            await handler(message, cancellationToken);
        }
    }
}
