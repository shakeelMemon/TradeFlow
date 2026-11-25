namespace TradeFlow.Legacy;

/// <summary>Envelope Calypso writes to the legacy queue. Payload is JSON for the given MessageType.</summary>
public sealed record LegacyMessage(
    string MessageId,
    string MessageType,
    string CorrelationId,
    string Payload,
    DateTimeOffset SentAt);
