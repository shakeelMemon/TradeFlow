namespace TradeFlow.Legacy;

/// <summary>
/// Point-to-point legacy queue (MSMQ in production-like runs, a folder queue elsewhere).
/// </summary>
public interface ILegacyQueue
{
    void Send(string queueName, LegacyMessage message);

    /// <summary>
    /// Receives the next message and runs <paramref name="handler"/>. The message is removed
    /// only if the handler succeeds; if it throws, the message goes back on the queue.
    /// Returns false when no message arrived within <paramref name="timeout"/>.
    /// </summary>
    bool TryProcessNext(string queueName, TimeSpan timeout, Action<LegacyMessage> handler);
}
