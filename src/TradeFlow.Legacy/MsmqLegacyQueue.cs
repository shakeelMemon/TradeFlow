using System.Text.Json;
using MSMQ.Messaging;

namespace TradeFlow.Legacy;

/// <summary>
/// MSMQ implementation using private, transactional queues. Receive and handler run inside one
/// MSMQ transaction, so a failed hand-off leaves the message on the queue (no message loss).
/// </summary>
public sealed class MsmqLegacyQueue : ILegacyQueue
{
    private static readonly XmlMessageFormatter Formatter = new(new[] { typeof(string) });

    private static string PathFor(string queueName) => $@".\private$\{queueName}";

    private static void EnsureQueue(string path)
    {
        if (!MessageQueue.Exists(path))
        {
            MessageQueue.Create(path, transactional: true);
        }
    }

    public void Send(string queueName, LegacyMessage message)
    {
        var path = PathFor(queueName);
        EnsureQueue(path);
        using var queue = new MessageQueue(path);
        using var msg = new Message(JsonSerializer.Serialize(message), Formatter)
        {
            Label = message.MessageType,
            Recoverable = true,
        };
        queue.Send(msg, MessageQueueTransactionType.Single);
    }

    public bool TryProcessNext(string queueName, TimeSpan timeout, Action<LegacyMessage> handler)
    {
        var path = PathFor(queueName);
        EnsureQueue(path);
        using var queue = new MessageQueue(path) { Formatter = Formatter };
        using var tx = new MessageQueueTransaction();
        tx.Begin();
        try
        {
            using var msg = queue.Receive(timeout, tx);
            var body = (string)msg.Body;
            var message = JsonSerializer.Deserialize<LegacyMessage>(body)
                ?? throw new InvalidOperationException("Empty legacy message.");
            handler(message);
            tx.Commit();
            return true;
        }
        catch (MessageQueueException ex) when (ex.MessageQueueErrorCode == MessageQueueErrorCode.IOTimeout)
        {
            tx.Abort();
            return false;
        }
        catch
        {
            tx.Abort();
            throw;
        }
    }
}
