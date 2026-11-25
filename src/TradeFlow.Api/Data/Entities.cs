using TradeFlow.Contracts;

namespace TradeFlow.Api.Data;

public sealed class OrderEntity
{
    public Guid Id { get; set; }
    public string ClientOrderId { get; set; } = "";
    public string SourceSystem { get; set; } = "";
    public string MessageType { get; set; } = "";
    public string InstrumentId { get; set; } = "";
    public Side Side { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; } = "";
    public string Portfolio { get; set; } = "";
    public DateTimeOffset TradeTime { get; set; }
    public OrderStatus Status { get; set; }
    public string? RejectReason { get; set; }
    public string? ExternalBookingId { get; set; }
    public string CorrelationId { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? BookedAt { get; set; }
    public string? BrokerOrderId { get; set; }
    public decimal FilledQuantity { get; set; }
    public decimal? AveragePrice { get; set; }

    public static OrderEntity From(TradeOrderRequest r, string correlationId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        ClientOrderId = r.ClientOrderId ?? "",
        SourceSystem = r.SourceSystem ?? "",
        MessageType = r.MessageType ?? "",
        InstrumentId = r.InstrumentId ?? "",
        Side = r.Side,
        Quantity = r.Quantity,
        Price = r.Price,
        Currency = r.Currency ?? "",
        Portfolio = r.Portfolio ?? "",
        TradeTime = r.TradeTime,
        CorrelationId = correlationId,
        ReceivedAt = now,
    };

    public IReadOnlyList<string> GetErrors() =>
        string.IsNullOrEmpty(RejectReason) ? [] : RejectReason.Split("; ");

    public OrderResponse ToResponse() =>
        new(Id, ClientOrderId, SourceSystem, Status, GetErrors(), ExternalBookingId, CorrelationId, BrokerOrderId, FilledQuantity, AveragePrice);

    public OrderAcceptedEvent ToAcceptedEvent() =>
        new(Id, ClientOrderId, SourceSystem, MessageType, InstrumentId, Side, Quantity, Price, Currency, Portfolio, TradeTime, CorrelationId);

    public OrderFilledEvent ToFilledEvent() =>
        new(Id, ClientOrderId, InstrumentId, Side, FilledQuantity, AveragePrice ?? Price, Currency, Portfolio, BrokerOrderId ?? "", CorrelationId);
}

/// <summary>One FIX ExecutionReport received from the broker. ExecId is unique, so a repeat is ignored.</summary>
public sealed class ExecutionEntity
{
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    public string ExecId { get; set; } = "";
    public string ExecType { get; set; } = "";
    public string OrdStatus { get; set; } = "";
    public decimal LastQty { get; set; }
    public decimal LastPx { get; set; }
    public decimal CumQty { get; set; }
    public decimal LeavesQty { get; set; }
    public decimal AvgPx { get; set; }
    public string? Text { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>Transactional outbox: written in the same transaction as the order, published afterwards.</summary>
public sealed class OutboxMessage
{
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    public string EventType { get; set; } = "";
    public string Body { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Append-only audit trail of every state change.</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public DateTimeOffset At { get; set; }

    public static AuditEntry For(OrderEntity order, string action, string detail, DateTimeOffset at) => new()
    {
        OrderId = order.Id,
        Action = action,
        Detail = detail,
        CorrelationId = order.CorrelationId,
        At = at,
    };
}
