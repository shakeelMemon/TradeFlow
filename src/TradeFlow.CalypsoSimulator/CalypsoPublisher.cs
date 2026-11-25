using Microsoft.Extensions.Options;
using TradeFlow.Contracts;
using TradeFlow.Legacy;

namespace TradeFlow.CalypsoSimulator;

public sealed class SimulatorOptions
{
    public int IntervalMs { get; set; } = 1500;

    /// <summary>0 = run until stopped.</summary>
    public int MaxMessages { get; set; }

    /// <summary>Share of orders deliberately broken (unknown instrument, fat-finger price, over limit).</summary>
    public double BadOrderRate { get; set; } = 0.10;

    /// <summary>Share of orders sent twice, like a legacy redelivery.</summary>
    public double DuplicateRate { get; set; } = 0.05;
}

/// <summary>
/// Plays the role of Calypso: writes orders, trades, prices and positions to the legacy
/// queue exactly as the old system did. It knows nothing about the new API.
/// </summary>
public sealed class CalypsoPublisher(
    ILegacyQueue queue, LegacyQueueOptions legacy, IOptions<SimulatorOptions> options, ILogger<CalypsoPublisher> logger)
    : BackgroundService
{
    private sealed record SimInstrument(string Id, string Currency, decimal Price, string Portfolio, decimal Lot);

    private static readonly SimInstrument[] Instruments =
    [
        new("CAN-2.75-2035", "CAD", 97.50m, "CAD-FI-CORE", 1_000_000m),
        new("CAN-3.25-2030", "CAD", 101.20m, "CAD-FI-CORE", 1_000_000m),
        new("ONT-3.65-2033", "CAD", 99.10m, "CAD-FI-CORE", 1_000_000m),
        new("UST-4.25-2035", "USD", 98.75m, "USD-FI-GLOBAL", 1_000_000m),
        new("RY.TO", "CAD", 165.00m, "CAD-EQ-ACTIVE", 100m),
        new("TD.TO", "CAD", 92.00m, "CAD-EQ-ACTIVE", 100m),
    ];

    private readonly Random _random = new();
    private readonly string _runId = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        logger.LogInformation("Calypso simulator writing to {Queue} ({Transport}) every {Interval} ms",
            legacy.CalypsoOutQueue, legacy.Transport, settings.IntervalMs);

        LegacyMessage? lastOrder = null;
        for (var i = 1; settings.MaxMessages == 0 || i <= settings.MaxMessages; i++)
        {
            LegacyMessage message;
            if (lastOrder is not null && _random.NextDouble() < settings.DuplicateRate)
            {
                message = lastOrder with { MessageId = Guid.NewGuid().ToString("N"), SentAt = DateTimeOffset.UtcNow };
                logger.LogInformation("Re-sending {Id} as a duplicate (legacy redelivery)", lastOrder.MessageId);
            }
            else
            {
                message = Next(i, settings);
            }

            queue.Send(legacy.CalypsoOutQueue, message);
            logger.LogInformation("Sent {Type} #{Number}", message.MessageType, i);
            if (message.MessageType is LegacyMessageTypes.Order or LegacyMessageTypes.Trade)
            {
                lastOrder = message;
            }

            try
            {
                await Task.Delay(settings.IntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private LegacyMessage Next(int number, SimulatorOptions settings)
    {
        var roll = _random.NextDouble();
        var type = roll switch
        {
            < 0.45 => LegacyMessageTypes.Order,
            < 0.70 => LegacyMessageTypes.Trade,
            < 0.90 => LegacyMessageTypes.Price,
            _ => LegacyMessageTypes.Position,
        };
        var instrument = Instruments[_random.Next(Instruments.Length)];
        var now = DateTimeOffset.UtcNow;

        string payload = type switch
        {
            LegacyMessageTypes.Price => Json.Serialize(new PriceUpdate(instrument.Id, Jitter(instrument.Price, 0.01), instrument.Currency, now)),
            LegacyMessageTypes.Position => Json.Serialize(new PositionSnapshot(instrument.Portfolio, instrument.Id, instrument.Lot * _random.Next(1, 50), now)),
            _ => Json.Serialize(BuildOrder(number, type, instrument, now, settings)),
        };

        return new LegacyMessage(Guid.NewGuid().ToString("N"), type, Guid.NewGuid().ToString("N"), payload, now);
    }

    private TradeOrderRequest BuildOrder(int number, string type, SimInstrument instrument, DateTimeOffset now, SimulatorOptions settings)
    {
        var order = new TradeOrderRequest(
            ClientOrderId: $"CLY-{now:yyyyMMdd}-{_runId}-{number:D5}",
            SourceSystem: "Calypso",
            MessageType: type,
            InstrumentId: instrument.Id,
            Side: _random.Next(2) == 0 ? Side.Buy : Side.Sell,
            Quantity: instrument.Lot * _random.Next(1, 10),
            Price: Jitter(instrument.Price, 0.005),
            Currency: instrument.Currency,
            Portfolio: instrument.Portfolio,
            TradeTime: now);

        if (_random.NextDouble() >= settings.BadOrderRate)
        {
            return order;
        }

        return _random.Next(3) switch
        {
            0 => order with { InstrumentId = "XYZ-UNKNOWN" },
            1 => order with { Price = Math.Round(order.Price * 1.5m, 2) },   // fat finger
            _ => order with { Quantity = order.Quantity * 1000 },            // over the notional limit
        };
    }

    private decimal Jitter(decimal price, double pct) =>
        Math.Round(price * (1 + (decimal)((_random.NextDouble() * 2 - 1) * pct)), 2);
}
