using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace TradeFlow.Messaging;

internal static class ServiceBusClientFactory
{
    /// <summary>
    /// Identity-based sign-in when FullyQualifiedNamespace is set: locally this uses your Azure CLI login,
    /// in Azure a Managed Identity. Otherwise falls back to a SAS connection string.
    /// </summary>
    public static ServiceBusClient Create(MessagingOptions options)
    {
        var sb = options.AzureServiceBus;
        if (!string.IsNullOrWhiteSpace(sb.FullyQualifiedNamespace))
        {
            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeVisualStudioCredential = true,
                ExcludeVisualStudioCodeCredential = true,
                ExcludeSharedTokenCacheCredential = true,
                ExcludeInteractiveBrowserCredential = true,
            });
            return new ServiceBusClient(sb.FullyQualifiedNamespace, credential);
        }
        return new ServiceBusClient(sb.ConnectionString);
    }
}

public sealed class AzureServiceBusPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;

    public AzureServiceBusPublisher(MessagingOptions options)
    {
        _client = ServiceBusClientFactory.Create(options);
        _sender = _client.CreateSender(options.AzureServiceBus.Topic);
    }

    public Task PublishAsync(BusMessage message, CancellationToken cancellationToken)
    {
        var sbMessage = new ServiceBusMessage(message.Body)
        {
            MessageId = message.MessageId, // enables duplicate detection on the topic
            CorrelationId = message.CorrelationId,
            Subject = message.EventType,
            ContentType = "application/json",
        };
        sbMessage.ApplicationProperties["eventType"] = message.EventType;
        return _sender.SendMessageAsync(sbMessage, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}

public sealed class AzureServiceBusConsumer(MessagingOptions options, ILogger<AzureServiceBusConsumer> logger) : IEventConsumer
{
    public async Task RunAsync(Func<BusMessage, CancellationToken, Task> handler, CancellationToken cancellationToken)
    {
        await using var client = ServiceBusClientFactory.Create(options);
        var subscription = string.IsNullOrWhiteSpace(options.Subscription)
            ? options.AzureServiceBus.Subscription
            : options.Subscription;
        await using var processor = client.CreateProcessor(
            options.AzureServiceBus.Topic,
            subscription,
            new ServiceBusProcessorOptions { AutoCompleteMessages = false, MaxConcurrentCalls = 4 });

        processor.ProcessMessageAsync += async args =>
        {
            var m = args.Message;
            var message = new BusMessage(m.Subject ?? "", m.Body.ToString(), m.MessageId, m.CorrelationId ?? "");
            try
            {
                await handler(message, args.CancellationToken);
                await args.CompleteMessageAsync(m, args.CancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Handler failed for {MessageId} (delivery {Count}); abandoning for retry",
                    m.MessageId, m.DeliveryCount);
                await args.AbandonMessageAsync(m, cancellationToken: args.CancellationToken);
            }
        };
        processor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Service Bus error from {Source}", args.ErrorSource);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(cancellationToken);
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        await processor.StopProcessingAsync(CancellationToken.None);
    }
}
