using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using TradeFlow.Contracts;
using TradeFlow.Legacy;
using TradeFlow.Messaging;

namespace TradeFlow.SimCorpSimulator;

public sealed class SimCorpOptions
{
    public string ApiBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>Share of bookings that fail on purpose, to demo retries and the dead-letter queue.</summary>
    public double FailureRate { get; set; }

    /// <summary>
    /// When true (Routing:OrdersViaFix), orders are executed at the broker first via the FIX gateway, and
    /// SimCorp books them when the OrderFilled event arrives. Trades are already executed and are booked directly.
    /// </summary>
    public bool OrdersViaFix { get; set; }
}

public sealed class BookingClient(HttpClient http)
{
    public async Task ConfirmAsync(Guid orderId, BookingConfirmation booking, string correlationId, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"api/orders/{orderId}/booking")
        {
            Content = JsonContent.Create(booking, options: Json.Options),
        };
        message.Headers.Add("X-Correlation-ID", correlationId);
        using var response = await http.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// Plays SimCorp Dimension (the book of record) on the modern side: consumes OrderAccepted events from the bus (Service Bus,
/// SQS or the local folder bus), books the trade and confirms back to the API.
/// </summary>
public sealed class BookingWorker(
    IEventConsumer consumer, BookingClient api, IOptions<SimCorpOptions> options,
    MessagingOptions messaging, ILogger<BookingWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SimCorp booking worker listening on {Provider}", messaging.Provider);
        return consumer.RunAsync(HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(BusMessage message, CancellationToken ct)
    {
        switch (message.EventType)
        {
            case EventTypes.OrderAccepted:
            {
                var order = Json.Deserialize<OrderAcceptedEvent>(message.Body);
                if (options.Value.OrdersViaFix
                    && string.Equals(order.MessageType, LegacyMessageTypes.Order, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogDebug("{ClientOrderId} is an order: waiting for the broker fill before booking", order.ClientOrderId);
                    return;
                }
                await BookAsync(order.OrderId, order.ClientOrderId, order.Side, order.Quantity, order.InstrumentId,
                    order.Price, order.CorrelationId, "trade", ct);
                break;
            }
            case EventTypes.OrderFilled:
            {
                var fill = Json.Deserialize<OrderFilledEvent>(message.Body);
                await BookAsync(fill.OrderId, fill.ClientOrderId, fill.Side, fill.FilledQuantity, fill.InstrumentId,
                    fill.AveragePrice, fill.CorrelationId, $"fill from {fill.BrokerOrderId}", ct);
                break;
            }
            default:
                logger.LogDebug("Ignoring {EventType}", message.EventType);
                break;
        }
    }

    private async Task BookAsync(Guid orderId, string clientOrderId, Side side, decimal quantity, string instrumentId,
        decimal price, string correlationId, string what, CancellationToken ct)
    {
        if (Random.Shared.NextDouble() < options.Value.FailureRate)
        {
            throw new InvalidOperationException($"Simulated SimCorp booking failure for {clientOrderId}");
        }

        var bookingId = $"SCD-{Random.Shared.Next(10_000_000, 99_999_999)}";
        // If the API is down, wait and retry here instead of failing the message, so an outage
        // never pushes good events to the dead-letter queue.
        while (true)
        {
            try
            {
                await api.ConfirmAsync(orderId, new BookingConfirmation(bookingId, "SimCorp"), correlationId, ct);
                break;
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
            {
                logger.LogWarning("API unavailable, retrying booking of {ClientOrderId} in 2 s: {Error}", clientOrderId, ex.Message);
                await Task.Delay(2000, ct);
            }
        }
        logger.LogInformation("Booked {ClientOrderId} ({What}) {Side} {Quantity} {Instrument} @ {Price} as {BookingId}",
            clientOrderId, what, side, quantity, instrumentId, price, bookingId);
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } code } => (int)code >= 500 || (int)code == 408 || (int)code == 429,
        TaskCanceledException => true,
        _ => false,
    };
}

/// <summary>SimCorp's existing legacy input: prices and positions that have not been migrated yet.</summary>
public sealed class LegacyFeedWorker(ILegacyQueue queue, LegacyQueueOptions legacy, ILogger<LegacyFeedWorker> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() =>
    {
        logger.LogInformation("SimCorp legacy feed reading {Queue} ({Transport})", legacy.SimCorpInQueue, legacy.Transport);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                queue.TryProcessNext(legacy.SimCorpInQueue, TimeSpan.FromSeconds(2), m =>
                    logger.LogInformation("Legacy path: received {Type} {Payload}", m.MessageType, m.Payload));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Legacy feed read failed");
                stoppingToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
            }
        }
    }, stoppingToken);
}
