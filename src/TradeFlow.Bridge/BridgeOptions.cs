namespace TradeFlow.Bridge;

public sealed class BridgeOptions
{
    public string ApiBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>
    /// Message types already moved to the new API (comma separated). Everything else keeps
    /// flowing to SimCorp over the legacy queue. Add "Price,Position" to finish the migration.
    /// </summary>
    public string MigratedTypes { get; set; } = "Order,Trade";

    /// <summary>Hand-off attempts before a message is parked on the dead-letter queue.</summary>
    public int MaxAttempts { get; set; } = 5;

    public ISet<string> MigratedTypeSet =>
        MigratedTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
