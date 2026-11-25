using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TradeFlow.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>Registers the publisher and consumer for the provider chosen in Messaging:Provider.</summary>
    public static IServiceCollection AddTradeFlowMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Messaging").Get<MessagingOptions>() ?? new MessagingOptions();
        services.AddSingleton(options);

        switch (options.Provider.ToLowerInvariant())
        {
            case "azureservicebus":
                if (string.IsNullOrWhiteSpace(options.AzureServiceBus.FullyQualifiedNamespace)
                    && string.IsNullOrWhiteSpace(options.AzureServiceBus.ConnectionString))
                {
                    throw new InvalidOperationException(
                        "Set Messaging:AzureServiceBus:FullyQualifiedNamespace (identity sign-in) or " +
                        "Messaging__AzureServiceBus__ConnectionString (SAS key) before using AzureServiceBus.");
                }
                services.AddSingleton<IEventPublisher, AzureServiceBusPublisher>();
                services.AddSingleton<IEventConsumer, AzureServiceBusConsumer>();
                break;

            case "aws":
                services.AddSingleton<IEventPublisher, AwsSnsPublisher>();
                services.AddSingleton<IEventConsumer, AwsSqsConsumer>();
                break;

            case "inmemory":
                services.AddSingleton<InMemoryBus>();
                services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<InMemoryBus>());
                services.AddSingleton<IEventConsumer>(sp => sp.GetRequiredService<InMemoryBus>());
                break;

            case "localfile":
                services.AddSingleton<LocalFileBus>();
                services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<LocalFileBus>());
                services.AddSingleton<IEventConsumer>(sp => sp.GetRequiredService<LocalFileBus>());
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown Messaging:Provider '{options.Provider}'. Use LocalFile, AzureServiceBus, Aws or InMemory.");
        }
        return services;
    }
}
