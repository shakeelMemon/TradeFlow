using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TradeFlow.Legacy;

public sealed class LegacyQueueOptions
{
    /// <summary>"Msmq" (Windows) or "File".</summary>
    public string Transport { get; set; } = "Msmq";

    /// <summary>Root folder for the File transport.</summary>
    public string FileRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TradeFlow", "legacy-queues");

    /// <summary>Queue Calypso writes to.</summary>
    public string CalypsoOutQueue { get; set; } = "tradeflow.calypso.out";

    /// <summary>Queue SimCorp still reads for message types that have not been migrated.</summary>
    public string SimCorpInQueue { get; set; } = "tradeflow.simcorp.in";

    /// <summary>Where the bridge parks messages that keep failing.</summary>
    public string DeadLetterQueue { get; set; } = "tradeflow.deadletter";
}

public static class LegacyServiceCollectionExtensions
{
    public static IServiceCollection AddLegacyQueue(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Legacy").Get<LegacyQueueOptions>() ?? new LegacyQueueOptions();
        services.AddSingleton(options);

        if (string.Equals(options.Transport, "File", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ILegacyQueue>(new FileLegacyQueue(options.FileRoot));
        }
        else if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<ILegacyQueue, MsmqLegacyQueue>();
        }
        else
        {
            throw new PlatformNotSupportedException(
                "MSMQ only runs on Windows. Set Legacy:Transport to \"File\" on this machine.");
        }
        return services;
    }
}
