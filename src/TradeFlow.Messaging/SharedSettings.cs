using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace TradeFlow.Messaging;

public static class SharedSettings
{
    /// <summary>
    /// Loads tradeflow.shared.json (copied next to every executable) so all four processes use the same
    /// Messaging and Legacy settings. Secrets such as the Service Bus connection string are deliberately
    /// NOT in that file; set them as environment variables (e.g. Messaging__AzureServiceBus__ConnectionString).
    /// Environment variables are re-added afterwards so they still win.
    /// </summary>
    public static void AddTradeFlowSharedSettings(this IConfigurationManager configuration, IHostEnvironment environment)
    {
        if (environment.IsEnvironment("Testing"))
        {
            return;
        }
        configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "tradeflow.shared.json"), optional: true, reloadOnChange: false);
        configuration.AddEnvironmentVariables();
    }
}
