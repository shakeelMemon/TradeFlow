using Amazon;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Microsoft.Extensions.Logging;
using PublishRequest = Amazon.SimpleNotificationService.Model.PublishRequest;
using ReceiveMessageRequest = Amazon.SQS.Model.ReceiveMessageRequest;
using ReceiveMessageResponse = Amazon.SQS.Model.ReceiveMessageResponse;
using SnsAttribute = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using SqsMessage = Amazon.SQS.Model.Message;

namespace TradeFlow.Messaging;

/// <summary>Publishes to an SNS topic. Credentials come from the default AWS chain (aws configure).</summary>
public sealed class AwsSnsPublisher : IEventPublisher, IDisposable
{
    private readonly MessagingOptions _options;
    private readonly AmazonSimpleNotificationServiceClient _sns;

    public AwsSnsPublisher(MessagingOptions options)
    {
        _options = options;
        _sns = new AmazonSimpleNotificationServiceClient(RegionEndpoint.GetBySystemName(options.Aws.Region));
    }

    public Task PublishAsync(BusMessage message, CancellationToken cancellationToken)
    {
        var request = new PublishRequest
        {
            TopicArn = _options.Aws.TopicArn,
            Message = message.Body,
            MessageAttributes = new Dictionary<string, SnsAttribute>
            {
                ["eventType"] = new() { DataType = "String", StringValue = message.EventType },
                ["correlationId"] = new() { DataType = "String", StringValue = message.CorrelationId },
                ["messageId"] = new() { DataType = "String", StringValue = message.MessageId },
            },
        };
        return _sns.PublishAsync(request, cancellationToken);
    }

    public void Dispose() => _sns.Dispose();
}

/// <summary>
/// Long-polls an SQS queue subscribed to the SNS topic with raw message delivery.
/// Messages are deleted only after the handler succeeds; failures become visible again and
/// move to the DLQ via the queue's redrive policy.
/// </summary>
public sealed class AwsSqsConsumer(MessagingOptions options, ILogger<AwsSqsConsumer> logger) : IEventConsumer
{
    public async Task RunAsync(Func<BusMessage, CancellationToken, Task> handler, CancellationToken cancellationToken)
    {
        using var sqs = new AmazonSQSClient(RegionEndpoint.GetBySystemName(options.Aws.Region));
        while (!cancellationToken.IsCancellationRequested)
        {
            ReceiveMessageResponse response;
            try
            {
                response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = options.Aws.QueueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 20,
                    MessageAttributeNames = new List<string> { "All" },
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            foreach (var m in response.Messages ?? new List<SqsMessage>())
            {
                string Attr(string name) =>
                    m.MessageAttributes != null && m.MessageAttributes.TryGetValue(name, out var v) ? v.StringValue : "";

                var message = new BusMessage(Attr("eventType"), m.Body, Attr("messageId"), Attr("correlationId"));
                try
                {
                    await handler(message, cancellationToken);
                    await sqs.DeleteMessageAsync(options.Aws.QueueUrl, m.ReceiptHandle, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Handler failed for SQS message {MessageId}; it will be retried", m.MessageId);
                }
            }
        }
    }
}
