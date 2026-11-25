using TradeFlow.Contracts;

namespace TradeFlow.Api.Validation;

/// <summary>Pre-trade validation run on every order before it is accepted and sent to SimCorp.</summary>
public sealed class OrderValidator(ReferenceData referenceData, TimeProvider clock)
{
    private static readonly HashSet<string> AcceptedTypes =
        new(StringComparer.OrdinalIgnoreCase) { LegacyMessageTypes.Order, LegacyMessageTypes.Trade };

    public List<string> Validate(TradeOrderRequest order)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(order.ClientOrderId)) errors.Add("ClientOrderId is required");
        if (string.IsNullOrWhiteSpace(order.SourceSystem)) errors.Add("SourceSystem is required");
        if (!AcceptedTypes.Contains(order.MessageType ?? "")) errors.Add($"MessageType '{order.MessageType}' is not Order or Trade");
        if (order.Quantity <= 0) errors.Add("Quantity must be greater than zero");
        if (order.Price <= 0) errors.Add("Price must be greater than zero");
        if (order.TradeTime > clock.GetUtcNow().AddMinutes(5)) errors.Add("TradeTime is in the future");

        if (!referenceData.Instruments.TryGetValue(order.InstrumentId ?? "", out var instrument))
        {
            errors.Add($"Unknown instrument '{order.InstrumentId}'");
        }
        else
        {
            if (!string.Equals(order.Currency, instrument.Currency, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Currency {order.Currency} does not match instrument currency {instrument.Currency}");
            }

            if (order.Price > 0)
            {
                var deviation = Math.Abs(order.Price - instrument.ReferencePrice) / instrument.ReferencePrice;
                if (deviation > referenceData.PriceTolerance)
                {
                    errors.Add($"Price {order.Price} is {deviation:P0} away from reference {instrument.ReferencePrice} (fat-finger check)");
                }
            }

            if (!referenceData.PortfolioNotionalLimits.TryGetValue(order.Portfolio ?? "", out var limit))
            {
                errors.Add($"Unknown portfolio '{order.Portfolio}'");
            }
            else
            {
                var notional = order.Quantity * order.Price * instrument.PriceFactor;
                if (notional > limit)
                {
                    errors.Add($"Notional {notional:N0} exceeds portfolio limit {limit:N0}");
                }
            }
        }

        return errors;
    }
}
