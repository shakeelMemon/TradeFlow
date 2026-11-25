using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeFlow.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<Side>))]
public enum Side { Buy, Sell }

[JsonConverter(typeof(JsonStringEnumConverter<OrderStatus>))]
public enum OrderStatus { Rejected, Accepted, Published, SentToBroker, PartiallyFilled, Filled, BrokerRejected, Booked }

/// <summary>Message types Calypso puts on the legacy queue.</summary>
public static class LegacyMessageTypes
{
    public const string Order = "Order";
    public const string Trade = "Trade";
    public const string Price = "Price";
    public const string Position = "Position";
}

/// <summary>Event types published on the modern message bus.</summary>
public static class EventTypes
{
    public const string OrderAccepted = "OrderAccepted";
    public const string OrderFilled = "OrderFilled";
}

/// <summary>An order or executed trade submitted for validation and booking.</summary>
public sealed record TradeOrderRequest(
    string ClientOrderId,
    string SourceSystem,
    string MessageType,
    string InstrumentId,
    Side Side,
    decimal Quantity,
    decimal Price,
    string Currency,
    string Portfolio,
    DateTimeOffset TradeTime);

public sealed record PriceUpdate(string InstrumentId, decimal Price, string Currency, DateTimeOffset AsOf);

public sealed record PositionSnapshot(string Portfolio, string InstrumentId, decimal Quantity, DateTimeOffset AsOf);

/// <summary>Published to SimCorp once an order passes validation.</summary>
public sealed record OrderAcceptedEvent(
    Guid OrderId,
    string ClientOrderId,
    string SourceSystem,
    string MessageType,
    string InstrumentId,
    Side Side,
    decimal Quantity,
    decimal Price,
    string Currency,
    string Portfolio,
    DateTimeOffset TradeTime,
    string CorrelationId);

public sealed record BookingConfirmation(string ExternalBookingId, string BookedBy);

/// <summary>Published once the broker has fully filled an order; SimCorp books the fill.</summary>
public sealed record OrderFilledEvent(
    Guid OrderId,
    string ClientOrderId,
    string InstrumentId,
    Side Side,
    decimal FilledQuantity,
    decimal AveragePrice,
    string Currency,
    string Portfolio,
    string BrokerOrderId,
    string CorrelationId);

/// <summary>A FIX ExecutionReport (35=8), as the FIX gateway forwards it to the API.</summary>
public sealed record ExecutionReportDto(
    string ExecId,
    string BrokerOrderId,
    string ExecType,
    string OrdStatus,
    decimal LastQty,
    decimal LastPx,
    decimal CumQty,
    decimal LeavesQty,
    decimal AvgPx,
    string? Text);

public sealed record OrderResponse(
    Guid OrderId,
    string ClientOrderId,
    string SourceSystem,
    OrderStatus Status,
    IReadOnlyList<string> Errors,
    string? ExternalBookingId,
    string CorrelationId,
    string? BrokerOrderId = null,
    decimal FilledQuantity = 0,
    decimal? AveragePrice = null);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name}.");
}
