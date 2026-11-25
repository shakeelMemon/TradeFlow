namespace TradeFlow.Api.Validation;

/// <param name="PriceFactor">Bonds are quoted per 100 of face value (0.01); equities per share (1).</param>
public sealed record Instrument(string Id, string Description, string AssetClass, string Currency, decimal ReferencePrice, decimal PriceFactor);

/// <summary>
/// Static reference data for the demo. In a real desk this comes from the security master and
/// the risk/limits system (and reference prices from Bloomberg).
/// </summary>
public sealed class ReferenceData
{
    public IReadOnlyDictionary<string, Instrument> Instruments { get; } =
        new[]
        {
            new Instrument("CAN-2.75-2035", "Government of Canada 2.75% 2035", "FixedIncome", "CAD", 97.50m, 0.01m),
            new Instrument("CAN-3.25-2030", "Government of Canada 3.25% 2030", "FixedIncome", "CAD", 101.20m, 0.01m),
            new Instrument("ONT-3.65-2033", "Province of Ontario 3.65% 2033", "FixedIncome", "CAD", 99.10m, 0.01m),
            new Instrument("UST-4.25-2035", "US Treasury 4.25% 2035", "FixedIncome", "USD", 98.75m, 0.01m),
            new Instrument("RY.TO", "Royal Bank of Canada", "Equity", "CAD", 165.00m, 1m),
            new Instrument("TD.TO", "Toronto-Dominion Bank", "Equity", "CAD", 92.00m, 1m),
        }.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum notional per order, by portfolio, in the instrument currency.</summary>
    public IReadOnlyDictionary<string, decimal> PortfolioNotionalLimits { get; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["CAD-FI-CORE"] = 50_000_000m,
            ["USD-FI-GLOBAL"] = 25_000_000m,
            ["CAD-EQ-ACTIVE"] = 5_000_000m,
        };

    /// <summary>Fat-finger check: reject prices more than this fraction away from the reference price.</summary>
    public decimal PriceTolerance { get; } = 0.10m;
}
