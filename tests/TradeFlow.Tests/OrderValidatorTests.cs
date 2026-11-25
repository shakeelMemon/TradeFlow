using TradeFlow.Api.Validation;
using TradeFlow.Contracts;

namespace TradeFlow.Tests;

public class OrderValidatorTests
{
    private readonly OrderValidator _validator = new(new ReferenceData(), TimeProvider.System);

    internal static TradeOrderRequest ValidBondOrder(string clientOrderId = "T-1") => new(
        ClientOrderId: clientOrderId,
        SourceSystem: "Calypso",
        MessageType: LegacyMessageTypes.Order,
        InstrumentId: "CAN-2.75-2035",
        Side: Side.Buy,
        Quantity: 5_000_000m,
        Price: 97.45m,
        Currency: "CAD",
        Portfolio: "CAD-FI-CORE",
        TradeTime: DateTimeOffset.UtcNow);

    [Fact]
    public void Valid_order_passes() => Assert.Empty(_validator.Validate(ValidBondOrder()));

    [Fact]
    public void Unknown_instrument_is_rejected() =>
        Assert.Contains(_validator.Validate(ValidBondOrder() with { InstrumentId = "XYZ" }), e => e.Contains("Unknown instrument"));

    [Fact]
    public void Fat_finger_price_is_rejected() =>
        Assert.Contains(_validator.Validate(ValidBondOrder() with { Price = 150m }), e => e.Contains("fat-finger"));

    [Fact]
    public void Currency_mismatch_is_rejected() =>
        Assert.Contains(_validator.Validate(ValidBondOrder() with { Currency = "USD" }), e => e.Contains("does not match"));

    [Fact]
    public void Notional_over_portfolio_limit_is_rejected() =>
        // 60m face x 97.45 / 100 = 58.5m notional > 50m limit
        Assert.Contains(_validator.Validate(ValidBondOrder() with { Quantity = 60_000_000m }), e => e.Contains("exceeds portfolio limit"));

    [Fact]
    public void Price_and_position_messages_are_not_orders() =>
        Assert.Contains(_validator.Validate(ValidBondOrder() with { MessageType = LegacyMessageTypes.Price }), e => e.Contains("MessageType"));
}
